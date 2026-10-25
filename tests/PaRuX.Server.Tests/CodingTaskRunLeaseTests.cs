using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingTaskRunLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-task-leases-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [Fact]
    public void Duplicate_executors_are_rejected_without_blocking_independent_tasks()
    {
        var lease = new CodingTaskRunLease(_directory);
        using (lease.Enter("task-a"))
        {
            Assert.True(lease.IsActive("task-a"));
            Assert.Throws<InvalidOperationException>(() => lease.Enter("task-a"));
            using (lease.Enter("task-b"))
                Assert.True(lease.IsActive("task-b"));
            Assert.False(lease.IsActive("task-b"));
        }
        Assert.False(lease.IsActive("task-a"));
        using (lease.Enter("task-a"))
            Assert.True(lease.IsActive("task-a"));
    }

    [Fact]
    public void Scoped_lease_is_returned_even_when_task_throws()
    {
        var lease = new CodingTaskRunLease(_directory);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using IDisposable scope = lease.Enter("task-a");
            throw new InvalidOperationException("task failure");
        }));
        Assert.False(lease.IsActive("task-a"));
    }

    [Fact]
    public void Double_dispose_does_not_unlock_a_new_owner()
    {
        var lease = new CodingTaskRunLease(_directory);
        IDisposable first = lease.Enter("task-a");
        first.Dispose();
        using IDisposable second = lease.Enter("task-a");
        first.Dispose();
        Assert.True(lease.IsActive("task-a"));
    }

    [Fact]
    public void A_second_independent_lease_instance_cannot_open_a_live_OS_task_lock()
    {
        var firstProcess = new CodingTaskRunLease(_directory);
        var anotherProcess = new CodingTaskRunLease(_directory);
        IDisposable owner = firstProcess.Enter("task-under-test");
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => anotherProcess.Enter("task-under-test"));
            Assert.False(anotherProcess.IsActive("task-under-test"));
            using (anotherProcess.Enter("independent-task"))
                Assert.True(anotherProcess.IsActive("independent-task"));
        }
        finally { owner.Dispose(); }

        using (anotherProcess.Enter("task-under-test"))
            Assert.True(anotherProcess.IsActive("task-under-test"));
        // The lock file is retained intentionally, but never the OS lock.
        Assert.Equal(2, Directory.GetFiles(_directory, "*.lock").Length);
    }

    [Fact]
    public void Scope_release_after_exception_leaves_no_process_or_handle_lock()
    {
        var first = new CodingTaskRunLease(_directory);
        var second = new CodingTaskRunLease(_directory);
        Assert.Throws<ApplicationException>((Action)(() =>
        {
            using IDisposable scope = first.Enter("on-exception");
            throw new ApplicationException("simulated crash path");
        }));
        using IDisposable reopened = second.Enter("on-exception");
        Assert.True(second.IsActive("on-exception"));
    }

    [Fact]
    public async Task Parallel_starts_accept_exactly_one_executor_per_session()
    {
        var lease = new CodingTaskRunLease(_directory);
        using IDisposable owner = lease.Enter("same-task");
        Task<bool>[] results = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => lease.TryEnter("same-task"))).ToArray();
        Assert.All(await Task.WhenAll(results), accepted => Assert.False(accepted));
    }
}
