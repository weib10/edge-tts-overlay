using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EdgeTtsOverlay.Models;
using EdgeTtsOverlay.Services;
using Forms = System.Windows.Forms;

namespace EdgeTtsOverlay;

/// <summary>快速換聲音用的顯示項目；ComboBox 綁的是 Label，存回設定的是 Id。</summary>
public sealed record VoiceChoice(string Id, string Label)
{
    // 任何走 ToString 的路徑（樣板沒套到、UI Automation 名稱）都要看到人話，不是 record 的預設字串。
    public override string ToString() => Label;
}

public partial class MainWindow : Window, IDisposable
{
    // 視窗比玻璃大一圈：Gutter 是陰影環的留白，定位與展開的數學都要記得扣掉它。
    private const double Gutter = 18;
    private const double CapsuleCollapsed = 76;
    private const double CapsuleExpanded = 410;
    private const double DetailsHeight = CapsuleExpanded - CapsuleCollapsed;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:8766/"), Timeout = TimeSpan.FromSeconds(35) };
    private readonly PythonServer _server = new();
    private readonly PlaybackCoordinator _playback;
    private IReadOnlyList<VoiceInfo> _voices = [];
    private HotkeyManager? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private bool _expanded;
    private bool _syncingVoices;
    private double? _topBeforeExpand;
    private double _topAfterExpand;

    public MainWindow()
    {
        InitializeComponent();
        _playback = new(new TtsClient(_http), () => _settings);
        _playback.Changed += () => Dispatcher.Invoke(UpdateUi);
        QueueList.ItemsSource = _playback.Queue;
        Details.Opacity = 0;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow(); SetupTray(); SetupHotkeys(); TtsClient.CleanupTempFiles();
        SyncHints(); SyncQuickControls(); UpdateUi();
        try
        {
            var client = new TtsClient(_http);
            await _server.StartAsync(client, CancellationToken.None);
            // 清單取不到只降級狀態列，朗讀本身不靠它；同一份清單也餵給快速換聲音的下拉。
            try { _voices = await client.GetVoicesAsync(); } catch { _voices = []; }
            StatusText.Text = TtsClient.DescribeVoiceStatus(_voices, _settings.Voice);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        // 後端起不來時也要跑：清單是空的就把下拉收起來，不要留一個點了沒反應的空殼。
        PopulateVoices();
        if (_hotkeys?.Errors.Count > 0) StatusText.Text = _hotkeys.Errors[0];
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, -20);
        var uiTest = Environment.GetCommandLineArgs().Contains("--ui-test");
        SetWindowLong(hwnd, -20, style | 0x08000000 | (uiTest ? 0 : 0x00000080)); // WS_EX_NOACTIVATE | TOOLWINDOW
        var source = HwndSource.FromHwnd(hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0021) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }); // MA_NOACTIVATE

        // WPF per-pixel alpha owns the whole silhouette. A DWM backdrop/frame
        // would paint a second, differently rounded surface behind this one，而且
        // 2026-09-14 實測過 SetWindowCompositionAttribute 的 acrylic：layered window
        // 收不到，畫面完全沒變（見 design-system MASTER.md「為什麼沒有真模糊」）。
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
        // 沒有正在讀的東西時，第二行拿去講快捷鍵，比印「等待文字」有用。
        SentenceText.Text = _playback.Current is null ? $"按 {_settings.ReplaceHotkey} 朗讀剪貼簿" : _playback.CurrentSentence;
        CurrentText.Text = _playback.Current is null ? "目前沒有朗讀中的句子" : _playback.CurrentSentence;
        var action = _playback.IsPaused ? "繼續朗讀" : "暫停朗讀";
        PauseButton.Content = _playback.IsPaused ? "\uE768" : "\uE769";
        PauseButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, action);

        var current = _playback.Current;
        var total = current?.Segments.Count ?? 0;
        if (current is not null && total > 0)
        {
            var done = current.State == "已完成" ? total : Math.Min(current.CurrentSegment, total);
            Progress.Maximum = total;
            Progress.Value = done;
            SegmentText.Text = $"{Math.Min(done + 1, total)} / {total} 句";
        }
        else { Progress.Maximum = 1; Progress.Value = 0; SegmentText.Text = "—"; }

        QueueEmpty.Visibility = _playback.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = current is not null;
        UpdateSelectionActions();
    }

    /// <summary>只更新跟選取有關的按鈕。點佇列不要走 UpdateUi——那會把 StatusText 上
    /// 「剪貼簿沒有文字」這類一次性訊息洗成播放狀態。</summary>
    private void UpdateSelectionActions()
    {
        var hasSelection = QueueList.SelectedItem is ReadingItem;
        PlaySelectedButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;
    }

    /// <summary>快捷鍵是設定值，不是常數；空佇列與提示列都直接講目前這一組。</summary>
    private void SyncHints()
    {
        QueueEmpty.Text = $"佇列是空的\n{_settings.ReplaceHotkey} 朗讀剪貼簿 · {_settings.AppendHotkey} 追加";
        HintText.Text = $"{_settings.ReplaceHotkey} 朗讀 · {_settings.AppendHotkey} 追加 · {_settings.PauseHotkey} 暫停 · {_settings.ToggleHotkey} 顯示／隱藏";
    }

    /// <summary>設定寫檔失敗不該讓整篇朗讀跟著死；講出來就好。</summary>
    private void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception ex) { StatusText.Text = $"設定沒存起來：{ex.Message}"; }
    }

    private void SyncQuickControls() => RateText.Text = (1 + _settings.Rate / 100.0).ToString("0.0") + "×";

    private void PopulateVoices()
    {
        // 幾百個聲音塞不進一個下拉，所以只給目前語言的；要跨語言仍然走設定視窗。
        var prefix = _settings.Voice.Split('-')[0];
        var choices = _voices
            .Where(voice => voice.Locale.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(voice => new VoiceChoice(voice.Id, ShortVoiceLabel(voice)))
            .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (choices.All(choice => choice.Id != _settings.Voice) &&
            _voices.FirstOrDefault(voice => voice.Id == _settings.Voice) is { } selected)
            choices.Insert(0, new VoiceChoice(selected.Id, ShortVoiceLabel(selected)));

        _syncingVoices = true;
        VoiceBox.ItemsSource = choices;
        VoiceBox.SelectedValue = _settings.Voice;
        _syncingVoices = false;
        // 清單取不到時留一個空下拉只會讓人一直點，直接讓它退場。
        VoiceBox.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private static string ShortVoiceLabel(VoiceInfo voice)
    {
        var parts = voice.Id.Split('-');
        var name = parts.Length >= 3 ? string.Join('-', parts[2..]) : voice.Id;
        if (name.EndsWith("Neural", StringComparison.Ordinal)) name = name[..^"Neural".Length];
        var locale = parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : voice.Locale;
        var gender = voice.Gender switch { "Female" => " 女", "Male" => " 男", _ => "" };
        return $"{name}（{locale}{gender}）";
    }

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        DetailsRow.Height = new GridLength(_expanded ? DetailsHeight : 0);
        ExpandButton.ToolTip = _expanded ? "收合" : "展開";
        // Name 要固定（驗收腳本靠它找按鈕），狀態講在 HelpText。
        System.Windows.Automation.AutomationProperties.SetHelpText(ExpandButton, _expanded ? "目前是展開的，按一下收合" : "目前是收合的，按一下展開");
        var target = (_expanded ? CapsuleExpanded : CapsuleCollapsed) + Gutter * 2;
        var work = GetCurrentWorkArea();
        if (_expanded)
        {
            // 往下長不下就先把自己拉上來，收合時要還回去；不還的話每展開一次就往上跳一段，
            // 而且 OnClosing 會把跳完的位置當成新家存起來。夾的是玻璃的邊，不是視窗的。
            _topBeforeExpand = Top;
            if (Top + target - Gutter > work.Bottom) Top = Math.Max(work.Top - Gutter, work.Bottom - target + Gutter);
            _topAfterExpand = Top;
        }
        else if (_topBeforeExpand is double previous && Math.Abs(Top - _topAfterExpand) < 0.5)
        {
            Top = previous;
            _topBeforeExpand = null;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(HeightProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_expanded ? 180 : 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        Details.BeginAnimation(OpacityProperty, new DoubleAnimation(_expanded ? 1 : 0, TimeSpan.FromMilliseconds(_expanded ? 200 : 110)) { BeginTime = TimeSpan.FromMilliseconds(_expanded ? 70 : 0) });
    }

    private void OnPause(object sender, RoutedEventArgs e) => _playback.TogglePause();
    private async void OnStop(object sender, RoutedEventArgs e) => await _playback.StopAsync();
    private async void OnRetry(object sender, RoutedEventArgs e) => await _playback.RetryAsync();
    private void OnSkip(object sender, RoutedEventArgs e) => _playback.Skip();
    private async void OnClear(object sender, RoutedEventArgs e) => await _playback.ClearAsync();
    private async void OnRemove(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is ReadingItem item) await _playback.RemoveAsync(item); }
    private async void OnPlaySelected(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is ReadingItem item) await _playback.PlayAsync(item); }
    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private void OnSlower(object sender, RoutedEventArgs e) => AdjustRate(-10);
    private void OnFaster(object sender, RoutedEventArgs e) => AdjustRate(10);

    /// <summary>已經預抓的幾段仍是舊語速，換到新語速要等那幾段放完；這是刻意的，不值得為它中斷朗讀。</summary>
    private void AdjustRate(int delta)
    {
        var rate = Math.Clamp(_settings.Rate + delta, -50, 100);
        if (rate == _settings.Rate) return;
        _settings.Rate = rate; TrySave(); SyncQuickControls();
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVoices || VoiceBox.SelectedValue is not string id || id == _settings.Voice) return;
        _settings.Voice = id; TrySave();
    }

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
    private async void OpenSettings()
    {
        if (_playback.Current is not null && !_playback.IsPaused) _playback.TogglePause();
        var dialog = new SettingsWindow(_settings, new TtsClient(_http)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        TrySave(); _hotkeys?.Dispose(); SetupHotkeys();
        // 啟動時後端沒起來的話 _voices 還是空的；設定視窗剛成功抓過一份，這裡補抓就補得到。
        if (_voices.Count == 0) { try { _voices = await new TtsClient(_http).GetVoicesAsync(); } catch { } }
        SyncHints(); SyncQuickControls(); PopulateVoices(); UpdateUi();
    }
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var current = e.OriginalSource as DependencyObject;
        while (current is not null) { if (current is System.Windows.Controls.Primitives.ButtonBase) return; current = VisualTreeHelper.GetParent(current); }
        DragMove();
        // 拖完就記起來；只在 OnClosing 抄的話，非正常結束會把這次移動吃掉。
        _settings.Left = Left; _settings.Top = Top;
    }

    private void PlaceWindow()
    {
        if (_settings.Left is double left && _settings.Top is double top && IsVisibleOnAnyScreen(left, top)) { Left = left; Top = top; }
        else
        {
            // 24 DIP 是玻璃到工作區邊緣的距離，所以位移要扣掉陰影留白。
            var area = GetCurrentWorkArea();
            Left = area.Right - Width - (24 - Gutter);
            Top = area.Bottom - Height - (24 - Gutter);
        }
        ClampIntoWorkArea();
    }

    /// <summary>
    /// 夾的是玻璃的矩形，不是視窗的（視窗四周各多 Gutter 是透明的）。
    /// 存檔位置可能來自視窗還沒有陰影留白的舊版——那時存的 Left 就是玻璃的左緣，
    /// 直接沿用會讓膠囊右邊 34 DIP（含停止與展開）掉到工作區外面。
    /// </summary>
    private void ClampIntoWorkArea()
    {
        var area = GetCurrentWorkArea();
        var minLeft = area.Left - Gutter;
        var maxLeft = area.Right - Width + Gutter;
        var minTop = area.Top - Gutter;
        var maxTop = area.Bottom - Height + Gutter;
        if (maxLeft >= minLeft) Left = Math.Clamp(Left, minLeft, maxLeft);
        if (maxTop >= minTop) Top = Math.Clamp(Top, minTop, maxTop);
    }
    private static bool IsVisibleOnAnyScreen(double x, double y) => x >= SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    private bool _allowClose;
    public void Quit() { _allowClose = true; Close(); System.Windows.Application.Current.Shutdown(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); return; }
        _settings.Left = Left; _settings.Top = Top; TrySave();
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
