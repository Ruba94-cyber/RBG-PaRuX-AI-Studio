using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DeclaredSizeProbeTests
{
    private const string Revision = "dddddddddddddddddddddddddddddddddddddddd";
    private const string OriginalUrl = "https://huggingface.co/test/declared-size/resolve/" + Revision + "/model.gguf";

    private sealed record SeenRequest(string Method, string Uri);

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SeenRequest> _requests = new();
        public IReadOnlyList<SeenRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(new SeenRequest(request.Method.Method, request.RequestUri!.AbsoluteUri));
            return await respond(request, cancellationToken);
        }
    }

    private sealed class TempModels : IDisposable
    {
        private readonly IDisposable _scope;

        public TempModels()
        {
            Root = Path.Combine(Path.GetTempPath(), "parux-declared-size-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _scope = ModelDownloadService.UseModelsDirectoryForTests(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class FixedDestinationSpace : IModelDestinationSpaceProvider
    {
        public long? GetAvailableBytes(string destinationDirectory) => 1L << 40;
    }

    [Fact]
    public async Task Redirect_length_is_ignored_and_successful_location_probe_supplies_total()
    {
        using var models = new TempModels();
        byte[] content = [1, 2, 3, 4, 5, 6];
        var location = new Uri("https://huggingface.co/test/declared-size/resolve/main/followed.gguf");
        var metadata = new ScriptedHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == OriginalUrl
                ? Response(HttpStatusCode.Found, declaredLength: 8, location: location)
                : request.RequestUri == location
                    ? Response(HttpStatusCode.OK, declaredLength: content.Length)
                    : throw new InvalidOperationException("Unexpected metadata probe URI.")));
        var transfer = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body: content)));
        ModelDownloadService service = CreateService(transfer, metadata);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        Assert.Equal(content.Length, started.TotalBytes);

        ModelDownloadStatus done = await WaitForTerminalAsync(service, started.Id);

        Assert.Equal("completed", done.Status);
        Assert.Contains(metadata.Requests, request => request.Method == "HEAD" && request.Uri == location.AbsoluteUri);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "model.gguf")));
    }

    [Fact]
    public async Task Successful_location_probe_size_is_used_by_the_completeness_guard()
    {
        using var models = new TempModels();
        byte[] shortContent = [1, 2, 3, 4];
        const long declaredLength = 8;
        var location = new Uri("https://huggingface.co/test/declared-size/resolve/main/followed-short.gguf");
        var metadata = new ScriptedHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == OriginalUrl
                ? Response(HttpStatusCode.Found, location: location)
                : request.RequestUri == location
                    ? Response(HttpStatusCode.OK, declaredLength: declaredLength)
                    : throw new InvalidOperationException("Unexpected metadata probe URI.")));
        var transfer = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body: shortContent)));
        ModelDownloadService service = CreateService(transfer, metadata);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        ModelDownloadStatus done = await WaitForTerminalAsync(service, started.Id);

        Assert.Equal("failed", done.Status);
        Assert.Contains("declared size", done.Error); // a response shorter than the probed size is refused
        Assert.Contains(metadata.Requests, request => request.Method == "HEAD" && request.Uri == location.AbsoluteUri);
    }

    [Fact]
    public async Task Partial_content_probe_length_is_not_a_completeness_requirement()
    {
        using var models = new TempModels();
        byte[] content = [1, 2, 3, 4];
        var metadata = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.PartialContent, declaredLength: 8)));
        var transfer = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body: content)));
        ModelDownloadService service = CreateService(transfer, metadata);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        ModelDownloadStatus done = await WaitForTerminalAsync(service, started.Id);

        Assert.Equal("completed", done.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "model.gguf")));
    }

    [Fact]
    public async Task Not_found_content_length_does_not_become_a_completeness_requirement()
    {
        using var models = new TempModels();
        byte[] content = [1, 2, 3, 4];
        var metadata = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.NotFound, declaredLength: 8)));
        var transfer = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body: content)));
        ModelDownloadService service = CreateService(transfer, metadata);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        ModelDownloadStatus done = await WaitForTerminalAsync(service, started.Id);

        Assert.Equal("completed", done.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "model.gguf")));
    }

    [Fact]
    public async Task Successful_head_probe_and_existing_transfer_still_complete()
    {
        using var models = new TempModels();
        byte[] content = [8, 7, 6, 5, 4];
        var metadata = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, declaredLength: content.Length)));
        var transfer = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body: content)));
        ModelDownloadService service = CreateService(transfer, metadata);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        ModelDownloadStatus done = await WaitForTerminalAsync(service, started.Id);

        Assert.Equal("completed", done.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "model.gguf")));
    }

    private static ModelDownloadService CreateService(HttpMessageHandler transferHandler, HttpMessageHandler metadataHandler) =>
        new(new HttpClient(transferHandler), new HttpClient(metadataHandler),
            destinationSpaceProvider: new FixedDestinationSpace())
        {
            TransferPolicy = new ModelDownloadTransferPolicy
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                StallTimeout = TimeSpan.FromSeconds(2),
                MaxAttempts = 1
            },
            GamingActive = () => false,
            BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
            {
                UsedBytes = 0,
                LimitBytes = 700L * 1024 * 1024 * 1024,
                QuotaHeadroomBytes = 700L * 1024 * 1024 * 1024,
                DestinationFreeBytes = 1L << 40,
                WithinBudget = true
            })
        };

    private static ModelDownloadRequest Request() => new()
    {
        RepoId = "test/declared-size",
        Revision = Revision,
        FileName = "model.gguf",
        DownloadUrl = OriginalUrl,
        SizeBytes = 0
    };

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        long? declaredLength = null,
        Uri? location = null,
        byte[]? body = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body ?? [])
        };
        if (declaredLength is long length) response.Content.Headers.ContentLength = length;
        if (location is not null) response.Headers.Location = location;
        return response;
    }

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status?.Status is "completed" or "failed") return status;
            await Task.Delay(10);
        }

        throw new TimeoutException("The model download did not reach a terminal state.");
    }
}
