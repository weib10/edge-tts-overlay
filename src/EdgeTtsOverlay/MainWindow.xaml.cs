using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using EdgeTtsOverlay.Models;
using EdgeTtsOverlay.Services;
using Forms = System.Windows.Forms;

namespace EdgeTtsOverlay;

public partial class MainWindow : Window, IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:8766/"), Timeout = TimeSpan.FromSeconds(35) };
    private readonly PythonServer _server = new();
    private readonly PlaybackCoordinator _playback;
    private HotkeyManager? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private bool _expanded;

    public MainWindow()
    {
        InitializeComponent();
        _playback = new(new TtsClient(_http), () => _settings);
        _playback.Changed += () => Dispatcher.Invoke(UpdateUi);
        QueueList.ItemsSource = _playback.Queue;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow(); SetupTray(); SetupHotkeys(); TtsClient.CleanupTempFiles();
        try
        {
            await _server.StartAsync(new TtsClient(_http), CancellationToken.None);
            StatusText.Text = await new TtsClient(_http).DescribeVoiceStatusAsync(_settings.Voice);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        if (_hotkeys?.Errors.Count > 0) StatusText.Text = _hotkeys.Errors[0];
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, -20);
        var uiTest = Environment.GetCommandLineArgs().Contains("--ui-test");
        SetWindowLong(hwnd, -20, style | 0x08000000 | (uiTest ? 0 : 0x00000080)); // WS_EX_NOACTIVATE | TOOLWINDOW
        var source = HwndSource.FromHwnd(hwnd);
        source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        source.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0021) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }); // MA_NOACTIVATE

        // WPF per-pixel alpha owns the whole silhouette. A DWM backdrop/frame
        // would paint a second, differently rounded surface behind this one.

    }

    private void SetupHotkeys()
    {
        _hotkeys = new(this);
        _hotkeys.Register(1, _settings.ToggleHotkey, ShowOverlay);
        _hotkeys.Register(2, _settings.ReplaceHotkey, () => ReadClipboard(false));
        _hotkeys.Register(3, _settings.AppendHotkey, () => ReadClipboard(true));
        _hotkeys.Register(4, _settings.PauseHotkey, _playback.TogglePause);
        _hotkeys.Register(5, _settings.StopHotkey, () => _ = _playback.StopAsync());
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Edge TTS Local", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示", null, (_, _) => ShowOverlay(true));
        menu.Items.Add("設定", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("結束", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowOverlay();
    }

    public void ShowOverlay() => ShowOverlay(false);
    public void ShowOverlay(bool forceShow) => Dispatcher.Invoke(() => { if (IsVisible && !forceShow) Hide(); else { Show(); Topmost = true; } });

    private void ReadClipboard(bool append)
    {
        try { if (System.Windows.Clipboard.ContainsText()) AddText(System.Windows.Clipboard.GetText(), append); else StatusText.Text = "剪貼簿沒有文字"; }
        catch (Exception ex) { StatusText.Text = $"無法讀取剪貼簿：{ex.Message}"; }
    }

    private void AddText(string original, bool append)
    {
        if (original.Length > 20_000) { StatusText.Text = "文字超過 20,000 字元，請縮短後再試"; return; }
        var prepared = TextProcessor.Prepare(original, _settings.ReadingMode, _settings.Pronunciations);
        var segments = TextSegmenter.Split(prepared);
        if (segments.Count == 0) { StatusText.Text = "沒有可朗讀內容"; return; }
        var title = original.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "未命名";
        if (title.Length > 42) title = title[..42] + "…";
        var item = new ReadingItem { Title = title, OriginalText = original, Segments = segments };
        if (append) _playback.Append(item); else _ = _playback.ReplaceAsync(item);
    }

    private void UpdateUi()
    {
        StatusText.Text = _playback.DisplayStatus;
        SentenceText.Text = _playback.CurrentSentence;
        CurrentText.Text = _playback.CurrentSentence;
        var action = _playback.IsPaused ? "繼續朗讀" : "暫停朗讀";
        PauseButton.Content = _playback.IsPaused ? "\uE768" : "\uE769";
        PauseButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, action);
    }

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        DetailsRow.Height = _expanded ? new GridLength(284) : new GridLength(0);
        var work = GetCurrentWorkArea();
        if (_expanded && Top + 372 > work.Bottom) Top = Math.Max(work.Top, work.Bottom - 372 - 16);
        BeginAnimation(HeightProperty, new DoubleAnimation(_expanded ? 372 : 76, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void OnPause(object sender, RoutedEventArgs e) => _playback.TogglePause();
    private async void OnStop(object sender, RoutedEventArgs e) => await _playback.StopAsync();
    private async void OnRetry(object sender, RoutedEventArgs e) => await _playback.RetryAsync();
    private void OnSkip(object sender, RoutedEventArgs e) => _playback.Skip();
    private async void OnClear(object sender, RoutedEventArgs e) => await _playback.ClearAsync();
    private async void OnRemove(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is ReadingItem item) await _playback.RemoveAsync(item); }
    private async void OnPlaySelected(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is ReadingItem item) await _playback.PlayAsync(item); }
    private void OnEdit(object sender, RoutedEventArgs e)
    {
        var dialog = new TextEntryWindow(_settings, AddText) { Owner = this };
        dialog.ShowDialog();
    }
    private void OnQueueEdit(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not ReadingItem item) return;
        new TextEntryWindow(_settings, AddText, item.OriginalText) { Owner = this }.ShowDialog();
    }
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings() { if (_playback.Current is not null && !_playback.IsPaused) _playback.TogglePause(); var dialog = new SettingsWindow(_settings, new TtsClient(_http)) { Owner = this }; if (dialog.ShowDialog() == true) { _settings.Save(); _hotkeys?.Dispose(); SetupHotkeys(); } }
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var current = e.OriginalSource as DependencyObject;
        while (current is not null) { if (current is System.Windows.Controls.Primitives.ButtonBase) return; current = System.Windows.Media.VisualTreeHelper.GetParent(current); }
        DragMove();
    }

    private void PlaceWindow()
    {
        if (_settings.Left is double left && _settings.Top is double top && IsVisibleOnAnyScreen(left, top)) { Left = left; Top = top; }
        else { var area = GetCurrentWorkArea(); Left = area.Right - Width - 24; Top = area.Bottom - Height - 24; }
    }
    private static bool IsVisibleOnAnyScreen(double x, double y) => x >= SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    private bool _allowClose;
    public void Quit() { _allowClose = true; Close(); System.Windows.Application.Current.Shutdown(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); return; }
        _settings.Left = Left; _settings.Top = Top; _settings.Save();
    }
    public void Dispose() { _allowClose = true; _hotkeys?.Dispose(); _tray?.Dispose(); _playback.Dispose(); _server.Dispose(); _http.Dispose(); TtsClient.CleanupTempFiles(); }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private Rect GetCurrentWorkArea()
    {
        var source = PresentationSource.FromVisual(this); var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && source?.CompositionTarget is { } target)
        {
            var a = target.TransformFromDevice.Transform(new System.Windows.Point(info.Work.Left, info.Work.Top)); var b = target.TransformFromDevice.Transform(new System.Windows.Point(info.Work.Right, info.Work.Bottom));
            return new Rect(a, b);
        }
        return SystemParameters.WorkArea;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
