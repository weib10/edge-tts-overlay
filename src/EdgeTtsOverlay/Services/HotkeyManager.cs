using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EdgeTtsOverlay.Services;

public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    public List<string> Errors { get; } = [];

    public HotkeyManager(System.Windows.Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(WndProc);
    }

    public void Register(int id, string value, Action action)
    {
        if (!TryParse(value, out var modifiers, out var key) || !RegisterHotKey(_handle, id, modifiers, key))
        { Errors.Add($"快捷鍵 {value} 無法註冊，可能已被其他程式使用。"); return; }
        _actions[id] = action;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && _actions.TryGetValue(wParam.ToInt32(), out var action)) { action(); handled = true; }
        return IntPtr.Zero;
    }

    private static bool TryParse(string value, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts[..^1]) modifiers |= part.ToUpperInvariant() switch
        { "ALT" => 0x0001u, "CTRL" or "CONTROL" => 0x0002u, "SHIFT" => 0x0004u, "WIN" => 0x0008u, _ => 0u };
        var last = parts.LastOrDefault();
        if (last is { Length: 1 }) key = char.ToUpperInvariant(last[0]);
        else if (last?.StartsWith('F') == true && int.TryParse(last[1..], out var f) && f is >= 1 and <= 24) key = 0x6Fu + (uint)f;
        return key != 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_handle, id);
        _source.RemoveHook(WndProc);
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
