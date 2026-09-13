using System.Threading;
using System.Windows;

namespace EdgeTtsOverlay;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showExisting;
    private EventWaitHandle? _exitExisting;
    private CancellationTokenSource? _watcher;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "EdgeTtsLocal.SingleInstance", out var created);
        if (!created)
        {
            var eventName = e.Args.Contains("--shutdown") ? "EdgeTtsLocal.Exit" : "EdgeTtsLocal.Show";
            try { EventWaitHandle.OpenExisting(eventName).Set(); } catch { }
            Shutdown();
            return;
        }

        if (e.Args.Contains("--shutdown")) { Shutdown(); return; }

        _showExisting = new EventWaitHandle(false, EventResetMode.AutoReset, "EdgeTtsLocal.Show");
        _exitExisting = new EventWaitHandle(false, EventResetMode.AutoReset, "EdgeTtsLocal.Exit");
        _watcher = new CancellationTokenSource();

        _mainWindow = new MainWindow();
        if (e.Args.Contains("--ui-test")) _mainWindow.ShowInTaskbar = true;
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _ = Task.Run(async () =>
        {
            while (!_watcher.IsCancellationRequested)
            {
                var signaled = WaitHandle.WaitAny([_showExisting, _exitExisting], 500);
                if (signaled == 0)
                    await Dispatcher.InvokeAsync(() => _mainWindow.ShowOverlay(true));
                else if (signaled == 1) { await Dispatcher.InvokeAsync(() => _mainWindow.Quit()); break; }
                await Task.Yield();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.Dispose();
        _watcher?.Cancel();
        _showExisting?.Dispose();
        _exitExisting?.Dispose();
        _watcher?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
