using System.IO;
using Microsoft.Data.Sqlite;

namespace XPostArchive.Desktop;

internal static class DesktopArchiveStore
{
    public static List<PostListItem> LoadPosts()
    {
        var dbPath = TagCatalogStore.ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return [];
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            var posts = LoadPostRows(conn);
            var tagsByPostId = LoadTagsByPostId(conn);
            var mediaByPostId = LoadMediaByPostId(conn);

            return posts
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
        }
        catch
        {
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

        if (videos.Any(x => x.DownloadStatus == "pending" || x.DownloadStatus == "downloading"))
        {
            return "動画をバックグラウンドで保存中";
        }

        if (videos.Any(x => x.DownloadStatus == "failed"))
        {
            return "動画保存に失敗した項目があります";
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
}
