param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [ValidateSet('auto','generic','blackwell')] [string] $Architecture = 'auto',
    [string] $DestinationRoot = '',
    [string] $WorkRoot = '',
    [switch] $KeepSource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-LocalAiRoot {
    param([string] $Explicit)
    if ($Explicit) { return [IO.Path]::GetFullPath($Explicit) }
    $config = Join-Path $env:LOCALAPPDATA 'PaRuX\localai-paths.json'
    if (Test-Path $config) {
        try {
            $doc = Get-Content -Raw $config | ConvertFrom-Json
            if ($doc.Root -and (Test-Path $doc.Root)) { return [IO.Path]::GetFullPath([string]$doc.Root) }
            if ($doc.root -and (Test-Path $doc.root)) { return [IO.Path]::GetFullPath([string]$doc.root) }
        } catch { }
    }
    return Join-Path $env:LOCALAPPDATA 'PaRuX\LocalAI'
}

function Assert-Command {
    param([string] $Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is required to build a local llama.cpp runtime."
    }
}

Assert-Command git
Assert-Command cmake

$root = Resolve-LocalAiRoot $DestinationRoot
if (-not $WorkRoot) { $WorkRoot = Join-Path $env:TEMP ('parux-llama-build-' + [Guid]::NewGuid().ToString('N')) }
$source = Join-Path $WorkRoot 'llama.cpp'
$build = Join-Path $WorkRoot 'build'
New-Item -ItemType Directory -Force $WorkRoot | Out-Null

try {
    Write-Host "Cloning llama.cpp $Tag..."
    & git clone --depth 1 --branch $Tag https://github.com/ggml-org/llama.cpp.git $source
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }

    if ($Architecture -eq 'auto') {
        $Architecture = 'generic'
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            try {
                $caps = & nvidia-smi --query-gpu=compute_cap --format=csv,noheader,nounits 2>$null
                if ($LASTEXITCODE -eq 0 -and ($caps | Where-Object { [double]$_ -ge 12.0 })) { $Architecture = 'blackwell' }
            } catch { }
        }
    }

    $runtimeTag = $Tag
    $cmakeArgs = @(
        '-S', $source,
        '-B', $build,
        '-DGGML_CUDA=ON',
        '-DGGML_CUDA_GRAPHS=ON',
        '-DGGML_NATIVE=OFF',
        '-DLLAMA_CURL=ON'
    )

    if ($Architecture -eq 'blackwell') {
        # llama.cpp's current CUDA CMake requires CUDA >= 12.8 for Blackwell and explicitly uses
        # 120a-real so architecture-specific FP4 tensor-core instructions are compiled in.
        $cmakeArgs += '-DCMAKE_CUDA_ARCHITECTURES=120a-real'
        $runtimeTag = "$Tag-blackwell120a"
    }

    if (Get-Command ninja -ErrorAction SilentlyContinue) {
        $cmakeArgs += @('-G','Ninja','-DCMAKE_BUILD_TYPE=Release')
    }

    Write-Host "Configuring $Architecture CUDA runtime..."
    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }

    Write-Host 'Building llama-server + llama-bench...'
    & cmake --build $build --config Release --target llama-server llama-bench --parallel
    if ($LASTEXITCODE -ne 0) { throw 'CMake build failed.' }

    $server = Get-ChildItem -Path $build -Filter 'llama-server.exe' -Recurse | Select-Object -First 1
    $bench = Get-ChildItem -Path $build -Filter 'llama-bench.exe' -Recurse | Select-Object -First 1
    if (-not $server -or -not $bench) { throw 'Build completed but llama-server.exe/llama-bench.exe could not be located.' }

    # cuda12.4 is a legacy PaRuX folder suffix retained for compatibility with existing runtime
    # discovery. The real toolkit/build identity is encoded in runtimeTag and the binary itself.
    $destination = Join-Path (Join-Path $root 'runtime') ("llama.cpp-$runtimeTag-cuda12.4")
    if (Test-Path $destination) { throw "Runtime destination already exists: $destination" }
    New-Item -ItemType Directory -Force $destination | Out-Null

    $binDir = $server.Directory.FullName
    Copy-Item (Join-Path $binDir '*') $destination -Recurse -Force
    if (-not (Test-Path (Join-Path $destination 'llama-server.exe')) -or
        -not (Test-Path (Join-Path $destination 'llama-bench.exe'))) {
        throw 'Staged runtime failed executable validation.'
    }

    $metadata = [ordered]@{
        tag = $runtimeTag
        upstreamTag = $Tag
        architecture = $Architecture
        cudaArchitectures = if ($Architecture -eq 'blackwell') { '120a-real' } else { 'llama.cpp default' }
        builtUtc = [DateTime]::UtcNow.ToString('O')
        source = 'https://github.com/ggml-org/llama.cpp'
        activated = $false
        note = 'Build is intentionally not activated. Use PaRuX Runtime Race to compare it before promotion.'
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $destination 'parux-build.json')

    Write-Host ''
    Write-Host "Built PaRuX-managed runtime: $destination"
    Write-Host 'It is NOT active. Open Runtime Manager > Race installed runtimes to benchmark it against the current build.'
}
finally {
    if (-not $KeepSource -and (Test-Path $WorkRoot)) {
        Remove-Item -Recurse -Force $WorkRoot -ErrorAction SilentlyContinue
    }
}
