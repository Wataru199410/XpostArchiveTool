using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XPostArchive.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 101, 52));

    private List<PostListItem> _allItems = new();
    private List<TagCatalogItem> _tagCatalog = new();
    private readonly List<EditableTagItem> _editableTags = new();
    private string? _selectedTag;
    private PostListItem? _selectedItem;
    private SortOption _selectedSort = SortOption.SavedAtDesc;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSortOptions();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var result = await App.ApiManager.EnsureStartedAsync();

        if (!result.ok)
        {
            MessageBox.Show(result.message, "API 起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        LoadSavedPosts();
        ShowHomeState();
    }

    private void ReloadPosts_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirPath = _selectedItem?.DirPath;
        LoadSavedPosts();

        if (!string.IsNullOrWhiteSpace(selectedDirPath))
        {
            var updated = _allItems.FirstOrDefault(x => x.DirPath == selectedDirPath);
            if (updated is not null)
            {
                ShowPostDetail(updated);
                PostsCardList.SelectedItem = updated;
                return;
            }
        }

        ShowHomeState();
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _selectedTag = null;
        if (TagFilterList.Items.Count > 0)
        {
            TagFilterList.SelectedIndex = 0;
        }

        _selectedItem = null;
        PostsCardList.SelectedItem = null;
        ShowHomeState();
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SortOrderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortOrderComboBox.SelectedItem is not SortOptionItem option)
        {
            return;
        }

        _selectedSort = option.Value;
        ApplyFilter();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ForceJapaneseIme(SearchBox);

    private void DetailNoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedItem is null || !DetailNoteTextBox.IsEnabled)
        {
            return;
        }

        SetMemoStatus("メモはまだ保存されていません。");
    }

    private void TagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagFilterList.SelectedItem is not TagFilterItem tag)
        {
            _selectedTag = null;
            TagSummaryText.Text = "すべての投稿を表示中";
        }
        else
        {
            _selectedTag = tag.Key == "__ALL__" ? null : tag.Key;
            TagSummaryText.Text = _selectedTag is null ? "すべての投稿を表示中" : $"タグ: {_selectedTag}";
        }

        ApplyFilter();
    }

    private void PostsCardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PostsCardList.SelectedItem is not PostListItem item)
        {
            if (_selectedItem is null)
            {
                ShowHomeState();
            }

            return;
        }

        ShowPostDetail(item);
    }

    private void CardOpenDetail_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PostListItem item)
        {
            return;
        }

        PostsCardList.SelectedItem = item;
        ShowPostDetail(item);
    }

    private void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirPath = _selectedItem?.DirPath;
        var archiveRoot = TagCatalogStore.ResolveArchiveRootPath();
        var window = new TagManagementWindow(archiveRoot) { Owner = this };
        window.ShowDialog();

        LoadSavedPosts();

        if (!string.IsNullOrWhiteSpace(selectedDirPath))
        {
            var updated = _allItems.FirstOrDefault(x => x.DirPath == selectedDirPath);
            if (updated is not null)
            {
                PostsCardList.SelectedItem = updated;
                ShowPostDetail(updated);
                return;
            }
        }

        ShowHomeState();
    }

    private void OpenTagEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        var window = new TagEditWindow(
            _editableTags.Select(x => x.Name).ToList(),
            _tagCatalog,
            ApplyTagsFromEditor)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void SaveDetailEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        var tags = _editableTags
            .Select(x => x.Name.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!SaveSelectedPostChanges(tags, DetailNoteTextBox.Text ?? string.Empty, out var updated, out var errorMessage))
        {
            SetMemoStatus(errorMessage, isError: true);
            return;
        }

        SetMemoStatus("メモを保存しました。", isSuccess: true);
        ShowPostDetail(updated);
    }

    private void LoadSavedPosts()
    {
        var archiveRoot = TagCatalogStore.ResolveArchiveRootPath();
        var items = new List<PostListItem>();

        if (Directory.Exists(archiveRoot))
        {
            foreach (var dir in Directory.GetDirectories(archiveRoot, "tweet-*", SearchOption.TopDirectoryOnly))
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath))
                {
                    continue;
                }

                try
                {
                    var meta = JsonSerializer.Deserialize<MetaJson>(File.ReadAllText(metaPath), JsonOptions);
                    if (meta is null)
                    {
                        continue;
                    }

                    var savedAtValue = DateTimeOffset.TryParse(meta.saved_at, out var savedAtDto)
                        ? savedAtDto
                        : (DateTimeOffset?)null;
                    var createdAtValue = DateTimeOffset.TryParse(meta.created_at, out var createdAtDto)
                        ? createdAtDto
                        : (DateTimeOffset?)null;
                    var savedAt = savedAtValue.HasValue
                        ? savedAtValue.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : meta.saved_at;

                    var mediaFiles = BuildMediaFileItems(dir, meta);
                    var thumbnailPath = mediaFiles.FirstOrDefault(x => x.Type == "image")?.FullPath ?? string.Empty;

                    items.Add(new PostListItem
                    {
                        SavedAt = savedAt,
                        SavedAtSortValue = savedAtValue,
                        CreatedAtSortValue = createdAtValue,
                        Author = string.IsNullOrWhiteSpace(meta.author?.name)
                            ? meta.author?.handle ?? string.Empty
                            : $"{meta.author.name} ({meta.author.handle})",
                        Text = meta.text ?? string.Empty,
                        Tags = meta.tags is { Count: > 0 } ? string.Join(", ", meta.tags) : "(タグなし)",
                        TagList = meta.tags ?? new List<string>(),
                        TweetId = meta.tweet_id ?? string.Empty,
                        DirPath = dir,
                        ImageCount = meta.media?.images?.Count ?? 0,
                        VideoCount = meta.media?.videos?.Count ?? 0,
                        MediaFiles = mediaFiles,
                        ThumbnailPath = thumbnailPath,
                        Note = meta.note ?? string.Empty
                    });
                }
                catch
                {
                    // ignore broken meta.json
                }
            }
        }

        _allItems = items.OrderByDescending(x => x.SavedAt).ToList();
        _tagCatalog = TagCatalogStore.LoadCatalog(archiveRoot);
        BuildTagFilters();
        ApplyFilter();
    }

    private void BuildTagFilters()
    {
        var tagCounts = _tagCatalog
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name)
            .Select(x => new TagFilterItem { Key = x.Name, Name = x.Name, Count = x.Count })
            .ToList();

        tagCounts.Insert(0, new TagFilterItem { Key = "__ALL__", Name = "すべて", Count = _allItems.Count });
        TagFilterList.ItemsSource = tagCounts;

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(_selectedTag))
        {
            var selected = tagCounts.FindIndex(x => string.Equals(x.Key, _selectedTag, StringComparison.OrdinalIgnoreCase));
            selectedIndex = selected >= 0 ? selected : 0;
        }

        TagFilterList.SelectedIndex = selectedIndex;
    }

    private void ApplyFilter()
    {
        var keyword = (SearchBox.Text ?? string.Empty).Trim();

        IEnumerable<PostListItem> query = _allItems;
        if (!string.IsNullOrWhiteSpace(_selectedTag))
        {
            query = query.Where(x => x.TagList.Any(t => string.Equals(t, _selectedTag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                ContainsIgnoreCase(x.Text, keyword) ||
                ContainsIgnoreCase(x.Author, keyword) ||
                ContainsIgnoreCase(x.Tags, keyword) ||
                ContainsIgnoreCase(x.TweetId, keyword));
        }

        var list = ApplySort(query).ToList();
        PostsCardList.ItemsSource = list;

        var totalImages = _allItems.Sum(x => x.ImageCount);
        var totalVideos = _allItems.Sum(x => x.VideoCount);
        ListStatsText.Text = $"{list.Count} / {_allItems.Count} 件表示  |  画像 {totalImages} 件  |  動画 {totalVideos} 件";

        if (_selectedItem is not null)
        {
            var current = list.FirstOrDefault(x => x.DirPath == _selectedItem.DirPath);
            if (current is not null)
            {
                PostsCardList.SelectedItem = current;
                ShowPostDetail(current);
                return;
            }
        }

        PostsCardList.SelectedItem = null;
        ShowHomeState(list.Count == 0
            ? "条件に一致する投稿がありません。"
            : "左の一覧から投稿を選択してください。");
    }

    private void ShowHomeState(string? subTitle = null)
    {
        _selectedItem = null;
        DetailTitleText.Text = "投稿詳細";
        DetailSubTitleText.Text = subTitle ?? "左の一覧から投稿を選択してください。";
        DetailTweetText.Text = "一覧から投稿を選択すると、ここに本文が表示されます。";
        DetailTagText.Text = "タグ: なし";
        DetailMediaList.ItemsSource = new[]
        {
            new MediaFileItem
            {
                Type = "info",
                Display = "投稿を選択するとメディア一覧が表示されます。",
                FullPath = string.Empty
            }
        };

        _editableTags.Clear();
        RefreshTagEditor();
        DetailNoteTextBox.Text = string.Empty;
        DetailNoteTextBox.IsEnabled = false;
        OpenTagEditorButton.IsEnabled = false;
        SetTagActionStatus(string.Empty);
        SetMemoStatus(string.Empty);
    }

    private void ShowPostDetail(PostListItem item)
    {
        _selectedItem = item;
        DetailTitleText.Text = item.Author;
        DetailSubTitleText.Text = $"Tweet ID: {item.TweetId}  |  保存日時: {item.SavedAt}";
        DetailTweetText.Text = string.IsNullOrWhiteSpace(item.Text) ? "(本文なし)" : item.Text;

        _editableTags.Clear();
        foreach (var tag in item.TagList.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            _editableTags.Add(new EditableTagItem { Name = tag.Trim() });
        }

        RefreshTagEditor();
        DetailNoteTextBox.Text = item.Note ?? string.Empty;
        DetailNoteTextBox.IsEnabled = true;
        OpenTagEditorButton.IsEnabled = true;
        SetTagActionStatus(string.Empty);
        SetMemoStatus(string.Empty);

        DetailMediaList.ItemsSource = item.MediaFiles.Count == 0
            ? new[]
            {
                new MediaFileItem
                {
                    Type = "info",
                    Display = "この投稿に保存済みメディアはありません。",
                    FullPath = string.Empty
                }
            }
            : item.MediaFiles;
    }

    private TagEditSaveResult ApplyTagsFromEditor(IReadOnlyList<string> tags)
    {
        if (_selectedItem is null)
        {
            return new TagEditSaveResult(false, "投稿が選択されていません。");
        }

        if (!SaveSelectedPostChanges(tags.ToList(), _selectedItem.Note, out var updated, out var errorMessage))
        {
            SetTagActionStatus(errorMessage, isError: true);
            return new TagEditSaveResult(false, errorMessage);
        }

        SetTagActionStatus("タグを保存しました。", isSuccess: true);
        ShowPostDetail(updated);
        return new TagEditSaveResult(true, "タグを保存しました。");
    }

    private bool SaveSelectedPostChanges(List<string> tags, string note, out PostListItem updated, out string errorMessage)
    {
        updated = _selectedItem!;
        errorMessage = string.Empty;

        if (_selectedItem is null)
        {
            errorMessage = "投稿が選択されていません。";
            return false;
        }

        try
        {
            var metaPath = Path.Combine(_selectedItem.DirPath, "meta.json");
            if (!File.Exists(metaPath))
            {
                errorMessage = "meta.json が見つかりません。";
                return false;
            }

            var text = File.ReadAllText(metaPath);
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null)
            {
                errorMessage = "meta.json を読み込めませんでした。";
                return false;
            }

            var normalizedTags = tags
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var originalTags = _selectedItem.TagList
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tagArray = new JsonArray();
            foreach (var currentTag in normalizedTags)
            {
                tagArray.Add(currentTag);
            }

            TagCatalogStore.PersistTagsToDatabase(normalizedTags.Concat(originalTags));
            node["tags"] = tagArray;
            node["note"] = note;

            var output = JsonSerializer.Serialize(
                node,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                });
            File.WriteAllText(metaPath, output);

            var targetDir = _selectedItem.DirPath;
            LoadSavedPosts();

            updated = _allItems.FirstOrDefault(x => x.DirPath == targetDir) ?? _selectedItem;
            PostsCardList.SelectedItem = updated;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private void DetailMediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedMedia();

    private void OpenMedia_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaFileItem media)
        {
            return;
        }

        OpenFile(media.FullPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null || !Directory.Exists(_selectedItem.DirPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = _selectedItem.DirPath, UseShellExecute = true });
    }

    private void OpenSelectedMedia()
    {
        if (DetailMediaList.SelectedItem is not MediaFileItem media)
        {
            return;
        }

        OpenFile(media.FullPath);
    }

    private static void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void RefreshTagEditor()
    {
        DetailTagItems.ItemsSource = null;
        DetailTagItems.ItemsSource = _editableTags.OrderBy(x => x.Name).ToList();
        DetailTagText.Text = _editableTags.Count == 0
            ? "タグ: なし"
            : $"タグ: {string.Join(", ", _editableTags.Select(x => $"#{x.Name}"))}";
    }

    private void SetTagActionStatus(string text, bool isError = false, bool isSuccess = false)
    {
        TagActionStatusText.Text = text;
        TagActionStatusText.Foreground = isError ? ErrorBrush : isSuccess ? SuccessBrush : NeutralBrush;
    }

    private void SetMemoStatus(string text, bool isError = false, bool isSuccess = false)
    {
        DetailSaveStatusText.Text = text;
        DetailSaveStatusText.Foreground = isError ? ErrorBrush : isSuccess ? SuccessBrush : NeutralBrush;
    }

    private static bool ContainsIgnoreCase(string source, string keyword) =>
        source?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void ForceJapaneseIme(TextBox textBox)
    {
        InputMethod.SetIsInputMethodEnabled(textBox, true);
        InputMethod.SetPreferredImeState(textBox, InputMethodState.DoNotCare);
    }

    private static List<MediaFileItem> BuildMediaFileItems(string dirPath, MetaJson meta)
    {
        var list = new List<MediaFileItem>();

        if (meta.media?.images is not null)
        {
            foreach (var image in meta.media.images)
            {
                list.Add(new MediaFileItem
                {
                    Type = "image",
                    Display = $"[画像] {image.local_path}",
                    FullPath = Path.GetFullPath(Path.Combine(dirPath, image.local_path))
                });
            }
        }

        if (meta.media?.videos is not null)
        {
            foreach (var video in meta.media.videos)
            {
                list.Add(new MediaFileItem
                {
                    Type = "video",
                    Display = $"[動画] {video.local_path}",
                    FullPath = Path.GetFullPath(Path.Combine(dirPath, video.local_path))
                });
            }
        }

        return list;
    }

    private void InitializeSortOptions()
    {
        SortOrderComboBox.ItemsSource = new[]
        {
            new SortOptionItem("保存日時: 新しい順", SortOption.SavedAtDesc),
            new SortOptionItem("保存日時: 古い順", SortOption.SavedAtAsc),
            new SortOptionItem("投稿日時: 新しい順", SortOption.CreatedAtDesc),
            new SortOptionItem("投稿日時: 古い順", SortOption.CreatedAtAsc),
            new SortOptionItem("投稿者名: A-Z", SortOption.AuthorAsc),
            new SortOptionItem("投稿者名: Z-A", SortOption.AuthorDesc)
        };
        SortOrderComboBox.SelectedIndex = 0;
    }

    private IEnumerable<PostListItem> ApplySort(IEnumerable<PostListItem> items)
    {
        return _selectedSort switch
        {
            SortOption.SavedAtAsc => items.OrderBy(x => x.SavedAtSortValue).ThenBy(x => x.TweetId),
            SortOption.SavedAtDesc => items.OrderByDescending(x => x.SavedAtSortValue).ThenByDescending(x => x.TweetId),
            SortOption.CreatedAtAsc => items.OrderBy(x => x.CreatedAtSortValue).ThenBy(x => x.TweetId),
            SortOption.CreatedAtDesc => items.OrderByDescending(x => x.CreatedAtSortValue).ThenByDescending(x => x.TweetId),
            SortOption.AuthorAsc => items.OrderBy(x => x.Author, StringComparer.CurrentCulture).ThenByDescending(x => x.SavedAtSortValue),
            SortOption.AuthorDesc => items.OrderByDescending(x => x.Author, StringComparer.CurrentCulture).ThenByDescending(x => x.SavedAtSortValue),
            _ => items
        };
    }
}

public sealed class PostListItem
{
    public string SavedAt { get; init; } = string.Empty;
    public DateTimeOffset? SavedAtSortValue { get; init; }
    public DateTimeOffset? CreatedAtSortValue { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public List<string> TagList { get; init; } = new();
    public string TweetId { get; init; } = string.Empty;
    public string DirPath { get; init; } = string.Empty;
    public int ImageCount { get; init; }
    public int VideoCount { get; init; }
    public string ThumbnailPath { get; init; } = string.Empty;
    public List<MediaFileItem> MediaFiles { get; init; } = new();
    public string Note { get; init; } = string.Empty;
}

public sealed class TagFilterItem
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Display => $"{Name} ({Count})";
}

public sealed class MediaFileItem
{
    public string Type { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
}

public sealed class EditableTagItem
{
    public string Name { get; init; } = string.Empty;
}

public sealed record TagCatalogItem(string Name, int Count);
public sealed record TagEditSaveResult(bool Success, string Message);
public sealed record SortOptionItem(string Label, SortOption Value)
{
    public override string ToString() => Label;
}

public enum SortOption
{
    SavedAtDesc,
    SavedAtAsc,
    CreatedAtDesc,
    CreatedAtAsc,
    AuthorAsc,
    AuthorDesc
}

public sealed record MetaJson(
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

public sealed record MetaAuthor(string handle, string name);
public sealed record MetaMedia(List<MetaImage> images, List<MetaVideo> videos);
public sealed record MetaImage(string original_url, string local_path);
public sealed record MetaVideo(string playlist_url, string local_path);
