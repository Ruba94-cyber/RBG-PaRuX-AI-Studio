using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EngineNeutralVramSchedulerTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    [Fact]
    public void Already_resident_variant_is_reused_without_eviction()
    {
        var scheduler = new EngineNeutralVramScheduler();
        var resident = Resident("exllamav3", "qwen-exl3", 0, 10 * GiB, VramWorkloadPriority.Interactive, inUse: true);

        VramSchedulePlan plan = scheduler.Plan(
            new[] { Adapter(0, 24 * GiB) },
            new[] { resident },
            Request("exllamav3", "qwen-exl3", 10 * GiB));

        Assert.Equal(VramScheduleDecision.KeepResident, plan.Decision);
        Assert.Empty(plan.Evict);
        Assert.Single(plan.TargetSlices);
    }

    [Fact]
    public void Interactive_request_evicts_idle_background_model_but_never_active_work()
    {
        var scheduler = new EngineNeutralVramScheduler();
        var idle = Resident("llama", "small", 0, 8 * GiB, VramWorkloadPriority.Background, inUse: false);
        var active = Resident("llama", "active", 0, 5 * GiB, VramWorkloadPriority.Interactive, inUse: true);

        VramSchedulePlan plan = scheduler.Plan(
            new[] { Adapter(0, 24 * GiB) },
            new[] { idle, active },
            Request("exllamav3", "strong", 9 * GiB));

        Assert.Equal(VramScheduleDecision.LoadAfterEviction, plan.Decision);
        Assert.Contains(idle, plan.Evict);
        Assert.DoesNotContain(active, plan.Evict);
    }

    [Fact]
    public void Equal_priority_idle_model_is_not_evicted_to_avoid_thrash()
    {
        var scheduler = new EngineNeutralVramScheduler();
        var resident = Resident("llama", "chat-a", 0, 14 * GiB, VramWorkloadPriority.Interactive, inUse: false);

        VramSchedulePlan plan = scheduler.Plan(
            new[] { Adapter(0, 16 * GiB) },
            new[] { resident },
            Request("exllamav3", "chat-b", 8 * GiB));

        Assert.Equal(VramScheduleDecision.Reject, plan.Decision);
        Assert.Empty(plan.Evict);
    }

    [Fact]
    public void Multi_gpu_is_used_only_when_request_and_engine_allow_it()
    {
        var scheduler = new EngineNeutralVramScheduler();
        VramAdapterCapacity[] adapters = { Adapter(0, 12 * GiB), Adapter(1, 12 * GiB) };

        VramSchedulePlan denied = scheduler.Plan(
            adapters,
            Array.Empty<VramResidentAllocation>(),
            Request("llama", "huge", 16 * GiB) with { AllowMultiGpu = false, EngineSupportsMultiGpu = true });
        VramSchedulePlan allowed = scheduler.Plan(
            adapters,
            Array.Empty<VramResidentAllocation>(),
            Request("llama", "huge", 16 * GiB) with { AllowMultiGpu = true, EngineSupportsMultiGpu = true });

        Assert.Equal(VramScheduleDecision.Reject, denied.Decision);
        Assert.Equal(VramScheduleDecision.Load, allowed.Decision);
        Assert.True(allowed.TargetSlices.Count >= 2);
        Assert.Equal(16 * GiB, allowed.TargetSlices.Sum(slice => slice.Bytes));
    }

    [Fact]
    public void Preferred_adapter_wins_when_both_can_fit()
    {
        var scheduler = new EngineNeutralVramScheduler();

        VramSchedulePlan plan = scheduler.Plan(
            new[] { Adapter(0, 24 * GiB), Adapter(1, 24 * GiB) },
            Array.Empty<VramResidentAllocation>(),
            Request("llama", "model", 8 * GiB) with { PreferredAdapterIndices = new[] { 1, 0 } });

        Assert.Equal(VramScheduleDecision.Load, plan.Decision);
        Assert.Single(plan.TargetSlices);
        Assert.Equal(1, plan.TargetSlices[0].AdapterIndex);
    }

    [Fact]
    public void Dedicated_headroom_is_always_preserved()
    {
        long usable = EngineNeutralVramScheduler.UsableBytes(16 * GiB);

        Assert.True(usable < 16 * GiB);
        Assert.True(usable <= (long)(16 * GiB * 0.90));
    }

    private static VramAdapterCapacity Adapter(int index, long bytes) =>
        new(index, "GPU" + index, bytes);

    private static VramResidentAllocation Resident(
        string engine,
        string variant,
        int adapter,
        long bytes,
        VramWorkloadPriority priority,
        bool inUse) =>
        new(
            engine,
            variant,
            new[] { new VramAllocationSlice(adapter, bytes) },
            priority,
            inUse,
            Pinned: false,
            LastUsedUtc: DateTime.UtcNow.AddMinutes(-10));

    private static VramLoadRequest Request(string engine, string variant, long bytes) =>
        new(
            engine,
            variant,
            bytes,
            VramWorkloadPriority.Interactive,
            AllowMultiGpu: false,
            EngineSupportsMultiGpu: false);
}
