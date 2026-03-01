namespace XPostArchive.Desktop;

public partial class App : System.Windows.Application
{
    internal static LocalApiManager ApiManager { get; } = new();

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        ApiManager.Dispose();
        base.OnExit(e);
    }
}
