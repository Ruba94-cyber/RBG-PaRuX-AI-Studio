[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentMsi,
    [string]$PreviousMsi = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) { throw "MSI acceptance tests require Windows." }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this acceptance harness from an elevated PowerShell. The PaRuX MSI is per-machine."
}

$CurrentMsi = [IO.Path]::GetFullPath($CurrentMsi)
if (-not (Test-Path -LiteralPath $CurrentMsi -PathType Leaf)) { throw "Current MSI not found: $CurrentMsi" }
if ([IO.Path]::GetExtension($CurrentMsi) -ne ".msi") { throw "CurrentMsi must be an .msi file." }

if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
    $PreviousMsi = [IO.Path]::GetFullPath($PreviousMsi)
    if (-not (Test-Path -LiteralPath $PreviousMsi -PathType Leaf)) { throw "Previous MSI not found: $PreviousMsi" }
    if ([IO.Path]::GetExtension($PreviousMsi) -ne ".msi") { throw "PreviousMsi must be an .msi file." }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$logRoot = Join-Path $repoRoot "artifacts\installer\acceptance"
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$programFiles64 = if (-not [string]::IsNullOrWhiteSpace($env:ProgramW6432)) {
    $env:ProgramW6432
} else {
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
}
$installDir = Join-Path $programFiles64 "PaRuX"
$appExe = Join-Path $installDir "PaRuX.exe"
$serverExe = Join-Path $installDir "PaRuX.Server\PaRuX.Server.exe"
$serverDll = Join-Path $installDir "PaRuX.Server\PaRuX.Server.dll"
$cliExe = Join-Path $installDir "Cli\parux.exe"
$updateHelperExe = Join-Path $installDir "UpdateHelper\PaRuX.UpdateHelper.exe"
$cliPath = Join-Path $installDir "Cli"
$licenseFile = Join-Path $installDir "LICENSE.txt"
$noticesFile = Join-Path $installDir "THIRD_PARTY_NOTICES.md"
$cudaNoticeFile = Join-Path $installDir "NVIDIA_CUDA_NOTICE.txt"
$cudaBaselineDir = Join-Path $installDir "PaRuX.Server\runtime-baseline\llama.cpp-b11871-cuda13.4"
$cudaBaselineFiles = @(
    (Join-Path $cudaBaselineDir "llama-server.exe"),
    (Join-Path $cudaBaselineDir "cudart64_13.dll"),
    (Join-Path $cudaBaselineDir "cublas64_13.dll"),
    (Join-Path $cudaBaselineDir "cublasLt64_13.dll")
)
$startMenuShortcut = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\PaRuX\PaRuX.lnk"
$systemEnvironmentKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"

# The application still uses the legacy LOCALAPPDATA root for backward compatibility. MSI must
# never own or remove it; a future state-root migration is a separate Server migration, not installer cleanup.
$stateTestRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "PaRuX\_msi-acceptance"
$sentinel = Join-Path $stateTestRoot ("sentinel-" + [Guid]::NewGuid().ToString("N") + ".txt")
New-Item -ItemType Directory -Path $stateTestRoot -Force | Out-Null
$sentinelValue = "PaRuX MSI state-preservation sentinel " + [Guid]::NewGuid().ToString("N")
Set-Content -LiteralPath $sentinel -Value $sentinelValue -Encoding UTF8

function Invoke-Msi([string]$Arguments, [string]$Name, [bool]$ExpectSuccess = $true) {
    $log = Join-Path $logRoot ($Name + ".log")
    $fullArguments = $Arguments + " /norestart /l*v `"$log`""
    Write-Host "msiexec $fullArguments"
    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $fullArguments -Wait -PassThru
    if ($ExpectSuccess -and $process.ExitCode -notin @(0, 3010)) {
        throw "$Name failed with MSI exit code $($process.ExitCode). See $log"
    }
    if (-not $ExpectSuccess -and $process.ExitCode -in @(0, 3010)) {
        throw "$Name unexpectedly succeeded; the downgrade guard did not hold. See $log"
    }
    return $process.ExitCode
}

function Assert-Sentinel {
    if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
        throw "MSI operation removed mutable PaRuX LOCALAPPDATA state: sentinel is missing."
    }
    $actual = (Get-Content -LiteralPath $sentinel -Raw).Trim()
    if ($actual -ne $sentinelValue) {
        throw "MSI operation modified mutable PaRuX LOCALAPPDATA state: sentinel content changed."
    }
}

function Get-SystemPathParts {
    $raw = (Get-ItemProperty -LiteralPath $systemEnvironmentKey -Name Path -ErrorAction Stop).Path
    return @($raw -split ';' | ForEach-Object { $_.Trim().TrimEnd('\') } | Where-Object { $_ })
}

function Assert-CliPathInstalled {
    $wanted = $cliPath.TrimEnd('\')
    if (-not (Get-SystemPathParts | Where-Object { $_ -ieq $wanted })) {
        throw "PaRuX CLI folder is not registered in the machine PATH: $cliPath"
    }
}

function Assert-CliPathRemoved {
    $wanted = $cliPath.TrimEnd('\')
    if (Get-SystemPathParts | Where-Object { $_ -ieq $wanted }) {
        throw "PaRuX CLI folder remains in the machine PATH after uninstall: $cliPath"
    }
}

function Assert-Installed([bool]$RequireUpdateHelper = $true) {
    $required = @(
        @{ Path = $appExe; Label = "desktop executable" },
        @{ Path = $serverExe; Label = "Server executable" },
        @{ Path = $serverDll; Label = "Server assembly" },
        @{ Path = $cliExe; Label = "CLI executable" },
        @{ Path = $licenseFile; Label = "license" },
        @{ Path = $noticesFile; Label = "third-party notices" },
        @{ Path = $cudaNoticeFile; Label = "NVIDIA CUDA notice" },
        @{ Path = $startMenuShortcut; Label = "machine Start Menu shortcut" }
    )
    foreach ($path in $cudaBaselineFiles) {
        $required += @{ Path = $path; Label = "bundled CUDA 13.4 baseline payload" }
    }
    if ($RequireUpdateHelper) {
        $required += @{ Path = $updateHelperExe; Label = "protected Update Helper executable" }
    }
    foreach ($item in $required) {
        if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf)) {
            throw "PaRuX $($item.Label) is missing after install: $($item.Path)"
        }
    }
    Assert-CliPathInstalled
    Assert-Sentinel
}

function Assert-Uninstalled {
    $removed = @($appExe, $serverExe, $serverDll, $cliExe, $updateHelperExe, $cudaNoticeFile, $startMenuShortcut) + $cudaBaselineFiles
    foreach ($path in $removed) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            throw "MSI-owned PaRuX file remains after uninstall: $path"
        }
    }
    Assert-CliPathRemoved
    Assert-Sentinel
}

try {
    if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
        Write-Host "=== Install previous MSI ==="
        Invoke-Msi "/i `"$PreviousMsi`" /qn" "01-install-previous" | Out-Null
        # Older accepted MSI baselines may predate the protected Update Helper.
        Assert-Installed $false

        Write-Host "=== Major upgrade to current MSI ==="
        Invoke-Msi "/i `"$CurrentMsi`" /qn" "02-major-upgrade" | Out-Null
        Assert-Installed

        Write-Host "=== Prove downgrade is blocked ==="
        $downgradeCode = Invoke-Msi "/i `"$PreviousMsi`" /qn" "03-downgrade-must-fail" $false
        Write-Host "Downgrade correctly failed with MSI exit code $downgradeCode."
        Assert-Installed
    } else {
        Write-Host "=== Clean install current MSI ==="
        Invoke-Msi "/i `"$CurrentMsi`" /qn" "01-clean-install" | Out-Null
        Assert-Installed
    }

    Write-Host "=== Uninstall current MSI ==="
    Invoke-Msi "/x `"$CurrentMsi`" /qn" "04-uninstall" | Out-Null
    Assert-Uninstalled

    Write-Host ""
    Write-Host "PASS: MSI install/upgrade/downgrade/uninstall boundaries held."
    Write-Host "PASS: PaRuX.exe + PaRuX.Server.exe + parux.exe + protected Update Helper were installed as one product."
    Write-Host "PASS: CLI PATH registration was installed and removed by MSI."
    Write-Host "PASS: mutable state under LOCALAPPDATA\PaRuX survived every MSI operation."
    Write-Host "Logs: $logRoot"
}
finally {
    try { Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue } catch { }
    try {
        if ((Test-Path -LiteralPath $stateTestRoot) -and -not (Get-ChildItem -LiteralPath $stateTestRoot -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $stateTestRoot -Force -ErrorAction SilentlyContinue
        }
    } catch { }
}
