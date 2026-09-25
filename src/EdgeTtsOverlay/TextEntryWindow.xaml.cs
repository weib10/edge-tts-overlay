using System.Windows;
using EdgeTtsOverlay.Models;
using EdgeTtsOverlay.Services;

namespace EdgeTtsOverlay;
public partial class TextEntryWindow : Window
{
    private readonly AppSettings _settings; private readonly Action<string, bool> _submit;
    public TextEntryWindow(AppSettings settings, Action<string, bool> submit, string? initialText = null)
    { InitializeComponent(); SourceInitialized += (_, _) => DarkTitleBar.Apply(this); _settings = settings; _submit = submit; if (initialText is not null) OriginalBox.Text = initialText; else try { if (System.Windows.Clipboard.ContainsText()) OriginalBox.Text = System.Windows.Clipboard.GetText(); } catch { } OriginalBox.Focus(); }
    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    { if (PreviewBox is null) return; PreviewBox.Text = TextProcessor.Prepare(OriginalBox.Text, _settings.ReadingMode, _settings.Pronunciations); CountText.Text = $"{OriginalBox.Text.Length:N0} / 20,000"; }
    private void OnAppend(object sender, RoutedEventArgs e) => Submit(true);
    private void OnReplace(object sender, RoutedEventArgs e) => Submit(false);
    private void Submit(bool append) { if (OriginalBox.Text.Length is 0 or > 20000) { System.Windows.MessageBox.Show("請輸入 1 至 20,000 字元。", "文字長度"); return; } _submit(OriginalBox.Text, append); DialogResult = true; }
}
