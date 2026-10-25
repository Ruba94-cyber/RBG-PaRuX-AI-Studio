using System;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An "Allow once" card authorizes exactly one run. Two defects are pinned here. The card was spent before the run was
/// admitted, so a busy mouse and keyboard left it "already used" with nothing run (CU-09). And nothing tested that the spend
/// is persisted before the executor starts, which is what stops a second run after a double click or a restart (TI-04).
/// </summary>
public sealed class ComputerUseApprovalAdmissionTests
{
    private static ComputerUseRunGate NewGate() => new(new SemaphoreSlim(1, 1));

    private static Task<ComputerUseApprovalAdmission.Refusal> Attempt(
        ComputerUseRunGate gate,
        bool spent,
        string? payload,
        Func<bool> gamingActive,
        Action markSpent,
        Func<ComputerUseRunGate.Lease, Task> execute) =>
        ComputerUseApprovalAdmission.RunOnceAsync(spent, payload, gate.TryEnter, gamingActive, markSpent, execute);

    [Fact]
    public async Task The_approval_is_spent_and_persisted_before_the_executor_starts()
    {
        ComputerUseRunGate gate = NewGate();
        bool spent = false;
        bool spentWhenExecutorStarted = false;

        ComputerUseApprovalAdmission.Refusal refusal = await Attempt(
            gate, false, "goal", () => false,
            () => spent = true,
            _ =>
            {
                spentWhenExecutorStarted = spent;
                return Task.CompletedTask;
            });

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.None, refusal);
        Assert.True(spentWhenExecutorStarted, "A second click or a restart must not be able to replay a run that already started.");
    }

    [Fact]
    public async Task A_spent_card_never_runs_again_when_it_is_clicked_again()
    {
        ComputerUseRunGate gate = NewGate();
        bool spent = false;
        int runs = 0;
        Func<ComputerUseRunGate.Lease, Task> run = _ =>
        {
            runs++;
            return Task.CompletedTask;
        };

        ComputerUseApprovalAdmission.Refusal first = await Attempt(gate, false, "goal", () => false, () => spent = true, run);
        ComputerUseApprovalAdmission.Refusal second = await Attempt(gate, spent, "goal", () => false, () => spent = true, run);

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.None, first);
        Assert.Equal(ComputerUseApprovalAdmission.Refusal.Unavailable, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_card_without_an_action_is_unavailable_and_is_not_spent()
    {
        ComputerUseRunGate gate = NewGate();
        bool spent = false;
        int runs = 0;

        ComputerUseApprovalAdmission.Refusal refusal = await Attempt(
            gate, false, "   ", () => false, () => spent = true, _ =>
            {
                runs++;
                return Task.CompletedTask;
            });

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.Unavailable, refusal);
        Assert.False(spent);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task A_busy_device_refuses_without_spending_the_card_so_it_stays_usable()
    {
        // Another run holds the mouse and keyboard. The card must not be spent, and nothing may run.
        ComputerUseRunGate gate = NewGate();
        using ComputerUseRunGate.Lease? otherRun = gate.TryEnter();
        Assert.NotNull(otherRun);
        bool spent = false;
        int runs = 0;
        Func<ComputerUseRunGate.Lease, Task> run = _ =>
        {
            runs++;
            return Task.CompletedTask;
        };

        ComputerUseApprovalAdmission.Refusal busy = await Attempt(gate, false, "goal", () => false, () => spent = true, run);

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.GateBusy, busy);
        Assert.False(spent);
        Assert.Equal(0, runs);

        otherRun!.Dispose();

        ComputerUseApprovalAdmission.Refusal retry = await Attempt(gate, false, "goal", () => false, () => spent = true, run);

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.None, retry);
        Assert.True(spent);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Gaming_Mode_refuses_without_spending_the_card_and_gives_the_gate_back()
    {
        ComputerUseRunGate gate = NewGate();
        bool spent = false;
        int runs = 0;

        ComputerUseApprovalAdmission.Refusal refusal = await Attempt(
            gate, false, "goal", () => true, () => spent = true, _ =>
            {
                runs++;
                return Task.CompletedTask;
            });

        Assert.Equal(ComputerUseApprovalAdmission.Refusal.GamingActive, refusal);
        Assert.False(spent);
        Assert.Equal(0, runs);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task The_gate_is_held_while_the_run_executes_and_released_afterwards()
    {
        ComputerUseRunGate gate = NewGate();
        bool busyDuringRun = false;

        await Attempt(gate, false, "goal", () => false, () => { }, _ =>
        {
            busyDuringRun = gate.IsBusy;
            return Task.CompletedTask;
        });

        Assert.True(busyDuringRun);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task A_run_that_fails_releases_the_gate_and_keeps_the_card_spent()
    {
        // The run may have started before it failed, so the approval is not handed back.
        ComputerUseRunGate gate = NewGate();
        bool spent = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Attempt(
            gate, false, "goal", () => false, () => spent = true,
            _ => throw new InvalidOperationException("the executor failed")));

        Assert.True(spent);
        Assert.False(gate.IsBusy);
    }
}
