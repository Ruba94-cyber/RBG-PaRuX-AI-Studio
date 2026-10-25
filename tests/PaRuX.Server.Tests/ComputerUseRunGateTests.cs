using System;
using System.Linq;
using System.Threading;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Chat-started and worker-started runs drive the same mouse and keyboard. Before this gate, a second run
/// could start while the first was still clicking, and the two interleaved input into each other's windows.
/// The gate admits one run at a time, and the others are refused before they send any input.
/// </summary>
public sealed class ComputerUseRunGateTests
{
    [Fact]
    public void A_second_run_is_refused_while_the_first_holds_the_gate()
    {
        var gate = new ComputerUseRunGate(new SemaphoreSlim(1, 1));

        using var first = gate.TryEnter();
        var second = gate.TryEnter();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(gate.IsBusy);
    }

    [Fact]
    public void The_gate_is_free_again_once_the_holder_disposes_its_lease()
    {
        var gate = new ComputerUseRunGate(new SemaphoreSlim(1, 1));
        Assert.False(gate.IsBusy);

        var first = gate.TryEnter();
        first!.Dispose();

        Assert.False(gate.IsBusy);
        Assert.NotNull(gate.TryEnter());
    }

    [Fact]
    public void Disposing_a_stale_lease_a_second_time_cannot_free_the_gate_from_a_later_run()
    {
        var gate = new ComputerUseRunGate(new SemaphoreSlim(1, 1));
        var stale = gate.TryEnter();
        stale!.Dispose();

        var later = gate.TryEnter();
        stale.Dispose();

        Assert.NotNull(later);
        Assert.True(gate.IsBusy);
        Assert.Null(gate.TryEnter());
    }

    [Fact]
    public void Of_many_simultaneous_runs_exactly_one_is_admitted()
    {
        var gate = new ComputerUseRunGate(new SemaphoreSlim(1, 1));
        var leases = new ComputerUseRunGate.Lease?[64];

        Parallel.For(0, leases.Length, i => leases[i] = gate.TryEnter());

        Assert.Equal(1, leases.Count(lease => lease != null));
    }
}
