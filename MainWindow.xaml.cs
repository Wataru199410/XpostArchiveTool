using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XPostArchive.Desktop;

public partial class MainWindow : Window
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 101, 52));

    private List<PostListItem> _allItems = [];
    private List<TagCatalogItem> _tagCatalog = [];
    private readonly List<EditableTagItem> _editableTags = [];
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedSpecialFilter;
    private PostListItem? _selectedItem;
    private SortOption _selectedSort = SortOption.SavedAtDesc;
    private bool _isUpdatingTagFilterSelection;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSortOptions();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var result = await App.ApiManager.EnsureStartedAsync();
        if (!result.ok)
        {
            MessageBox.Show(result.message, "API エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        LoadSavedPosts();
        ShowHomeState();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var activeDownloads = DesktopArchiveStore.GetActiveVideoDownloadCount();
        if (activeDownloads <= 0)
        {
            return;
        }

        var message = activeDownloads == 1
            ? "動画をバックグラウンドで保存中です。アプリを終了すると中断されます。終了しますか？"
            : $"動画を{activeDownloads}件バックグラウンドで保存中です。アプリを終了すると中断されます。終了しますか？";
        var result = MessageBox.Show(message, "動画保存中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
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
        _selectedTags.Clear();
        _selectedSpecialFilter = null;
        SetTagFilterSelectionSilently(() => TagFilterList.UnselectAll());

        _selectedItem = null;
        PostsCardList.SelectedItem = null;
        ShowHomeState();
        UpdateTagSummary();
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

        SetMemoStatus("メモは未保存です。");
    }

    private void TagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTagFilterSelection)
        {
            return;
        }

        NormalizeTagSelection(e);
        SyncSelectedTagsFromUi();
        UpdateTagSummary();
        ApplyFilter();
    }

    private void TagFilterListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || !item.IsSelected)
        {
            return;
        }

        SetTagFilterSelectionSilently(() => TagFilterList.SelectedItems.Remove(item.DataContext));
        SyncSelectedTagsFromUi();
        UpdateTagSummary();
        ApplyFilter();
        e.Handled = true;
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

    private void PostCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not PostListItem item)
        {
            return;
        }

        PostsCardList.SelectedItem = item;
        ShowPostDetail(item);
    }

    private void CardOpenQuotedPost_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PostListItem item)
        {
            return;
        }

        OpenQuotedPost(item);
    }

    private void CardOpenReferencingPost_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PostListItem item)
        {
            return;
        }

        OpenReferencingPost(item);
    }

    private void CardDeletePost_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PostListItem item)
        {
            return;
        }

        DeletePost(item);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirPath = _selectedItem?.DirPath;
        var window = new TagManagementWindow(TagCatalogStore.ResolveArchiveRootPath()) { Owner = this };
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

        var window = new TagEditWindow(_editableTags.Select(x => x.Name).ToList(), _tagCatalog, ApplyTagsFromEditor)
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
        _allItems = DesktopArchiveStore.LoadPosts();
        _tagCatalog = TagCatalogStore.LoadCatalog();
        BuildTagFilters();
        ApplyFilter();
    }

    private void BuildTagFilters()
    {
        var tagCounts = _tagCatalog
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .Select(x => new TagFilterItem
            {
                Key = x.Name,
                Name = x.Name,
                Count = x.Count
            })
            .ToList();

        tagCounts.Insert(0, new TagFilterItem
        {
            Key = "__UNTAGGED__",
            Name = "絞り込み: タグなし",
            Count = _allItems.Count(x => x.TagList.Count == 0),
            IsSpecial = true
        });
        tagCounts.Insert(0, new TagFilterItem
        {
            Key = "__ALL__",
            Name = "絞り込み: すべて",
            Count = _allItems.Count,
            IsSpecial = true
        });

        TagFilterList.ItemsSource = tagCounts;

        SetTagFilterSelectionSilently(() =>
        {
            TagFilterList.UnselectAll();

            if (string.Equals(_selectedSpecialFilter, "__UNTAGGED__", StringComparison.Ordinal))
            {
                var untagged = tagCounts.FirstOrDefault(x => x.Key == "__UNTAGGED__");
                if (untagged is not null)
                {
                    TagFilterList.SelectedItems.Add(untagged);
                }
            }
            else
            {
                foreach (var tag in tagCounts.Where(x => !x.IsSpecial && _selectedTags.Contains(x.Key)))
                {
                    TagFilterList.SelectedItems.Add(tag);
                }
            }
        });

        SyncSelectedTagsFromUi();
        UpdateTagSummary();
    }

    private void ApplyFilter()
    {
        var keyword = (SearchBox.Text ?? string.Empty).Trim();

        IEnumerable<PostListItem> query = _allItems;
        if (string.Equals(_selectedSpecialFilter, "__UNTAGGED__", StringComparison.Ordinal))
        {
            query = query.Where(x => x.TagList.Count == 0);
        }
        else if (_selectedTags.Count > 0)
        {
            query = query.Where(x =>
                _selectedTags.All(selectedTag =>
                    x.TagList.Any(t => string.Equals(t, selectedTag, StringComparison.OrdinalIgnoreCase))));
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
        ListStatsText.Text = $"{list.Count} / {_allItems.Count} 件 | 画像 {totalImages} | 動画 {totalVideos}";

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
        ShowHomeState(list.Count == 0 ? "条件に一致する投稿がありません。" : "左の一覧から投稿を選択してください。");
    }

    private void ShowHomeState(string? subTitle = null)
    {
        _selectedItem = null;
        DetailTitleText.Text = "投稿詳細";
        DetailSubTitleText.Text = subTitle ?? "左の一覧から投稿を選択してください。";
        DetailTweetText.Text = "投稿本文がここに表示されます。";
        OpenQuotedPostButton.Visibility = Visibility.Collapsed;
        OpenReferencingPostButton.Visibility = Visibility.Collapsed;
        DetailTagText.Text = "タグ: なし";
        DetailMediaList.ItemsSource = new[]
        {
            new MediaFileItem
            {
                Type = "info",
                Display = "メディア一覧がここに表示されます。",
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
        DetailSubTitleText.Text = $"Tweet ID: {item.TweetId} | 保存日時: {item.SavedAt}";
        DetailTweetText.Text = string.IsNullOrWhiteSpace(item.Text) ? "(本文なし)" : item.Text;
        OpenQuotedPostButton.Visibility = string.IsNullOrWhiteSpace(item.QuotedTweetId) ? Visibility.Collapsed : Visibility.Visible;
        OpenReferencingPostButton.Visibility = string.IsNullOrWhiteSpace(item.ReferencedByTweetId) ? Visibility.Collapsed : Visibility.Visible;
        OpenReferencingPostButton.Content = item.ReferencedByCount > 1 ? $"引用先の投稿を開く ({item.ReferencedByCount})" : "引用先の投稿を開く";
        OpenQuotedPostButton.Content = "引用元の投稿を開く";

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
                    Display = "保存済みメディアはありません。",
                    FullPath = string.Empty
                }
            }
            : item.MediaFiles;
    }

    private void OpenQuotedPost_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        OpenQuotedPost(_selectedItem);
    }

    private void OpenReferencingPost_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        OpenReferencingPost(_selectedItem);
    }

    private void OpenQuotedPost(PostListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.QuotedTweetId))
        {
            return;
        }

        var quotedItem = _allItems.FirstOrDefault(x => string.Equals(x.TweetId, item.QuotedTweetId, StringComparison.Ordinal));
        if (quotedItem is null)
        {
            MessageBox.Show("引用元の投稿は一覧に見つかりません。", "引用元が見つかりません", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PostsCardList.SelectedItem = quotedItem;
        PostsCardList.ScrollIntoView(quotedItem);
        ShowPostDetail(quotedItem);
    }

    private void OpenReferencingPost(PostListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ReferencedByTweetId))
        {
            return;
        }

        var referencingItem = _allItems.FirstOrDefault(x => string.Equals(x.TweetId, item.ReferencedByTweetId, StringComparison.Ordinal));
        if (referencingItem is null)
        {
            MessageBox.Show("引用先の投稿は一覧に見つかりません。", "引用先が見つかりません", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PostsCardList.SelectedItem = referencingItem;
        PostsCardList.ScrollIntoView(referencingItem);
        ShowPostDetail(referencingItem);
    }

    private TagEditSaveResult ApplyTagsFromEditor(IReadOnlyList<string> tags)
    {
        if (_selectedItem is null)
        {
            return new TagEditSaveResult(false, "投稿が選択されていません。");
        }

        if (!SaveSelectedPostChanges(tags.ToList(), DetailNoteTextBox.Text ?? string.Empty, out var updated, out var errorMessage))
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

        var targetDir = _selectedItem.DirPath;
        if (!DesktopArchiveStore.SavePostChanges(targetDir, tags, note, out errorMessage))
        {
            return false;
        }

        LoadSavedPosts();
        updated = _allItems.FirstOrDefault(x => x.DirPath == targetDir) ?? _selectedItem;
        PostsCardList.SelectedItem = updated;
        return true;
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

    private void DeletePost_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        DeletePost(_selectedItem);
    }

    private void DeletePost(PostListItem target)
    {
        var deleteQuoted = false;

        if (target.HasQuotedPost)
        {
            var choice = MessageBox.Show(
                "この投稿には引用元があります。\n\nはい: 引用元も含めて削除\nいいえ: この投稿だけ削除\nキャンセル: 中止",
                "投稿を削除",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            deleteQuoted = choice == MessageBoxResult.Yes;
        }
        else
        {
            var confirm = MessageBox.Show(
                "この投稿を削除しますか？",
                "投稿を削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var deleteTweetIds = new List<string> { target.TweetId };
        if (deleteQuoted && target.HasQuotedPost)
        {
            deleteTweetIds.Add(target.QuotedTweetId);
        }

        if (!DesktopArchiveStore.DeletePostsByTweetIds(deleteTweetIds, out var errorMessage))
        {
            MessageBox.Show(errorMessage, "投稿の削除に失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadSavedPosts();
        PostsCardList.SelectedItem = null;
        ShowHomeState("左の一覧から投稿を選択してください。");
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
        DetailTagItems.ItemsSource = _editableTags.OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
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

    private void NormalizeTagSelection(SelectionChangedEventArgs e)
    {
        var selectedItems = TagFilterList.SelectedItems.Cast<TagFilterItem>().ToList();
        if (selectedItems.Count == 0)
        {
            return;
        }

        var addedSpecial = e.AddedItems.OfType<TagFilterItem>().LastOrDefault(x => x.IsSpecial);
        if (addedSpecial is not null)
        {
            SetTagFilterSelectionSilently(() =>
            {
                TagFilterList.UnselectAll();
                TagFilterList.SelectedItems.Add(addedSpecial);
            });
            return;
        }

        var selectedRegulars = selectedItems.Where(x => !x.IsSpecial).ToList();
        if (selectedRegulars.Count > 0 && selectedItems.Any(x => x.IsSpecial))
        {
            SetTagFilterSelectionSilently(() =>
            {
                TagFilterList.UnselectAll();
                foreach (var item in selectedRegulars)
                {
                    TagFilterList.SelectedItems.Add(item);
                }
            });
        }
    }

    private void SetTagFilterSelectionSilently(Action action)
    {
        _isUpdatingTagFilterSelection = true;
        try
        {
            action();
        }
        finally
        {
            _isUpdatingTagFilterSelection = false;
        }
    }

    private void SyncSelectedTagsFromUi()
    {
        _selectedTags.Clear();
        _selectedSpecialFilter = null;

        foreach (var item in TagFilterList.SelectedItems.Cast<TagFilterItem>())
        {
            if (item.IsSpecial)
            {
                _selectedSpecialFilter = item.Key;
            }
            else
            {
                _selectedTags.Add(item.Key);
            }
        }
    }

    private void UpdateTagSummary()
    {
        if (string.Equals(_selectedSpecialFilter, "__UNTAGGED__", StringComparison.Ordinal))
        {
            TagSummaryText.Text = "タグなしの投稿を表示中";
            return;
        }

        if (_selectedTags.Count == 0)
        {
            TagSummaryText.Text = "すべての投稿を表示中";
            return;
        }

        TagSummaryText.Text = $"タグ: {string.Join("・", _selectedTags.OrderBy(x => x, StringComparer.CurrentCulture))} を含む";
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
    public List<string> TagList { get; init; } = [];
    public string TweetId { get; init; } = string.Empty;
    public string DirPath { get; init; } = string.Empty;
    public int ImageCount { get; init; }
    public int VideoCount { get; init; }
    public string ThumbnailPath { get; init; } = string.Empty;
    public List<MediaFileItem> MediaFiles { get; init; } = [];
    public string Note { get; init; } = string.Empty;
    public string VideoStatusText { get; init; } = string.Empty;
    public bool HasActiveVideoDownload { get; init; }
    public string QuotedTweetId { get; init; } = string.Empty;
    public string QuotedAuthor { get; init; } = string.Empty;
    public string QuotedText { get; init; } = string.Empty;
    public bool HasQuotedPost => !string.IsNullOrWhiteSpace(QuotedTweetId);
    public string ReferencedByTweetId { get; init; } = string.Empty;
    public int ReferencedByCount { get; init; }
    public bool HasReferencingPost => !string.IsNullOrWhiteSpace(ReferencedByTweetId);
}

public sealed class TagFilterItem
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool IsSpecial { get; init; }
    public string Display => $"{Name} ({Count})";
}

public sealed class MediaFileItem
{
    public string Type { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string DownloadStatus { get; init; } = "completed";
    public string DownloadError { get; init; } = string.Empty;
    public string StatusText => DownloadStatus switch
    {
        "pending" => "動画は保存待ちです",
        "downloading" => "動画をバックグラウンドで保存中です",
        "failed" => string.IsNullOrWhiteSpace(DownloadError) ? "動画保存に失敗しました" : $"動画保存に失敗しました: {DownloadError}",
        _ => "動画保存済み"
    };
}

public sealed class FileImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
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
