param(
    [ValidateSet('Smoke', 'Regression', 'HardwareE2E')]
    [string]$Suite = 'Smoke',
    [string]$AppPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($Suite -eq 'HardwareE2E') {
    # No test carries the HardwareE2E trait yet, so this lane cannot execute anything. Exiting 0 would report a
    # documented hardware command as passed while running zero tests, so the lane reports NOT_RUN and fails.
    Write-Error 'NOT_RUN: HardwareE2E has no executable tests yet (no test carries Category=HardwareE2E). Run the real-model procedures in PARUX_EXECUTION_PLAN.md manually; this command does not count as a hardware pass.'
    exit 1
}

if ($AppPath) {
    $env:PARUX_UI_APP_PATH = (Resolve-Path -LiteralPath $AppPath).Path
}
else {
    # PaRuX.Server is a sibling payload of the unpackaged desktop app. Testing bin\Debug directly
    # misses that production layout and makes startup failures look like UIA failures.
    $layout = Join-Path $repo ('artifacts\validation\ui-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $appLayout = Join-Path $layout 'app'
    $serverLayout = Join-Path $appLayout 'PaRuX.Server'
    dotnet publish src\PaRuX.App\PaRuX.App.csproj -c Debug -r win-x64 --self-contained true -o $appLayout
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet publish src\PaRuX.Server\PaRuX.Server.csproj -c Debug -r win-x64 --self-contained true -o $serverLayout
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:PARUX_UI_APP_PATH = Join-Path $appLayout 'PaRuX.exe'
}
$filter = if ($Suite -eq 'Smoke') { 'Category=Smoke' } else { 'Category!=HardwareE2E' }
dotnet test tests\PaRuX.UITests\PaRuX.UITests.csproj -c Debug --filter $filter
exit $LASTEXITCODE
