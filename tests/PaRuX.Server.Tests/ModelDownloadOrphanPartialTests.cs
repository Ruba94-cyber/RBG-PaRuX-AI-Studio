using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadOrphanPartialTests : IDisposable
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] CompleteModel = [10, 20, 30, 40, 50, 60, 70, 80];
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "orphan-partial-tests", Guid.NewGuid().ToString("N"));

    public ModelDownloadOrphanPartialTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Startup_scan_is_opt_in_and_lists_only_top_level_partials_with_complete_identity_as_paused()
    {
        WritePartial("restart.gguf", [10, 20, 30], expectedSize: CompleteModel.Length);
        WritePartial("no-identity.gguf", [1]);
        WritePartial("invalid-identity.gguf", [2], identityJson: "{");
        WritePartial("incomplete.gguf", [3], identityJson: JsonSerializer.Serialize(new { RepoId = "owner/model" }));

        string nestedDirectory = Path.Combine(_directory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        WritePartial("nested.gguf", [4], nestedDirectory, expectedSize: 4);

        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var disabled = new ModelDownloadService(scanOrphanPartials: false);
        Assert.Empty(disabled.List());

        var enabled = new ModelDownloadService(scanOrphanPartials: true);
        ModelDownloadStatus status = Assert.Single(enabled.List());
        Assert.Equal("paused", status.Status);
        Assert.Equal("restart.gguf", status.FileName);
        Assert.Equal(Revision, status.Revision);
        Assert.Equal(3, status.BytesDownloaded);
        Assert.Equal(CompleteModel.Length, status.TotalBytes);
        Assert.Null(status.FailureClass);
        Assert.Contains("previous PaRuX Core session", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discovered_partial_resumes_by_download_id_and_suppresses_duplicate_catalog_row()
    {
        const string fileName = "resume.gguf";
        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        WritePartial(fileName, CompleteModel[..3], expectedSize: CompleteModel.Length);

        var resumeCatalog = new ModelDownloadResumeCatalog(_directory);
        resumeCatalog.Remember(CreateRequest(fileName, CompleteModel.Length));

        var handler = new RangeResumeHandler(CompleteModel);
        var completionHandler = new RecordingCompletionHandler();
        using var client = new HttpClient(handler);
        var service = new ModelDownloadService(
            client,
            client,
            completionHandler: completionHandler,
            destinationSpaceProvider: new FixedSpaceProvider(),
            scanOrphanPartials: true)
        {
            BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
            {
                WithinBudget = true,
                LimitBytes = long.MaxValue,
                QuotaHeadroomBytes = long.MaxValue,
                DestinationFreeBytes = long.MaxValue
            })
        };

        ModelDownloadStatus paused = Assert.Single(service.List());
        Assert.Single(resumeCatalog.ListResumable(Array.Empty<ModelDownloadStatus>()));
        Assert.Empty(resumeCatalog.ListResumable(service.List()));

        ModelDownloadStatus started = await service.CreateDownloadAsync(
            new ModelDownloadRequest { ResumeFromDownloadId = paused.Id }, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalStatusAsync(service, started.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal("bytes=3-", handler.RangeHeader);
        Assert.Equal(CompleteModel, await File.ReadAllBytesAsync(Path.Combine(_directory, fileName)));
        (string modelPath, string label) = await completionHandler.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Path.Combine(_directory, fileName), modelPath);
        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), label);
    }

    [Fact]
    public void Startup_scan_ignores_a_partial_whose_source_identity_names_a_different_file()
    {
        WritePartial("mismatch.gguf", [5, 6], identityJson: JsonSerializer.Serialize(new
        {
            RepoId = "owner/model",
            Revision,
            FileName = "other.gguf",
            DownloadUrl = DownloadUrl("other.gguf"),
            ExpectedSizeBytes = CompleteModel.Length,
            ExpectedSha256 = (string?)null
        }));

        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var enabled = new ModelDownloadService(scanOrphanPartials: true);

        Assert.Empty(enabled.List());
    }

    private void WritePartial(string fileName, byte[] contents, string? directory = null, long? expectedSize = null, string? identityJson = null)
    {
        string targetDirectory = directory ?? _directory;
        Directory.CreateDirectory(targetDirectory);
        string partialPath = Path.Combine(targetDirectory, fileName + ".partial");
        File.WriteAllBytes(partialPath, contents);
        if (identityJson is not null)
        {
            File.WriteAllText(partialPath + ".source.json", identityJson);
            return;
        }

        if (expectedSize is null) return;
        File.WriteAllText(partialPath + ".source.json", JsonSerializer.Serialize(new
        {
            RepoId = "owner/model",
            Revision,
            FileName = fileName,
            DownloadUrl = DownloadUrl(fileName),
            ExpectedSizeBytes = expectedSize,
            ExpectedSha256 = (string?)null
        }));
    }

    private static ModelDownloadRequest CreateRequest(string fileName, long sizeBytes) => new()
    {
        RepoId = "owner/model",
        Revision = Revision,
        FileName = fileName,
        DownloadUrl = DownloadUrl(fileName),
        SizeBytes = sizeBytes
    };

    private static string DownloadUrl(string fileName) =>
        "https://huggingface.co/owner/model/resolve/" + Revision + "/" + fileName;

    private static async Task<ModelDownloadStatus> WaitForTerminalStatusAsync(ModelDownloadService service, string id)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status?.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(10);
        }

        throw new TimeoutException("The resumed partial did not reach a terminal download state.");
    }

    private sealed class FixedSpaceProvider : IModelDestinationSpaceProvider
    {
        public long? GetAvailableBytes(string destinationDirectory) => long.MaxValue;
    }

    private sealed class RecordingCompletionHandler : IModelDownloadCompletionHandler
    {
        private readonly TaskCompletionSource<(string ModelPath, string Label)> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string ModelPath, string Label)> Completion => _completion.Task;

        public void OnGgufDownloaded(string modelPath, string label) => _completion.TrySetResult((modelPath, label));
    }

    private sealed class RangeResumeHandler(byte[] contents) : HttpMessageHandler
    {
        public string? RangeHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = contents.Length;
                return Task.FromResult(head);
            }

            RangeHeader = request.Headers.Range?.ToString();
            long start = request.Headers.Range?.Ranges.SingleOrDefault()?.From ?? 0;
            if (start < 0 || start >= contents.Length)
                throw new InvalidOperationException("The resume request used an unexpected byte range.");

            byte[] remainder = contents[(int)start..];
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(remainder)
            };
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, contents.Length - 1, contents.Length);
            return Task.FromResult(response);
        }
    }
}
