using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var appConfig = AppConfig.From(builder.Configuration);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(appConfig.DatabasePath))!);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(appConfig.TokenFilePath))!);
Directory.CreateDirectory(Path.GetFullPath(appConfig.StorageRootPath));

await Db.InitializeAsync(appConfig.DatabasePath, Path.Combine(AppContext.BaseDirectory, "schema.sql"));

builder.Services.AddSingleton(appConfig);
builder.Services.AddHttpClient();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var authHeader = context.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiError.Unauthorized("Authorizationヘッダがありません。"));
        return;
    }

    var token = authHeader[7..].Trim();
    var expected = await Auth.LoadOrCreateTokenAsync(appConfig.TokenFilePath);
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiError.Unauthorized("トークンが不正です。"));
        return;
    }

    await next();
});

app.MapGet("/api/v1/health", () => Results.Ok(new { ok = true, service = "x-post-archive-api" }));

app.MapPost("/api/v1/auth/bootstrap", async () =>
{
    var token = await Auth.LoadOrCreateTokenAsync(appConfig.TokenFilePath);
    return Results.Ok(new { ok = true, token });
});

app.MapPost("/api/v1/posts", async (
    SavePostRequest request,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    var validationError = ValidateRequest(request);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    if (!DateTimeOffset.TryParse(request.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
    {
        return Results.BadRequest(ApiError.BadRequest("CREATED_AT_INVALID", "created_at はISO 8601形式で指定してください。"));
    }

    var savedAt = DateTimeOffset.Now;
    var postDir = Path.Combine(Path.GetFullPath(appConfig.StorageRootPath), $"tweet-{request.tweet_id}");
    if (Directory.Exists(postDir))
    {
        return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じtweet_idは既に保存済みです。"));
    }

    await using var conn = Db.Open(appConfig.DatabasePath);
    await conn.OpenAsync(ct);

    var exists = await Db.PostExistsAsync(conn, request.tweet_id, ct);
    if (exists)
    {
        return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じtweet_idは既に保存済みです。"));
    }

    Directory.CreateDirectory(postDir);
    Directory.CreateDirectory(Path.Combine(postDir, "images"));
    Directory.CreateDirectory(Path.Combine(postDir, "videos"));

    var downloadedImages = new List<MediaRow>();
    var downloadedVideos = new List<MediaRow>();
    try
    {
        var screenshotPath = Path.Combine(postDir, "screenshot.png");
        var screenshotBytes = DecodeDataUrlPng(request.screenshot_base64);
        await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, ct);

        var httpClient = httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XPostArchive/1.0");

        for (var i = 0; i < request.images.Count; i++)
        {
            var originalUrl = request.images[i].url;
            var finalUrl = UrlUtil.PreferOrig(originalUrl);
            var ext = UrlUtil.DetectExtension(finalUrl, "jpg");
            var rel = Path.Combine("images", $"{(i + 1):000}.{ext}");
            var abs = Path.Combine(postDir, rel);

            await DownloadFileAsync(httpClient, finalUrl, abs, ct);
            downloadedImages.Add(new MediaRow("image", originalUrl, rel.Replace('\\', '/'), i));
        }

        for (var i = 0; i < request.video_playlists.Count; i++)
        {
            var m3u8Url = request.video_playlists[i].m3u8_url;
            var rel = Path.Combine("videos", $"{(i + 1):000}.mp4");
            var abs = Path.Combine(postDir, rel);

            var result = await Video.DownloadAsync(appConfig.FfmpegPath, m3u8Url, abs, appConfig.VideoRetryCount, ct);
            if (!result.Ok)
            {
                TryDeleteDirectory(postDir);
                return Results.UnprocessableEntity(ApiError.RetryableVideoFail(result.ErrorMessage));
            }

            downloadedVideos.Add(new MediaRow("video", m3u8Url, rel.Replace('\\', '/'), i));
        }

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var authorId = await Db.UpsertAuthorAsync(conn, tx, request.author.handle, request.author.name, savedAt, ct);
        var postId = await Db.InsertPostAsync(conn, tx, request, authorId, createdAt, savedAt, postDir, ct);

        foreach (var media in downloadedImages.Concat(downloadedVideos))
        {
            await Db.InsertMediaAsync(conn, tx, postId, media, ct);
        }

        foreach (var rawTag in request.tags)
        {
            var tagName = TagUtil.Normalize(rawTag);
            if (string.IsNullOrWhiteSpace(tagName))
            {
                continue;
            }

            var tagId = await Db.UpsertTagAsync(conn, tx, tagName, savedAt, ct);
            await Db.InsertPostTagAsync(conn, tx, postId, tagId, savedAt, ct);
        }

        var meta = new MetaJson(
            request.tweet_id,
            request.url,
            new MetaAuthor(request.author.handle, request.author.name),
            request.created_at,
            request.text,
            savedAt.ToString("o"),
            request.tags.Select(TagUtil.Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList(),
            request.note,
            new MetaMedia(
                downloadedImages.Select(i => new MetaImage(i.original_url ?? string.Empty, i.local_path)).ToList(),
                downloadedVideos.Select(v => new MetaVideo(v.original_url ?? string.Empty, v.local_path)).ToList()
            )
        );

        var metaPath = Path.Combine(postDir, "meta.json");
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions.Pretty), ct);

        await tx.CommitAsync(ct);

        return Results.Ok(new
        {
            ok = true,
            post_id = postId,
            dir_path = postDir
        });
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        TryDeleteDirectory(postDir);
        return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じtweet_idは既に保存済みです。"));
    }
    catch (Exception ex)
    {
        TryDeleteDirectory(postDir);
        return Results.BadRequest(ApiError.BadRequest("SAVE_FAILED", ex.ToString()));
    }
});

app.Run($"http://{appConfig.Host}:{appConfig.Port}");

static ApiError? ValidateRequest(SavePostRequest request)
{
    if (request.tags is null) return ApiError.BadRequest("TAGS_REQUIRED", "tags は空配列で送信してください。");
    if (request.images is null) return ApiError.BadRequest("IMAGES_REQUIRED", "images は空配列で送信してください。");
    if (request.video_playlists is null) return ApiError.BadRequest("VIDEO_PLAYLISTS_REQUIRED", "video_playlists は空配列で送信してください。");
    if (string.IsNullOrWhiteSpace(request.tweet_id)) return ApiError.BadRequest("TWEET_ID_REQUIRED", "tweet_id は必須です。");
    if (!Regex.IsMatch(request.tweet_id, @"^\d+$")) return ApiError.BadRequest("TWEET_ID_INVALID", "tweet_id は数値のみ指定してください。");
    if (string.IsNullOrWhiteSpace(request.url)) return ApiError.BadRequest("URL_REQUIRED", "url は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.handle)) return ApiError.BadRequest("AUTHOR_HANDLE_REQUIRED", "author.handle は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.name)) return ApiError.BadRequest("AUTHOR_NAME_REQUIRED", "author.name は必須です。");
    if (string.IsNullOrWhiteSpace(request.created_at)) return ApiError.BadRequest("CREATED_AT_REQUIRED", "created_at は必須です。");
    if (string.IsNullOrWhiteSpace(request.text)) return ApiError.BadRequest("TEXT_REQUIRED", "text は必須です。");
    if (string.IsNullOrWhiteSpace(request.screenshot_base64)) return ApiError.BadRequest("SCREENSHOT_REQUIRED", "screenshot_base64 は必須です。");
    if (request.screenshot_base64.Length > 15_000_000) return ApiError.BadRequest("PAYLOAD_TOO_LARGE", "screenshot_base64 が大きすぎます。");
    if (!UrlUtil.IsAllowedPostUrl(request.url)) return ApiError.BadRequest("URL_INVALID", "url は X のポストURLを指定してください。");

    foreach (var image in request.images)
    {
        if (!UrlUtil.IsAllowedImageUrl(image.url))
        {
            return ApiError.BadRequest("IMAGE_URL_INVALID", $"許可されていない画像URLです: {image.url}");
        }
    }

    foreach (var video in request.video_playlists)
    {
        if (!UrlUtil.IsAllowedVideoUrl(video.m3u8_url))
        {
            return ApiError.BadRequest("VIDEO_URL_INVALID", $"許可されていない動画URLです: {video.m3u8_url}");
        }
    }

    return null;
}

static byte[] DecodeDataUrlPng(string dataUrl)
{
    var marker = "base64,";
    var idx = dataUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (idx < 0)
    {
        throw new InvalidOperationException("screenshot_base64 が data URL 形式ではありません。");
    }

    var base64 = dataUrl[(idx + marker.Length)..];
    return Convert.FromBase64String(base64);
}

static async Task DownloadFileAsync(HttpClient client, string url, string outPath, CancellationToken ct)
{
    using var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    await using var fs = File.OpenWrite(outPath);
    await response.Content.CopyToAsync(fs, ct);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
    catch
    {
        // cleanup best effort
    }
}

sealed record AppConfig(string Host, int Port, string StorageRootPath, string DatabasePath, string TokenFilePath, string FfmpegPath, int VideoRetryCount)
{
    public static AppConfig From(IConfiguration config)
    {
        var ffmpegPath = config["Video:FfmpegPath"] ?? "tools/ffmpeg.exe";
        if (!Path.IsPathRooted(ffmpegPath))
        {
            ffmpegPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ffmpegPath));
        }

        return new AppConfig(
            config["Server:Host"] ?? "127.0.0.1",
            int.TryParse(config["Server:Port"], out var p) ? p : 18765,
            config["Storage:RootPath"] ?? "./XArchive",
            config["Database:Path"] ?? "./data/archive.db",
            config["Auth:TokenFilePath"] ?? "./data/auth_token.txt",
            ffmpegPath,
            int.TryParse(config["Video:RetryCount"], out var r) ? r : 2
        );
    }
}

sealed record SavePostRequest(
    string tweet_id,
    string url,
    Author author,
    string created_at,
    string text,
    List<string> tags,
    string? note,
    string screenshot_base64,
    List<ImageInput> images,
    List<VideoPlaylistInput> video_playlists
)
{
    public SavePostRequest() : this("", "", new Author("", ""), "", "", [], null, "", [], [])
    {
    }
}

sealed record Author(string handle, string name);
sealed record ImageInput(string url);
sealed record VideoPlaylistInput(string m3u8_url);
sealed record MediaRow(string media_type, string? original_url, string local_path, int sort_order);

sealed record ApiError(bool ok, string error_code, string message, bool can_retry = false)
{
    public static ApiError BadRequest(string code, string message) => new(false, code, message);
    public static ApiError Unauthorized(string message) => new(false, "UNAUTHORIZED", message);
    public static ApiError Conflict(string code, string message) => new(false, code, message);
    public static ApiError RetryableVideoFail(string message) => new(false, "VIDEO_DOWNLOAD_FAILED", message, can_retry: true);
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

static class Auth
{
    public static async Task<string> LoadOrCreateTokenAsync(string tokenFilePath)
    {
        var full = Path.GetFullPath(tokenFilePath);
        if (File.Exists(full))
        {
            var existing = await File.ReadAllTextAsync(full);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing.Trim();
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(full, token);
        return token;
    }
}

static class UrlUtil
{
    public static string PreferOrig(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.Contains("twimg.com", StringComparison.OrdinalIgnoreCase)) return url;

        var builder = new UriBuilder(uri);
        var map = ParseQuery(builder.Query);
        map["name"] = "orig";
        builder.Query = string.Join("&", map.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return builder.Uri.AbsoluteUri;
    }

    public static string DetectExtension(string url, string fallback)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).Trim('.');
            return string.IsNullOrWhiteSpace(ext) ? fallback : ext.ToLowerInvariant();
        }
        catch
        {
            return fallback;
        }
    }

    public static bool IsAllowedPostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.Contains("/status/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedImageUrl(string url)
    {
        return IsHttpsHost(url, "pbs.twimg.com", ".twimg.com");
    }

    public static bool IsAllowedVideoUrl(string url)
    {
        return IsHttpsHost(url, "video.twimg.com", ".twimg.com");
    }

    private static bool IsHttpsHost(string url, params string[] allowedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return allowedHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            (host.StartsWith(".") && uri.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trim = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trim)) return map;

        foreach (var pair in trim.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            map[key] = value;
        }

        return map;
    }
}

static class TagUtil
{
    public static string Normalize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var trimmed = tag.Trim();
        return System.Text.RegularExpressions.Regex.Replace(trimmed, "\\s+", " ");
    }
}

static class Video
{
    public static async Task<(bool Ok, string ErrorMessage)> DownloadAsync(
        string ffmpegPath,
        string m3u8Url,
        string outPath,
        int retryCount,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{m3u8Url}\" -c copy \"{outPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process is null)
                {
                    return (false, "ffmpeg の起動に失敗しました。");
                }

                await process.WaitForExitAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync();

                if (process.ExitCode == 0 && File.Exists(outPath))
                {
                    return (true, string.Empty);
                }

                if (attempt == retryCount)
                {
                    return (false, $"ffmpeg終了コード: {process.ExitCode}\n{stderr}");
                }
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    return (false, ex.ToString());
                }
            }
        }

        return (false, "動画保存に失敗しました。");
    }
}

static class Db
{
    public static async Task InitializeAsync(string dbPath, string schemaPath)
    {
        var fullDbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);
        var connString = $"Data Source={fullDbPath}";
        await using var conn = new SqliteConnection(connString);
        await conn.OpenAsync();

        var sql = await File.ReadAllTextAsync(schemaPath);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static SqliteConnection Open(string dbPath)
    {
        var fullDbPath = Path.GetFullPath(dbPath);
        return new SqliteConnection($"Data Source={fullDbPath}");
    }

    public static async Task<bool> PostExistsAsync(SqliteConnection conn, string tweetId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
        cmd.Parameters.AddWithValue("$tweet_id", tweetId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public static async Task<long> UpsertAuthorAsync(SqliteConnection conn, SqliteTransaction tx, string handle, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = @"
INSERT INTO authors (handle, name, created_at, updated_at)
VALUES ($handle, $name, $now, $now)
ON CONFLICT(handle) DO UPDATE SET
  name = excluded.name,
  updated_at = excluded.updated_at;";
            upsert.Parameters.AddWithValue("$handle", handle);
            upsert.Parameters.AddWithValue("$name", name);
            upsert.Parameters.AddWithValue("$now", now.ToString("o"));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT id FROM authors WHERE handle = $handle";
        select.Parameters.AddWithValue("$handle", handle);
        return (long)(await select.ExecuteScalarAsync(ct) ?? 0L);
    }

    public static async Task<long> InsertPostAsync(SqliteConnection conn, SqliteTransaction tx, SavePostRequest request, long authorId, DateTimeOffset createdAt, DateTimeOffset savedAt, string dirPath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO posts (tweet_id, url, author_id, created_at, text, note, saved_at, dir_path)
VALUES ($tweet_id, $url, $author_id, $created_at, $text, $note, $saved_at, $dir_path);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$tweet_id", request.tweet_id);
        cmd.Parameters.AddWithValue("$url", request.url);
        cmd.Parameters.AddWithValue("$author_id", authorId);
        cmd.Parameters.AddWithValue("$created_at", createdAt.ToString("o"));
        cmd.Parameters.AddWithValue("$text", request.text);
        cmd.Parameters.AddWithValue("$note", (object?)request.note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$saved_at", savedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$dir_path", dirPath);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public static async Task InsertMediaAsync(SqliteConnection conn, SqliteTransaction tx, long postId, MediaRow media, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO media (post_id, media_type, original_url, local_path, sort_order)
VALUES ($post_id, $media_type, $original_url, $local_path, $sort_order);";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$media_type", media.media_type);
        cmd.Parameters.AddWithValue("$original_url", (object?)media.original_url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$local_path", media.local_path);
        cmd.Parameters.AddWithValue("$sort_order", media.sort_order);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> UpsertTagAsync(SqliteConnection conn, SqliteTransaction tx, string tag, DateTimeOffset now, CancellationToken ct)
    {
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = @"
INSERT INTO tags (name, created_at)
VALUES ($name, $now)
ON CONFLICT(name) DO NOTHING;";
            upsert.Parameters.AddWithValue("$name", tag);
            upsert.Parameters.AddWithValue("$now", now.ToString("o"));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT id FROM tags WHERE name = $name";
        select.Parameters.AddWithValue("$name", tag);
        return (long)(await select.ExecuteScalarAsync(ct) ?? 0L);
    }

    public static async Task InsertPostTagAsync(SqliteConnection conn, SqliteTransaction tx, long postId, long tagId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO post_tags (post_id, tag_id, created_at)
VALUES ($post_id, $tag_id, $now)
ON CONFLICT(post_id, tag_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$tag_id", tagId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

sealed record MetaJson(
    string tweet_id,
    string url,
    MetaAuthor author,
    string created_at,
    string text,
    string saved_at,
    List<string> tags,
    string? note,
    MetaMedia media
);

sealed record MetaAuthor(string handle, string name);
sealed record MetaMedia(List<MetaImage> images, List<MetaVideo> videos);
sealed record MetaImage(string original_url, string local_path);
sealed record MetaVideo(string playlist_url, string local_path);
