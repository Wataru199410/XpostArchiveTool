using System.IO;
using Microsoft.Data.Sqlite;

namespace XPostArchive.Desktop;

internal static class TagCatalogStore
{
    public static List<TagCatalogItem> LoadCatalog(string? _ = null)
    {
        var dbPath = ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return [];
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT t.name, COUNT(pt.post_id) AS usage_count
FROM tags t
LEFT JOIN post_tags pt ON pt.tag_id = t.id
GROUP BY t.id, t.name
ORDER BY usage_count DESC, t.name COLLATE NOCASE ASC;";

            using var reader = cmd.ExecuteReader();
            var items = new List<TagCatalogItem>();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                items.Add(new TagCatalogItem(name, reader.GetInt32(1)));
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    public static void AddTag(string tag)
    {
        PersistTagsToDatabase(new[] { tag });
    }

    public static void DeleteTag(string tag, string? _ = null)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var dbPath = ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        long? tagId = null;
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id FROM tags WHERE name = $name";
            select.Parameters.AddWithValue("$name", tag.Trim());
            var value = select.ExecuteScalar();
            if (value is long id)
            {
                tagId = id;
            }
        }

        if (tagId is null)
        {
            return;
        }

        using (var deleteLinks = conn.CreateCommand())
        {
            deleteLinks.CommandText = "DELETE FROM post_tags WHERE tag_id = $tag_id";
            deleteLinks.Parameters.AddWithValue("$tag_id", tagId.Value);
            deleteLinks.ExecuteNonQuery();
        }

        using var deleteTag = conn.CreateCommand();
        deleteTag.CommandText = "DELETE FROM tags WHERE id = $tag_id";
        deleteTag.Parameters.AddWithValue("$tag_id", tagId.Value);
        deleteTag.ExecuteNonQuery();
    }

    public static void PersistTagsToDatabase(IEnumerable<string> tags)
    {
        var normalizedTags = tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedTags.Count == 0)
        {
            return;
        }

        var dbPath = ResolveDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        foreach (var tag in normalizedTags)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO tags (name, created_at)
VALUES ($name, $created_at)
ON CONFLICT(name) DO NOTHING;";
            cmd.Parameters.AddWithValue("$name", tag);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public static string ResolveArchiveRootPath()
    {
        var envPath = Environment.GetEnvironmentVariable("XPOST_ARCHIVE_ROOT");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            return Path.Combine(projectRoot, "XArchive");
        }

        return Path.Combine(GetLocalAppDataRoot(), "XArchive");
    }

    public static string ResolveDatabasePath()
    {
        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            return Path.Combine(projectRoot, "data", "archive.db");
        }

        return Path.Combine(GetLocalAppDataRoot(), "data", "archive.db");
    }

    private static string GetLocalAppDataRoot()
    {
        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XPostArchive"));
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
