using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConfirmedActionTests
{
    [Fact]
    public async Task Declined_confirmation_never_performs_the_delete()
    {
        int performed = 0;

        bool ran = await ConfirmedAction.RunIfConfirmedAsync(
            _ => Task.FromResult(false),
            _ =>
            {
                performed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, performed);
    }

    [Fact]
    public async Task Confirmed_action_runs_exactly_once()
    {
        int performed = 0;

        bool ran = await ConfirmedAction.RunIfConfirmedAsync(
            _ => Task.FromResult(true),
            _ =>
            {
                performed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(1, performed);
    }

    [Fact]
    public async Task Confirmation_that_cannot_be_shown_fails_closed()
    {
        int performed = 0;

        bool ran = await ConfirmedAction.RunIfConfirmedAsync(
            _ => throw new InvalidOperationException("no XamlRoot to show the dialog"),
            _ =>
            {
                performed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, performed);
    }

    [Fact]
    public async Task Failure_of_the_action_itself_is_reported_to_the_caller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmedAction.RunIfConfirmedAsync(
            _ => Task.FromResult(true),
            _ => throw new InvalidOperationException("the server refused the delete"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_confirmation_propagates_and_deletes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int performed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmedAction.RunIfConfirmedAsync(
            ct => Task.FromCanceled<bool>(ct),
            _ =>
            {
                performed++;
                return Task.CompletedTask;
            },
            cts.Token));

        Assert.Equal(0, performed);
    }
}
