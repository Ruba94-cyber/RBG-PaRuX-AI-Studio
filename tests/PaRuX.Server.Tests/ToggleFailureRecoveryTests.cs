using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ToggleFailureRecoveryTests
{
    [Fact]
    public async Task Failed_disable_shows_On_when_the_server_still_has_the_fallback_armed()
    {
        // The DELETE failed and the server kept the fallback: the switch must read On, not the clicked Off.
        ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(
            lastKnownServerState: true,
            readServerState: _ => Task.FromResult<bool?>(true),
            CancellationToken.None);

        Assert.True(recovered.Displayed);
        Assert.True(recovered.ServerConfirmed);
    }

    [Fact]
    public async Task Server_state_wins_when_it_differs_from_the_last_known_state()
    {
        ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(
            lastKnownServerState: true,
            readServerState: _ => Task.FromResult<bool?>(false),
            CancellationToken.None);

        Assert.False(recovered.Displayed);
        Assert.True(recovered.ServerConfirmed);
    }

    [Fact]
    public async Task Unreadable_server_keeps_the_last_known_state_and_is_reported_as_unconfirmed()
    {
        ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(
            lastKnownServerState: true,
            readServerState: _ => throw new InvalidOperationException("the core is not answering"),
            CancellationToken.None);

        Assert.True(recovered.Displayed);
        Assert.False(recovered.ServerConfirmed);
    }

    [Fact]
    public async Task Item_no_longer_listed_keeps_the_last_known_state()
    {
        ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(
            lastKnownServerState: false,
            readServerState: _ => Task.FromResult<bool?>(null),
            CancellationToken.None);

        Assert.False(recovered.Displayed);
        Assert.False(recovered.ServerConfirmed);
    }

    [Fact]
    public async Task Cancellation_is_not_mistaken_for_an_unreadable_server()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToggleFailureRecovery.RecoverAsync(
            lastKnownServerState: true,
            readServerState: ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<bool?>(false);
            },
            cts.Token));
    }
}
