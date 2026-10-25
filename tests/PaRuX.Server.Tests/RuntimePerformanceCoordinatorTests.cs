using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// RuntimePerformanceCoordinator.Shared is a process-wide singleton, so a second, independent
/// (non-reentrant) AcquireAsync must be able to acquire the gate after an earlier lease was
/// disposed - regression coverage for a real deadlock where the first-ever acquire/release cycle
/// in a process never actually released the underlying semaphore, permanently wedging every later
/// caller. See RuntimePerformanceCoordinator's class doc comment for the AsyncLocal-across-await
/// root cause.
/// </summary>
public sealed class RuntimePerformanceCoordinatorTests
{
    [Fact]
    public async Task A_second_independent_acquire_succeeds_after_the_first_lease_is_disposed()
    {
        await using (await RuntimePerformanceCoordinator.Shared.AcquireAsync(CancellationToken.None))
        {
            // First lease held and released via normal `await using` disposal.
        }

        Task secondAcquire = Task.Run(async () =>
        {
            await using IAsyncDisposable lease = await RuntimePerformanceCoordinator.Shared.AcquireAsync(CancellationToken.None);
        });

        Task completed = await Task.WhenAny(secondAcquire, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondAcquire, completed);
        await secondAcquire;
    }

    [Fact]
    public async Task A_reentrant_acquire_within_the_same_call_chain_does_not_block_on_itself()
    {
        await using IAsyncDisposable outer = await RuntimePerformanceCoordinator.Shared.AcquireAsync(CancellationToken.None);

        Task innerAcquire = Task.Run(async () =>
        {
            await using IAsyncDisposable inner = await RuntimePerformanceCoordinator.Shared.AcquireAsync(CancellationToken.None);
        });

        Task completed = await Task.WhenAny(innerAcquire, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(innerAcquire, completed);
        await innerAcquire;
    }
}
