param(
    [string]$ExtensionsRoot = (Join-Path $HOME ".vscode\extensions")
)

$ErrorActionPreference = "Stop"
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$package = Get-Content (Join-Path $source "package.json") -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($package.name) -or [string]::IsNullOrWhiteSpace($package.version)) {
    throw "Invalid PaRuX VS Code extension package.json."
}

$destinationName = "$($package.publisher).$($package.name)-$($package.version)"
$destination = Join-Path $ExtensionsRoot $destinationName
New-Item -ItemType Directory -Force -Path $ExtensionsRoot | Out-Null

if (Test-Path $destination) {
    Remove-Item -Recurse -Force $destination
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$files = @("package.json", "extension.js", "core-client.js", "README.md")
foreach ($file in $files) {
    $path = Join-Path $source $file
    if (-not (Test-Path $path)) { throw "Missing extension file: $file" }
    Copy-Item $path (Join-Path $destination $file) -Force
}

Write-Host "Installed PaRuX Agent VS Code extension to: $destination"
Write-Host "Reload/restart VS Code, then open Explorer > PaRuX Agents."
