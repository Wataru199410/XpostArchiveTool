using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace XPostArchive.Desktop;

internal static class TagCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static List<TagCatalogItem> LoadCatalog(string archiveRoot)
    {
        var counts = LoadTagCountsFromArchive(archiveRoot);
        var catalog = counts
            .Select(kv => new TagCatalogItem(kv.Key, kv.Value))
            .ToList();

        var dbPath = ResolveDatabasePath();
        if (!File.Exists(dbPath))
        {
            return catalog;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM tags ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (catalog.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                catalog.Add(new TagCatalogItem(name, 0));
            }
        }
        catch
        {
            // Fall back to archive-derived tags only.
        }

        return catalog;
    }

    public static void AddTag(string tag)
    {
        PersistTagsToDatabase(new[] { tag });
    }

    public static void DeleteTag(string tag, string archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var normalized = tag.Trim();

        DeleteTagFromArchive(normalized, archiveRoot);
        DeleteTagFromDatabase(normalized);
    }

    public static void PersistTagsToDatabase(IEnumerable<string> tags)
    {
        var normalizedTags = tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedTags.Count == 0) return;

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
            var projectArchive = Path.Combine(projectRoot, "XArchive");
            if (Directory.Exists(projectArchive))
            {
                return projectArchive;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "XArchive"));
    }

    public static string ResolveDatabasePath()
    {
        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            return Path.Combine(projectRoot, "data", "archive.db");
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "archive.db"));
    }

    private static Dictionary<string, int> LoadTagCountsFromArchive(string archiveRoot)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(archiveRoot))
        {
            return counts;
        }

        foreach (var dir in Directory.GetDirectories(archiveRoot, "tweet-*", SearchOption.TopDirectoryOnly))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var meta = JsonSerializer.Deserialize<MetaJson>(File.ReadAllText(metaPath), JsonOptions);
                if (meta?.tags is null) continue;

                foreach (var tag in meta.tags.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    counts[tag] = counts.TryGetValue(tag, out var count) ? count + 1 : 1;
                }
            }
            catch
            {
                // Ignore broken files.
            }
        }

        return counts;
    }

    private static void DeleteTagFromArchive(string tag, string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot)) return;

        foreach (var dir in Directory.GetDirectories(archiveRoot, "tweet-*", SearchOption.TopDirectoryOnly))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
                if (node?["tags"] is not JsonArray tags) continue;

                var remaining = tags
                    .Select(x => x?.GetValue<string>() ?? string.Empty)
                    .Where(x => !string.Equals(x, tag, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var outputTags = new JsonArray();
                foreach (var current in remaining)
                {
                    outputTags.Add(current);
                }

                node["tags"] = outputTags;
                var output = JsonSerializer.Serialize(
                    node,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                    });
                File.WriteAllText(metaPath, output);
            }
            catch
            {
                // Ignore broken files.
            }
        }
    }

    private static void DeleteTagFromDatabase(string tag)
    {
        var dbPath = ResolveDatabasePath();
        if (!File.Exists(dbPath)) return;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        long? tagId = null;
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id FROM tags WHERE name = $name";
            select.Parameters.AddWithValue("$name", tag);
            var value = select.ExecuteScalar();
            if (value is long id)
            {
                tagId = id;
            }
        }

        if (tagId is null) return;

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

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var desktopCsproj = Path.Combine(dir.FullName, "XPostArchive.Desktop.csproj");
            var apiCsproj = Path.Combine(dir.FullName, "XPostArchive.Api.csproj");
            if (File.Exists(desktopCsproj) || File.Exists(apiCsproj)) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
