<#
.SYNOPSIS
Parse-only validation of the local agent acceptance scripts.
.DESCRIPTION
Does not execute tests, start apps, access credentials or mutate the PC.
The parser must reject truncated/duplicated runner scripts before they can
claim xUnit or native Windows acceptance. A syntax PASS is NOT test acceptance.
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-validation-syntax.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$targets = @(
    'tools\test-agent-platform-v2.ps1',
    'tools\test-agent-native-ui-e2e.ps1',
    'tools\test-agent-native-modal-e2e.ps1',
    'tools\test-agent-live-computer-use.ps1',
    'tools\test-winapp-functional.ps1',
    'tools\test-winapp-ui.ps1',
    'tools\test-ui.ps1'
)
foreach ($relative in $targets) {
    $path = Join-Path $repo $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ('Required agent validation script is missing: ' + $relative)
    }
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        $first = $parseErrors[0]
        throw ('PowerShell syntax FAIL: ' + $relative + ' line ' +
            $first.Extent.StartLineNumber + ': ' + $first.Message)
    }
}
# Parse-only PowerShell validation cannot catch Add-Type source that the Windows PowerShell/.NET Framework
# compiler rejects. Compile the modal helper type in memory; its P/Invoke methods are declarations only and
# are not called by this gate.
$modalPath = Join-Path $repo 'tools\test-agent-native-modal-e2e.ps1'
$modalText = [IO.File]::ReadAllText($modalPath)
$modalSourceMatch = [regex]::Match($modalText,
    "(?ms)^\s*Add-Type\s+-TypeDefinition\s+@'\r?\n(?<source>.*?)^'@\s+-ErrorAction\s+Stop")
if (-not $modalSourceMatch.Success) {
    throw 'Modal acceptance script does not contain the expected Add-Type source block.'
}
$modalSource = $modalSourceMatch.Groups['source'].Value
$stableNamespace = 'namespace PaRuXTestOnly {'
if (-not $modalSource.Contains($stableNamespace)) {
    throw 'Modal Add-Type source is missing its expected namespace declaration.'
}
$validationNamespace = 'PaRuXTestOnlySyntaxCheck_' + [guid]::NewGuid().ToString('N')
$modalSource = $modalSource.Replace($stableNamespace, ('namespace ' + $validationNamespace + ' {'))
Add-Type -TypeDefinition $modalSource -ErrorAction Stop
# The previous agent runner could be accidentally duplicated in one file
# while keeping balanced syntax. Require exactly one main test declaration.
$runner = [IO.File]::ReadAllText((Join-Path $repo 'tools\test-agent-platform-v2.ps1'))
if ($runner.Split([string[]]@('function Invoke-Gate {'), [StringSplitOptions]::None).Length -ne 2 -or
    $runner.Split([string[]]@('$results = [ordered]@{'), [StringSplitOptions]::None).Length -ne 2 -or
    $runner.Split([string[]]@('[CmdletBinding()]'), [StringSplitOptions]::None).Length -ne 2) {
    throw 'PowerShell agent runner contains duplicated or missing structural blocks.'
}
# Keep the live-agent acceptance script structurally singular even when
# a duplicate tail happens to parse: no false green QA report.
$live = [IO.File]::ReadAllText((Join-Path $repo 'tools\test-agent-live-computer-use.ps1'))
if ($live.Split([string[]]@('[CmdletBinding()]'), [StringSplitOptions]::None).Length -ne 2 -or
    $live.Split([string[]]@('$report.outcome = ''FAIL'''), [StringSplitOptions]::None).Length -ne 2 -or
    $live.Split([string[]]@("if (`$report.outcome -ne 'PASS')"), [StringSplitOptions]::None).Length -ne 2 -or
    $live.Split([string[]]@('Only cancel this test''s own unfinished task'), [StringSplitOptions]::None).Length -ne 2 -or
    -not $live.Contains('Stop-Process -Id $secondaryFixture.Id') -or
    -not $live.Contains('$receipt.finalDistinctWindowsVerified -lt 2') -or
    -not $live.Contains('$secondTreeFinal = Read-BoundedWinAppTree')) {
    throw 'Live Computer Use E2E script is duplicated, truncated, or missing required multi-window checks.'
}
Write-Output 'PASS: all local agent validation scripts parse and the main runner has one entry point.'
Write-Output 'This is a source syntax check, not a Windows, .NET, WinApp or FlaUI functional PASS.'
