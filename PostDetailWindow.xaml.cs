using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XPostArchive.Desktop;

public partial class PostDetailWindow : Window
{
    private static readonly Regex AllowedTagPattern = new(
        @"^[A-Za-z0-9\uFF10-\uFF19\uFF21-\uFF3A\uFF41-\uFF5A\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF ]+$",
        RegexOptions.Compiled);

    private readonly PostListItem _item;
    private readonly List<TagChipItem> _editableTags = new();

    public PostDetailWindow(PostListItem item)
    {
        _item = item;
        InitializeComponent();
        Bind();
    }

    private void Bind()
    {
        TitleText.Text = _item.Author;
        SubTitleText.Text = $"Tweet: {_item.TweetId}  |  Saved: {_item.SavedAt}";
        TweetText.Text = _item.Text;

        _editableTags.Clear();
        foreach (var tag in _item.TagList.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            _editableTags.Add(new TagChipItem { Name = tag.Trim() });
        }
        RefreshTagUi();

        var images = _item.MediaFiles.Where(x => x.Type == "image").ToList();
        if (images.Count == 0)
        {
            ImageList.ItemsSource = new[]
            {
                new MediaFileItem
                {
                    Type = "info",
                    Display = "この投稿に画像はありません。",
                    FullPath = ""
                }
            };
            return;
        }

        ImageList.ItemsSource = images;
    }

    private void NewTagText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ForceJapaneseIme(NewTagText);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        var raw = (NewTagText.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!AllowedTagPattern.IsMatch(raw))
        {
            MessageBox.Show("タグには日本語、英字、数字のみ使用できます。", "タグ入力", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_editableTags.Any(t => string.Equals(t.Name, raw, StringComparison.OrdinalIgnoreCase)))
        {
            NewTagText.Text = "";
            return;
        }

        _editableTags.Add(new TagChipItem { Name = raw });
        NewTagText.Text = "";
        RefreshTagUi();
    }

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        var tagName = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tagName)) return;
        _editableTags.RemoveAll(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
        RefreshTagUi();
    }

    private void SaveTags_Click(object sender, RoutedEventArgs e)
    {
        var tags = _editableTags
            .Select(x => x.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (DesktopArchiveStore.SavePostChanges(_item.DirPath, tags, _item.Note ?? string.Empty, out var errorMessage))
        {
            MessageBox.Show("タグを保存しました。", "タグ保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(errorMessage, "タグ保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RefreshTagUi()
    {
        TagItems.ItemsSource = null;
        TagItems.ItemsSource = _editableTags.OrderBy(x => x.Name).ToList();
        TagText.Text = _editableTags.Count == 0
            ? "タグ: (なし)"
            : $"タグ: {string.Join(", ", _editableTags.Select(x => x.Name))}";
    }

    private void ImageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedImage();
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaFileItem media) return;
        OpenFile(media.FullPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_item.DirPath)) return;
        Process.Start(new ProcessStartInfo { FileName = _item.DirPath, UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSelectedImage()
    {
        if (ImageList.SelectedItem is not MediaFileItem media) return;
        OpenFile(media.FullPath);
    }

    private static void ForceJapaneseIme(TextBox textBox)
    {
        InputMethod.SetIsInputMethodEnabled(textBox, true);
        InputMethod.SetPreferredImeState(textBox, InputMethodState.DoNotCare);
    }

    private static void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}

public sealed class TagChipItem
{
    public string Name { get; init; } = "";
}
