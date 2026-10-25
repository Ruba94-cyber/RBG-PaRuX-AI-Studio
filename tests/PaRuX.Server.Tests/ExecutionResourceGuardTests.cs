using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionResourceGuardTests
{
    [Fact]
    public void Known_measurements_below_limits_are_admitted()
    {
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(72.5, new[]
            {
                Gpu(total: 24_576, used: 12_000, temperature: 74)
            }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: true);

        Assert.True(decision.Allowed);
        Assert.True(decision.TelemetryComplete);
        Assert.Equal(72.5, decision.SystemRamUsedPercent);
        Assert.InRange(decision.MaximumVramUsedPercent!.Value, 48.8, 48.9);
        Assert.Equal(74, decision.MaximumGpuTemperatureC);
    }

    [Theory]
    [InlineData(90, 50, 70, "system RAM")]
    [InlineData(50, 95, 70, "GPU VRAM")]
    [InlineData(50, 50, 90, "GPU temperature")]
    public void Measured_limit_violation_is_rejected(
        double ramUsed, int vramUsedPercent, double temperature, string expectedReason)
    {
        const int total = 10_000;
        int used = total * vramUsedPercent / 100;
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(ramUsed, new[] { Gpu(total, used, temperature) }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: true);

        Assert.False(decision.Allowed);
        Assert.Contains(expectedReason, decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pre_transition_thermal_check_does_not_apply_ram_until_replacement_is_loaded()
    {
        ExecutionResourceDecision ramDeferred = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(99, new[] { Gpu(total: 10_000, used: 9_800, temperature: 80) }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: false,
            enforceCurrentSystemRam: false);
        ExecutionResourceDecision hot = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(50, new[] { Gpu(total: 10_000, used: 5_000, temperature: 90) }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: false,
            enforceCurrentSystemRam: false);

        Assert.True(ramDeferred.Allowed);
        Assert.Equal(99, ramDeferred.SystemRamUsedPercent);
        Assert.False(hot.Allowed);
        Assert.Contains("GPU temperature", hot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_rejection_says_what_would_clear_it()
    {
        // Observed on a clean install: image generation was refused with "system RAM 97.5% > 86%"
        // and nothing else. The refusal was correct - a 27B model was resident - but the message
        // left no way to act on it, which reads as a dead end rather than a guardrail.
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(97.5, new[] { Gpu(total: 10_000, used: 5_000, temperature: 60) }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: true);

        Assert.False(decision.Allowed);
        Assert.Contains("system RAM", decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Runtime Manager", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_swap_style_check_does_not_reject_old_model_vram_occupancy()
    {
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(50, new[] { Gpu(total: 10_000, used: 9_800, temperature: 70) }),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: false);

        Assert.True(decision.Allowed);
        Assert.InRange(decision.MaximumVramUsedPercent!.Value, 97.9, 98.1);
    }

    [Fact]
    public void Media_admission_rejects_ram_overage_after_the_media_runtime_is_loaded()
    {
        // The outgoing text model has already been replaced. The current measurement therefore
        // includes the incoming media assistant and ComfyUI, with no speculative RAM credit.
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(
                SystemRamUsedPercent: 91.2,
                Gpus: new[] { Gpu(total: 10_000, used: 9_800, temperature: 70) },
                SystemRamReclaimablePercent: 0),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: false);

        Assert.False(decision.Allowed);
        Assert.Equal(91.2, decision.SystemRamUsedPercent);
        Assert.Contains("system RAM", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_gpu_telemetry_is_explicit_but_does_not_disable_non_nvidia_machines()
    {
        ExecutionResourceDecision decision = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(60, Array.Empty<GpuPerformanceTelemetryDto>()),
            Budget(ram: 86, vram: 92, temperature: 85),
            enforceCurrentVram: true);

        Assert.True(decision.Allowed);
        Assert.False(decision.TelemetryComplete);
        Assert.Contains("GPU telemetry", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Strict_current_vram_guard_waits_for_fresh_gpu_telemetry_before_admitting_training()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<List<GpuPerformanceTelemetryDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetryProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(async ct =>
        {
            started.TrySetResult();
            return await complete.Task.WaitAsync(ct);
        });
        ModelExecutionProfile profile = new()
        {
            Budget = Budget(ram: 98, vram: 92, temperature: 85)
        };

        Task<ExecutionResourceDecision> pending = ExecutionResourceGuard.CheckCurrentAsync(
            profile, enforceCurrentVram: true, CancellationToken.None, gpuTelemetryProvider: telemetryProvider);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        complete.SetResult(new List<GpuPerformanceTelemetryDto>
        {
            Gpu(total: 10_000, used: 9_500, temperature: 70)
        });

        ExecutionResourceDecision decision = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(decision.Allowed);
        Assert.True(decision.TelemetryComplete);
        Assert.Contains("GPU VRAM", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_resource_guard_does_not_wait_for_a_slow_GPU_refresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<List<GpuPerformanceTelemetryDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetryProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(async ct =>
        {
            started.TrySetResult();
            return await finish.Task.WaitAsync(ct);
        });
        ModelExecutionProfile profile = new()
        {
            Budget = Budget(ram: 98, vram: 92, temperature: 85)
        };

        Task<ExecutionResourceDecision> pending = ExecutionResourceGuard.CheckCurrentAsync(
            profile, enforceCurrentVram: false, CancellationToken.None, gpuTelemetryProvider: telemetryProvider);

        bool responseWasReadyBeforeTelemetry = pending.IsCompleted;
        finish.TrySetResult(new List<GpuPerformanceTelemetryDto>());
        Assert.True(responseWasReadyBeforeTelemetry);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Invalid_profile_guardrails_fail_closed()
    {
        ExecutionResourceBudget invalid = Budget(ram: 99, vram: 92, temperature: 85);
        Assert.Throws<InvalidDataException>(() => ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(10, Array.Empty<GpuPerformanceTelemetryDto>()),
            invalid,
            enforceCurrentVram: false));
    }

    private static ExecutionResourceBudget Budget(int ram, int vram, int temperature) => new()
    {
        MaxSystemRamPercent = ram,
        MaxVramPercent = vram,
        MaxGpuTemperatureC = temperature
    };

    private static GpuPerformanceTelemetryDto Gpu(int total, int used, double temperature) => new()
    {
        Index = 0,
        Name = "test-gpu",
        MemoryTotalMib = total,
        MemoryUsedMib = used,
        TemperatureC = temperature
    };
}
