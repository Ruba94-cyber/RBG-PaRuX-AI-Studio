using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class FreeSpaceUnknownFailClosedTests
{
    private static readonly byte[] DownloadBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_refuses_unknown_free_space_with_actionable_typed_error(bool providerThrows)
    {
        using var modelsDirectory = new IsolatedModelsDirectory();
        var provider = new FakeModelDestinationSpaceProvider([null], throwOnRead: providerThrows);
        using var harness = new DownloadHarness(provider);

        ModelDownloadStatus? admitted = null;
        Exception? result = await Record.ExceptionAsync(async () =>
        {
            admitted = await harness.Service.CreateDownloadAsync(RequestFor(NewFileName()), CancellationToken.None);
        });

        // On the unfixed path the service accepts the unknown reading and starts a background transfer.
        // Let that transfer finish before removing its isolated destination so the regression test cleans up safely.
        if (result is null && admitted is not null)
            await WaitForTerminalAsync(harness.Service, admitted.Id);

        InvalidOperationException refusal = Assert.IsType<InvalidOperationException>(result);
        Assert.Contains("free space could not be verified", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choose a writable folder", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(modelsDirectory.Path, provider.LastDestinationDirectory);
        Assert.Equal(1, provider.ReadCount);
    }

    [Fact]
    public async Task Known_sufficient_space_is_admitted_and_known_insufficient_space_is_refused()
    {
        using (var modelsDirectory = new IsolatedModelsDirectory())
        {
            var provider = new FakeModelDestinationSpaceProvider([long.MaxValue]);
            using var harness = new DownloadHarness(provider);

            ModelDownloadStatus admitted = await harness.Service.CreateDownloadAsync(
                RequestFor(NewFileName()), CancellationToken.None);
            ModelDownloadStatus final = await WaitForTerminalAsync(harness.Service, admitted.Id);

            Assert.Equal("completed", final.Status);
            Assert.Null(final.FailureClass);
            Assert.Equal(modelsDirectory.Path, provider.LastDestinationDirectory);
        }

        using (var modelsDirectory = new IsolatedModelsDirectory())
        {
            var provider = new FakeModelDestinationSpaceProvider([0]);
            using var harness = new DownloadHarness(provider);

            InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Service.CreateDownloadAsync(RequestFor(NewFileName()), CancellationToken.None));

            Assert.Contains("Not enough free space on the model drive", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be verified", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(modelsDirectory.Path, provider.LastDestinationDirectory);
            Assert.Equal(1, provider.ReadCount);
        }
    }

    [Fact]
    public async Task Unknown_mid_stream_sample_does_not_fail_an_admitted_transfer()
    {
        using var modelsDirectory = new IsolatedModelsDirectory();
        var provider = new FakeModelDestinationSpaceProvider([long.MaxValue, long.MaxValue, null]);
        using var harness = new DownloadHarness(provider);

        ModelDownloadStatus admitted = await harness.Service.CreateDownloadAsync(
            RequestFor(NewFileName()), CancellationToken.None);
        ModelDownloadStatus final = await WaitForTerminalAsync(harness.Service, admitted.Id);

        Assert.Equal("completed", final.Status);
        Assert.Null(final.FailureClass);
        Assert.Equal(3, provider.ReadCount);
        Assert.Equal(modelsDirectory.Path, provider.LastDestinationDirectory);
    }

    private static string NewFileName() => "parux-space-test-" + Guid.NewGuid().ToString("N") + ".gguf";

    private static ModelDownloadRequest RequestFor(string fileName) => new()
    {
        RepoId = "example/repo",
        FileName = fileName,
        DownloadUrl = "https://huggingface.co/example/repo/resolve/main/" + fileName
    };

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? current = service.Get(id);
            if (current is not null && current.Status is "completed" or "failed" or "cancelled") return current;
            await Task.Delay(10);
        }

        throw new TimeoutException("The isolated test transfer did not finish in time.");
    }

    private sealed class IsolatedModelsDirectory : IDisposable
    {
        private readonly IDisposable _scope;

        public IsolatedModelsDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "parux-free-space-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _scope = ModelDownloadService.UseModelsDirectoryForTests(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            _scope.Dispose();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class DownloadHarness : IDisposable
    {
        private readonly HttpClient _transferClient = new(new FakeHandler());
        private readonly HttpClient _metadataClient = new(new FakeHandler());

        public DownloadHarness(IModelDestinationSpaceProvider provider)
        {
            Service = new ModelDownloadService(
                httpClient: _transferClient,
                metadataProbe: _metadataClient,
                destinationSpaceProvider: provider)
            {
                GamingActive = () => false,
                BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
                {
                    WithinBudget = true,
                    LimitBytes = long.MaxValue,
                    QuotaHeadroomBytes = long.MaxValue
                }),
                TransferPolicy = new ModelDownloadTransferPolicy
                {
                    FreeSpaceCheckIntervalBytes = 1
                }
            };
        }

        public ModelDownloadService Service { get; }

        public void Dispose()
        {
            _metadataClient.Dispose();
            _transferClient.Dispose();
        }
    }

    private sealed class FakeModelDestinationSpaceProvider(long?[] readings, bool throwOnRead = false)
        : IModelDestinationSpaceProvider
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);
        public string? LastDestinationDirectory { get; private set; }

        public long? GetAvailableBytes(string destinationDirectory)
        {
            LastDestinationDirectory = destinationDirectory;
            int index = Interlocked.Increment(ref _readCount) - 1;
            if (throwOnRead) throw new IOException("The isolated space probe failed.");
            return readings[Math.Min(index, readings.Length - 1)];
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                head.Content.Headers.ContentLength = DownloadBytes.Length;
                return Task.FromResult(head);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(DownloadBytes)
            });
        }
    }
}
