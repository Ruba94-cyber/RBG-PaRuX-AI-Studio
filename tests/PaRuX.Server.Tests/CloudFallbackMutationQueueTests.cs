using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CloudFallbackMutationQueueTests
{
    [Fact]
    public async Task A_later_disable_runs_after_an_in_flight_save_and_is_final_server_intent()
    {
        var queue = new CloudFallbackMutationQueue();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enabled = false;
        var save = queue.EnqueueAsync(async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            enabled = true;
            return enabled;
        });
        await firstStarted.Task;
        Task<bool> disable = queue.EnqueueAsync(() => Task.FromResult(enabled = false));

        Assert.False(disable.IsCompleted);
        releaseFirst.SetResult();
        await save;
        await disable;

        Assert.False(enabled);
    }

    [Fact]
    public async Task A_later_save_runs_after_an_in_flight_disable_and_is_final_server_intent()
    {
        var queue = new CloudFallbackMutationQueue();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enabled = true;
        var disable = queue.EnqueueAsync(async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            enabled = false;
            return enabled;
        });
        await firstStarted.Task;
        Task<bool> save = queue.EnqueueAsync(() => Task.FromResult(enabled = true));

        Assert.False(save.IsCompleted);
        releaseFirst.SetResult();
        await disable;
        await save;

        Assert.True(enabled);
    }
}
