using System.Windows;
using EdgeTtsOverlay.Models;
using EdgeTtsOverlay.Services;

namespace EdgeTtsOverlay;
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings; private readonly TtsClient _client; private CancellationTokenSource? _previewCts;
    public SettingsWindow(AppSettings settings, TtsClient client)
    {
        InitializeComponent(); _settings = settings; _client = client; SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        RateBox.Text = settings.Rate.ToString(); VolumeBox.Text = settings.Volume.ToString(); PitchBox.Text = settings.Pitch.ToString(); ReadingModeBox.IsChecked = settings.ReadingMode; StartupBox.IsChecked = settings.StartWithWindows;
        ToggleBox.Text = settings.ToggleHotkey; ReplaceBox.Text = settings.ReplaceHotkey; AppendBox.Text = settings.AppendHotkey; PauseBox.Text = settings.PauseHotkey; StopBox.Text = settings.StopHotkey;
        DictionaryBox.Text = string.Join(Environment.NewLine, settings.Pronunciations.Select(x => $"{x.Key}={x.Value}")); Loaded += async (_, _) => { try { VoiceBox.ItemsSource = await _client.GetVoicesAsync(); VoiceBox.SelectedValue = settings.Voice; } catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "無法取得聲音"); } }; Closed += (_, _) => _previewCts?.Cancel();
    }
    private AppSettings? BuildDraft()
    {
        if (!int.TryParse(RateBox.Text, out var rate) || rate is < -50 or > 100 || !int.TryParse(VolumeBox.Text, out var volume) || volume is < -50 or > 50 || !int.TryParse(PitchBox.Text, out var pitch) || pitch is < -50 or > 50) { System.Windows.MessageBox.Show("速度、音量或音高超出範圍。", "設定錯誤"); return null; }
        var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); foreach (var line in DictionaryBox.Text.Split('\n')) { var p = line.IndexOf('='); if (p > 0 && p < line.Length - 1) dict[line[..p].Trim()] = line[(p+1)..].Trim(); }
        return new AppSettings { Voice = VoiceBox.SelectedValue as string ?? _settings.Voice, Rate = rate, Volume = volume, Pitch = pitch,
            ReadingMode = ReadingModeBox.IsChecked == true, StartWithWindows = StartupBox.IsChecked == true,
            ToggleHotkey = ToggleBox.Text, ReplaceHotkey = ReplaceBox.Text, AppendHotkey = AppendBox.Text, PauseHotkey = PauseBox.Text, StopHotkey = StopBox.Text, Pronunciations = dict };
    }
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft(); if (draft is null) return; _previewCts?.Cancel(); _previewCts = new(); var token = _previewCts.Token;
        try { var prepared = TextProcessor.Prepare(TestSentenceBox.Text, draft.ReadingMode, draft.Pronunciations); using var output = new NAudioOutput(); foreach (var segment in TextSegmenter.Split(prepared)) { var path = await _client.SynthesizeAsync(segment, draft, token); try { await output.PlayAsync(path, false, token); } finally { File.Delete(path); } } }
        catch (OperationCanceledException) { } catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "試聽失敗"); }
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft(); if (draft is null) return;
        _settings.Voice=draft.Voice; _settings.Rate=draft.Rate; _settings.Volume=draft.Volume; _settings.Pitch=draft.Pitch; _settings.ReadingMode=draft.ReadingMode; _settings.StartWithWindows=draft.StartWithWindows;
        _settings.ToggleHotkey=draft.ToggleHotkey; _settings.ReplaceHotkey=draft.ReplaceHotkey; _settings.AppendHotkey=draft.AppendHotkey; _settings.PauseHotkey=draft.PauseHotkey; _settings.StopHotkey=draft.StopHotkey; _settings.Pronunciations=draft.Pronunciations;
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("找不到程式組件路徑。");
        if (_settings.StartWithWindows) run.SetValue("EdgeTtsLocal", $"\"{dotnet}\" \"{assembly}\""); else run.DeleteValue("EdgeTtsLocal", false);
        DialogResult = true;
    }
}
