using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaExecutionCoordinatorConcurrentUpgradeTests
{
    [Fact]
    public async Task Cancelled_upgrade_never_restores_a_reader_under_an_active_writer()
    {
        var coordinator = new MediaExecutionCoordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var scope = MediaExecutionCoordinator.EnterContextScope();
        await using var reader = await coordinator.AcquireTextAsync("cancelled-chat", timeout.Token);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(async () =>
        {
            using var independent = MediaExecutionCoordinator.EnterContextScope();
            var pending = coordinator.AcquireAsync("media", timeout.Token);
            queued.SetResult();
            await using var lease = await pending;
            active.SetResult();
            await release.Task.WaitAsync(timeout.Token);
        });
        await queued.Task;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task waiting = coordinator.WaitForMediaAvailabilityAsync(stop.Token);
        await active.Task.WaitAsync(timeout.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.Equal(0, coordinator.Snapshot().ActiveTextReaders);
        reader.Dispose();
        release.SetResult();
        await writer;
        Assert.Equal(0, coordinator.Snapshot().ActiveTextReaders);
    }

    [Fact]
    public async Task Warm_chat_recheck_yields_its_reservation_to_a_queued_writer()
    {
        var coordinator = new MediaExecutionCoordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writerQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = MediaExecutionCoordinator.EnterContextScope();
        await using var reader = await coordinator.AcquireTextAsync("warm-chat", timeout.Token);
        Task writer = Task.Run(async () =>
        {
            using var independentScope = MediaExecutionCoordinator.EnterContextScope();
            var pending = coordinator.AcquireAsync("media", timeout.Token);
            writerQueued.SetResult();
            await using var lease = await pending;
            Assert.Equal(0, coordinator.Snapshot().ActiveTextReaders);
        });
        await writerQueued.Task;
        await coordinator.WaitForMediaAvailabilityAsync(timeout.Token);
        await writer;
        Assert.Equal(1, coordinator.Snapshot().ActiveTextReaders);
    }

    [Fact]
    public async Task Nested_context_restores_parent_reader_ownership()
    {
        var coordinator = new MediaExecutionCoordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var outer = MediaExecutionCoordinator.EnterContextScope();
        await using var reader = await coordinator.AcquireTextAsync("parent", timeout.Token);
        using (MediaExecutionCoordinator.EnterContextScope()) { }
        await using var writer = await coordinator.AcquireAsync("transition", timeout.Token);
        Assert.Equal(0, coordinator.Snapshot().ActiveTextReaders);
    }

    [Fact]
    public async Task Two_independent_chat_requests_can_both_prepare_a_model_without_mutual_deadlock()
    {
        var coordinator = new MediaExecutionCoordinator();
        var bothReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int readers = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        async Task Request(string name)
        {
            using var scope = MediaExecutionCoordinator.EnterContextScope();
            await using var reader = await coordinator.AcquireTextAsync(name, timeout.Token);
            if (Interlocked.Increment(ref readers) == 2) bothReading.TrySetResult();
            await bothReading.Task.WaitAsync(timeout.Token);
            await using var transition = await coordinator.AcquireAsync("text-transition:" + name, timeout.Token);
        }

        Task requests = Task.WhenAll(Task.Run(() => Request("one")), Task.Run(() => Request("two")));
        Exception? error = await Record.ExceptionAsync(async () => await requests);
        Assert.True(error == null,
            "Two HTTP chat readers must not hold each other hostage while both request a model transition. " + error);
        Assert.Equal(0, coordinator.Snapshot().ActiveTextReaders);
        Assert.Null(coordinator.Snapshot().ActiveOwner);
    }
}
