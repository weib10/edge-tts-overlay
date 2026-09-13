using System.Text.Json;

namespace EdgeTtsOverlay.Models;

public sealed class AppSettings
{
    public string Voice { get; set; } = "zh-TW-HsiaoChenNeural";
    public int Rate { get; set; }
    public int Volume { get; set; }
    public int Pitch { get; set; }
    public bool ReadingMode { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+T";
    public string ReplaceHotkey { get; set; } = "Ctrl+Alt+R";
    public string AppendHotkey { get; set; } = "Ctrl+Alt+A";
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";
    public string StopHotkey { get; set; } = "Ctrl+Alt+S";
    public Dictionary<string, string> Pronunciations { get; set; } = DefaultPronunciations();
    public double? Left { get; set; }
    public double? Top { get; set; }

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeTtsLocal");
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                loaded.Pronunciations = NormalizePronunciations(loaded.Pronunciations);
                if (loaded.Left is double left && !double.IsFinite(left)) loaded.Left = null;
                if (loaded.Top is double top && !double.IsFinite(top)) loaded.Top = null;
                return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Dictionary<string, string> DefaultPronunciations() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["API"] = "A P I", ["GPU"] = "G P U", ["CPU"] = "C P U", ["JSON"] = "JSON",
        ["CLI"] = "C L I", ["SDK"] = "S D K", ["IDE"] = "I D E", ["UI"] = "U I",
        ["UX"] = "U X", ["HTTP"] = "H T T P", ["HTTPS"] = "H T T P S", ["URL"] = "U R L",
        ["HTML"] = "H T M L", ["CSS"] = "C S S", ["SQL"] = "S Q L"
    };

    public static Dictionary<string, string> NormalizePronunciations(IDictionary<string, string>? source)
    {
        var normalized = source is null
            ? DefaultPronunciations()
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        // Migrate the old built-in spelling that made mixed-language voices say “J, son”.
        // Only this exact legacy value is changed; user-authored pronunciations are preserved.
        if (normalized.TryGetValue("JSON", out var json) && string.Equals(json.Trim(), "J SON", StringComparison.OrdinalIgnoreCase))
            normalized["JSON"] = "JSON";
        return normalized;
    }
}
