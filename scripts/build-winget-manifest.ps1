[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$MsiPath = "",
    [string]$InstallerUrl = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# A winget manifest is not something you can write ahead of a release: it has to name the exact
# published installer URL, that file's SHA-256, and the MSI's ProductCode, and getting any of the
# three wrong means the package installs the wrong bytes or never upgrades. So the manifest lives
# here as a template and this script fills it in from an MSI that actually exists.

$repoRoot = Split-Path -Parent $PSScriptRoot
$templateDir = Join-Path $repoRoot "packaging\winget"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj") -Raw
    $Version = [string]($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be major.minor.patch. Got '$Version'."
}

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $repoRoot ("artifacts\installer\PaRuX-{0}-win-x64.msi" -f $Version)
}
if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
    throw "No MSI at $MsiPath. Build it first with scripts\build-msi.ps1."
}

if ([string]::IsNullOrWhiteSpace($InstallerUrl)) {
    $InstallerUrl = "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v$Version/PaRuX-$Version-win-x64.msi"
}
# The URL has to be the published one, not a local path: winget downloads it on every install.
if ($InstallerUrl -notmatch '^https://') {
    throw "InstallerUrl must be an https URL to the published MSI. Got '$InstallerUrl'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ("artifacts\winget\manifests\r\RBG\PaRuXAIStudio\{0}" -f $Version)
}

# SHA-256 computed via .NET rather than Get-FileHash, for the same PSModulePath reason documented
# in stage-msi-runtime.ps1: this must not depend on module autoloading being populated.
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $stream = [System.IO.File]::OpenRead($MsiPath)
    try { $hashBytes = $sha256.ComputeHash($stream) } finally { $stream.Dispose() }
} finally { $sha256.Dispose() }
$hash = -join ($hashBytes | ForEach-Object { $_.ToString("X2") })

# winget matches an installed package to its manifest by ProductCode; without the real one an
# upgrade shows up as a second, separate installation.
$productCode = ""
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember(
        "OpenDatabase", "InvokeMethod", $null, $installer, @($MsiPath, 0))
    $view = $database.GetType().InvokeMember(
        "OpenView", "InvokeMethod", $null, $database,
        @("SELECT Value FROM Property WHERE Property = 'ProductCode'"))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -ne $record) {
        $productCode = $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1)
    }
} catch {
    throw @"
Could not read the MSI ProductCode ($($_.Exception.Message)).
That value is required - winget uses it to recognise an existing installation, and a manifest
without the real one makes every upgrade look like a fresh, parallel install.
"@
}
if ([string]::IsNullOrWhiteSpace($productCode)) { throw "The MSI has no ProductCode property." }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$replacements = @{
    "__VERSION__"      = $Version
    "__SHA256__"       = $hash
    "__INSTALLERURL__" = $InstallerUrl
    "__PRODUCTCODE__"  = $productCode
}

$outputs = @{
    "version.yaml.template"      = "RBG.PaRuXAIStudio.yaml"
    "installer.yaml.template"    = "RBG.PaRuXAIStudio.installer.yaml"
    "locale.en-US.yaml.template" = "RBG.PaRuXAIStudio.locale.en-US.yaml"
}

foreach ($template in $outputs.Keys) {
    $source = Join-Path $templateDir $template
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing template: $source" }
    $text = Get-Content -LiteralPath $source -Raw
    foreach ($key in $replacements.Keys) { $text = $text.Replace($key, $replacements[$key]) }
    if ($text -match '__[A-Z]+__') {
        throw "Template $template still contains an unfilled placeholder after substitution."
    }
    $destination = Join-Path $OutputDirectory $outputs[$template]
    # Not Set-Content -Encoding utf8NoBOM: that encoding name is Core-only (PowerShell 7+) and
    # throws a parameter-binding error under Windows PowerShell 5.1, which is still the default
    # `powershell.exe` on every out-of-the-box Windows install a release operator might use.
    # winget's own manifest schema requires UTF-8 without a byte-order mark, so the encoding is
    # written explicitly via .NET rather than trusted to whichever "utf8" a given host resolves to.
    [System.IO.File]::WriteAllText($destination, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $destination"
}

Write-Host ""
Write-Host "Version:      $Version"
Write-Host "MSI:          $MsiPath"
Write-Host "SHA-256:      $hash"
Write-Host "ProductCode:  $productCode"
Write-Host "InstallerUrl: $InstallerUrl"
Write-Host ""
Write-Host "Next: validate, then submit."
Write-Host "  winget validate --manifest `"$OutputDirectory`""
Write-Host "  winget install --manifest `"$OutputDirectory`"   # local install test"
Write-Host "Then open a PR against microsoft/winget-pkgs with these three files under"
Write-Host "  manifests/r/RBG/PaRuXAIStudio/$Version/"
Write-Host "The URL above must already resolve to the published MSI - winget downloads it on install."
