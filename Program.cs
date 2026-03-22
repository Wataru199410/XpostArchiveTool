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
        var quotedPostId = await Db.ResolveQuotedPostIdAsync(conn, tx, request.quoted_tweet_id, ct);
        if (!string.IsNullOrWhiteSpace(request.quoted_tweet_id) && quotedPostId is null)
        {
            TryDeleteDirectory(stagingDir);
            return Results.BadRequest(ApiError.BadRequest("QUOTED_POST_NOT_FOUND", "引用元の投稿が先に保存されていません。"));
        }
        var postId = await Db.InsertPostAsync(conn, tx, request, authorId, quotedPostId, createdAt, savedAt, postDir, ct);
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
app.MapPut("/api/v1/posts/{tweetId}", async (string tweetId, UpdatePostRequest request, VideoDownloadQueue videoQueue, CancellationToken ct) =>
{
    if (!string.Equals(tweetId, request.tweet_id, StringComparison.Ordinal))
        return Results.BadRequest(ApiError.BadRequest("TWEET_ID_MISMATCH", "URL の tweet_id と本文の tweet_id が一致しません。"));
    var validationError = ValidateUpdateRequestClean(request);
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
        var videos = request.video_playlists
            .Where(x => !string.IsNullOrWhiteSpace(x.m3u8_url))
            .Select((x, index) => new MediaRow("video", x.m3u8_url, Path.Combine("videos", $"{(index + 1):000}.{Video.GetPreferredExtension(x.m3u8_url)}").Replace('\\', '/'), index))
            .ToList();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        if (false && await Db.HasActiveVideoDownloadsAsync(conn, tx, existing.id, ct))
        {
            return Results.Conflict(ApiError.Conflict("VIDEO_DOWNLOAD_IN_PROGRESS", "動画をバックグラウンドで保存中のため、投稿はまだ更新できません。動画保存完了後に再度お試しください。"));
        }
        var authorId = await Db.UpsertAuthorAsync(conn, tx, request.author.handle, request.author.name, DateTimeOffset.Now, ct);
        var quotedPostId = await Db.ResolveQuotedPostIdAsync(conn, tx, request.quoted_tweet_id, ct);
        if (!string.IsNullOrWhiteSpace(request.quoted_tweet_id) && quotedPostId is null)
        {
            return Results.BadRequest(ApiError.BadRequest("QUOTED_POST_NOT_FOUND", "引用元の投稿が先に保存されていません。"));
        }
        await Db.UpdatePostAsync(conn, tx, existing.id, request.url, authorId, quotedPostId, createdAt, request.text ?? string.Empty, request.note, ct);
        await Db.ReplacePostTagsAsync(conn, tx, existing.id, tags, DateTimeOffset.Now, ct);
        var existingVideos = await Db.LoadMediaByTypeAsync(conn, tx, existing.id, "video", ct);
        var videoJobs = new List<VideoDownloadJob>();
        var filesToDeleteAfterCommit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasRequestedVideoUpdate = videos.Count > 0;
        var requestedVideoKeys = new HashSet<string>(videos.Select(x => $"{x.local_path}\n{x.original_url ?? string.Empty}"), StringComparer.OrdinalIgnoreCase);
        var existingVideoKeys = new HashSet<string>(existingVideos.Select(x => $"{x.local_path}\n{x.original_url}"), StringComparer.OrdinalIgnoreCase);
        var requestedVideosNeedRepair = hasRequestedVideoUpdate && videos.Any(media =>
        {
            var fullPath = Path.Combine(existing.dir_path, media.local_path);
            return !existingVideos.Any(x =>
                string.Equals(x.local_path, media.local_path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.original_url, media.original_url ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                x.download_status == "completed" &&
                File.Exists(fullPath) &&
                Video.IsUsableSavedVideoFile(fullPath));
        });
        var requestedVideosMatchExisting = hasRequestedVideoUpdate
            && requestedVideoKeys.Count == existingVideoKeys.Count
            && requestedVideoKeys.SetEquals(existingVideoKeys)
            && !requestedVideosNeedRepair;
        var hasActiveVideoDownloads = await Db.HasActiveVideoDownloadsAsync(conn, tx, existing.id, ct);

        if (hasActiveVideoDownloads && hasRequestedVideoUpdate && !requestedVideosMatchExisting)
        {
            return Results.Conflict(ApiError.Conflict("VIDEO_DOWNLOAD_IN_PROGRESS", "動画をバックグラウンドで保存中のため、動画の変更はまだ反映できません。動画保存完了後に再度お試しください。"));
        }

        if (hasRequestedVideoUpdate && !requestedVideosMatchExisting)
        {
            var desiredLocalPaths = new HashSet<string>(videos.Select(x => x.local_path), StringComparer.OrdinalIgnoreCase);
            foreach (var existingVideo in existingVideos.Where(x => !desiredLocalPaths.Contains(x.local_path)))
            {
                var obsoleteFullPath = Path.Combine(existing.dir_path, existingVideo.local_path);
                if (File.Exists(obsoleteFullPath))
                {
                    filesToDeleteAfterCommit.Add(obsoleteFullPath);
                }
            }

            await Db.DeleteMediaByLocalPathsAsync(conn, tx, existing.id, "video", existingVideos.Where(x => !desiredLocalPaths.Contains(x.local_path)).Select(x => x.local_path).ToList(), ct);

            foreach (var media in videos)
            {
                var fullPath = Path.Combine(existing.dir_path, media.local_path);
                var matchingRows = existingVideos.Where(x => string.Equals(x.local_path, media.local_path, StringComparison.OrdinalIgnoreCase)).ToList();
                var hasReusableRow = matchingRows.Any(x =>
                    string.Equals(x.original_url, media.original_url, StringComparison.OrdinalIgnoreCase) &&
                    x.download_status == "completed" &&
                    File.Exists(fullPath) &&
                    Video.IsUsableSavedVideoFile(fullPath));

                if (hasReusableRow)
                {
                    var rowToKeep = matchingRows
                        .First(x =>
                            string.Equals(x.original_url, media.original_url, StringComparison.OrdinalIgnoreCase) &&
                            x.download_status == "completed" &&
                            File.Exists(fullPath) &&
                            Video.IsUsableSavedVideoFile(fullPath));
                    var duplicateIds = matchingRows.Where(x => x.id != rowToKeep.id).Select(x => x.id).ToList();
                    if (duplicateIds.Count > 0)
                    {
                        await Db.DeleteMediaByIdsAsync(conn, tx, duplicateIds, ct);
                    }

                    continue;
                }

                if (matchingRows.Count > 0)
                {
                    await Db.DeleteMediaByIdsAsync(conn, tx, matchingRows.Select(x => x.id).ToList(), ct);
                }

                if (File.Exists(fullPath))
                {
                    filesToDeleteAfterCommit.Add(fullPath);
                }

                var mediaId = await Db.InsertMediaAsync(conn, tx, existing.id, media, "pending", null, ct);
                videoJobs.Add(new VideoDownloadJob(mediaId, media.original_url ?? string.Empty, fullPath, appConfig.VideoRetryCount));
            }
        }
        await tx.CommitAsync(ct);
        foreach (var filePath in filesToDeleteAfterCommit)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VIDEO_UPDATE_DELETE_FAILED path={filePath} error={ex.Message}");
            }
        }
        foreach (var job in videoJobs)
        {
            videoQueue.Enqueue(job);
        }

        return Results.Ok(new { ok = true, updated = true, tweet_id = request.tweet_id, dir_path = existing.dir_path, background_video_count = videoJobs.Count });
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
    if (!string.IsNullOrWhiteSpace(request.quoted_tweet_id) && !Regex.IsMatch(request.quoted_tweet_id, @"^\d+$")) return ApiError.BadRequest("QUOTED_TWEET_ID_INVALID", "quoted_tweet_id は数値のみ指定してください。");
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
    if (!string.IsNullOrWhiteSpace(request.quoted_tweet_id) && !Regex.IsMatch(request.quoted_tweet_id, @"^\d+$")) return ApiError.BadRequest("QUOTED_TWEET_ID_INVALID", "quoted_tweet_id は数値のみ指定してください。");
    if (!UrlUtil.IsAllowedPostUrl(request.url)) return ApiError.BadRequest("URL_INVALID", "url は X の投稿 URL を指定してください。");
    return null;
}

static ApiError? ValidateUpdateRequestClean(UpdatePostRequest request)
{
    if (request.tags is null) return ApiError.BadRequest("TAGS_REQUIRED", "tags は配列で指定してください。");
    if (request.video_playlists is null) return ApiError.BadRequest("VIDEO_PLAYLISTS_REQUIRED", "video_playlists は配列で指定してください。");
    if (string.IsNullOrWhiteSpace(request.tweet_id)) return ApiError.BadRequest("TWEET_ID_REQUIRED", "tweet_id は必須です。");
    if (!Regex.IsMatch(request.tweet_id, @"^\d+$")) return ApiError.BadRequest("TWEET_ID_INVALID", "tweet_id は数字のみで指定してください。");
    if (string.IsNullOrWhiteSpace(request.url)) return ApiError.BadRequest("URL_REQUIRED", "url は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.handle)) return ApiError.BadRequest("AUTHOR_HANDLE_REQUIRED", "author.handle は必須です。");
    if (string.IsNullOrWhiteSpace(request.author.name)) return ApiError.BadRequest("AUTHOR_NAME_REQUIRED", "author.name は必須です。");
    if (string.IsNullOrWhiteSpace(request.created_at)) return ApiError.BadRequest("CREATED_AT_REQUIRED", "created_at は必須です。");
    if (!string.IsNullOrWhiteSpace(request.quoted_tweet_id) && !Regex.IsMatch(request.quoted_tweet_id, @"^\d+$")) return ApiError.BadRequest("QUOTED_TWEET_ID_INVALID", "quoted_tweet_id は数字のみで指定してください。");
    if (!UrlUtil.IsAllowedPostUrl(request.url)) return ApiError.BadRequest("URL_INVALID", "url は X の投稿 URL を指定してください。");
    foreach (var video in request.video_playlists)
    {
        if (!UrlUtil.IsAllowedVideoUrl(video.m3u8_url))
        {
            return ApiError.BadRequest("VIDEO_URL_INVALID", $"許可されていない動画 URL です: {video.m3u8_url}");
        }
    }

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
        var storageRootPath = ResolveDataPath(config["Storage:RootPath"] ?? "./XArchive", "XArchive");
        var databasePath = ResolveDataPath(config["Database:Path"] ?? "./data/archive.db", Path.Combine("data", "archive.db"));
        var tokenFilePath = ResolveDataPath(config["Auth:TokenFilePath"] ?? "./data/auth_token.txt", Path.Combine("data", "auth_token.txt"));
        return new AppConfig(
            config["Server:Host"] ?? "127.0.0.1",
            int.TryParse(config["Server:Port"], out var p) ? p : 18765,
            storageRootPath,
            databasePath,
            tokenFilePath,
            ffmpegPath,
            int.TryParse(config["Video:RetryCount"], out var r) ? r : 2
        );
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

    private static string ResolveDataPath(string configuredPath, string localAppDataRelativePath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            return Path.GetFullPath(Path.Combine(projectRoot, configuredPath));
        }

        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XPostArchive");
        return Path.GetFullPath(Path.Combine(appDataRoot, localAppDataRelativePath));
    }

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var desktopCsproj = Path.Combine(dir.FullName, "XPostArchive.Desktop.csproj");
            var apiCsproj = Path.Combine(dir.FullName, "XPostArchive.Api.csproj");
            if (File.Exists(desktopCsproj) || File.Exists(apiCsproj))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

sealed record SavePostRequest(string tweet_id, string url, Author author, string created_at, string text, List<string> tags, string? note, List<ImageInput> images, List<VideoPlaylistInput> video_playlists, string? quoted_tweet_id)
{
    public SavePostRequest() : this("", "", new Author("", ""), "", "", [], null, [], [], null) { }
}
sealed record UpdatePostRequest(string tweet_id, string url, Author author, string created_at, string text, List<string> tags, string? note, List<VideoPlaylistInput> video_playlists, string? quoted_tweet_id)
{
    public UpdatePostRequest() : this("", "", new Author("", ""), "", "", [], null, [], null) { }
}
sealed record Author(string handle, string name);
sealed record ImageInput(string url);
sealed record VideoPlaylistInput(string m3u8_url);
sealed record MediaRow(string media_type, string? original_url, string local_path, int sort_order);
sealed record VideoDownloadJob(long media_id, string source_url, string full_path, int retry_count);
sealed record LocalHlsInput(string VideoPlaylistPath, string? AudioPlaylistPath);
sealed record TagCatalogResponseItem(string name, int count);
sealed record QuotedPostSummary(string tweet_id, string author_handle, string author_name, string text);
sealed record ExistingPostResponse(long id, string tweet_id, string url, string created_at, string text, string? note, string saved_at, string dir_path, Author author, List<string> tags, QuotedPostSummary? quoted_post);
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
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const long MinVideoFileSizeBytes = 32 * 1024;

    public static async Task<(bool Ok, string ErrorMessage)> DownloadAsync(HttpClient httpClient, string ffmpegPath, string sourceUrl, string outPath, int retryCount, CancellationToken ct)
    {
        if (IsHlsPlaylistUrl(sourceUrl))
        {
            DeleteIfExists(outPath);
            return await DownloadHlsLocallyAsync(httpClient, ffmpegPath, sourceUrl, outPath, ct);
        }

        if (IsLikelyDirectVideoUrl(sourceUrl))
        {
            try
            {
                await DownloadDirectAsync(httpClient, sourceUrl, outPath, ct);
                if (!File.Exists(outPath))
                {
                    return (false, "動画ファイルの保存に失敗しました。");
                }

                var validate = await ValidateSavedVideoAsync(ffmpegPath, outPath, ct);
                return validate.Ok ? (true, string.Empty) : validate;
            }
            catch (Exception ex)
            {
                return (false, ex.ToString());
            }
        }

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourceUrl);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a?");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            if (string.Equals(Path.GetExtension(outPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-bsf:a");
                psi.ArgumentList.Add("aac_adtstoasc");
            }

            psi.ArgumentList.Add(outPath);
            try
            {
                using var process = Process.Start(psi);
                if (process is null) return (false, "ffmpeg の起動に失敗しました。");
                await process.WaitForExitAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode == 0 && File.Exists(outPath))
                {
                    var validate = await ValidateSavedVideoAsync(ffmpegPath, outPath, ct);
                    if (validate.Ok)
                    {
                        return (true, string.Empty);
                    }

                    if (attempt == retryCount)
                    {
                        return validate;
                    }
                }
                if (attempt == retryCount)
                {
                    if (IsHlsPlaylistUrl(sourceUrl))
                    {
                        DeleteIfExists(outPath);
                        var localFallback = await DownloadHlsLocallyAsync(httpClient, ffmpegPath, sourceUrl, outPath, ct);
                        if (localFallback.Ok)
                        {
                            return localFallback;
                        }

                        return (false, $"ffmpeg exit code={process.ExitCode}\n{stderr}\n{localFallback.ErrorMessage}");
                    }

                    return (false, $"ffmpeg exit code={process.ExitCode}\n{stderr}");
                }
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    if (IsHlsPlaylistUrl(sourceUrl))
                    {
                        DeleteIfExists(outPath);
                        var localFallback = await DownloadHlsLocallyAsync(httpClient, ffmpegPath, sourceUrl, outPath, ct);
                        if (localFallback.Ok)
                        {
                            return localFallback;
                        }

                        return (false, $"{ex}\n{localFallback.ErrorMessage}");
                    }

                    return (false, ex.ToString());
                }
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

    public static bool IsUsableSavedVideoFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= 32 * 1024;
        }
        catch
        {
            return false;
        }
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
        return false;
    }

    private static bool IsHlsPlaylistUrl(string sourceUrl)
    {
        return !string.IsNullOrWhiteSpace(sourceUrl)
            && sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(bool Ok, string ErrorMessage)> DownloadHlsLocallyAsync(HttpClient httpClient, string ffmpegPath, string sourceUrl, string outPath, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"xpost-hls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            Console.WriteLine($"VIDEO_HLS_LOCAL_START source={sourceUrl} workdir={workDir}");
            var localInput = await PrepareLocalHlsInputAsync(httpClient, sourceUrl, workDir, ct);
            Console.WriteLine($"VIDEO_HLS_LOCAL_INPUT source={sourceUrl} videoInput={localInput.VideoPlaylistPath} audioInput={localInput.AudioPlaylistPath ?? "(none)"}");
            var args = BuildFfmpegArgs(localInput, outPath);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "ffmpeg local remux start failed.");
            }

            await process.WaitForExitAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0 || !File.Exists(outPath))
            {
                Console.WriteLine($"VIDEO_HLS_LOCAL_FFMPEG_FAILED code={process.ExitCode} stderr={stderr}");
                return (false, $"local hls ffmpeg exit code={process.ExitCode}\n{stderr}");
            }

            return await ValidateSavedVideoAsync(ffmpegPath, outPath, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VIDEO_HLS_LOCAL_EXCEPTION source={sourceUrl} error={ex}");
            return (false, ex.ToString());
        }
        finally
        {
            DeleteDirectoryIfExists(workDir);
        }
    }

    private static async Task<LocalHlsInput> PrepareLocalHlsInputAsync(HttpClient httpClient, string sourceUrl, string workDir, CancellationToken ct)
    {
        var masterText = await httpClient.GetStringAsync(sourceUrl, ct);
        if (!masterText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalHlsInput(await DownloadMediaPlaylistAsync(httpClient, new Uri(sourceUrl), workDir, "video", ct), null);
        }

        var master = ParseMasterPlaylist(masterText);
        if (master.Streams.Count == 0)
        {
            return new LocalHlsInput(await DownloadMediaPlaylistAsync(httpClient, new Uri(sourceUrl), workDir, "video", ct), null);
        }

        var selectedStream = master.Streams
            .OrderByDescending(x => x.Bandwidth)
            .ThenByDescending(x => x.Uri.Contains("/avc1/", StringComparison.OrdinalIgnoreCase))
            .First();

        var videoPlaylistPath = await DownloadMediaPlaylistAsync(httpClient, ResolveUri(sourceUrl, selectedStream.Uri), workDir, "video", ct);
        if (string.IsNullOrWhiteSpace(selectedStream.AudioGroupId) || !master.AudioByGroupId.TryGetValue(selectedStream.AudioGroupId, out var audioUri))
        {
            return new LocalHlsInput(videoPlaylistPath, null);
        }

        var audioPlaylistPath = await DownloadMediaPlaylistAsync(httpClient, ResolveUri(sourceUrl, audioUri), workDir, "audio", ct);
        return new LocalHlsInput(videoPlaylistPath, audioPlaylistPath);
    }

    private static async Task<string> DownloadMediaPlaylistAsync(HttpClient httpClient, Uri playlistUri, string workDir, string prefix, CancellationToken ct)
    {
        var playlistText = await httpClient.GetStringAsync(playlistUri, ct);
        var lines = playlistText.Replace("\r\n", "\n").Split('\n');
        var rewritten = new List<string>(lines.Length);
        var index = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                var rewrittenTag = await RewriteTagUriAsync(httpClient, playlistUri, workDir, prefix, line, index, ct);
                rewritten.Add(rewrittenTag.Line);
                index = rewrittenTag.NextIndex;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                rewritten.Add(rawLine);
                continue;
            }

            var localName = await DownloadPlaylistAssetAsync(httpClient, ResolveUri(playlistUri, line), workDir, prefix, index++, ct);
            rewritten.Add(localName);
        }

        var playlistPath = Path.Combine(workDir, $"{prefix}.m3u8");
        await File.WriteAllTextAsync(playlistPath, string.Join("\n", rewritten), Utf8NoBom, ct);
        return playlistPath;
    }

    private static async Task<(string Line, int NextIndex)> RewriteTagUriAsync(HttpClient httpClient, Uri playlistUri, string workDir, string prefix, string line, int index, CancellationToken ct)
    {
        var match = Regex.Match(line, "URI=\"(?<uri>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (line, index);
        }

        var originalUri = match.Groups["uri"].Value;
        var localName = await DownloadPlaylistAssetAsync(httpClient, ResolveUri(playlistUri, originalUri), workDir, prefix, index, ct);
        return (line.Replace(originalUri, localName, StringComparison.Ordinal), index + 1);
    }

    private static async Task<string> DownloadPlaylistAssetAsync(HttpClient httpClient, Uri absoluteUri, string workDir, string prefix, int index, CancellationToken ct)
    {
        var extension = Path.GetExtension(absoluteUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".bin";
        }

        var localName = $"{prefix}-{index:0000}{extension}";
        var localPath = Path.Combine(workDir, localName);
        using var response = await httpClient.GetAsync(absoluteUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
        return localName;
    }

    private static MasterPlaylist ParseMasterPlaylist(string text)
    {
        var audioByGroupId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var streams = new List<MasterStream>();
        string? pendingStream = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase))
            {
                var attrs = ParseAttributes(line);
                if (attrs.TryGetValue("TYPE", out var type)
                    && type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase)
                    && attrs.TryGetValue("GROUP-ID", out var groupId)
                    && attrs.TryGetValue("URI", out var uri))
                {
                    audioByGroupId[groupId] = uri;
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingStream = line;
                continue;
            }

            if (pendingStream is not null && !line.StartsWith("#", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(pendingStream);
                var bandwidth = attrs.TryGetValue("BANDWIDTH", out var bandwidthText) && int.TryParse(bandwidthText, out var parsedBandwidth)
                    ? parsedBandwidth
                    : 0;
                attrs.TryGetValue("AUDIO", out var audioGroupId);
                attrs.TryGetValue("RESOLUTION", out var resolution);
                attrs.TryGetValue("CODECS", out var codecs);
                streams.Add(new MasterStream(line, bandwidth, audioGroupId, resolution, codecs));
                pendingStream = null;
            }
        }

        return new MasterPlaylist(audioByGroupId, streams);
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var separator = line.IndexOf(':');
        var text = separator >= 0 ? line[(separator + 1)..] : line;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "(?<key>[A-Z0-9-]+)=(?<value>\"[^\"]*\"|[^,]+)"))
        {
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
            {
                value = value[1..^1];
            }
            result[key] = value;
        }
        return result;
    }

    private static string BuildFfmpegArgs(LocalHlsInput input, string outPath)
    {
        var args = $"-nostdin -v error -y -f hls -allowed_extensions ALL -protocol_whitelist file,http,https,tcp,tls,crypto,data -i \"{input.VideoPlaylistPath}\"";
        if (!string.IsNullOrWhiteSpace(input.AudioPlaylistPath))
        {
            args += $" -f hls -allowed_extensions ALL -protocol_whitelist file,http,https,tcp,tls,crypto,data -i \"{input.AudioPlaylistPath}\"";
        }

        args += " -map 0:v:0";
        if (!string.IsNullOrWhiteSpace(input.AudioPlaylistPath))
        {
            args += " -map 1:a:0";
        }
        else
        {
            args += " -map 0:a?";
        }

        args += " -c copy -movflags +faststart";
        if (string.Equals(Path.GetExtension(outPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            args += " -bsf:a aac_adtstoasc";
        }

        args += $" \"{outPath}\"";
        return args;
    }

    private static Uri ResolveUri(string baseUri, string relativeOrAbsolute)
    {
        return ResolveUri(new Uri(baseUri), relativeOrAbsolute);
    }

    private static Uri ResolveUri(Uri baseUri, string relativeOrAbsolute)
    {
        return Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(baseUri, relativeOrAbsolute);
    }

    private static async Task DownloadDirectAsync(HttpClient httpClient, string sourceUrl, string outPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        using var res = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var contentType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"動画ではないレスポンスが返されました: {contentType}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(fs, ct);
    }

    private static async Task<(bool Ok, string ErrorMessage)> ValidateSavedVideoAsync(string ffmpegPath, string outPath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(outPath);
        if (!fileInfo.Exists)
        {
            return (false, "動画ファイルが作成されませんでした。");
        }

        if (fileInfo.Length < MinVideoFileSizeBytes)
        {
            return (false, $"動画ファイルが小さすぎます: {fileInfo.Length} bytes");
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-v error -i \"{outPath}\" -f null -",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (false, "ffmpeg による動画検証を開始できませんでした。");
        }

        await process.WaitForExitAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode != 0)
        {
            return (false, $"動画検証に失敗しました。\n{stderr}");
        }

        return (true, string.Empty);
    }

    private static void DeleteIfExists(string path)
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

    private static void DeleteDirectoryIfExists(string path)
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
        }
    }

    private sealed record MasterPlaylist(Dictionary<string, string> AudioByGroupId, List<MasterStream> Streams);
    private sealed record MasterStream(string Uri, int Bandwidth, string? AudioGroupId, string? Resolution, string? Codecs);
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
        await EnsurePostsColumnsAsync(conn);
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
    public static async Task<long?> ResolveQuotedPostIdAsync(SqliteConnection conn, SqliteTransaction tx, string? quotedTweetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(quotedTweetId))
        {
            return null;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
        cmd.Parameters.AddWithValue("$tweet_id", quotedTweetId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is long id ? id : null;
    }

    public static async Task<long> InsertPostAsync(SqliteConnection conn, SqliteTransaction tx, SavePostRequest request, long authorId, long? quotedPostId, DateTimeOffset createdAt, DateTimeOffset savedAt, string dirPath, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO posts (tweet_id, url, author_id, quoted_post_id, created_at, text, note, saved_at, dir_path) VALUES ($tweet_id, $url, $author_id, $quoted_post_id, $created_at, $text, $note, $saved_at, $dir_path); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$tweet_id", request.tweet_id);
        cmd.Parameters.AddWithValue("$url", request.url);
        cmd.Parameters.AddWithValue("$author_id", authorId);
        cmd.Parameters.AddWithValue("$quoted_post_id", (object?)quotedPostId ?? DBNull.Value);
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

    public static async Task<HashSet<string>> LoadMediaOriginalUrlsAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string mediaType, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT original_url FROM media WHERE post_id = $post_id AND media_type = $media_type AND original_url IS NOT NULL;";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$media_type", mediaType);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
            {
                urls.Add(reader.GetString(0));
            }
        }
        return urls;
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

    private static async Task EnsurePostsColumnsAsync(SqliteConnection conn)
    {
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(posts);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await pragma.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("quoted_post_id"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE posts ADD COLUMN quoted_post_id INTEGER NULL;";
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
    public static async Task UpdatePostAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string url, long authorId, long? quotedPostId, DateTimeOffset createdAt, string text, string? note, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE posts SET url = $url, author_id = $author_id, quoted_post_id = $quoted_post_id, created_at = $created_at, text = $text, note = $note WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$author_id", authorId);
        cmd.Parameters.AddWithValue("$quoted_post_id", (object?)quotedPostId ?? DBNull.Value);
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

    public static async Task<bool> HasActiveVideoDownloadsAsync(SqliteConnection conn, SqliteTransaction tx, long postId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT COUNT(1)
FROM media
WHERE post_id = $post_id
  AND media_type = 'video'
  AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";
        cmd.Parameters.AddWithValue("$post_id", postId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        return count > 0;
    }

    public static async Task DeleteMediaByTypeAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string mediaType, CancellationToken ct)
    {
        await using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM media WHERE post_id = $post_id AND media_type = $media_type";
        delete.Parameters.AddWithValue("$post_id", postId);
        delete.Parameters.AddWithValue("$media_type", mediaType);
        await delete.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<(long id, string local_path, string download_status, string original_url)>> LoadMediaByTypeAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string mediaType, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, local_path, COALESCE(download_status, 'completed'), COALESCE(original_url, '') FROM media WHERE post_id = $post_id AND media_type = $media_type ORDER BY sort_order ASC, id ASC";
        cmd.Parameters.AddWithValue("$post_id", postId);
        cmd.Parameters.AddWithValue("$media_type", mediaType);

        var rows = new List<(long id, string local_path, string download_status, string original_url)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    public static async Task DeleteMediaByIdsAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyCollection<long> mediaIds, CancellationToken ct)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        await using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        var placeholders = mediaIds.Select((_, index) => $"$id{index}").ToList();
        delete.CommandText = $"DELETE FROM media WHERE id IN ({string.Join(", ", placeholders)})";
        var indexParam = 0;
        foreach (var mediaId in mediaIds)
        {
            delete.Parameters.AddWithValue($"$id{indexParam}", mediaId);
            indexParam++;
        }

        await delete.ExecuteNonQueryAsync(ct);
    }

    public static async Task DeleteMediaByLocalPathsAsync(SqliteConnection conn, SqliteTransaction tx, long postId, string mediaType, IReadOnlyCollection<string> localPaths, CancellationToken ct)
    {
        if (localPaths.Count == 0)
        {
            return;
        }

        await using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        var placeholders = localPaths.Select((_, index) => $"$path{index}").ToList();
        delete.CommandText = $"DELETE FROM media WHERE post_id = $post_id AND media_type = $media_type AND local_path IN ({string.Join(", ", placeholders)})";
        delete.Parameters.AddWithValue("$post_id", postId);
        delete.Parameters.AddWithValue("$media_type", mediaType);
        var indexParam = 0;
        foreach (var localPath in localPaths)
        {
            delete.Parameters.AddWithValue($"$path{indexParam}", localPath);
            indexParam++;
        }

        await delete.ExecuteNonQueryAsync(ct);
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
        QuotedPostSummary? quotedPost = null;
        await using (var quotedCmd = conn.CreateCommand())
        {
            quotedCmd.CommandText = @"
SELECT qp.tweet_id, qa.handle, qa.name, qp.text
FROM posts p
JOIN posts qp ON qp.id = p.quoted_post_id
JOIN authors qa ON qa.id = qp.author_id
WHERE p.id = $post_id
LIMIT 1;";
            quotedCmd.Parameters.AddWithValue("$post_id", postId);
            await using var quotedReader = await quotedCmd.ExecuteReaderAsync(ct);
            if (await quotedReader.ReadAsync(ct))
            {
                quotedPost = new QuotedPostSummary(
                    quotedReader.GetString(0),
                    quotedReader.GetString(1),
                    quotedReader.GetString(2),
                    quotedReader.IsDBNull(3) ? string.Empty : quotedReader.GetString(3));
            }
        }

        return new ExistingPostResponse(postId, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), new Author(reader.GetString(8), reader.GetString(9)), tags, quotedPost);
    }
}
sealed class VideoDownloadQueue
{
    private readonly Channel<VideoDownloadJob> _channel = Channel.CreateUnbounded<VideoDownloadJob>();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfig _appConfig;
    private int _isPumpRunning;

    public VideoDownloadQueue(IHttpClientFactory httpClientFactory, AppConfig appConfig)
    {
        _httpClientFactory = httpClientFactory;
        _appConfig = appConfig;
    }

    public void Enqueue(VideoDownloadJob job)
    {
        _channel.Writer.TryWrite(job);
        StartPumpIfNeeded();
    }

    public IAsyncEnumerable<VideoDownloadJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    private void StartPumpIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _isPumpRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync())
            {
                while (_channel.Reader.TryRead(out var job))
                {
                    try
                    {
                        await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "downloading", null, CancellationToken.None);
                        var client = _httpClientFactory.CreateClient();
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("XPostArchive/1.0");
                        Console.WriteLine($"VIDEO_JOB_START media_id={job.media_id} source={job.source_url} path={job.full_path}");
                        var result = await Video.DownloadAsync(client, _appConfig.FfmpegPath, job.source_url, job.full_path, job.retry_count, CancellationToken.None);
                        if (result.Ok)
                        {
                            Console.WriteLine($"VIDEO_JOB_OK media_id={job.media_id} path={job.full_path}");
                            await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "completed", null, CancellationToken.None);
                        }
                        else
                        {
                            Console.WriteLine($"VIDEO_JOB_FAILED media_id={job.media_id} error={result.ErrorMessage}");
                            VideoDownloadWorker.DeletePartialFile(job.full_path);
                            await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "failed", result.ErrorMessage, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"VIDEO_JOB_EXCEPTION media_id={job.media_id} error={ex}");
                        VideoDownloadWorker.DeletePartialFile(job.full_path);
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
        }
        finally
        {
            Interlocked.Exchange(ref _isPumpRunning, 0);
            if (_channel.Reader.Count > 0)
            {
                StartPumpIfNeeded();
            }
        }
    }
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
                Console.WriteLine($"VIDEO_JOB_START media_id={job.media_id} source={job.source_url} path={job.full_path}");
                var result = await Video.DownloadAsync(client, _appConfig.FfmpegPath, job.source_url, job.full_path, job.retry_count, stoppingToken);
                if (result.Ok)
                {
                    Console.WriteLine($"VIDEO_JOB_OK media_id={job.media_id} path={job.full_path}");
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "completed", null, stoppingToken);
                }
                else
                {
                    Console.WriteLine($"VIDEO_JOB_FAILED media_id={job.media_id} error={result.ErrorMessage}");
                    DeletePartialFile(job.full_path);
                    await Db.UpdateMediaDownloadStateAsync(_appConfig.DatabasePath, job.media_id, "failed", result.ErrorMessage, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"VIDEO_JOB_CANCELLED media_id={job.media_id} path={job.full_path}");
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
                Console.WriteLine($"VIDEO_JOB_EXCEPTION media_id={job.media_id} error={ex}");
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

    internal static void DeletePartialFile(string path)
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
