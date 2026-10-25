using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VramResidencyPlannerTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;

    public VramResidencyPlannerTests() => GpuResidencyReservationStore.ClearForTests();

    [Fact]
    public void Required_target_survives_pressure_without_silent_model_substitution()
    {
        DateTime now = new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
        VramResidencyPlanner.Plan plan = VramResidencyPlanner.Build(
            new[]
            {
                new VramResidencyPlanner.Candidate("target", 12 * GiB, true, true, now, 10, "coding"),
                new VramResidencyPlanner.Candidate("fast", 8 * GiB, false, false, now, 100, "coding")
            },
            physicalBytes: 16 * GiB,
            reservedBytes: 8 * GiB,
            activeWorkload: "coding",
            nowUtc: now,
            idleTtl: TimeSpan.FromMinutes(45),
            maxResidents: 3);

        Assert.Contains(plan.Selected, item => item.Id == "target");
        Assert.DoesNotContain(plan.Selected, item => item.Id == "fast");
        Assert.Contains(plan.Decisions, item => item.Id == "target" && item.Reason.Contains("Required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Manual_pin_beats_unpinned_candidate_when_only_one_extra_model_fits()
    {
        DateTime now = new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
        VramResidencyPlanner.Plan plan = VramResidencyPlanner.Build(
            new[]
            {
                new VramResidencyPlanner.Candidate("target", 6 * GiB, true, false, now, 1, "chat"),
                new VramResidencyPlanner.Candidate("pinned", 5 * GiB, false, true, now.AddMinutes(-30), 1, "chat"),
                new VramResidencyPlanner.Candidate("hot", 5 * GiB, false, false, now, 100, "chat")
            },
            physicalBytes: 12 * GiB,
            reservedBytes: 0,
            activeWorkload: "chat",
            nowUtc: now,
            idleTtl: TimeSpan.FromMinutes(45));

        Assert.Contains(plan.Selected, item => item.Id == "pinned");
        Assert.DoesNotContain(plan.Selected, item => item.Id == "hot");
    }

    [Fact]
    public void Expired_idle_candidate_is_evicted_when_not_pinned()
    {
        DateTime now = new(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
        VramResidencyPlanner.Plan plan = VramResidencyPlanner.Build(
            new[]
            {
                new VramResidencyPlanner.Candidate("target", 4 * GiB, true, false, now, 1, "chat"),
                new VramResidencyPlanner.Candidate("old", 2 * GiB, false, false, now.AddHours(-2), 100, "chat"),
                new VramResidencyPlanner.Candidate("recent", 2 * GiB, false, false, now.AddMinutes(-2), 1, "chat")
            },
            physicalBytes: 12 * GiB,
            reservedBytes: 0,
            activeWorkload: "chat",
            nowUtc: now,
            idleTtl: TimeSpan.FromMinutes(45));

        Assert.Contains(plan.Selected, item => item.Id == "recent");
        Assert.Contains(plan.Evicted, item => item.Id == "old" && item.Reason.Contains("idle TTL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reservation_ledger_is_bounded_replaceable_and_releasable()
    {
        GpuResidencyReservationStore.Set("training", "training", 6 * GiB, "LoRA job");
        GpuResidencyReservationStore.Set("media", "media", 4 * GiB, "ComfyUI");
        Assert.Equal(10 * GiB, GpuResidencyReservationStore.GetReservedBytes());

        GpuResidencyReservationStore.Set("training", "training", 2 * GiB, "smaller job");
        Assert.Equal(6 * GiB, GpuResidencyReservationStore.GetReservedBytes());
        Assert.Equal(4 * GiB, GpuResidencyReservationStore.GetReservedBytes("training"));

        GpuResidencyReservationStore.Release("media");
        Assert.Equal(2 * GiB, GpuResidencyReservationStore.GetReservedBytes());
    }

    public void Dispose() => GpuResidencyReservationStore.ClearForTests();
}
