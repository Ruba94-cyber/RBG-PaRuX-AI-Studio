param(
    [string]$BundleRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($BundleRoot)) {
    $BundleRoot = $PSScriptRoot
}
$BundleRoot = [System.IO.Path]::GetFullPath($BundleRoot)
$manifestPath = Join-Path $BundleRoot "MANIFEST.sha256"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "MANIFEST.sha256 is missing from the Full Offline bundle."
}

$expected = @{}
foreach ($line in Get-Content -LiteralPath $manifestPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') {
        throw "Malformed MANIFEST.sha256 line: $line"
    }
    $hash = $Matches[1].ToLowerInvariant()
    $relative = $Matches[2].Replace('/', '\')
    if ([System.IO.Path]::IsPathRooted($relative) -or $relative.Contains('..\')) {
        throw "Unsafe manifest path: $relative"
    }
    $key = $relative.ToLowerInvariant()
    if ($expected.ContainsKey($key)) { throw "Duplicate manifest path: $relative" }
    $expected[$key] = [ordered]@{ Relative = $relative; Hash = $hash }
}
if ($expected.Count -lt 1) { throw "MANIFEST.sha256 is empty." }

$rootPrefix = $BundleRoot.TrimEnd('\') + '\'
foreach ($entry in $expected.Values) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $BundleRoot $entry.Relative))
    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escaped the bundle root: $($entry.Relative)"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Bundle file is missing: $($entry.Relative)"
    }
    $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Hash) {
        throw "SHA-256 mismatch for $($entry.Relative): expected $($entry.Hash), got $actual"
    }
}

# A verification manifest is most useful when it is complete, not merely a list of selected files.
# Reject unexpected files as well so a repacked bundle cannot silently acquire untracked payload.
$actualFiles = @(Get-ChildItem -LiteralPath $BundleRoot -Recurse -File |
    Where-Object { $_.FullName -ne $manifestPath })
foreach ($file in $actualFiles) {
    $full = [System.IO.Path]::GetFullPath($file.FullName)
    if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundle file escaped its root: $full"
    }
    $relative = $full.Substring($rootPrefix.Length)
    if (-not $expected.ContainsKey($relative.ToLowerInvariant())) {
        throw "Unexpected untracked file in Full Offline bundle: $relative"
    }
}
if ($actualFiles.Count -ne $expected.Count) {
    throw "Full Offline bundle inventory count does not match MANIFEST.sha256."
}

Write-Host "PaRuX Full Offline bundle verified successfully."
Write-Host "Files: $($expected.Count)"
