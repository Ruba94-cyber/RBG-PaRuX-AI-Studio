using System;
using System.Threading.Tasks;

namespace PaRuX.Services;

/// <summary>Runs cloud-fallback mutations in the order UI actions are accepted, so a late earlier request cannot win server state.</summary>
public sealed class CloudFallbackMutationQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> EnqueueAsync<T>(Func<Task<T>> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            previous = _tail;
            _tail = completion.Task;
        }

        return RunAsync();

        async Task<T> RunAsync()
        {
            try
            {
                await previous.ConfigureAwait(false);
                return await mutation().ConfigureAwait(false);
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }
}
