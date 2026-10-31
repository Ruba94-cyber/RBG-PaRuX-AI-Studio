using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SpeechWarmupCoordinatorTests
{
    [Fact]
    public async Task Failed_attempt_can_be_retried()
    {
        var coordinator = new SpeechWarmupCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(_ => { started.SetResult(); throw new InvalidOperationException(); }, TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(_ => { retried.SetResult(); return Task.CompletedTask; }, TimeSpan.FromMinutes(1));
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_callback_error_is_reported_and_does_not_block_restart()
    {
        var coordinator = new SpeechWarmupCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(async ct =>
        {
            using var registration = ct.Register(() => throw new InvalidOperationException("fixture"));
            started.SetResult();
            await finish.Task;
        }, TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = coordinator.CurrentTask!;
        Assert.IsType<AggregateException>(coordinator.Cancel());
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(_ => { retry.SetResult(); return Task.CompletedTask; }, TimeSpan.FromMinutes(1));
        await retry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        finish.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Invalid_timeout_does_not_block_retry()
    {
        var coordinator = new SpeechWarmupCoordinator();
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Start(_ => Task.CompletedTask, TimeSpan.FromMilliseconds(-2)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(_ => { started.SetResult(); return Task.CompletedTask; }, TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_completion_cannot_reset_or_cancel_new_attempt(bool oldSucceeds)
    {
        var coordinator = new SpeechWarmupCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        coordinator.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            firstStarted.SetResult();
            await firstFinish.Task;
            if (!oldSucceeds) throw new InvalidOperationException();
        }, TimeSpan.FromMinutes(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = coordinator.CurrentTask!;
        coordinator.Cancel();
        coordinator.Start(async ct =>
        {
            Interlocked.Increment(ref calls);
            using var registration = ct.Register(() => secondCancelled.TrySetResult());
            secondStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, TimeSpan.FromMinutes(1));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = coordinator.CurrentTask!;
        firstFinish.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Start(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, TimeSpan.FromMinutes(1));
        Assert.Same(second, coordinator.CurrentTask);
        Assert.Equal(2, calls);
        coordinator.Cancel();
        await secondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
