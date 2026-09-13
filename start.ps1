$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
dotnet build "$ProjectRoot\src\EdgeTtsOverlay\EdgeTtsOverlay.csproj" -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$ReleaseDll = "$ProjectRoot\src\EdgeTtsOverlay\bin\Release\net8.0-windows\EdgeTtsOverlay.dll"
$DotnetHost = (Get-Command dotnet -ErrorAction Stop).Source
$StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
$StartInfo.FileName = $DotnetHost
$StartInfo.Arguments = "`"$ReleaseDll`""
$StartInfo.WorkingDirectory = $ProjectRoot
$StartInfo.UseShellExecute = $false
$StartInfo.CreateNoWindow = $true
$OverlayProcess = [System.Diagnostics.Process]::Start($StartInfo)

# A detached GUI launch can otherwise look successful even when Windows blocks the
# newly built assembly. Wait only for the local health endpoint; it does not call
# the remote TTS service.
$Ready = $false
for ($Attempt = 0; $Attempt -lt 30; $Attempt++) {
    Start-Sleep -Milliseconds 250
    if ($OverlayProcess.HasExited) {
        if ($OverlayProcess.ExitCode -eq 0) {
            Write-Host "An existing Edge TTS Overlay instance received the show request."
            exit 0
        }
        Write-Error "Edge TTS Overlay exited during startup (exit code $($OverlayProcess.ExitCode)). Run the DLL in a terminal to see the operating-system error."
        exit $OverlayProcess.ExitCode
    }
    try {
        $Health = Invoke-RestMethod -Uri "http://127.0.0.1:8766/health" -TimeoutSec 1
        if ($Health.status -eq "ok") {
            $Ready = $true
            break
        }
    } catch {
        # The local server normally needs a moment to start.
    }
}
if (-not $Ready) {
    Write-Error "Edge TTS Overlay started, but its local service did not become healthy within 7.5 seconds."
    exit 1
}
Write-Host "Edge TTS Overlay is running (local service healthy)."
