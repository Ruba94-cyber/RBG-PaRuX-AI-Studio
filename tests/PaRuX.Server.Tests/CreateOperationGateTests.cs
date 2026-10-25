using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CreateOperationGateTests
{
    [Fact]
    public void A_second_start_is_refused_while_a_job_runs_and_the_running_job_is_not_cancelled()
    {
        var gate = new CreateOperationGate();
        CreateOperationTicket? first = gate.TryBegin();

        CreateOperationTicket? second = gate.TryBegin();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(gate.IsBusy);
        Assert.False(first!.Token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_reaches_the_job_that_is_running()
    {
        var gate = new CreateOperationGate();
        CreateOperationTicket ticket = gate.TryBegin()!;

        bool requested = gate.CancelActive();

        Assert.True(requested);
        Assert.True(ticket.Token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_with_nothing_running_reports_that_nothing_was_cancelled()
    {
        var gate = new CreateOperationGate();

        Assert.False(gate.CancelActive());
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public void A_stale_ticket_that_finishes_late_does_not_clear_the_running_job()
    {
        var gate = new CreateOperationGate();
        CreateOperationTicket older = gate.TryBegin()!;
        gate.Finish(older);
        CreateOperationTicket running = gate.TryBegin()!;

        gate.Finish(older);

        Assert.True(gate.IsBusy);
        Assert.Null(gate.TryBegin());
        Assert.True(gate.CancelActive());
        Assert.True(running.Token.IsCancellationRequested);
    }

    [Fact]
    public void Finishing_the_running_job_frees_the_gate_for_the_next_one()
    {
        var gate = new CreateOperationGate();
        CreateOperationTicket ticket = gate.TryBegin()!;

        gate.Finish(ticket);
        CreateOperationTicket? next = gate.TryBegin();

        Assert.False(ticket == next);
        Assert.NotNull(next);
        Assert.True(gate.IsBusy);
    }

    [Fact]
    public void Finishing_twice_is_harmless()
    {
        var gate = new CreateOperationGate();
        CreateOperationTicket ticket = gate.TryBegin()!;
        gate.Finish(ticket);

        gate.Finish(ticket);

        Assert.False(gate.IsBusy);
        Assert.False(gate.CancelActive());
    }
}
