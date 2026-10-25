using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaExecutionCoordinatorTests
{
    [Fact]
    public async Task Concurrent_text_readers_share_gpu_without_serializing_each_other()
    {
        var coordinator = new MediaExecutionCoordinator();
        MediaExecutionCoordinator.Lease first = await coordinator.AcquireTextAsync("text-a", CancellationToken.None);
        MediaExecutionCoordinator.Lease second = await coordinator.AcquireTextAsync("text-b", CancellationToken.None);
        try
        {
            MediaExecutionCoordinator.Status status = coordinator.Snapshot();
            Assert.Equal(2, status.ActiveTextReaders);
            Assert.Null(status.ActiveOwner);
            Assert.Equal(0, status.Waiting);
        }
        finally
        {
            second.Dispose();
            first.Dispose();
        }
    }

    [Fact]
    public async Task Waiting_media_writer_blocks_later_reader_but_not_existing_reader_completion()
    {
        var coordinator = new MediaExecutionCoordinator();
        MediaExecutionCoordinator.Lease firstReader = await coordinator
            .AcquireTextAsync("text-existing", CancellationToken.None);

        Task<MediaExecutionCoordinator.Lease> writerTask = coordinator
            .AcquireAsync("media-image", CancellationToken.None).AsTask();
        await WaitUntilAsync(() => coordinator.Snapshot().Waiting == 1);

        Task<MediaExecutionCoordinator.Lease> laterReaderTask = coordinator
            .AcquireTextAsync("text-later", CancellationToken.None).AsTask();
        await Task.Delay(25);
        Assert.False(writerTask.IsCompleted);
        Assert.False(laterReaderTask.IsCompleted);
        Assert.Equal(1, coordinator.Snapshot().ActiveTextReaders);

        firstReader.Dispose();
        MediaExecutionCoordinator.Lease writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            MediaExecutionCoordinator.Status duringWriter = coordinator.Snapshot();
            Assert.Equal("media-image", duringWriter.ActiveOwner);
            Assert.Equal(0, duringWriter.ActiveTextReaders);
            Assert.False(laterReaderTask.IsCompleted);
        }
        finally
        {
            writer.Dispose();
        }

        MediaExecutionCoordinator.Lease laterReader = await laterReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Equal(1, coordinator.Snapshot().ActiveTextReaders);
        }
        finally
        {
            laterReader.Dispose();
        }
    }

    [Fact]
    public async Task Cancelled_waiting_writer_releases_priority_for_new_readers()
    {
        var coordinator = new MediaExecutionCoordinator();
        MediaExecutionCoordinator.Lease existingReader = await coordinator
            .AcquireTextAsync("text-existing", CancellationToken.None);
        using var writerCancellation = new CancellationTokenSource();
        Task<MediaExecutionCoordinator.Lease> writerTask = coordinator
            .AcquireAsync("media-cancelled", writerCancellation.Token).AsTask();
        await WaitUntilAsync(() => coordinator.Snapshot().Waiting == 1);

        Task<MediaExecutionCoordinator.Lease> blockedReader = coordinator
            .AcquireTextAsync("text-blocked", CancellationToken.None).AsTask();
        Assert.False(blockedReader.IsCompleted);

        writerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writerTask);
        await WaitUntilAsync(() => coordinator.Snapshot().Waiting == 0);

        MediaExecutionCoordinator.Lease admittedReader = await blockedReader.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Equal(2, coordinator.Snapshot().ActiveTextReaders);
        }
        finally
        {
            admittedReader.Dispose();
            existingReader.Dispose();
        }
    }

    [Fact]
    public async Task Media_writer_is_exclusive_against_all_text_readers()
    {
        var coordinator = new MediaExecutionCoordinator();
        MediaExecutionCoordinator.Lease writer = await coordinator
            .AcquireAsync("engine-race", CancellationToken.None);
        try
        {
            Task<MediaExecutionCoordinator.Lease> reader = coordinator
                .AcquireTextAsync("text", CancellationToken.None).AsTask();
            await Task.Delay(25);
            Assert.False(reader.IsCompleted);
            Assert.Equal("engine-race", coordinator.Snapshot().ActiveOwner);
            writer.Dispose();

            MediaExecutionCoordinator.Lease admitted = await reader.WaitAsync(TimeSpan.FromSeconds(2));
            admitted.Dispose();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public async Task Context_reader_can_acquire_transition_writer_without_self_deadlock()
    {
        var coordinator = new MediaExecutionCoordinator();
        using (MediaExecutionCoordinator.EnterContextScope())
        {
            MediaExecutionCoordinator.Lease reader = await coordinator
                .AcquireTextAsync("chat:interactive", CancellationToken.None);
            try
            {
                // The reader's own async context needs to perform an exclusive text model transition.
                MediaExecutionCoordinator.Lease transition = await coordinator
                    .AcquireAsync("text-transition:candidate:standard", CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    MediaExecutionCoordinator.Status status = coordinator.Snapshot();
                    Assert.Equal("text-transition:candidate:standard", status.ActiveOwner);
                }
                finally
                {
                    transition.Dispose();
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
    }


    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out waiting for scheduler state transition.");
    }
}
