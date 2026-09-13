$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
& "$ProjectRoot\.venv\Scripts\python.exe" -m unittest server.test_app server.test_live -v
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$ProjectRoot\tests\EdgeTtsOverlay.Tests\EdgeTtsOverlay.Tests.csproj" --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build "$ProjectRoot\src\EdgeTtsOverlay\EdgeTtsOverlay.csproj" -c Release --no-restore
exit $LASTEXITCODE
