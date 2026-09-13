<#
.SYNOPSIS
    Tier-three acceptance: real screen capture, StatusText readout and real mouse
    expand/collapse against a running overlay.

.DESCRIPTION
    The capture comes from the DWM-composited screen, so it proves transparency and
    layering. The clicks go through real mouse events, so they prove hit-testing and
    no-activate (the foreground window must not change).

    Start the overlay with --ui-test first; normal mode adds TOOLWINDOW and is not
    enumerable:
        dotnet .\src\EdgeTtsOverlay\bin\Release\net8.0-windows\EdgeTtsOverlay.dll --ui-test

    PNGs land in artifacts\. When done, close with --shutdown and reopen via start.ps1.

    This file stays ASCII-only on purpose: Windows PowerShell 5.1 reads unmarked .ps1
    files as ANSI, which corrupts non-ASCII string literals.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$Prefix = "overlay",
    # Capture only; by default the script really clicks expand and then collapse.
    [switch]$NoInteraction
)

$ErrorActionPreference = "Stop"
# StatusText is zh-tw; without this the console swallows it under code page 950.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

Add-Type @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class OverlayCapture {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@ -ReferencedAssemblies System.Drawing

# Per-monitor v2. Without it a secondary monitor's coordinates get rescaled by the
# primary monitor's DPI and the capture lands off-screen (all black).
[void][OverlayCapture]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ExpandButtonName = [char]0x5C55 + [char]0x958B + [char]0x8A73 + [char]0x7D30 + [char]0x5167 + [char]0x5BB9  # "expand details" in zh-tw

function Get-OverlayProcess {
    $candidates = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*EdgeTtsOverlay.dll*" -and $_.CommandLine -notlike "*--shutdown*" })
    if ($candidates.Count -eq 0) { throw "No running overlay found. Start it with --ui-test first." }
    if ($candidates.Count -gt 1) { throw "Multiple overlay processes: $(($candidates.ProcessId) -join ', ')." }
    return $candidates[0].ProcessId
}

function Get-OverlayWindow([int]$OverlayPid) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OverlayPid)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    if (-not $window) { throw "UI Automation cannot see the overlay window. Normal mode sets TOOLWINDOW; use --ui-test." }
    return $window
}

function Get-StatusText($Window) {
    # x:Name in the XAML surfaces as AutomationId; the first Text descendant is a
    # blank-named decoration, so match the id rather than taking whatever comes first.
    $status = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "StatusText")))
    if ($status) { return $status.Current.Name }
    $texts = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)))
    foreach ($text in $texts) {
        if (-not [string]::IsNullOrWhiteSpace($text.Current.Name)) { return $text.Current.Name }
    }
    return ""
}

function Get-OverlayHeight([IntPtr]$Handle) {
    $rect = New-Object OverlayCapture+RECT
    if (-not [OverlayCapture]::GetWindowRect($Handle, [ref]$rect)) { throw "GetWindowRect failed." }
    return ($rect.Bottom - $rect.Top)
}

function Save-OverlayShot([IntPtr]$Handle, [string]$Path, [int]$Padding = 70) {
    $rect = New-Object OverlayCapture+RECT
    if (-not [OverlayCapture]::GetWindowRect($Handle, [ref]$rect)) { throw "GetWindowRect failed." }
    $bitmap = New-Object System.Drawing.Bitmap(
        (($rect.Right - $rect.Left) + $Padding * 2), (($rect.Bottom - $rect.Top) + $Padding * 2))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(($rect.Left - $Padding), ($rect.Top - $Padding), 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Invoke-OverlayClick($Button) {
    $bounds = $Button.Current.BoundingRectangle
    $origin = New-Object System.Drawing.Point
    [void][OverlayCapture]::GetCursorPos([ref]$origin)
    [void][OverlayCapture]::SetCursorPos([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
    Start-Sleep -Milliseconds 200
    [OverlayCapture]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    Start-Sleep -Milliseconds 60
    [OverlayCapture]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
    Start-Sleep -Milliseconds 900
    [void][OverlayCapture]::SetCursorPos($origin.X, $origin.Y)
}

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$overlayPid = Get-OverlayProcess
$handle = (Get-Process -Id $overlayPid).MainWindowHandle
$window = Get-OverlayWindow $overlayPid
Write-Host "overlay pid=$overlayPid hwnd=$handle"
Write-Host "StatusText: $(Get-StatusText $window)"

$collapsedPath = Join-Path $OutputDirectory ($Prefix + "_collapsed.png")
Save-OverlayShot $handle $collapsedPath
$collapsedHeight = Get-OverlayHeight $handle
Write-Host "collapsed height=$collapsedHeight -> $collapsedPath"

if ($NoInteraction) { return }

$expandButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $ExpandButtonName)))
if (-not $expandButton) { throw "Expand button not found." }

$foregroundBefore = [OverlayCapture]::GetForegroundWindow()
Invoke-OverlayClick $expandButton
$foregroundAfter = [OverlayCapture]::GetForegroundWindow()

$expandedPath = Join-Path $OutputDirectory ($Prefix + "_expanded.png")
Save-OverlayShot $handle $expandedPath
$expandedHeight = Get-OverlayHeight $handle
Write-Host "expanded height=$expandedHeight -> $expandedPath"

# Both normal mode and --ui-test carry WS_EX_NOACTIVATE: clicking must not focus us.
if ($foregroundBefore -eq $foregroundAfter -and $foregroundAfter -ne $handle) {
    Write-Host "no-activate PASS (foreground hwnd $foregroundBefore unchanged)"
} else {
    Write-Warning "no-activate FAIL: before=$foregroundBefore after=$foregroundAfter overlay=$handle"
}

Invoke-OverlayClick $expandButton
Write-Host "collapsed again height=$(Get-OverlayHeight $handle)"

if ($expandedHeight -le $collapsedHeight) {
    throw "Expand did not grow the window: collapsed=$collapsedHeight expanded=$expandedHeight"
}
Write-Host "capture complete"
