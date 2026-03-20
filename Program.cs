using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50 * 1024 * 1024);
var appConfig = AppConfig.From(builder.Configuration);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(appConfig.DatabasePath))!);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(appConfig.TokenFilePath))!);
Directory.CreateDirectory(Path.GetFullPath(appConfig.StorageRootPath));
await Db.InitializeAsync(appConfig.DatabasePath, Path.Combine(AppContext.BaseDirectory, "schema.sql"));
builder.Services.AddSingleton(appConfig);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<VideoDownloadQueue>();
builder.Services.AddHostedService<VideoDownloadWorker>();
var app = builder.Build();
await Db.MarkIncompleteVideoDownloadsInterruptedAsync(appConfig.DatabasePath, CancellationToken.None);

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    var authHeader = context.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiError.Unauthorized("Authorization ヘッダーがありません。"));
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
app.MapPost("/api/v1/auth/bootstrap", async () => Results.Ok(new { ok = true, token = await Auth.LoadOrCreateTokenAsync(appConfig.TokenFilePath) }));
app.MapGet("/api/v1/tags", async (CancellationToken ct) =>
{
    await using var conn = Db.Open(appConfig.DatabasePath);
    await conn.OpenAsync(ct);
    return Results.Ok(new { ok = true, tags = await TagCatalog.LoadAsync(conn, ct) });
});
app.MapGet("/api/v1/posts/{tweetId}", async (string tweetId, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(tweetId)) return Results.BadRequest(ApiError.BadRequest("TWEET_ID_REQUIRED", "tweet_id は必須です。"));
    await using var conn = Db.Open(appConfig.DatabasePath);
    await conn.OpenAsync(ct);
    var existing = await PostStore.LoadByTweetIdAsync(conn, tweetId, ct);
    return existing is null ? Results.Ok(new { ok = true, exists = false }) : Results.Ok(new { ok = true, exists = true, post = existing });
});
app.MapPost("/api/v1/posts", async (SavePostRequest request, IHttpClientFactory httpClientFactory, VideoDownloadQueue videoQueue, CancellationToken ct) =>
{
    var validationError = ValidateRequest(request);
    if (validationError is not null) return Results.BadRequest(validationError);
    if (!DateTimeOffset.TryParse(request.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
        return Results.BadRequest(ApiError.BadRequest("CREATED_AT_INVALID", "created_at は ISO 8601 形式で指定してください。"));
    var savedAt = DateTimeOffset.Now;
    var storageRoot = Path.GetFullPath(appConfig.StorageRootPath);
    var postDir = Path.Combine(storageRoot, $"tweet-{request.tweet_id}");
    var stagingDir = Path.Combine(storageRoot, ".staging", $"tweet-{request.tweet_id}-{Guid.NewGuid():N}");
    var finalDirCreated = false;
    if (Directory.Exists(postDir)) return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じ tweet_id は既に保存済みです。"));
    await using var conn = Db.Open(appConfig.DatabasePath);
    await conn.OpenAsync(ct);
    if (await Db.PostExistsAsync(conn, request.tweet_id, ct)) return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じ tweet_id は既に保存済みです。"));
    Directory.CreateDirectory(Path.Combine(stagingDir, "images"));
    Directory.CreateDirectory(Path.Combine(stagingDir, "videos"));
    try
    {
        var httpClient = httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XPostArchive/1.0");
        var images = new List<MediaRow>();
        var videos = new List<MediaRow>();
        for (var i = 0; i < request.images.Count; i++)
        {
            var originalUrl = request.images[i].url;
            var finalUrl = UrlUtil.PreferOrig(originalUrl);
            var rel = Path.Combine("images", $"{(i + 1):000}.{UrlUtil.DetectExtension(finalUrl, "jpg")}");
            await DownloadFileAsync(httpClient, finalUrl, Path.Combine(stagingDir, rel), ct);
            images.Add(new MediaRow("image", originalUrl, rel.Replace('\\', '/'), i));
        }
        for (var i = 0; i < request.video_playlists.Count; i++)
        {
            var sourceUrl = request.video_playlists[i].m3u8_url;
            var rel = Path.Combine("videos", $"{(i + 1):000}.{Video.GetPreferredExtension(sourceUrl)}");
            videos.Add(new MediaRow("video", sourceUrl, rel.Replace('\\', '/'), i));
        }
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var authorId = await Db.UpsertAuthorAsync(conn, tx, request.author.handle, request.author.name, savedAt, ct);
        var postId = await Db.InsertPostAsync(conn, tx, request, authorId, createdAt, savedAt, postDir, ct);
        foreach (var media in images) await Db.InsertMediaAsync(conn, tx, postId, media, "completed", null, ct);
        var videoJobs = new List<VideoDownloadJob>();
        foreach (var media in videos)
        {
            var mediaId = await Db.InsertMediaAsync(conn, tx, postId, media, "pending", null, ct);
            videoJobs.Add(new VideoDownloadJob(mediaId, media.original_url ?? string.Empty, Path.Combine(postDir, media.local_path), appConfig.VideoRetryCount));
        }
        foreach (var rawTag in request.tags.Select(TagUtil.Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tagId = await Db.UpsertTagAsync(conn, tx, rawTag, savedAt, ct);
            await Db.InsertPostTagAsync(conn, tx, postId, tagId, savedAt, ct);
        }
        if (Directory.Exists(postDir))
        {
            TryDeleteDirectory(stagingDir);
            return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じ tweet_id は既に保存済みです。"));
        }
        Directory.Move(stagingDir, postDir);
        finalDirCreated = true;
        await tx.CommitAsync(ct);
        foreach (var job in videoJobs)
        {
            videoQueue.Enqueue(job);
        }
        return Results.Ok(new { ok = true, post_id = postId, dir_path = postDir, background_video_count = videoJobs.Count });
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        if (finalDirCreated) TryDeleteDirectory(postDir);
        TryDeleteDirectory(stagingDir);
        return Results.Conflict(ApiError.Conflict("POST_ALREADY_EXISTS", "同じ tweet_id は既に保存済みです。"));
    }
    catch (Exception ex)
    {
        if (finalDirCreated) TryDeleteDirectory(postDir);
        TryDeleteDirectory(stagingDir);
        return Results.BadRequest(ApiError.BadRequest("SAVE_FAILED", ex.ToString()));
    }
});
app.MapPut("/api/v1/posts/{tweetId}", async (string tweetId, UpdatePostRequest request, CancellationToken ct) =>
{
    if (!string.Equals(tweetId, request.tweet_id, StringComparison.Ordinal))
        return Results.BadRequest(ApiError.BadRequest("TWEET_ID_MISMATCH", "URL の tweet_id と本文の tweet_id が一致しません。"));
    var validationError = ValidateUpdateRequest(request);
    if (validationError is not null) return Results.BadRequest(validationError);
    if (!DateTimeOffset.TryParse(request.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
        return Results.BadRequest(ApiError.BadRequest("CREATED_AT_INVALID", "created_at は ISO 8601 形式で指定してください。"));
    await using var conn = Db.Open(appConfig.DatabasePath);
    await conn.OpenAsync(ct);
    var existing = await PostStore.LoadByTweetIdAsync(conn, tweetId, ct);
    if (existing is null) return Results.NotFound(ApiError.BadRequest("POST_NOT_FOUND", "保存済み投稿が見つかりません。"));
    if (!Directory.Exists(existing.dir_path)) return Results.NotFound(ApiError.BadRequest("POST_DIR_NOT_FOUND", "保存済み投稿のディレクトリが見つかりません。"));
    try
    {
        var tags = request.tags.Select(TagUtil.Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var authorId = await Db.UpsertAuthorAsync(conn, tx, request.author.handle, request.author.name, DateTimeOffset.Now, ct);
        await Db.UpdatePostAsync(conn, tx, existing.id, request.url, authorId, createdAt, request.text ?? string.Empty, request.note, ct);
        await Db.ReplacePostTagsAsync(conn, tx, existing.id, tags, DateTimeOffset.Now, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { ok = true, updated = true, tweet_id = request.tweet_id, dir_path = existing.dir_path });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ApiError.BadRequest("UPDATE_FAILED", ex.ToString()));
    }
});
app.Run($"http://{appConfig.Host}:{appConfig.Port}");

static ApiError? ValidateRequest(SavePostRequest request)
{
    if (request.tags is null) return ApiError.BadRequest("TAGS_REQUIRED", "tags は配列で指定してください。");
    if (request.images is null) return ApiError.BadRequest("IMAGES_REQUIRED", "images は配列で指定してください。");
    if (request.video_playlists is null) return ApiError.BadRequest("VIDEO_PLAYLISTS_REQUIRED", "video_playlists は配列で指定してください。");
    if (string.IsNullOrWhiteSpace(request.tweet_id)) return ApiError.BadRequest("TWEET_ID_REQUIRED", "tweet_id は必須です。");
    if (!Regex.IsMatch(request.tweet_id, @"^\d+$")) return ApiError.BadRequest("TWEET_ID_INVALID", "tweet_id は数値のみ指定してください。");
    if (string.IsNullOrWhiteSpace(request.url)) return ApiError.BadRequest("URL_REQUIRED", "url は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.handle)) return ApiError.BadRequest("AUTHOR_HANDLE_REQUIRED", "author.handle は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.name)) return ApiError.BadRequest("AUTHOR_NAME_REQUIRED", "author.name は必須です。");
    if (string.IsNullOrWhiteSpace(request.created_at)) return ApiError.BadRequest("CREATED_AT_REQUIRED", "created_at は必須です。");
    if (!UrlUtil.IsAllowedPostUrl(request.url)) return ApiError.BadRequest("URL_INVALID", "url は X の投稿 URL を指定してください。");
    foreach (var image in request.images) if (!UrlUtil.IsAllowedImageUrl(image.url)) return ApiError.BadRequest("IMAGE_URL_INVALID", $"許可されていない画像 URL です: {image.url}");
    foreach (var video in request.video_playlists) if (!UrlUtil.IsAllowedVideoUrl(video.m3u8_url)) return ApiError.BadRequest("VIDEO_URL_INVALID", $"許可されていない動画 URL です: {video.m3u8_url}");
    return null;
}

static ApiError? ValidateUpdateRequest(UpdatePostRequest request)
{
    if (request.tags is null) return ApiError.BadRequest("TAGS_REQUIRED", "tags は配列で指定してください。");
    if (string.IsNullOrWhiteSpace(request.tweet_id)) return ApiError.BadRequest("TWEET_ID_REQUIRED", "tweet_id は必須です。");
    if (!Regex.IsMatch(request.tweet_id, @"^\d+$")) return ApiError.BadRequest("TWEET_ID_INVALID", "tweet_id は数値のみ指定してください。");
    if (string.IsNullOrWhiteSpace(request.url)) return ApiError.BadRequest("URL_REQUIRED", "url は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.handle)) return ApiError.BadRequest("AUTHOR_HANDLE_REQUIRED", "author.handle は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.name)) return ApiError.BadRequest("AUTHOR_NAME_REQUIRED", "author.name は必須です。");
    if (string.IsNullOrWhiteSpace(request.created_at)) return ApiError.BadRequest("CREATED_AT_REQUIRED", "created_at は必須です。");
    if (!UrlUtil.IsAllowedPostUrl(request.url)) return ApiError.BadRequest("URL_INVALID", "url は X の投稿 URL を指定してください。");
    return null;
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
    try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
}

sealed record AppConfig(string Host, int Port, string StorageRootPath, string DatabasePath, string TokenFilePath, string FfmpegPath, int VideoRetryCount)
{
    public static AppConfig From(IConfiguration config)
    {
        var ffmpegPath = config["Video:FfmpegPath"] ?? "third_party/ffmpeg/ffmpeg.exe";
        if (!Path.IsPathRooted(ffmpegPath))
        {
            ffmpegPath = ResolveRelativePath(ffmpegPath);
        }
        return new AppConfig(config["Server:Host"] ?? "127.0.0.1", int.TryParse(config["Server:Port"], out var p) ? p : 18765, config["Storage:RootPath"] ?? "./XArchive", config["Database:Path"] ?? "./data/archive.db", config["Auth:TokenFilePath"] ?? "./data/auth_token.txt", ffmpegPath, int.TryParse(config["Video:RetryCount"], out var r) ? r : 2);
    }

    private static string ResolveRelativePath(string relativePath)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", relativePath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", relativePath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", relativePath))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}

sealed record SavePostRequest(string tweet_id, string url, Author author, string created_at, string text, List<string> tags, string? note, List<ImageInput> images, List<VideoPlaylistInput> video_playlists)
{
    public SavePostRequest() : this("", "", new Author("", ""), "", "", [], null, [], []) { }
}
sealed record UpdatePostRequest(string tweet_id, string url, Author author, string created_at, string text, List<string> tags, string? note)
{
    public UpdatePostRequest() : this("", "", new Author("", ""), "", "", [], null) { }
}
sealed record Author(string handle, string name);
sealed record ImageInput(string url);
sealed record VideoPlaylistInput(string m3u8_url);
sealed record MediaRow(string media_type, string? original_url, string local_path, int sort_order);
sealed record VideoDownloadJob(long media_id, string source_url, string full_path, int retry_count);
sealed record TagCatalogResponseItem(string name, int count);
sealed record ExistingPostResponse(long id, string tweet_id, string url, string created_at, string text, string? note, string saved_at, string dir_path, Author author, List<string> tags);
sealed record ApiError(bool ok, string error_code, string message, bool can_retry = false)
{
    public static ApiError BadRequest(string code, string message) => new(false, code, message);
    public static ApiError Unauthorized(string message) => new(false, "UNAUTHORIZED", message);
    public static ApiError Conflict(string code, string message) => new(false, code, message);
    public static ApiError RetryableVideoFail(string message) => new(false, "VIDEO_DOWNLOAD_FAILED", message, true);
}
static class Auth
{
    public static async Task<string> LoadOrCreateTokenAsync(string tokenFilePath)
    {
        var full = Path.GetFullPath(tokenFilePath);
        if (File.Exists(full))
        {
            var existing = await File.ReadAllTextAsync(full);
            if (!string.IsNullOrWhiteSpace(existing)) return existing.Trim();
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Contains("twimg.com", StringComparison.OrdinalIgnoreCase)) return url;
        var builder = new UriBuilder(uri);
        var map = ParseQuery(builder.Query);
        map["name"] = "orig";
        builder.Query = string.Join("&", map.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return builder.Uri.AbsoluteUri;
    }
    public static string DetectExtension(string url, string fallback)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).Trim('.');
            return string.IsNullOrWhiteSpace(ext) ? fallback : ext.ToLowerInvariant();
        }
        catch { return fallback; }
    }
    public static bool IsAllowedPostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.Contains("/status/", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsAllowedImageUrl(string url) => IsHttpsHost(url, "pbs.twimg.com", ".twimg.com");
    public static bool IsAllowedVideoUrl(string url) => IsHttpsHost(url, "video.twimg.com", ".twimg.com");
    private static bool IsHttpsHost(string url, params string[] allowedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return allowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || (host.StartsWith(".") && uri.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)));
    }
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            map[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }
        return map;
    }
}
static class TagUtil
{
    public static string Normalize(string tag) => string.IsNullOrWhiteSpace(tag) ? string.Empty : Regex.Replace(tag.Trim(), "\\s+", " ");
}
static class Video
{
    private static readonly string[] DirectVideoExtensions = ["mp4", "m4v", "mov", "webm", "ts", "mkv"];

    public static async Task<(bool Ok, string ErrorMessage)> DownloadAsync(HttpClient httpClient, string ffmpegPath, string sourceUrl, string outPath, int retryCount, CancellationToken ct)
    {
        if (IsLikelyDirectVideoUrl(sourceUrl))
        {
            try
            {
                await DownloadDirectAsync(httpClient, sourceUrl, outPath, ct);
                return File.Exists(outPath)
                    ? (true, string.Empty)
                    : (false, "動画ファイルの保存に失敗しました。");
            }
            catch (Exception ex)
            {
                return (false, ex.ToString());
            }
        }

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            var psi = new ProcessStartInfo { FileName = ffmpegPath, Arguments = $"-y -i \"{sourceUrl}\" -c copy \"{outPath}\"", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            try
            {
                using var process = Process.Start(psi);
                if (process is null) return (false, "ffmpeg の起動に失敗しました。");
                await process.WaitForExitAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode == 0 && File.Exists(outPath)) return (true, string.Empty);
                if (attempt == retryCount) return (false, $"ffmpeg exit code={process.ExitCode}\n{stderr}");
            }
            catch (Exception ex)
            {
                if (attempt == retryCount) return (false, ex.ToString());
            }
        }
        return (false, "動画保存に失敗しました。");
    }
    public static string GetPreferredExtension(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return "mp4";
        }

        var extension = UrlUtil.DetectExtension(sourceUrl, "mp4");
        return DirectVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension : "mp4";
    }

    private static bool IsLikelyDirectVideoUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        var extension = UrlUtil.DetectExtension(sourceUrl, string.Empty);
        if (DirectVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            && (sourceUrl.Contains("/ext_tw_video/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/amplify_video/", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DownloadDirectAsync(HttpClient httpClient, string sourceUrl, string outPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        using var res = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var contentType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"動画ではないレスポンスが返されました: {contentType}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(fs, ct);
    }
}
static class Db
{
    public static async Task InitializeAsync(string dbPath, string schemaPath)
    {
        var fullDbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);
        await using var conn = new SqliteConnection($"Data Source={fullDbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync(schemaPath);
        await cmd.ExecuteNonQueryAsync();
        await EnsureMediaColumnsAsync(conn);
    }
    public static SqliteConnection Open(string dbPath) => new($"Data Source={Path.GetFullPath(dbPath)}");
    public static async Task<bool> PostExistsAsync(SqliteConnection conn, string tweetId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
        cmd.Parameters.AddWithValue("$tweet_id", tweetId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
    public static async Task<long> UpsertAuthorAsync(SqliteConnection conn, SqliteTransaction tx, string handle, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = "INSERT INTO authors (handle, name, created_at, updated_at) VALUES ($handle, $name, $now, $now) ON CONFLICT(handle) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at;";
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
        cmd.CommandText = "INSERT INTO posts (tweet_id, url, author_id, created_at, text, note, saved_at, dir_path) VALUES ($tweet_id, $url, $author_id, $created_at, $text, $note, $saved_at, $dir_path); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$tweet_id", request.tweet_id);
        cmd.Parameters.AddWithValue("$url", request.url);
        cmd.Parameters.AddWithValue("$author_id", authorId);
        cmd.Parameters.AddWithValue("$created_at", createdAt.ToString("o"));
        cmd.Parameters.AddWithValue("$text", request.text ?? string.Empty);
        cmd.Parameters.AddWithValue("$note", (object?)request.note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$saved_at", savedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$dir_path", dirPath);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
    public static async Task<long> InsertMediaAsync(SqliteConnection conn, SqliteTransaction tx, long postId, MediaRow media, string downloadStatus, string? downloadError, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO media (post_id, media_type, original_url, local_path, download_status, download_error, sort_order) VALUES ($post_id, $media_type, $original_url, $local_path, $download_status, $download_error, $sort_order); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$media_type", media.media_type);
        cmd.Parameters.AddWithValue("$original_url", (object?)media.original_url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$local_path", media.local_path);
        cmd.Parameters.AddWithValue("$download_status", downloadStatus);
        cmd.Parameters.AddWithValue("$download_error", (object?)downloadError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sort_order", media.sort_order);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public static async Task UpdateMediaDownloadStateAsync(string dbPath, long mediaId, string downloadStatus, string? downloadError, CancellationToken ct)
    {
        await using var conn = Open(dbPath);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE media SET download_status = $download_status, download_error = $download_error WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", mediaId);
        cmd.Parameters.AddWithValue("$download_status", downloadStatus);
        cmd.Parameters.AddWithValue("$download_error", (object?)downloadError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task MarkIncompleteVideoDownloadsInterruptedAsync(string dbPath, CancellationToken ct)
    {
        await using var conn = Open(dbPath);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE media
SET download_status = 'failed',
    download_error = 'アプリの終了により動画保存が中断されました。'
WHERE media_type = 'video'
  AND download_status IN ('pending', 'downloading');";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureMediaColumnsAsync(SqliteConnection conn)
    {
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(media);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await pragma.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("download_status"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE media ADD COLUMN download_status TEXT NOT NULL DEFAULT 'completed';";
            await alter.ExecuteNonQueryAsync();
        }

        if (!columns.Contains("download_error"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE media ADD COLUMN download_error TEXT NULL;";
            await alter.ExecuteNonQueryAsync();
        }
    }
    public static async Task<long> UpsertTagAsync(SqliteConnection conn, SqliteTransaction tx, string tag, DateTimeOffset now, CancellationToken ct)
    {
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = "INSERT INTO tags (name, created_at) VALUES ($name, $now) ON CONFLICT(name) DO NOTHING;";
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
        cmd.CommandText = "INSERT INTO post_tags (post_id, tag_id, created_at) VALUES ($post_id, $tag_id, $now) ON CONFLICT(post_id, tag_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$tag_id", tagId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public static async Task UpdatePostAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string url, long authorId, DateTimeOffset createdAt, string text, string? note, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE posts SET url = $url, author_id = $author_id, created_at = $created_at, text = $text, note = $note WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$author_id", authorId);
        cmd.Parameters.AddWithValue("$created_at", createdAt.ToString("o"));
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public static async Task ReplacePostTagsAsync(SqliteConnection conn, SqliteTransaction tx, long postId, List<string> tags, DateTimeOffset now, CancellationToken ct)
    {
        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM post_tags WHERE post_id = $post_id";
            delete.Parameters.AddWithValue("$post_id", postId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var tag in tags)
        {
            var tagId = await UpsertTagAsync(conn, tx, tag, now, ct);
            await InsertPostTagAsync(conn, tx, postId, tagId, now, ct);
        }
    }
}
static class TagCatalog
{
    public static async Task<List<TagCatalogResponseItem>> LoadAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT t.name, COUNT(pt.post_id) AS usage_count FROM tags t LEFT JOIN post_tags pt ON pt.tag_id = t.id GROUP BY t.id, t.name ORDER BY usage_count DESC, t.name COLLATE NOCASE ASC;";
        var items = new List<TagCatalogResponseItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) items.Add(new TagCatalogResponseItem(reader.GetString(0), reader.GetInt32(1)));
        }
        return items;
    }
}
static class PostStore
{
    public static async Task<ExistingPostResponse?> LoadByTweetIdAsync(SqliteConnection conn, string tweetId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT p.id, p.tweet_id, p.url, p.created_at, p.text, p.note, p.saved_at, p.dir_path, a.handle, a.name FROM posts p JOIN authors a ON a.id = p.author_id WHERE p.tweet_id = $tweet_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$tweet_id", tweetId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var postId = reader.GetInt64(0);
        var tags = new List<string>();
        await using (var tagCmd = conn.CreateCommand())
        {
            tagCmd.CommandText = "SELECT t.name FROM post_tags pt JOIN tags t ON t.id = pt.tag_id WHERE pt.post_id = $post_id ORDER BY t.name COLLATE NOCASE ASC;";
            tagCmd.Parameters.AddWithValue("$post_id", postId);
            await using var tagReader = await tagCmd.ExecuteReaderAsync(ct);
            while (await tagReader.ReadAsync(ct))
            {
                if (!tagReader.IsDBNull(0)) tags.Add(tagReader.GetString(0));
            }
        }
        return new ExistingPostResponse(postId, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), new Author(reader.GetString(8), reader.GetString(9)), tags);
    }
}
sealed class VideoDownloadQueue
{
    private readonly Channel<VideoDownloadJob> _channel = Channel.CreateUnbounded<VideoDownloadJob>();

    public void Enqueue(VideoDownloadJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<VideoDownloadJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

sealed class VideoDownloadWorker : BackgroundService
{
    private readonly VideoDownloadQueue _queue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfig _appConfig;

    public VideoDownloadWorker(VideoDownloadQueue queue, IHttpClientFactory httpClientFactory, AppConfig appConfig)
    {
        _queue = queue;
        _httpClientFactory = httpClientFactory;
        _appConfig = appConfig;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "downloading", null, stoppingToken);
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("XPostArchive/1.0");
                var result = await Video.DownloadAsync(client, _appConfig.FfmpegPath, job.source_url, job.full_path, job.retry_count, stoppingToken);
                if (result.Ok)
                {
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "completed", null, stoppingToken);
                }
                else
                {
                    DeletePartialFile(job.full_path);
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "failed", result.ErrorMessage, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                DeletePartialFile(job.full_path);
                try
                {
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "failed", "アプリの終了により動画保存が中断されました。", CancellationToken.None);
                }
                catch
                {
                }
                throw;
            }
            catch (Exception ex)
            {
                DeletePartialFile(job.full_path);
                try
                {
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "failed", ex.Message, CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
