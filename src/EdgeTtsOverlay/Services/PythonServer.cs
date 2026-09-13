using System.Diagnostics;

namespace EdgeTtsOverlay.Services;

public sealed class PythonServer : IDisposable
{
    private Process? _process;
    public string? LastError { get; private set; }

    public async Task StartAsync(TtsClient client, CancellationToken cancellationToken)
    {
        if (await client.IsHealthyAsync(cancellationToken)) return;
        var root = FindProjectRoot();
        if (root is null) throw new InvalidOperationException("找不到 server/app.py，請從專案內啟動。");
        var python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new InvalidOperationException("找不到 .venv，請先執行 setup.ps1。");
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(python, "-m uvicorn server.app:app --host 127.0.0.1 --port 8766")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data; };
        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(150, cancellationToken);
            if (_process.HasExited) throw new InvalidOperationException($"語音服務啟動失敗：{LastError}");
            if (await client.IsHealthyAsync(cancellationToken)) return;
        }
        throw new TimeoutException("語音服務未在期限內啟動。");
    }

    private static string? FindProjectRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "server", "app.py"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_process is { HasExited: false }) { try { _process.Kill(true); _process.WaitForExit(1500); } catch { } }
        _process?.Dispose();
    }
}
