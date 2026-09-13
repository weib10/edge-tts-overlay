$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot
$appXml = [xml](Get-Content "$root\src\EdgeTtsOverlay\App.xaml" -Raw -Encoding UTF8)
$resources = $appXml.DocumentElement.FirstChild.InnerXml
$dictionary = [System.Windows.Markup.XamlReader]::Parse('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + $resources + '</ResourceDictionary>')
$app = New-Object System.Windows.Application
$app.Resources = $dictionary
$markup = Get-Content "$root\src\EdgeTtsOverlay\MainWindow.xaml" -Raw -Encoding UTF8
# Load the production visual tree without playback/server event handlers.
$markup = $markup -replace 'x:Class="[^"]+"', '' -replace '\s(?:Click|MouseLeftButtonDown|MouseDoubleClick)="[^"]+"', ''
$window = [System.Windows.Markup.XamlReader]::Parse($markup)
if (-not $window.AllowsTransparency) { throw 'FAIL: native window is opaque outside the rounded surface (AllowsTransparency=False).' }
foreach ($height in @(76, 372)) {
    $window.Height = $height
    $window.FindName('DetailsRow').Height = [System.Windows.GridLength]::new(([Math]::Max(0, $height - 88)))
    $surface = $window.Content
    $surface.Measure([System.Windows.Size]::new(360, $height))
    $surface.Arrange([System.Windows.Rect]::new(0, 0, 360, $height))
    $surface.UpdateLayout()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(360, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    $pixels = New-Object byte[] (360 * $height * 4)
    $bitmap.CopyPixels($pixels, 1440, 0)
    foreach ($point in @(@(0,0), @(359,0), @(0,($height-1)), @(359,($height-1)))) {
        if ($pixels[($point[1]*360+$point[0])*4+3] -ne 0) { throw 'FAIL: corner is not transparent.' }
    }
    $alpha = $pixels[(($height-5)*360+180)*4+3]
    if ($alpha -lt 100 -or $alpha -gt 195) { throw "FAIL: surface alpha $alpha must balance transparency and readability." }
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $output = Join-Path $root "artifacts\overlay-$height.png"
    [System.IO.Directory]::CreateDirectory((Split-Path $output)) | Out-Null
    $stream = [System.IO.File]::Create($output)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Host "PASS: ${height}px overlay; transparent corners, surface alpha=$alpha; $output"
}
$window.Close()
$app.Shutdown()


