using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XPostArchive.Desktop;

public partial class TagEditWindow : Window
{
    private static readonly Regex AllowedTagPattern = new(
        @"^[A-Za-z0-9\uFF10-\uFF19\uFF21-\uFF3A\uFF41-\uFF5A\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF ]+$",
        RegexOptions.Compiled);
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 101, 52));

    private readonly List<string> _editingTags;
    private readonly List<TagCatalogItem> _catalog;
    private readonly Func<IReadOnlyList<string>, TagEditSaveResult> _saveChanges;

    public TagEditWindow(
        List<string> selectedTags,
        List<TagCatalogItem> catalog,
        Func<IReadOnlyList<string>, TagEditSaveResult> saveChanges)
    {
        _editingTags = selectedTags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _catalog = catalog.ToList();
        _saveChanges = saveChanges;

        InitializeComponent();
        Loaded += TagEditWindow_Loaded;
    }

    private void TagEditWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSelectedTags();
        UpdateSuggestions();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseLabel_Click(object sender, MouseButtonEventArgs e) => Close();

    private void ShowAddTag_Click(object sender, RoutedEventArgs e)
    {
        AddTagPanel.Visibility = Visibility.Visible;
        ShowAddTagButton.Visibility = Visibility.Collapsed;
        UpdateSuggestions();
        TagInputTextBox.Focus();
    }

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        var tagName = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        var updated = _editingTags
            .Where(x => !string.Equals(x, tagName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ApplyTagChange(updated, $"#{tagName} を外しました。");
    }

    private void TagInputTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        InputMethod.SetIsInputMethodEnabled(TagInputTextBox, true);
        InputMethod.SetPreferredImeState(TagInputTextBox, InputMethodState.DoNotCare);
    }

    private void TagInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TagInputCountText.Text = $"{(TagInputTextBox.Text ?? string.Empty).Length}/30";
        TagInputPlaceholder.Visibility = string.IsNullOrEmpty(TagInputTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSuggestions();
    }

    private void TagInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var firstCandidate = SuggestionListBox.Items.OfType<SuggestionItem>().FirstOrDefault();
        if (firstCandidate is null)
        {
            return;
        }

        AddTag(firstCandidate.RawTagName ?? firstCandidate.Name);
        e.Handled = true;
    }

    private void AddSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        var tagName = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is SuggestionItem item)
        {
            AddTag(item.RawTagName ?? item.Name);
            return;
        }

        AddTag(tagName);
    }

    private void AddTag(string tagName)
    {
        var normalized = (tagName ?? string.Empty).Trim();
        if (!TryNormalizeTag(normalized, out var tag))
        {
            SetStatus("タグには日本語、英数字、空白のみ使用できます。", isError: true);
            return;
        }

        if (_editingTags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("そのタグはすでに追加されています。", isError: true);
            return;
        }

        var updated = _editingTags
            .Concat(new[] { tag })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ApplyTagChange(updated, $"#{tag} を追加しました。"))
        {
            return;
        }

        TagInputTextBox.Text = string.Empty;
        UpdateSuggestions();
    }

    private bool ApplyTagChange(List<string> updatedTags, string successMessage)
    {
        var result = _saveChanges(updatedTags);
        if (!result.Success)
        {
            SetStatus(result.Message, isError: true);
            return false;
        }

        SyncCatalogCounts(_editingTags, updatedTags);
        _editingTags.Clear();
        _editingTags.AddRange(updatedTags);
        RefreshSelectedTags();
        UpdateSuggestions();
        SetStatus(successMessage, isSuccess: true);
        return true;
    }

    private void SyncCatalogCounts(IEnumerable<string> previousTags, IEnumerable<string> updatedTags)
    {
        var previousSet = previousTags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedSet = updatedTags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removed in previousSet.Except(updatedSet, StringComparer.OrdinalIgnoreCase))
        {
            UpdateCatalogCount(removed, -1);
        }

        foreach (var added in updatedSet.Except(previousSet, StringComparer.OrdinalIgnoreCase))
        {
            UpdateCatalogCount(added, 1);
        }
    }

    private void UpdateCatalogCount(string tagName, int delta)
    {
        var index = _catalog.FindIndex(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            if (delta > 0)
            {
                _catalog.Add(new TagCatalogItem(tagName, delta));
            }

            return;
        }

        var current = _catalog[index];
        var nextCount = Math.Max(0, current.Count + delta);
        _catalog[index] = current with { Count = nextCount };
    }

    private void RefreshSelectedTags()
    {
        SelectedTagsItemsControl.ItemsSource = null;
        SelectedTagsItemsControl.ItemsSource = _editingTags
            .OrderBy(x => x)
            .Select(x => new SelectedTagItem(x))
            .ToList();

        TagCountText.Text = $"{_editingTags.Count}/10";
    }

    private void UpdateSuggestions()
    {
        var query = (TagInputTextBox.Text ?? string.Empty).Trim();
        TagInputCountText.Text = $"{query.Length}/30";

        var suggestions = _catalog
            .Where(x => !_editingTags.Any(current => string.Equals(current, x.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(x => new SuggestionItem(x.Name, x.Count, false))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();

        if (TryNormalizeTag(query, out var newTag) &&
            !_editingTags.Any(x => string.Equals(x, newTag, StringComparison.OrdinalIgnoreCase)) &&
            !suggestions.Any(x => string.Equals(x.Name, newTag, StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Insert(0, new SuggestionItem($"「{newTag}」を新規作成", 0, true, newTag));
        }

        SuggestionListBox.ItemsSource = suggestions;
        SuggestionPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionHeaderText.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool TryNormalizeTag(string raw, out string tag)
    {
        tag = raw.Trim();

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        if (tag.Length > 30)
        {
            return false;
        }

        return AllowedTagPattern.IsMatch(tag);
    }

    private void SetStatus(string text, bool isError = false, bool isSuccess = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? ErrorBrush : isSuccess ? SuccessBrush : NeutralBrush;
    }
}

public sealed record SelectedTagItem(string Name)
{
    public string Display => $"#{Name}";
}

public sealed record SuggestionItem(string Name, int Count, bool IsCreateNew, string? RawTagName = null)
{
    public string ActionLabel => "+";
    public string CountLabel => $"{Count} 件";
}
