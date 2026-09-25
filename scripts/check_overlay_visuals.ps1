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
$markup = $markup -replace 'x:Class="[^"]+"', '' -replace '\s(?:Click|MouseLeftButtonDown|MouseDoubleClick|SelectionChanged)="[^"]+"', ''
$window = [System.Windows.Markup.XamlReader]::Parse($markup)
if (-not $window.AllowsTransparency) { throw 'FAIL: native window is opaque outside the rounded surface (AllowsTransparency=False).' }

# The window is Gutter DIP larger than the glass on every side; that ring holds the
# shadow and must stay see-through. Keep these in step with MainWindow.xaml.cs.
$width = 436
$gutter = 18
$stride = $width * 4
function Get-Alpha([byte[]]$Pixels, [int]$X, [int]$Y) { return $Pixels[($Y * $width + $X) * 4 + 3] }

foreach ($height in @(112, 446)) {
    $window.Height = $height
    $window.FindName('DetailsRow').Height = [System.Windows.GridLength]::new(([Math]::Max(0, $height - 112)))
    if ($height -gt 112) {
        # Fake rows so the PNG shows the real queue template (title + state + selected chip).
        # Playback would need the online service; the template does not.
        $queue = $window.FindName('QueueList')
        $queue.ItemsSource = @(
            [pscustomobject]@{ Title = 'Release notes 2026-09'; State = 'reading 3/18' },
            [pscustomobject]@{ Title = 'Weekly report draft'; State = 'queued' },
            [pscustomobject]@{ Title = 'Meeting minutes'; State = 'done' })
        $queue.SelectedIndex = 0
        $window.FindName('QueueEmpty').Visibility = [System.Windows.Visibility]::Collapsed
    }
    $surface = $window.Content
    $surface.Measure([System.Windows.Size]::new($width, $height))
    $surface.Arrange([System.Windows.Rect]::new(0, 0, $width, $height))
    $surface.UpdateLayout()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    $pixels = New-Object byte[] ($width * $height * 4)
    $bitmap.CopyPixels($pixels, $stride, 0)

    foreach ($point in @(@(0,0), @(($width-1),0), @(0,($height-1)), @(($width-1),($height-1)))) {
        if ((Get-Alpha $pixels $point[0] $point[1]) -ne 0) { throw 'FAIL: window corner is not transparent.' }
    }
    # Inside the glass: transparent enough to see the desktop, opaque enough to read.
    $surfaceAlpha = Get-Alpha $pixels 218 ($height - $gutter - 8)
    if ($surfaceAlpha -lt 100 -or $surfaceAlpha -gt 195) { throw "FAIL: surface alpha $surfaceAlpha must balance transparency and readability." }
    # Just outside the glass: the shadow is present but stays a shadow. Probe below AND
    # beside the capsule - the shadow is aimed downwards, so the bottom pixel alone would
    # still pass if the halo vanished everywhere else.
    $shadowAlpha = Get-Alpha $pixels 218 ($height - $gutter + 3)
    if ($shadowAlpha -lt 12 -or $shadowAlpha -gt 70) { throw "FAIL: shadow halo alpha $shadowAlpha is missing or too heavy." }
    $sideAlpha = Get-Alpha $pixels ($gutter - 3) ([int]($height / 2))
    if ($sideAlpha -lt 4 -or $sideAlpha -gt 60) { throw "FAIL: side shadow alpha $sideAlpha is missing or too heavy." }

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $output = Join-Path $root "artifacts\overlay-$height.png"
    [System.IO.Directory]::CreateDirectory((Split-Path $output)) | Out-Null
    $stream = [System.IO.File]::Create($output)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Host "PASS: ${height}px overlay; transparent corners, surface alpha=$surfaceAlpha, shadow alpha=$shadowAlpha/$sideAlpha; $output"
}
$window.Close()
$app.Shutdown()
