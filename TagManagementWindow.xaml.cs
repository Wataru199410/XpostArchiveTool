using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XPostArchive.Desktop;

public partial class TagManagementWindow : Window
{
    private const int MaxTagLength = 30;
    private static readonly Regex AllowedTagPattern = new(
        @"^[A-Za-z0-9\uFF10-\uFF19\uFF21-\uFF3A\uFF41-\uFF5A\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF ]+$",
        RegexOptions.Compiled);
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 101, 52));

    private readonly string _archiveRoot;

    public TagManagementWindow(string archiveRoot)
    {
        _archiveRoot = archiveRoot;
        InitializeComponent();
        Loaded += TagManagementWindow_Loaded;
    }

    private void TagManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ReloadTags();
    }

    private void NewTagTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        InputMethod.SetIsInputMethodEnabled(NewTagTextBox, true);
        InputMethod.SetPreferredImeState(NewTagTextBox, InputMethodState.DoNotCare);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeTagInput(NewTagTextBox.Text, out var tag))
        {
            SetStatus("タグには日本語、英数字、空白のみ使用できます。", isError: true);
            return;
        }

        if (TagCatalogStore.LoadCatalog(_archiveRoot).Any(x => string.Equals(x.Name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("そのタグはすでに存在します。", isError: true);
            return;
        }

        TagCatalogStore.AddTag(tag);
        NewTagTextBox.Text = string.Empty;
        ReloadTags();
        SetStatus("タグを追加しました。", isSuccess: true);
    }

    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        var tagName = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        var result = MessageBox.Show(
            $"タグ「{tagName}」を削除します。関連する投稿からもこのタグを外します。",
            "タグ削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        TagCatalogStore.DeleteTag(tagName, _archiveRoot);
        ReloadTags();
        SetStatus("タグを削除しました。", isSuccess: true);
    }

    private void ReloadTags()
    {
        var items = TagCatalogStore.LoadCatalog(_archiveRoot)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name)
            .Select(x => new TagManagementItem(x.Name, x.Count))
            .ToList();

        TagListBox.ItemsSource = items;
    }

    private void SetStatus(string text, bool isError = false, bool isSuccess = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? ErrorBrush : isSuccess ? SuccessBrush : NeutralBrush;
    }

    private static bool TryNormalizeTagInput(string? raw, out string tag)
    {
        tag = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        if (tag.Length > MaxTagLength)
        {
            return false;
        }

        return AllowedTagPattern.IsMatch(tag);
    }
}

public sealed record TagManagementItem(string Name, int Count)
{
    public string Display => Count == 0 ? "使用中の投稿: 0件" : $"使用中の投稿: {Count}件";
}
