using System.Windows;

namespace XPostArchive.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApiStatusText.Text = "起動中...";
        var result = await App.ApiManager.EnsureStartedAsync();
        ApiStatusText.Text = result.ok ? "稼働中" : $"起動失敗: {result.message}";

        if (!result.ok)
        {
            MessageBox.Show(
                result.message,
                "ローカルAPI起動エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private async void HealthCheck_Click(object sender, RoutedEventArgs e)
    {
        var ok = await App.ApiManager.IsHealthyAsync();
        ApiStatusText.Text = ok ? "稼働中" : "接続失敗";
    }
}
