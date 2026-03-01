using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace XPostArchive.Desktop;

internal sealed class LocalApiManager : IDisposable
{
    private const string HealthUrl = "http://127.0.0.1:18765/api/v1/health";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _serverProcess;

    public async Task<(bool ok, string message)> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (await IsHealthyAsync(ct))
        {
            return (true, "稼働中");
        }

        var exePath = ResolveServerExePath();
        if (!File.Exists(exePath))
        {
            return (false, $"API実行ファイルが見つかりません: {exePath}");
        }

        try
        {
            _serverProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            return (false, $"API起動に失敗しました: {ex.Message}");
        }

        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            if (await IsHealthyAsync(ct))
            {
                return (true, "稼働中");
            }
        }

        return (false, "API起動後もヘルスチェックに失敗しました。ポート競合や設定を確認してください。");
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(HealthUrl, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // no-op
        }
        finally
        {
            _serverProcess?.Dispose();
        }
    }

    private static string ResolveServerExePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "server", "XPostArchive.Api.exe"),
            Path.Combine(baseDir, "XPostArchive.Api.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
