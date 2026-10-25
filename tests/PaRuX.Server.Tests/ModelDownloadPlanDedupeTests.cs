using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadPlanDedupeTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Concurrent_plan_starts_reuse_in_flight_jobs_and_completed_restart_adds_no_jobs()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["split-Q4_K_M-00001-of-00002.gguf"] = first,
            ["split-Q4_K_M-00002-of-00002.gguf"] = second
        };
        string listing = Listing(new (string Path, byte[] Body)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", first),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", second)
        });
        var releaseTransfers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PlanTransferHandler(listing, bodies, releaseTransfers.Task);
        using var client = new HttpClient(handler);
        var service = new ModelDownloadService(client, client);

        IReadOnlyList<ModelDownloadStatus>[]? starts = null;
        try
        {
            starts = await Task.WhenAll(
                service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision),
                service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision))
                .WaitAsync(TimeSpan.FromSeconds(10));

            string[] firstIds = starts[0].Select(status => status.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            string[] secondIds = starts[1].Select(status => status.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, firstIds.Length);
            Assert.Equal(firstIds, secondIds);

            await handler.FirstTransferPerPartStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00001-of-00002.gguf"));
            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00002-of-00002.gguf"));

            releaseTransfers.TrySetResult();
            foreach (string id in firstIds)
                Assert.Equal("completed", (await WaitForTerminalAsync(service, id)).Status);

            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00001-of-00002.gguf"));
            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00002-of-00002.gguf"));

            int existingJobs = service.List().Count;
            InvalidOperationException alreadyPresent = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision));
            Assert.Contains("Every file of this model is already in the Managed Model Library", alreadyPresent.Message, StringComparison.Ordinal);
            Assert.Equal(existingJobs, service.List().Count);
            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00001-of-00002.gguf"));
            Assert.Equal(1, handler.TransferCount("split-Q4_K_M-00002-of-00002.gguf"));
        }
        finally
        {
            releaseTransfers.TrySetResult();
            if (starts != null)
            {
                string[] ids = starts.SelectMany(result => result).Select(status => status.Id)
                    .Distinct(StringComparer.Ordinal).ToArray();
                foreach (string id in ids)
                    await WaitForTerminalAsync(service, id);
            }
        }
    }

    [Fact]
    public async Task Removing_a_cancelled_part_does_not_requeue_its_live_sibling()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["split-Q4_K_M-00001-of-00002.gguf"] = first,
            ["split-Q4_K_M-00002-of-00002.gguf"] = second
        };
        string listing = Listing(new (string Path, byte[] Body)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", first),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", second)
        });
        var releaseTransfers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PlanTransferHandler(listing, bodies, releaseTransfers.Task, expectedListings: 1);
        using var client = new HttpClient(handler);
        var service = new ModelDownloadService(client, client);

        try
        {
            IReadOnlyList<ModelDownloadStatus> started = await service.StartModelDownloadPlanAsync(
                "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision).WaitAsync(TimeSpan.FromSeconds(10));
            await handler.FirstTransferPerPartStarted.WaitAsync(TimeSpan.FromSeconds(10));

            ModelDownloadStatus cancelledPart = Assert.Single(started, status => status.FileName == "split-Q4_K_M-00001-of-00002.gguf");
            ModelDownloadStatus livePart = Assert.Single(started, status => status.FileName == "split-Q4_K_M-00002-of-00002.gguf");
            Assert.True(service.Cancel(cancelledPart.Id));
            Assert.Equal("cancelled", (await WaitForTerminalAsync(service, cancelledPart.Id)).Status);
            Assert.True(service.Remove(cancelledPart.Id));

            IReadOnlyList<ModelDownloadStatus> repeated = await service.StartModelDownloadPlanAsync(
                "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision).WaitAsync(TimeSpan.FromSeconds(10));
            ModelDownloadStatus reused = Assert.Single(repeated);
            Assert.Equal(livePart.Id, reused.Id);
            Assert.Single(service.List());
        }
        finally
        {
            releaseTransfers.TrySetResult();
            foreach (ModelDownloadStatus status in service.List())
                await WaitForTerminalAsync(service, status.Id);
        }
    }

    [Fact]
    public async Task Plan_start_gate_is_removed_once_the_last_start_leaves()
    {
        using var library = new IsolatedLibrary();
        byte[] first = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 96).ToArray();
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["split-Q4_K_M-00001-of-00002.gguf"] = first,
            ["split-Q4_K_M-00002-of-00002.gguf"] = second
        };
        string listing = Listing(new (string Path, byte[] Body)[]
        {
            ("Q4_K_M/split-Q4_K_M-00001-of-00002.gguf", first),
            ("Q4_K_M/split-Q4_K_M-00002-of-00002.gguf", second)
        });
        var releaseTransfers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PlanTransferHandler(listing, bodies, releaseTransfers.Task);
        using var client = new HttpClient(handler);
        var service = new ModelDownloadService(client, client);

        try
        {
            Assert.Equal(0, service.PlanStartSlotCountForTests);
            IReadOnlyList<ModelDownloadStatus>[] starts = await Task.WhenAll(
                service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision),
                service.StartModelDownloadPlanAsync("acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, starts[0].Count);
            Assert.Equal(0, service.PlanStartSlotCountForTests);

            IReadOnlyList<ModelDownloadStatus> repeated = await service.StartModelDownloadPlanAsync(
                "acme/split", "Q4_K_M", null, CancellationToken.None, expectedRevision: Revision).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, repeated.Count);
            Assert.Equal(0, service.PlanStartSlotCountForTests);
        }
        finally
        {
            releaseTransfers.TrySetResult();
            foreach (ModelDownloadStatus status in service.List())
                await WaitForTerminalAsync(service, status.Id);
        }
    }

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status != null && status.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("A split-plan download did not reach a terminal state.");
    }

    private static string Listing(IEnumerable<(string Path, byte[] Body)> files) =>
        JsonSerializer.Serialize(new
        {
            id = "acme/split",
            sha = Revision,
            siblings = files.Select(file => new
            {
                rfilename = file.Path,
                size = file.Body.Length,
                lfs = new { size = file.Body.Length, oid = Convert.ToHexString(SHA256.HashData(file.Body)).ToLowerInvariant() }
            })
        });

    private sealed class PlanTransferHandler(
        string listing,
        IReadOnlyDictionary<string, byte[]> bodies,
        Task releaseTransfers,
        int expectedListings = 2) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _bothListings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstTransferPerPart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _transferCounts = new(StringComparer.Ordinal);
        private readonly int _expectedListings = expectedListings;
        private int _listingCount;

        public Task FirstTransferPerPartStarted => _firstTransferPerPart.Task;

        public int TransferCount(string fileName) => _transferCounts.TryGetValue(fileName, out int count) ? count : 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _listingCount) >= _expectedListings) _bothListings.TrySetResult();
                await _bothListings.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listing, Encoding.UTF8, "application/json")
                };
            }

            string fileName = Path.GetFileName(path);
            if (!bodies.TryGetValue(fileName, out byte[]? body))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                head.Content.Headers.ContentLength = body.Length;
                return head;
            }

            if (request.Method != HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            _transferCounts.AddOrUpdate(fileName, 1, static (_, count) => count + 1);
            if (bodies.Keys.All(name => TransferCount(name) > 0)) _firstTransferPerPart.TrySetResult();
            await releaseTransfers.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    private sealed class IsolatedLibrary : IDisposable
    {
        private readonly IDisposable _scope;

        public IsolatedLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "parux-plan-dedupe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _scope = ModelDownloadService.UseModelsDirectoryForTests(Root);
        }

        private string Root { get; }

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
