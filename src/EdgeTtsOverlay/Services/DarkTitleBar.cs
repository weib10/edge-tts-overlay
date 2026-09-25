using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EdgeTtsOverlay.Services;

/// <summary>
/// 設定與編輯視窗留著系統標題列（要能拖、能關），但內容是深色的。
/// 不叫這支的話 Windows 11 會配一條白色標題列，接在深色內容上面很突兀。
/// </summary>
public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var enabled = 1;
        // 舊版 Windows 10 會回非零錯誤碼；深色標題列本來就是加分項，失敗就算了。
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
