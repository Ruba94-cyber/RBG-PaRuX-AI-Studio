using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserTaskOwnerLeaseTests
{
    [Fact]
    public void Only_one_independent_task_owns_developer_browser_until_shutdown()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.False(lease.TryClaim(null));
        Assert.False(lease.TryClaim(""));
        Assert.True(lease.TryClaim("task-a"));
        Assert.True(lease.TryClaim("task-a"));
        Assert.True(lease.IsOwnedBy("task-a"));
        Assert.False(lease.TryClaim("task-b"));
        Assert.False(lease.BeginRelease("task-b"));
        Assert.True(lease.BeginRelease("task-a"));
        Assert.False(lease.IsOwnedBy("task-a"));
        Assert.False(lease.TryClaim("task-a"));
        Assert.False(lease.TryClaim("task-b"));
        Assert.False(lease.BeginRelease("task-a"));
        lease.CompleteRelease("task-b");
        Assert.False(lease.TryClaim("task-b"));
        lease.CompleteRelease("task-a");
        Assert.True(lease.TryClaim("task-b"));
        Assert.False(lease.IsOwnedBy("task-a"));
    }

    [Fact]
    public void Interrupted_shutdown_fails_closed_instead_of_lending_a_live_profile()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        Assert.True(lease.BeginRelease("task-a"));
        // Simulate a failed process shutdown: CompleteRelease is never called.
        Assert.False(lease.TryClaim("task-b"));
        Assert.False(lease.TryClaim("task-a"));
    }

    [Fact]
    public async Task Concurrent_claims_never_grant_two_owners()
    {
        var lease = new BrowserTaskOwnerLease();
        const int parallel = 30;
        var go = new ManualResetEventSlim(false);
        Task<bool>[] results = Enumerable.Range(0, parallel)
            .Select(i => Task.Run(() => { go.Wait(); return lease.TryClaim("task-" + i); }))
            .ToArray();
        go.Set();
        bool[] claims = await Task.WhenAll(results);
        Assert.Single(claims.Where(claim => claim));
    }

    [Fact]
    public async Task Release_completes_after_a_graceful_shutdown_that_is_proven()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        bool forced = false;

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => Task.CompletedTask,
            _ => { forced = true; return Task.CompletedTask; },
            () => true,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.True(released);
        Assert.False(forced);
        Assert.True(lease.TryClaim("task-b"));
    }

    [Fact]
    public async Task Failed_graceful_shutdown_is_followed_by_a_forced_stop_and_then_completes_when_proven()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        bool forced = false;

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => throw new InvalidOperationException("CDP socket already closed"),
            _ => { forced = true; return Task.CompletedTask; },
            () => true,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.True(released);
        Assert.True(forced);
        Assert.True(lease.TryClaim("task-b"));
    }

    [Fact]
    public async Task Release_that_cannot_be_proven_stopped_keeps_the_lease_locked()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => throw new InvalidOperationException("shutdown failed"),
            _ => throw new InvalidOperationException("kill refused"),
            () => false,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.False(released);
        Assert.False(lease.TryClaim("task-b"));
        Assert.False(lease.TryClaim("task-a"));
    }

    /// <summary>A browser that could not be proven stopped during release must not hold the lease until PaRuX
    /// restarts. Once a later check proves it stopped, the next claim succeeds.</summary>
    [Fact]
    public async Task A_release_proven_on_a_later_check_reopens_the_lease_for_the_next_task()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        bool proven = false;

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => throw new InvalidOperationException("shutdown failed"),
            _ => throw new InvalidOperationException("kill refused"),
            () => proven,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.False(released);
        Assert.False(lease.TryClaim("task-b"));

        proven = true;

        Assert.True(lease.TryClaim("task-b"));
        Assert.True(lease.IsOwnedBy("task-b"));
    }

    /// <summary>A proof check that throws once (for example a transient process-table failure) is treated as not
    /// proven for that attempt only. The next claim re-checks it and succeeds.</summary>
    [Fact]
    public async Task A_proof_that_fails_once_does_not_hold_the_lease_forever()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        int checks = 0;

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => throw new InvalidOperationException("shutdown failed"),
            _ => throw new InvalidOperationException("kill refused"),
            () =>
            {
                checks++;
                if (checks == 1) throw new InvalidOperationException("process table busy");
                return true;
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.False(released);
        Assert.True(lease.TryClaim("task-b"));
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task Hung_graceful_shutdown_is_bounded_and_then_forced()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        bool forced = false;

        bool released = await lease.ReleaseAsync(
            "task-a",
            _ => new TaskCompletionSource().Task, // never completes and ignores cancellation
            _ => { forced = true; return Task.CompletedTask; },
            () => true,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(1));

        Assert.True(released);
        Assert.True(forced);
        Assert.True(lease.TryClaim("task-b"));
    }

    [Fact]
    public async Task Only_the_owner_can_start_a_release()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        bool shutdownCalled = false;

        bool released = await lease.ReleaseAsync(
            "task-b",
            _ => { shutdownCalled = true; return Task.CompletedTask; },
            _ => Task.CompletedTask,
            () => true,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.False(released);
        Assert.False(shutdownCalled);
        Assert.True(lease.IsOwnedBy("task-a"));
    }

    [Fact]
    public async Task New_claims_stay_blocked_while_the_release_is_in_progress()
    {
        var lease = new BrowserTaskOwnerLease();
        Assert.True(lease.TryClaim("task-a"));
        var gate = new TaskCompletionSource();

        Task<bool> release = lease.ReleaseAsync(
            "task-a",
            async _ => await gate.Task,
            _ => Task.CompletedTask,
            () => true,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1));

        Assert.False(lease.TryClaim("task-b"));
        gate.SetResult();
        Assert.True(await release);
        Assert.True(lease.TryClaim("task-b"));
    }
}
