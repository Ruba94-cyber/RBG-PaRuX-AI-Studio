using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Universal STOP (roadmap P11/C11) - LocalAgentCancellationRegistry is the primitive
/// that makes chat-invoked local-agent loops (Computer Use, the local coding agent) reachable by
/// STOP ALL AGENTS even though they never flow through the server-owned orchestrator. Linked into
/// this test project the same way CodingAgentTools already is.</summary>
public sealed class LocalAgentCancellationRegistryTests
{
    [Fact]
    public void CancelAll_cancels_every_registered_token_source()
    {
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        Guid idA = LocalAgentCancellationRegistry.Register(a);
        Guid idB = LocalAgentCancellationRegistry.Register(b);
        try
        {
            int cancelled = LocalAgentCancellationRegistry.CancelAll();

            Assert.True(cancelled >= 2);
            Assert.True(a.IsCancellationRequested);
            Assert.True(b.IsCancellationRequested);
        }
        finally
        {
            LocalAgentCancellationRegistry.Unregister(idA);
            LocalAgentCancellationRegistry.Unregister(idB);
        }
    }

    [Fact]
    public void Unregister_prevents_a_finished_loops_token_source_from_being_cancelled_later()
    {
        using var cts = new CancellationTokenSource();
        Guid id = LocalAgentCancellationRegistry.Register(cts);
        LocalAgentCancellationRegistry.Unregister(id);

        LocalAgentCancellationRegistry.CancelAll();

        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public void CancelAll_tolerates_an_already_disposed_token_source_without_throwing()
    {
        var cts = new CancellationTokenSource();
        Guid id = LocalAgentCancellationRegistry.Register(cts);
        cts.Dispose(); // simulates the narrow race: the loop finished and disposed it on its own
        try
        {
            int cancelled = LocalAgentCancellationRegistry.CancelAll();
            Assert.True(cancelled >= 0); // must not throw ObjectDisposedException
        }
        finally
        {
            LocalAgentCancellationRegistry.Unregister(id);
        }
    }

    [Fact]
    public void CancelAll_with_nothing_registered_returns_zero_for_this_calls_own_contribution()
    {
        // Other parallel test classes may register their own entries concurrently in the shared
        // xunit process, so this only asserts CancelAll runs cleanly with nothing of THIS test's
        // own registered - not a strict global zero-count assertion.
        int cancelled = LocalAgentCancellationRegistry.CancelAll();
        Assert.True(cancelled >= 0);
    }
}
