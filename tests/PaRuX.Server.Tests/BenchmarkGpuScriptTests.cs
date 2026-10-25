using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Runs the pure helpers of scripts/benchmark-gpu.ps1 under PowerShell 7. The helpers are loaded from the script's
/// own syntax tree, so the test exercises the shipped code, not a copy. A hung nvidia-smi and a hung request are
/// stood in for by real sleeping PowerShell processes and jobs.
/// </summary>
public sealed class BenchmarkGpuScriptTests
{
    private const string Harness = @"
param([Parameter(Mandatory)][string]$ScriptPath)
$ErrorActionPreference = 'Stop'
$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw ('parse error: ' + $errors[0].Message) }
$wanted = @('Invoke-NvidiaSmiBounded', 'Resolve-GpuGate', 'Get-RunTeardownPlan', 'Get-GameModePhasePlan', 'Receive-ConcurrencyOutcomes')
$definitions = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and ($wanted -contains $node.Name) }, $true)
foreach ($definition in $definitions) { . ([ScriptBlock]::Create($definition.Extent.Text)) }

$sleeper = (Get-Command powershell.exe).Source
$report = [ordered]@{}

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$report.hungDriver = Invoke-NvidiaSmiBounded -Path $sleeper -Arguments @('-NoProfile', '-Command', 'Set-Content -LiteralPath $env:PARUX_GPU_HUNG_PID_FILE -Value $PID; Start-Sleep -Seconds 60') -TimeoutMilliseconds 5000
$report.hungDriverMs = [int]$clock.ElapsedMilliseconds
$report.hungDriverPid = if (Test-Path -LiteralPath $env:PARUX_GPU_HUNG_PID_FILE) { [int](Get-Content -LiteralPath $env:PARUX_GPU_HUNG_PID_FILE -Raw) } else { $null }
# The child sleeps for a minute, so a child that was not killed at the deadline is still running here, inside the run.
Start-Sleep -Milliseconds 500
$report.hungDriverAliveAfterDeadline = if ($null -ne $report.hungDriverPid) { $null -ne (Get-Process -Id $report.hungDriverPid -ErrorAction SilentlyContinue) } else { $null }
$report.answeringDriver = Invoke-NvidiaSmiBounded -Path $sleeper -Arguments @('-NoProfile', '-Command', 'Write-Output 42') -TimeoutMilliseconds 30000

$report.gatePresentHeavy = Resolve-GpuGate -NvidiaSmiPresent $true -HeavyProfile $true
$report.gatePresentBelowHeavy = Resolve-GpuGate -NvidiaSmiPresent $true -HeavyProfile $false
$report.gateAbsentBelowHeavy = Resolve-GpuGate -NvidiaSmiPresent $false -HeavyProfile $false
$report.gateAbsentReportsHeavy = Resolve-GpuGate -NvidiaSmiPresent $false -HeavyProfile $true

$report.teardownGameStarted = @(Get-RunTeardownPlan -SentGameStart $true -GamingWasActive $false -StartingProfile 'Qwen3.8-27B Standard GPU')
$report.teardownGamingAlreadyOn = @(Get-RunTeardownPlan -SentGameStart $false -GamingWasActive $true -StartingProfile 'Qwen3.8-27B Uncensored GPU')
$report.teardownNothingChanged = @(Get-RunTeardownPlan -SentGameStart $false -GamingWasActive $false -StartingProfile '')

$report.gameModeAlreadyOn = @(Get-GameModePhasePlan -GameAlreadyOn $true)
$report.gameModeOff = @(Get-GameModePhasePlan -GameAlreadyOn $false)

$hung = Start-ThreadJob -ScriptBlock { Start-Sleep -Seconds 120; @{ ok = $true; ms = 120000 } }
$finished = Start-ThreadJob -ScriptBlock { @{ ok = $true; ms = 5 } }
$report.batch = @(Receive-ConcurrencyOutcomes -Jobs @($finished, $hung) -DeadlineSeconds 3)
Remove-Job -Job $hung, $finished -Force -ErrorAction SilentlyContinue

$report | ConvertTo-Json -Depth 8 -Compress
";

    private static string? FindPwsh()
    {
        foreach (string candidate in new[] { "pwsh", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") })
        {
            try
            {
                using Process probe = Process.Start(new ProcessStartInfo(candidate, "-NoProfile -NonInteractive -Command exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();
                if (probe.WaitForExit(20_000) && probe.ExitCode == 0) return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "scripts", "benchmark-gpu.ps1"))) return dir;
        }
        throw new DirectoryNotFoundException("The repository root with scripts/benchmark-gpu.ps1 was not found.");
    }

    [Fact]
    public async Task The_shipped_helpers_bound_a_hung_driver_gate_the_physical_gpu_plan_the_teardown_and_the_batch_deadline()
    {
        string? pwsh = FindPwsh();
        Assert.True(pwsh != null, "PowerShell 7 (pwsh) is required to run the benchmark helper checks.");

        string harnessPath = Path.Combine(Path.GetTempPath(), "parux-gpu-harness-" + Guid.NewGuid().ToString("N") + ".ps1");
        // The hung driver writes its own PID here before it sleeps, so the test can check that its child was killed.
        string hungPidFile = Path.Combine(Path.GetTempPath(), "parux-gpu-hung-" + Guid.NewGuid().ToString("N") + ".pid");
        File.WriteAllText(harnessPath, Harness);
        try
        {
            var start = new ProcessStartInfo(pwsh!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["PARUX_GPU_HUNG_PID_FILE"] = hungPidFile;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(harnessPath);
            start.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "benchmark-gpu.ps1"));

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(170_000), "The helper checks did not finish in time.");
            string output = await stdout;
            string errors = await stderr;
            Assert.True(process.ExitCode == 0, "The helper checks failed: " + errors);

            string json = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement report = document.RootElement;

            // A driver that never answers is abandoned at its deadline and yields no output.
            Assert.Equal(JsonValueKind.Null, report.GetProperty("hungDriver").ValueKind);
            Assert.True(report.GetProperty("hungDriverMs").GetInt32() < 20_000, "The hung driver held the caller past its deadline.");
            // The process it started is killed at the deadline, not left running after its output was abandoned.
            JsonElement hungPid = report.GetProperty("hungDriverPid");
            Assert.True(hungPid.ValueKind == JsonValueKind.Number, "The hung driver never reported its PID, so its child cannot be checked.");
            // Checked inside the run: the harness outlives a child that was never killed, so the exit check below alone passes.
            Assert.Equal(JsonValueKind.False, report.GetProperty("hungDriverAliveAfterDeadline").ValueKind);
            Assert.True(await HasExitedAsync(hungPid.GetInt32(), TimeSpan.FromSeconds(10)),
                "The hung driver's child was left running after its deadline.");
            Assert.Equal("42", report.GetProperty("answeringDriver").GetString()!.Trim());

            // The physical-GPU gate follows nvidia-smi alone; the heavy-profile flag is kept separate.
            Assert.True(report.GetProperty("gatePresentHeavy").GetProperty("PhysicalGpuPresent").GetBoolean());
            Assert.Equal("", report.GetProperty("gatePresentHeavy").GetProperty("Gate").GetString());
            Assert.True(report.GetProperty("gatePresentBelowHeavy").GetProperty("PhysicalGpuPresent").GetBoolean());
            Assert.False(report.GetProperty("gatePresentBelowHeavy").GetProperty("HeavyGpuProfile").GetBoolean());
            Assert.Equal("", report.GetProperty("gatePresentBelowHeavy").GetProperty("Gate").GetString());
            Assert.False(report.GetProperty("gateAbsentBelowHeavy").GetProperty("PhysicalGpuPresent").GetBoolean());
            Assert.Equal("PHYSICAL_GPU_TEST_REQUIRED", report.GetProperty("gateAbsentBelowHeavy").GetProperty("Gate").GetString());
            Assert.Equal("PHYSICAL_GPU_TEST_REQUIRED", report.GetProperty("gateAbsentReportsHeavy").GetProperty("Gate").GetString());

            // A game marker is removed only when this run created it; the profile is restored only when no game was on.
            Assert.Equal(new[] { "GameStop", "Standard" }, Strings(report.GetProperty("teardownGameStarted")));
            Assert.Empty(Strings(report.GetProperty("teardownGamingAlreadyOn")));
            Assert.Empty(Strings(report.GetProperty("teardownNothingChanged")));

            // A game that is already on was started by someone else, so the phase sends neither GameStart nor GameStop.
            Assert.Empty(Strings(report.GetProperty("gameModeAlreadyOn")));
            Assert.Equal(new[] { "GameStart", "GameStop" }, Strings(report.GetProperty("gameModeOff")));

            // One deadline bounds the batch. A request still running at it is a failure, not a missing row.
            JsonElement batch = report.GetProperty("batch");
            Assert.Equal(2, batch.GetArrayLength());
            int succeeded = batch.EnumerateArray().Count(outcome => outcome.GetProperty("ok").GetBoolean());
            Assert.Equal(1, succeeded);
            Assert.Contains(batch.EnumerateArray(), outcome =>
                !outcome.GetProperty("ok").GetBoolean()
                && outcome.GetProperty("error").GetString()!.Contains("deadline", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(harnessPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(hungPidFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    /// <summary>True once the process has exited, or no longer runs under that PID.</summary>
    private static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using Process child = Process.GetProcessById(processId);
                if (child.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }
    }
}
