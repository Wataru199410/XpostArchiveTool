using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Data.Sqlite;

namespace XPostArchive.Desktop;

internal static class DesktopArchiveStore
{
    public static string? LastLoadError { get; private set; }

    public static List<PostListItem> LoadPosts()
    {
        LastLoadError = null;

        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return [];
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            if (!IsApiPossiblyRunningSafe())
            {
                MarkAllInterruptedVideoDownloadsSafe(conn);
            }

            var posts = LoadPostRows(conn);
            var tagsByPostId = LoadTagsByPostId(conn);
            var mediaByPostId = LoadMediaByPostId(conn);

            var items = posts
                .Select(post =>
                {
                    tagsByPostId.TryGetValue(post.Id, out var tagList);
                    mediaByPostId.TryGetValue(post.Id, out var mediaFiles);
                    tagList ??= [];
                    mediaFiles ??= [];

                    var thumbnailPath = mediaFiles.FirstOrDefault(x => x.Type == "image" && File.Exists(x.FullPath))?.FullPath ?? string.Empty;
                    var videoStatus = BuildVideoStatusText(mediaFiles);

                    return new PostListItem
                    {
                        SavedAt = post.SavedAtDisplay,
                        SavedAtSortValue = post.SavedAt,
                        CreatedAtSortValue = post.CreatedAt,
                        Author = string.IsNullOrWhiteSpace(post.AuthorName)
                            ? post.AuthorHandle
                            : $"{post.AuthorName} ({post.AuthorHandle})",
                        Text = post.Text ?? string.Empty,
                        Tags = tagList.Count > 0 ? string.Join(", ", tagList) : "タグなし",
                        TagList = tagList,
                        TweetId = post.TweetId,
                        DirPath = post.DirPath,
                        ImageCount = mediaFiles.Count(x => x.Type == "image"),
                        VideoCount = mediaFiles.Count(x => x.Type == "video"),
                        ThumbnailPath = thumbnailPath,
                        MediaFiles = mediaFiles,
                        Note = post.Note ?? string.Empty,
                        VideoStatusText = videoStatus,
                        HasActiveVideoDownload = mediaFiles.Any(x => x.Type == "video" && (x.DownloadStatus == "pending" || x.DownloadStatus == "downloading")),
                        QuotedTweetId = post.QuotedTweetId,
                        QuotedAuthor = post.QuotedAuthor,
                        QuotedText = post.QuotedText ?? string.Empty
                    };
                })
                .OrderByDescending(x => x.SavedAtSortValue)
                .ThenByDescending(x => x.TweetId)
                .ToList();

            var referencedByMap = items
                .Where(x => !string.IsNullOrWhiteSpace(x.QuotedTweetId))
                .GroupBy(x => x.QuotedTweetId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderByDescending(x => x.SavedAtSortValue)
                        .ThenByDescending(x => x.TweetId)
                        .ToList(),
                    StringComparer.Ordinal);

            return items
                .Select(item =>
                {
                    if (!referencedByMap.TryGetValue(item.TweetId, out var referencingPosts) || referencingPosts.Count == 0)
                    {
                        return item;
                    }

                    return new PostListItem
                    {
                        SavedAt = item.SavedAt,
                        SavedAtSortValue = item.SavedAtSortValue,
                        CreatedAtSortValue = item.CreatedAtSortValue,
                        Author = item.Author,
                        Text = item.Text,
                        Tags = item.Tags,
                        TagList = item.TagList,
                        TweetId = item.TweetId,
                        DirPath = item.DirPath,
                        ImageCount = item.ImageCount,
                        VideoCount = item.VideoCount,
                        ThumbnailPath = item.ThumbnailPath,
                        MediaFiles = item.MediaFiles,
                        Note = item.Note,
                        VideoStatusText = item.VideoStatusText,
                        HasActiveVideoDownload = item.HasActiveVideoDownload,
                        QuotedTweetId = item.QuotedTweetId,
                        QuotedAuthor = item.QuotedAuthor,
                        QuotedText = item.QuotedText,
                        ReferencedByTweetId = referencingPosts[0].TweetId,
                        ReferencedByCount = referencingPosts.Count
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return [];
        }
    }

    public static int GetActiveVideoDownloadCount()
    {
        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return 0;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM media WHERE media_type = 'video' AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public static bool SavePostChanges(string dirPath, IEnumerable<string> tags, string note, out string errorMessage)
    {
        errorMessage = string.Empty;

        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            errorMessage = "archive.db が見つかりません。";
            return false;
        }

        var normalizedTags = tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();

            long? postId = null;
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT id FROM posts WHERE dir_path = $dir_path LIMIT 1";
                select.Parameters.AddWithValue("$dir_path", dirPath);
                var value = select.ExecuteScalar();
                if (value is long id)
                {
                    postId = id;
                }
            }

            if (postId is null)
            {
                errorMessage = "投稿レコードが見つかりません。";
                return false;
            }

            using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE posts SET note = $note WHERE id = $id";
                update.Parameters.AddWithValue("$id", postId.Value);
                update.Parameters.AddWithValue("$note", note ?? string.Empty);
                update.ExecuteNonQuery();
            }

            using (var deleteLinks = conn.CreateCommand())
            {
                deleteLinks.Transaction = tx;
                deleteLinks.CommandText = "DELETE FROM post_tags WHERE post_id = $post_id";
                deleteLinks.Parameters.AddWithValue("$post_id", postId.Value);
                deleteLinks.ExecuteNonQuery();
            }

            foreach (var tag in normalizedTags)
            {
                long tagId;
                using (var upsertTag = conn.CreateCommand())
                {
                    upsertTag.Transaction = tx;
                    upsertTag.CommandText = @"
INSERT INTO tags (name, created_at)
VALUES ($name, $created_at)
ON CONFLICT(name) DO NOTHING;";
                    upsertTag.Parameters.AddWithValue("$name", tag);
                    upsertTag.Parameters.AddWithValue("$created_at", DateTimeOffset.Now.ToString("o"));
                    upsertTag.ExecuteNonQuery();
                }

                using (var selectTag = conn.CreateCommand())
                {
                    selectTag.Transaction = tx;
                    selectTag.CommandText = "SELECT id FROM tags WHERE name = $name";
                    selectTag.Parameters.AddWithValue("$name", tag);
                    tagId = (long)(selectTag.ExecuteScalar() ?? 0L);
                }

                using var insertLink = conn.CreateCommand();
                insertLink.Transaction = tx;
                insertLink.CommandText = @"
INSERT INTO post_tags (post_id, tag_id, created_at)
VALUES ($post_id, $tag_id, $created_at)
ON CONFLICT(post_id, tag_id) DO NOTHING;";
                insertLink.Parameters.AddWithValue("$post_id", postId.Value);
                insertLink.Parameters.AddWithValue("$tag_id", tagId);
                insertLink.Parameters.AddWithValue("$created_at", DateTimeOffset.Now.ToString("o"));
                insertLink.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool DeletePostsByTweetIds(IEnumerable<string> tweetIds, out string errorMessage)
    {
        return DeletePostsByTweetIdsSafe(tweetIds, out errorMessage);

#if false
        errorMessage = string.Empty;

        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            errorMessage = "archive.db が見つかりません。";
            return false;
        }

        try
        {
            var deleteTargets = tweetIds
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (deleteTargets.Count == 0)
            {
                errorMessage = "削除対象の投稿が見つかりません。";
                return false;
            }

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            var postIds = new List<long>(deleteTargets.Count);
            var dirPaths = new List<string>(deleteTargets.Count);
            using (var tx = conn.BeginTransaction())
            {
                foreach (var tweetId in deleteTargets)
                {
                    using var select = conn.CreateCommand();
                    select.Transaction = tx;
                    select.CommandText = "SELECT id, dir_path FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
                    select.Parameters.AddWithValue("$tweet_id", tweetId);
                    using var reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        errorMessage = "削除対象の投稿が見つかりません。";
                        return false;
                    }

                    postIds.Add(reader.GetInt64(0));
                    dirPaths.Add(reader.GetString(1));
                }
            }

            if (HasActiveVideoDownloads(conn, postIds))
            {
                errorMessage = "動画をバックグラウンドで保存中の投稿は削除できません。動画保存完了後に再度お試しください。";
                return false;
            }

            var movedDirectories = MoveDirectoriesToTrash(dirPaths.Distinct(StringComparer.Ordinal).ToList(), out errorMessage);
            if (movedDirectories is null)
            {
                return false;
            }

            try
            {
                using var tx = conn.BeginTransaction();
                foreach (var postId in postIds)
                {
                    using var delete = conn.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM posts WHERE id = $id";
                    delete.Parameters.AddWithValue("$id", postId);
                    delete.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRestoreDirectories(movedDirectories);
                errorMessage = ex.Message;
                return false;
            }

            TryDeleteMovedDirectories(movedDirectories);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
#endif
    }

    public static bool DeletePostsByTweetIdsSafe(IEnumerable<string> tweetIds, out string errorMessage)
    {
        return DeletePostsByTweetIdsSafeCore(tweetIds, out errorMessage);

#if false
        errorMessage = string.Empty;

        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            errorMessage = "archive.db が見つかりません。";
            return false;
        }

        try
        {
            var deleteTargets = tweetIds
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (deleteTargets.Count == 0)
            {
                errorMessage = "削除対象の投稿が見つかりません。";
                return false;
            }

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            var postIds = new List<long>(deleteTargets.Count);
            var dirPaths = new List<string>(deleteTargets.Count);
            using (var tx = conn.BeginTransaction())
            {
                foreach (var tweetId in deleteTargets)
                {
                    using var select = conn.CreateCommand();
                    select.Transaction = tx;
                    select.CommandText = "SELECT id, dir_path FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
                    select.Parameters.AddWithValue("$tweet_id", tweetId);
                    using var reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        errorMessage = "削除対象の投稿が見つかりません。";
                        return false;
                    }

                    postIds.Add(reader.GetInt64(0));
                    dirPaths.Add(reader.GetString(1));
                }
            }

            if (HasActiveVideoDownloadsSafe(conn, postIds))
            {
                if (IsApiPossiblyRunningSafe())
                {
                    errorMessage = "動画をバックグラウンドで保存中の投稿は削除できません。動画保存完了後に再度お試しください。";
                    return false;
                }

                MarkVideoDownloadsFailedSafe(conn, postIds);
                if (HasActiveVideoDownloadsSafe(conn, postIds))
                {
                    errorMessage = "動画保存状態を更新できなかったため、投稿を削除できませんでした。";
                    return false;
                }
            }

            using (var tx = conn.BeginTransaction())
            {
                foreach (var postId in postIds)
                {
                    using var delete = conn.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM posts WHERE id = $id";
                    delete.Parameters.AddWithValue("$id", postId);
                    delete.ExecuteNonQuery();
                }

                tx.Commit();
            }

            var movedDirectories = MoveDirectoriesToTrash(dirPaths.Distinct(StringComparer.Ordinal).ToList(), out _);
            if (movedDirectories is not null)
            {
                TryDeleteMovedDirectories(movedDirectories);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
#endif
    }

    private static bool DeletePostsByTweetIdsSafeCore(IEnumerable<string> tweetIds, out string errorMessage)
    {
        errorMessage = string.Empty;

        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            errorMessage = "archive.db が見つかりません。";
            return false;
        }

        try
        {
            var deleteTargets = tweetIds
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (deleteTargets.Count == 0)
            {
                errorMessage = "削除対象の投稿が見つかりません。";
                return false;
            }

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            var postIds = new List<long>(deleteTargets.Count);
            var dirPaths = new List<string>(deleteTargets.Count);
            using (var tx = conn.BeginTransaction())
            {
                foreach (var tweetId in deleteTargets)
                {
                    using var select = conn.CreateCommand();
                    select.Transaction = tx;
                    select.CommandText = "SELECT id, dir_path FROM posts WHERE tweet_id = $tweet_id LIMIT 1";
                    select.Parameters.AddWithValue("$tweet_id", tweetId);
                    using var reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        errorMessage = "削除対象の投稿が見つかりません。";
                        return false;
                    }

                    postIds.Add(reader.GetInt64(0));
                    dirPaths.Add(reader.GetString(1));
                }
            }

            if (HasActiveVideoDownloadsSafe(conn, postIds))
            {
                if (IsApiPossiblyRunningSafe())
                {
                    errorMessage = "動画をバックグラウンドで保存中の投稿は削除できません。動画保存完了後に再度お試しください。";
                    return false;
                }

                MarkVideoDownloadsFailedSafe(conn, postIds);
                if (HasActiveVideoDownloadsSafe(conn, postIds))
                {
                    errorMessage = "動画保存状態を更新できなかったため、投稿を削除できませんでした。";
                    return false;
                }
            }

            var movedDirectories = MoveDirectoriesToTrash(dirPaths.Distinct(StringComparer.Ordinal).ToList(), out errorMessage);
            if (movedDirectories is null)
            {
                return false;
            }

            try
            {
                using var tx = conn.BeginTransaction();
                foreach (var postId in postIds)
                {
                    using var delete = conn.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM posts WHERE id = $id";
                    delete.Parameters.AddWithValue("$id", postId);
                    delete.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRestoreDirectories(movedDirectories);
                errorMessage = ex.Message;
                return false;
            }

            TryDeleteMovedDirectories(movedDirectories);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool HasActiveVideoDownloads(SqliteConnection conn, IReadOnlyCollection<long> postIds)
    {
        if (postIds.Count == 0)
        {
            return false;
        }

        if (!IsApiAvailable())
        {
            return false;
        }

        using var cmd = conn.CreateCommand();
        var placeholders = postIds.Select((_, index) => $"$id{index}").ToList();
        cmd.CommandText = $@"
SELECT COUNT(1)
FROM media
WHERE post_id IN ({string.Join(", ", placeholders)})
  AND media_type = 'video'
  AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";

        var index = 0;
        foreach (var postId in postIds)
        {
            cmd.Parameters.AddWithValue($"$id{index}", postId);
            index++;
        }

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    private static bool HasActiveVideoDownloadsSafe(SqliteConnection conn, IReadOnlyCollection<long> postIds)
    {
        if (postIds.Count == 0)
        {
            return false;
        }

        using var cmd = conn.CreateCommand();
        var placeholders = postIds.Select((_, index) => $"$id{index}").ToList();
        cmd.CommandText = $@"
SELECT COUNT(1)
FROM media
WHERE post_id IN ({string.Join(", ", placeholders)})
  AND media_type = 'video'
  AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";

        var index = 0;
        foreach (var postId in postIds)
        {
            cmd.Parameters.AddWithValue($"$id{index}", postId);
            index++;
        }

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    private static void MarkVideoDownloadsFailedSafe(SqliteConnection conn, IReadOnlyCollection<long> postIds)
    {
        if (postIds.Count == 0)
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        var placeholders = postIds.Select((_, index) => $"$id{index}").ToList();
        cmd.CommandText = $@"
UPDATE media
SET download_status = 'failed',
    download_error = 'アプリ起動中に動画保存が中断されたため、削除前に失敗扱いへ更新しました。'
WHERE post_id IN ({string.Join(", ", placeholders)})
  AND media_type = 'video'
  AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";

        var index = 0;
        foreach (var postId in postIds)
        {
            cmd.Parameters.AddWithValue($"$id{index}", postId);
            index++;
        }

        cmd.ExecuteNonQuery();
    }

    private static void MarkAllInterruptedVideoDownloadsSafe(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE media
SET download_status = 'failed',
    download_error = 'アプリ起動中に動画保存が中断されたため、失敗扱いへ更新しました。'
WHERE media_type = 'video'
  AND COALESCE(download_status, 'completed') IN ('pending', 'downloading')";
        cmd.ExecuteNonQuery();
    }

    private static bool IsApiAvailable()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var response = client.GetAsync("http://127.0.0.1:18765/api/v1/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsApiPossiblyRunningSafe()
    {
        if (IsApiAvailable())
        {
            return true;
        }

        if (Process.GetProcessesByName("XPostArchive.Api").Length > 0)
        {
            return true;
        }

        return false;
    }

    private static List<MovedDirectoryItem>? MoveDirectoriesToTrash(List<string> dirPaths, out string errorMessage)
    {
        errorMessage = string.Empty;
        var movedDirectories = new List<MovedDirectoryItem>();
        var trashRoot = Path.Combine(TagCatalogStore.ResolveArchiveRootPath(), ".trash", Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var sourcePath in dirPaths)
            {
                if (!Directory.Exists(sourcePath))
                {
                    continue;
                }

                var targetPath = Path.Combine(trashRoot, Path.GetFileName(sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                Directory.Move(sourcePath, targetPath);
                movedDirectories.Add(new MovedDirectoryItem(sourcePath, targetPath));
            }

            return movedDirectories;
        }
        catch (Exception ex)
        {
            TryRestoreDirectories(movedDirectories);
            errorMessage = ex.Message;
            return null;
        }
    }

    private static void TryRestoreDirectories(IEnumerable<MovedDirectoryItem> movedDirectories)
    {
        foreach (var item in movedDirectories.Reverse())
        {
            try
            {
                if (Directory.Exists(item.TempPath) && !Directory.Exists(item.OriginalPath))
                {
                    Directory.Move(item.TempPath, item.OriginalPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteMovedDirectories(IEnumerable<MovedDirectoryItem> movedDirectories)
    {
        foreach (var item in movedDirectories)
        {
            try
            {
                if (Directory.Exists(item.TempPath))
                {
                    Directory.Delete(item.TempPath, true);
                }
            }
            catch
            {
            }
        }
    }

    private static List<PostRow> LoadPostRows(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT p.id, p.tweet_id, p.created_at, p.saved_at, p.dir_path, p.text, p.note, a.handle, a.name,
       qp.tweet_id, qa.handle, qa.name, qp.text
FROM posts p
JOIN authors a ON a.id = p.author_id
LEFT JOIN posts qp ON qp.id = p.quoted_post_id
LEFT JOIN authors qa ON qa.id = qp.author_id;";

        using var reader = cmd.ExecuteReader();
        var posts = new List<PostRow>();
        while (reader.Read())
        {
            var createdAt = DateTimeOffset.TryParse(reader.GetString(2), out var createdAtValue)
                ? createdAtValue
                : (DateTimeOffset?)null;
            var savedAt = DateTimeOffset.TryParse(reader.GetString(3), out var savedAtValue)
                ? savedAtValue
                : (DateTimeOffset?)null;
            var savedAtDisplay = savedAt.HasValue
                ? savedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : reader.GetString(3);

            posts.Add(new PostRow(
                reader.GetInt64(0),
                reader.GetString(1),
                createdAt,
                savedAt,
                savedAtDisplay,
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.IsDBNull(12) ? string.Empty : reader.GetString(12)));
        }

        return posts;
    }

    private static Dictionary<long, List<string>> LoadTagsByPostId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT pt.post_id, t.name
FROM post_tags pt
JOIN tags t ON t.id = pt.tag_id
ORDER BY pt.post_id, t.name COLLATE NOCASE ASC;";

        using var reader = cmd.ExecuteReader();
        var tags = new Dictionary<long, List<string>>();
        while (reader.Read())
        {
            var postId = reader.GetInt64(0);
            if (!tags.TryGetValue(postId, out var list))
            {
                list = [];
                tags[postId] = list;
            }

            list.Add(reader.GetString(1));
        }

        return tags;
    }

    private static Dictionary<long, List<MediaFileItem>> LoadMediaByPostId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.post_id, m.media_type, m.local_path, p.dir_path, COALESCE(m.download_status, 'completed'), m.download_error
FROM media m
JOIN posts p ON p.id = m.post_id
ORDER BY m.post_id, m.sort_order ASC;";

        using var reader = cmd.ExecuteReader();
        var media = new Dictionary<long, List<MediaFileItem>>();
        while (reader.Read())
        {
            var postId = reader.GetInt64(0);
            if (!media.TryGetValue(postId, out var list))
            {
                list = [];
                media[postId] = list;
            }

            var mediaType = reader.GetString(1);
            var localPath = reader.GetString(2);
            var dirPath = reader.GetString(3);
            var downloadStatus = reader.IsDBNull(4) ? "completed" : reader.GetString(4);
            var downloadError = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);

            list.Add(new MediaFileItem
            {
                Type = mediaType,
                Display = mediaType == "video" ? $"[video] {localPath}" : $"[image] {localPath}",
                FullPath = Path.GetFullPath(Path.Combine(dirPath, localPath)),
                DownloadStatus = downloadStatus,
                DownloadError = downloadError
            });
        }

        return media;
    }

    private static string BuildVideoStatusText(List<MediaFileItem> mediaFiles)
    {
        var videos = mediaFiles.Where(x => x.Type == "video").ToList();
        if (videos.Count == 0)
        {
            return string.Empty;
        }

        if (videos.Any(x => x.DownloadStatus is "pending" or "downloading"))
        {
            return "動画をバックグラウンドで保存中";
        }

        if (videos.Any(x => x.DownloadStatus == "failed"))
        {
            return "動画保存に失敗あり";
        }

        return "動画保存済み";
    }

    private sealed record PostRow(
        long Id,
        string TweetId,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? SavedAt,
        string SavedAtDisplay,
        string DirPath,
        string Text,
        string Note,
        string AuthorHandle,
        string AuthorName,
        string QuotedTweetId,
        string QuotedAuthorHandle,
        string QuotedAuthorName,
        string QuotedText)
    {
        public string QuotedAuthor => string.IsNullOrWhiteSpace(QuotedTweetId)
            ? string.Empty
            : string.IsNullOrWhiteSpace(QuotedAuthorName)
                ? QuotedAuthorHandle
                : $"{QuotedAuthorName} ({QuotedAuthorHandle})";
    }

    private sealed record MovedDirectoryItem(string OriginalPath, string TempPath);
}
