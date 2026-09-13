$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
if (-not (Test-Path "$ProjectRoot\.venv\Scripts\python.exe")) {
    py -3.12 -m venv "$ProjectRoot\.venv"
}
& "$ProjectRoot\.venv\Scripts\python.exe" -m pip install -r "$ProjectRoot\requirements.txt"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet restore "$ProjectRoot\src\EdgeTtsOverlay\EdgeTtsOverlay.csproj"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet restore "$ProjectRoot\tests\EdgeTtsOverlay.Tests\EdgeTtsOverlay.Tests.csproj"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Setup complete. Run .\start.ps1"
