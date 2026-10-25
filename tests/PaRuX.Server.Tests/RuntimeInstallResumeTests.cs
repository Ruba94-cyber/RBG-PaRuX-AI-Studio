using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Install jobs survive a Core restart, and an interrupted archive download continues only when the response proves the range it
/// continues from. The job file is written through the real store in isolated state and runtime directories. Download tests use a
/// loopback HTTP server behind a narrow release-host policy seam; production still accepts only the GitHub release infrastructure.
/// </summary>
public sealed class RuntimeInstallResumeTests : IDisposable
{
    private const string Backend = "text-vulkan";
    private static readonly RuntimeReleaseAsset ArchiveAsset = new(
        "llama.zip",
        "https://github.com/ggml-org/llama.cpp/releases/download/b1/llama.zip",
        1000,
        "sha256:" + new string('a', 64));

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-install-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _state;
    private readonly IDisposable _root;

    public RuntimeInstallResumeTests()
    {
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "root"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Resuming_an_interrupted_job_continues_over_real_http_206_and_verifies_the_archive()
    {
        string id = Guid.NewGuid().ToString("N");
        string tag = "b-resume-" + Guid.NewGuid().ToString("N")[..8];
        byte[] archive = BuildArchive();
        int prefixLength = archive.Length / 2;
        using var server = new LoopbackAssetServer(archive, ignoreRange: false, tag);
        SeedInterruptedJob(id, tag, archive[..prefixLength]);
        using HttpClient client = CreateLoopbackClient(tag, server, archive);
        RuntimeInstallService service = CreateService(client);

        RuntimeInstallStatus? accepted = service.ResumeJob(id);
        Assert.NotNull(accepted);
        Assert.Equal(id, accepted!.Id);
        Assert.Equal("queued", accepted.Stage);
        RuntimeInstallStatus completed = await WaitForTerminalAsync(service, id);
        await server.WaitForResponseAsync();

        Assert.Equal("completed", completed.Stage);
        Assert.Equal(206, server.ResponseStatusCode);
        Assert.Equal(prefixLength, server.RequestedRangeStart);
        Assert.True(File.Exists(Path.Combine(
            LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(Backend, tag), LocalAiPaths.LlamaServerExecutable)));
        Assert.Equal(tag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_restarts_from_the_full_response()
    {
        string id = Guid.NewGuid().ToString("N");
        string tag = "b-ignore-range-" + Guid.NewGuid().ToString("N")[..8];
        byte[] archive = BuildArchive();
        int prefixLength = archive.Length / 2;
        using var server = new LoopbackAssetServer(archive, ignoreRange: true, tag);
        SeedInterruptedJob(id, tag, archive[..prefixLength]);
        using HttpClient client = CreateLoopbackClient(tag, server, archive);
        RuntimeInstallService service = CreateService(client);

        Assert.NotNull(service.ResumeJob(id));
        RuntimeInstallStatus completed = await WaitForTerminalAsync(service, id);
        await server.WaitForResponseAsync();

        Assert.Equal("completed", completed.Stage);
        Assert.Equal(200, server.ResponseStatusCode);
        Assert.Equal(prefixLength, server.RequestedRangeStart);
        Assert.True(File.Exists(Path.Combine(
            LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(Backend, tag), LocalAiPaths.LlamaServerExecutable)));
    }

    [Fact]
    public async Task Resumed_bytes_are_rechecked_against_the_full_archive_digest()
    {
        string id = Guid.NewGuid().ToString("N");
        string tag = "b-resume-digest-" + Guid.NewGuid().ToString("N")[..8];
        byte[] archive = BuildArchive();
        int prefixLength = archive.Length / 2;
        byte[] corruptedPrefix = archive[..prefixLength];
        corruptedPrefix[0] ^= 0xFF;
        using var server = new LoopbackAssetServer(archive, ignoreRange: false, tag);
        SeedInterruptedJob(id, tag, corruptedPrefix);
        using HttpClient client = CreateLoopbackClient(tag, server, archive);
        RuntimeInstallService service = CreateService(client);

        Assert.NotNull(service.ResumeJob(id));
        RuntimeInstallStatus failed = await WaitForTerminalAsync(service, id);
        await server.WaitForResponseAsync();

        Assert.Equal("failed", failed.Stage);
        Assert.Contains("SHA-256", failed.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(206, server.ResponseStatusCode);
        Assert.Equal(prefixLength, server.RequestedRangeStart);
        Assert.False(Directory.Exists(Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(Backend, tag))));
        Assert.NotEqual(tag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
        Assert.False(File.Exists(Path.Combine(LocalAiPaths.RuntimeRoot, ".staging-" + id + ".zip")));
    }

    [Fact]
    public void A_job_can_only_be_resumed_while_interrupted()
    {
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [Job("completed-job", "completed")]);
        var service = new RuntimeInstallService(new LocalAiProcessManager(0));

        Assert.Throws<InvalidOperationException>(() => service.ResumeJob("completed-job"));
        Assert.Null(service.ResumeJob("missing-job"));
    }


    [Fact]
    public void A_new_install_is_not_scheduled_when_its_initial_state_cannot_be_saved()
    {
        var requests = new RefuseRequestsHandler();
        using var client = new HttpClient(requests);
        RuntimeInstallService service = CreateService(client);
        string file = LocalAiPaths.RuntimeInstallJobsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Directory.CreateDirectory(file); // A directory at the file path forces a safe write failure.

        IOException error = Assert.Throws<IOException>(() => service.CreateInstall(
            new RuntimeInstallRequest { Backend = Backend, Tag = "b987654" }));

        Assert.Contains("could not be saved", error.Message);
        Assert.Empty(service.ListJobs());
        Assert.Equal(0, requests.RequestCount);
    }

    [Fact]
    public void A_new_install_with_injected_non_io_store_failure_leaves_no_phantom_job()
    {
        var requests = new RefuseRequestsHandler();
        using var client = new HttpClient(requests);
        RuntimeInstallService service = CreateService(client,
            _ => throw new InvalidOperationException("Injected persistence refusal."));

        Assert.Throws<InvalidOperationException>(() => service.CreateInstall(
            new RuntimeInstallRequest { Backend = Backend, Tag = "b987654" }));

        Assert.Empty(service.ListJobs());
        Assert.Equal(0, requests.RequestCount);
    }

    [Fact]
    public void A_resume_is_not_scheduled_when_the_queued_state_cannot_be_saved()
    {
        string id = "unsaved-resume";
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [Job(id, "downloading")]);
        var requests = new RefuseRequestsHandler();
        using var client = new HttpClient(requests);
        var service = new RuntimeInstallService(new LocalAiProcessManager(0), client);
        File.Delete(LocalAiPaths.RuntimeInstallJobsFile);
        Directory.CreateDirectory(LocalAiPaths.RuntimeInstallJobsFile);

        Assert.Throws<IOException>(() => service.ResumeJob(id));

        Assert.Equal(RuntimeInstallJobStore.InterruptedStage, service.GetJob(id)!.Stage);
        Assert.Equal(0, requests.RequestCount);
    }

    [Fact]
    public void A_job_that_had_not_finished_is_listed_as_interrupted_after_a_restart_and_the_change_is_saved()
    {
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [Job("running1", "downloading"), Job("done1", "completed")]);

        var service = new RuntimeInstallService(new LocalAiProcessManager(0));

        RuntimeInstallStatus? interrupted = service.GetJob("running1");
        Assert.NotNull(interrupted);
        Assert.Equal(RuntimeInstallJobStore.InterruptedStage, interrupted!.Stage);
        Assert.Equal(RuntimeInstallJobStore.InterruptedMessage, interrupted.Error);
        Assert.False(service.CancelJob("running1"), "an interrupted job has no running install to cancel");
        Assert.Equal("completed", service.GetJob("done1")!.Stage);
        Assert.Equal(RuntimeInstallJobStore.InterruptedStage,
            RuntimeInstallJobStore.Load(LocalAiPaths.RuntimeInstallJobsFile).Single(job => job.Id == "running1").Stage);
    }

    [Fact]
    public void A_saved_job_list_round_trips_and_leaves_no_temporary_file_behind()
    {
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [Job("a", "queued"), Job("b", "failed")]);

        IReadOnlyList<RuntimeInstallStatus> loaded = RuntimeInstallJobStore.Load(LocalAiPaths.RuntimeInstallJobsFile);

        Assert.Equal(new[] { "a", "b" }, loaded.Select(job => job.Id).ToArray());
        Assert.Equal("failed", loaded[1].Stage);
        Assert.False(File.Exists(LocalAiPaths.RuntimeInstallJobsFile + ".tmp"));
    }

    [Fact]
    public void A_damaged_job_file_does_not_stop_the_service_from_starting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.RuntimeInstallJobsFile)!);
        File.WriteAllText(LocalAiPaths.RuntimeInstallJobsFile, "{ not json");

        var service = new RuntimeInstallService(new LocalAiProcessManager(0));

        Assert.Empty(service.ListJobs());
    }

    [Fact]
    public void A_missing_or_empty_partial_file_asks_for_the_whole_archive()
    {
        RuntimeDownloadResume.Plan plan = RuntimeDownloadResume.PlanFor(0, 1000);

        Assert.False(plan.Resumes);
        Assert.Equal(0, plan.RangeFrom);
    }

    [Fact]
    public void A_partial_file_that_is_a_proper_prefix_is_continued_from_its_length()
    {
        RuntimeDownloadResume.Plan plan = RuntimeDownloadResume.PlanFor(400, 1000);

        Assert.True(plan.Resumes);
        Assert.Equal(400, plan.RangeFrom);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1500)]
    public void A_partial_file_that_already_holds_the_whole_asset_is_not_trusted_and_starts_again(long partialBytes)
    {
        Assert.False(RuntimeDownloadResume.PlanFor(partialBytes, 1000).Resumes);
    }

    [Fact]
    public void The_expected_content_range_names_the_start_the_last_byte_and_the_total()
    {
        Assert.Equal("bytes 400-999/1000", RuntimeDownloadResume.ExpectedContentRange(400, 1000));
    }

    [Fact]
    public void A_partial_response_that_proves_the_requested_range_is_accepted()
    {
        using HttpResponseMessage response = PartialResponse(400, 999, 1000, contentLength: 600, host: "github.com");

        RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, ArchiveAsset, 400);
    }

    [Fact]
    public void A_partial_response_that_starts_somewhere_else_is_refused()
    {
        using HttpResponseMessage response = PartialResponse(300, 999, 1000, contentLength: 700, host: "github.com");

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, ArchiveAsset, 400));
    }

    [Fact]
    public void A_partial_response_with_the_wrong_total_or_remaining_length_is_refused()
    {
        using HttpResponseMessage wrongTotal = PartialResponse(400, 999, 2000, contentLength: 600, host: "github.com");
        using HttpResponseMessage wrongLength = PartialResponse(400, 999, 1000, contentLength: 999, host: "github.com");

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(wrongTotal, ArchiveAsset, 400));
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(wrongLength, ArchiveAsset, 400));
    }

    [Fact]
    public void A_partial_response_whose_range_stops_short_of_the_last_byte_is_refused()
    {
        using HttpResponseMessage response = PartialResponse(400, 500, 1000, contentLength: 600, host: "github.com");

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, ArchiveAsset, 400));
    }

    [Fact]
    public void A_partial_response_from_outside_github_is_refused()
    {
        using HttpResponseMessage response = PartialResponse(400, 999, 1000, contentLength: 600, host: "example.com");

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, ArchiveAsset, 400));
    }

    [Fact]
    public void A_release_asset_url_must_name_github_itself_not_the_release_cdn()
    {
        RuntimeArtifactSecurityPolicy.ValidateAsset(ArchiveAsset, "ggml-org", "llama.cpp");
        var cdnAsset = new RuntimeReleaseAsset(
            "llama.zip",
            "https://release-assets.githubusercontent.com/ggml-org/llama.cpp/releases/download/b1/llama.zip",
            1000,
            "sha256:" + new string('a', 64));

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateAsset(cdnAsset, "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void A_whole_file_response_is_checked_against_the_full_size_when_the_download_starts_over()
    {
        using HttpResponseMessage whole = WholeResponse(contentLength: 1000, host: "github.com");
        using HttpResponseMessage truncated = WholeResponse(contentLength: 900, host: "github.com");

        RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(whole, ArchiveAsset, 0);
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(truncated, ArchiveAsset, 0));
    }

    [Fact]
    public void A_whole_file_response_cannot_be_accepted_as_the_continuation_of_a_partial_file()
    {
        using HttpResponseMessage whole = WholeResponse(contentLength: 1000, host: "github.com");

        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(whole, ArchiveAsset, 400));
    }

    private static RuntimeInstallStatus Job(string id, string stage) => new()
    {
        Id = id,
        Backend = Backend,
        Tag = "b1",
        Stage = stage,
        BytesDownloaded = 10,
        TotalBytes = 100,
        CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
        UpdatedUtc = DateTime.UtcNow
    };

    private void SeedInterruptedJob(string id, string tag, byte[] partial)
    {
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [new RuntimeInstallStatus
        {
            Id = id,
            Backend = Backend,
            Tag = tag,
            Stage = "downloading",
            BytesDownloaded = partial.Length,
            TotalBytes = partial.Length * 2,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow
        }]);

        Directory.CreateDirectory(LocalAiPaths.RuntimeRoot);
        File.WriteAllBytes(Path.Combine(LocalAiPaths.RuntimeRoot, ".staging-" + id + ".zip.partial"), partial);
    }

    private static byte[] BuildArchive()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream binary = zip.CreateEntry(LocalAiPaths.LlamaServerExecutable).Open();
            binary.Write("test runtime binary"u8);
        }
        return memory.ToArray();
    }

    private HttpClient CreateLoopbackClient(string tag, LoopbackAssetServer server, byte[] archive)
    {
        string name = PlatformAssetNames.For(tag, Backend);
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        string release = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new { name, browser_download_url = server.AssetUri.ToString(), size = archive.Length, digest }
            }
        });
        return new HttpClient(new LoopbackReleaseHandler(tag, release));
    }

    [Fact]
    public void A_resume_restores_the_interrupted_state_when_saving_fails_with_any_exception()
    {
        string id = "unsaved-resume-any-exception";
        RuntimeInstallStatus persisted = Job(id, "downloading");
        persisted.Error = "previous error";
        RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, [persisted]);

        var requests = new RefuseRequestsHandler();
        using var client = new HttpClient(requests);
        bool failQueuedPersistence = false;
        Action<IReadOnlyList<RuntimeInstallStatus>> saveSnapshot = snapshot =>
        {
            if (failQueuedPersistence && snapshot.Single(status => status.Id == id).Stage == "queued")
                throw new InvalidOperationException("Injected non-IO runtime install persistence failure.");
            RuntimeInstallJobStore.Save(LocalAiPaths.RuntimeInstallJobsFile, snapshot);
        };
        RuntimeInstallService service = CreateService(client, saveSnapshot, () => new CountingCancellationTokenSource());
        RuntimeInstallStatus previous = service.GetJob(id)!;
        failQueuedPersistence = true;

        Assert.Throws<InvalidOperationException>(() => service.ResumeJob(id));

        RuntimeInstallStatus restored = service.GetJob(id)!;
        Assert.Equal(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(restored));
        Assert.Equal(0, requests.RequestCount);
    }

    private RuntimeInstallService CreateService(
        HttpClient client,
        Action<IReadOnlyList<RuntimeInstallStatus>>? saveJobSnapshot = null,
        Func<CancellationTokenSource>? cancellationTokenSourceFactory = null)
    {
        var process = new LocalAiProcessManager(0);
        var guard = new RuntimeUpdateRegressionGuard(
            process,
            new UnusedPerformanceProbe(),
            new RuntimeRegressionEvidenceStore(Path.Combine(_work, "state", "runtime-regression-evidence.json")));
        return new RuntimeInstallService(
            process,
            client,
            new GpuInventoryService(),
            new ComfyRuntimeStore(),
            guard,
            uri => uri.IsLoopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase),
            saveJobSnapshot,
            cancellationTokenSourceFactory);
    }

    private static async Task<RuntimeInstallStatus> WaitForTerminalAsync(RuntimeInstallService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            RuntimeInstallStatus? status = service.GetJob(id);
            if (status?.Stage is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Runtime install did not reach a terminal state.");
    }

    private sealed class UnusedPerformanceProbe : ILlamaPerformanceProbe
    {
        public Task<LlamaPerformanceProbeMeasurement> RunAsync(
            string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A first runtime install must not invoke a comparison probe.");
    }

    private sealed class RefuseRequestsHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            throw new InvalidOperationException("Unexpected request in persistence-failure test.");
        }
    }

private sealed class CountingCancellationTokenSource : CancellationTokenSource
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class LoopbackReleaseHandler : DelegatingHandler
    {
        private readonly string _releasePath;
        private readonly string _releaseJson;

        public LoopbackReleaseHandler(string tag, string releaseJson)
            : base(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            _releasePath = "/repos/ggml-org/llama.cpp/releases/tags/" + Uri.EscapeDataString(tag);
            _releaseJson = releaseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A runtime request has no URI.");
            if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.AbsolutePath, _releasePath, StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(_releaseJson, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
            if (uri.IsLoopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return base.SendAsync(request, cancellationToken);

            throw new InvalidOperationException("The test HTTP client refused a non-loopback request.");
        }
    }

    private sealed class LoopbackAssetServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _archive;
        private readonly bool _ignoreRange;
        private readonly string _expectedPath;
        private readonly Task _responseTask;

        public Uri AssetUri { get; }
        public long? RequestedRangeStart { get; private set; }
        public int ResponseStatusCode { get; private set; }

        public LoopbackAssetServer(byte[] archive, bool ignoreRange, string tag)
        {
            _archive = archive;
            _ignoreRange = ignoreRange;
            string name = PlatformAssetNames.For(tag, Backend);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            AssetUri = new Uri("http://127.0.0.1:" + port + "/ggml-org/llama.cpp/releases/download/"
                + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
            _expectedPath = AssetUri.AbsolutePath;
            _responseTask = RespondOnceAsync(_stop.Token);
        }

        public Task WaitForResponseAsync() => _responseTask.WaitAsync(TimeSpan.FromSeconds(15));

        private async Task RespondOnceAsync(CancellationToken cancellationToken)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string requestLine = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("The loopback request had no request line.");
            string? rangeText = null;
            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) break;
                int separator = line.IndexOf(':');
                if (separator > 0 && string.Equals(line[..separator], "Range", StringComparison.OrdinalIgnoreCase))
                    rangeText = line[(separator + 1)..].Trim();
            }

            string[] requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length != 3 || requestParts[0] != "GET" || requestParts[1] != _expectedPath)
                throw new InvalidDataException("The loopback request did not target the selected release asset.");

            if (RangeHeaderValue.TryParse(rangeText, out RangeHeaderValue? range))
                RequestedRangeStart = range.Ranges.SingleOrDefault()?.From;
            bool partial = !_ignoreRange && RequestedRangeStart is long start && start < _archive.Length;
            byte[] body = partial ? _archive[(int)RequestedRangeStart!.Value..] : _archive;
            ResponseStatusCode = partial ? 206 : 200;
            string status = partial ? "206 Partial Content" : "200 OK";
            var headers = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append("\r\n")
                .Append("Content-Length: ").Append(body.Length).Append("\r\n")
                .Append("Content-Type: application/zip\r\n")
                .Append("Connection: close\r\n");
            if (partial)
                headers.Append("Content-Range: bytes ").Append(RequestedRangeStart).Append('-')
                    .Append(_archive.Length - 1).Append('/').Append(_archive.Length).Append("\r\n");
            headers.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _responseTask.GetAwaiter().GetResult(); } catch { }
            _stop.Dispose();
        }
    }

    private static HttpResponseMessage PartialResponse(long from, long to, long total, long contentLength, string host)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://" + host + "/ggml-org/llama.cpp/releases/download/b1/llama.zip"),
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static HttpResponseMessage WholeResponse(long contentLength, string host)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://" + host + "/ggml-org/llama.cpp/releases/download/b1/llama.zip"),
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }
}
