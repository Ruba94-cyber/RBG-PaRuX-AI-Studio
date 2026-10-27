using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The consent gates of the companion fetch, and the download path behind them. The gate tests stop before a byte is
/// requested (the handler throws if it is reached). The download tests use an in-process transfer double that serves
/// synthetic bytes, so the real download service, size check, digest check and retry bound run without any network.
/// </summary>
public sealed class MediaCompanionFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _managedScope;

    public MediaCompanionFetchTests()
    {
        Directory.CreateDirectory(_dir);
        _managedScope = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_dir, "managed"));
    }

    public void Dispose()
    {
        _managedScope.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The companion fetch test reached the network: " + request.RequestUri);
    }

    private static ModelDownloadService Downloads() =>
        new(new HttpClient(new NoNetworkHandler()), new HttpClient(new NoNetworkHandler()));

    [Fact]
    public async Task Internal_companion_route_refuses_a_forged_fetchable_flag_without_a_digest()
    {
        var item = new MediaCompanionItem
        {
            Fetchable = true,
            Repository = "example/model",
            RepositoryPath = "te.safetensors",
            Revision = new string('a', 40),
            FileName = "te.safetensors",
            PinnedSizeBytes = 32
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Downloads().CreateCompanionDownloadAsync(item, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, item.FileName)));
    }

    private static SafetensorsHeader ZImageLayout() => new(0, 0,
        new[]
        {
            new SafetensorsTensor("noise_refiner.0.w", "BF16", new long[] { 1 }, 0, 0),
            new SafetensorsTensor("context_refiner.0.w", "BF16", new long[] { 1 }, 0, 0),
            new SafetensorsTensor("cap_embedder.0.w", "BF16", new long[] { 1 }, 0, 0),
            new SafetensorsTensor("x_embedder.w", "BF16", new long[] { 1 }, 0, 0)
        },
        new Dictionary<string, string>());

    private string Models(params (string Folder, string File, long Size)[] files)
    {
        string root = Path.Combine(_dir, "models-" + Guid.NewGuid().ToString("N"));
        foreach ((string folder, string file, long size) in files)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            using FileStream stream = File.Create(Path.Combine(root, folder, file));
            stream.SetLength(size);
        }
        return root;
    }

    private static MediaCompanionPlan ShippedPlan(string modelsRoot)
    {
        var installed = MediaCompanionResolver.ScanFolders(modelsRoot, new[] { "diffusion_models", "text_encoders", "vae" });
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(ZImageLayout(), MediaCompanionRegistry.Shipped);
        return MediaCompanionResolver.BuildPlan("image", "z_image_turbo_nvfp4.safetensors", detection, MediaCompanionRegistry.Shipped, installed);
    }

    private static MediaCompanionFetchRequest Request(MediaCompanionPlan plan, bool consent = true, string? hash = null) => new()
    {
        Type = "image",
        PlanHash = hash ?? plan.PlanHash,
        Consent = consent
    };

    [Fact]
    public async Task Fetch_is_refused_without_consent_before_anything_else()
    {
        MediaCompanionPlan plan = ShippedPlan(Models());

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), Models())
            .FetchAsync(plan, Request(plan, consent: false), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("consent", result.Message);
        Assert.Null(result.FailedStep);
    }

    [Fact]
    public async Task Fetch_is_refused_when_the_plan_changed_since_it_was_reviewed()
    {
        MediaCompanionPlan plan = ShippedPlan(Models());

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), Models())
            .FetchAsync(plan, Request(plan, hash: "stale-hash"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("changed since you reviewed it", result.Message);
    }

    [Fact]
    public async Task Plan_for_a_stale_hash_is_refused_before_anything_is_downloaded()
    {
        MediaCompanionPlan plan = ShippedPlan(Models());

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), Models())
            .FetchAsync(plan, Request(plan, hash: new string('0', 64)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(result.Installed);
    }

    [Fact]
    public async Task Nothing_is_fetched_when_every_companion_is_already_installed()
    {
        string models = Models(("text_encoders", "qwen_3_4b.safetensors", 8044982048), ("vae", "ae.safetensors", 335304388));
        MediaCompanionPlan plan = ShippedPlan(models);

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("already installed", result.Message);
        Assert.Empty(result.Installed);
    }

    [Fact]
    public async Task A_pinned_companion_with_the_same_name_and_size_is_skipped()
    {
        // A pinned test source: the file is present with the pinned size, so the fetch must not download it.
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(
            "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[],\"vae\":[]},\"families\":[{\"id\":\"demo\",\"displayName\":\"Demo\","
            + "\"support\":\"supported\",\"pipelines\":[\"image\"],\"templateFile\":\"z-image-smoke-api.json\","
            + "\"mainLoader\":{\"node\":\"1\",\"classType\":\"UNETLoader\",\"input\":\"unet_name\",\"folder\":\"diffusion_models\"},"
            + "\"requiredNodes\":[\"UNETLoader\"],\"signature\":{\"requiredStems\":[\"a.\"]},\"companions\":[{\"role\":\"text_encoder\","
            + "\"folder\":\"text_encoders\",\"fileName\":\"te.safetensors\",\"clipType\":\"demo\","
            + "\"source\":{\"repository\":\"owner/repo\",\"path\":\"split/te.safetensors\","
            + "\"revision\":\"0123456789abcdef0123456789abcdef01234567\",\"sizeBytes\":1000,"
            + "\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}}]}]}");
        string models = Models(("text_encoders", "te.safetensors", 1000));
        var installed = MediaCompanionResolver.ScanFolders(models, new[] { "text_encoders" });
        MediaFamilyDetection detection = new("demo", "Demo", "supported", null, false, false, 1);
        MediaCompanionPlan plan = MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, registry, installed);

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    public async Task A_pinned_companion_with_a_different_size_is_never_overwritten()
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Parse(
            "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[],\"vae\":[]},\"families\":[{\"id\":\"demo\",\"displayName\":\"Demo\","
            + "\"support\":\"supported\",\"pipelines\":[\"image\"],\"templateFile\":\"z-image-smoke-api.json\","
            + "\"mainLoader\":{\"node\":\"1\",\"classType\":\"UNETLoader\",\"input\":\"unet_name\",\"folder\":\"diffusion_models\"},"
            + "\"requiredNodes\":[\"UNETLoader\"],\"signature\":{\"requiredStems\":[\"a.\"]},\"companions\":[{\"role\":\"text_encoder\","
            + "\"folder\":\"text_encoders\",\"fileName\":\"te.safetensors\",\"clipType\":\"demo\","
            + "\"source\":{\"repository\":\"owner/repo\",\"path\":\"split/te.safetensors\","
            + "\"revision\":\"0123456789abcdef0123456789abcdef01234567\",\"sizeBytes\":1000,"
            + "\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}}]}]}");
        string models = Models(("text_encoders", "te.safetensors", 999));
        var installed = MediaCompanionResolver.ScanFolders(models, new[] { "text_encoders" });
        MediaFamilyDetection detection = new("demo", "Demo", "supported", null, false, false, 1);
        MediaCompanionPlan plan = MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, registry, installed);

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("fetch te.safetensors", result.FailedStep);
        Assert.Contains("different size", result.Message);
        Assert.Equal(999, new FileInfo(Path.Combine(models, "text_encoders", "te.safetensors")).Length);
        Assert.False(plan.Installable);
        Assert.Contains("another size", plan.Reason);
        Assert.Contains("text_encoders/te.safetensors", plan.Reason);
    }

    [Fact]
    public void The_download_url_comes_only_from_the_pinned_repository_path_and_revision()
    {
        var pinned = new MediaCompanionItem
        {
            Repository = "owner/repo",
            RepositoryPath = "split/te.safetensors",
            Revision = "0123456789abcdef0123456789abcdef01234567",
            Fetchable = true
        };

        Assert.Equal("https://huggingface.co/owner/repo/resolve/0123456789abcdef0123456789abcdef01234567/split/te.safetensors",
            MediaCompanionFetcher.ResolveUrlFor(pinned));
        Assert.Throws<InvalidOperationException>(() => MediaCompanionFetcher.ResolveUrlFor(new MediaCompanionItem { FileName = "x.safetensors" }));
    }

    // ---------- download path, offline: an in-process transfer double serves synthetic bytes ----------

    /// <summary>Serves fixed bytes for every request, or refuses GET with HTTP 500 when failGet is set. No socket is opened.</summary>
    private sealed class ServeBytesHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly bool _failGet;

        public ServeBytesHandler(byte[] content, bool failGet = false)
        {
            _content = content;
            _failGet = failGet;
        }

        /// <summary>GET requests for the companion file itself: one per transfer attempt.</summary>
        public int CompanionGetCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/te.safetensors", StringComparison.Ordinal))
                CompanionGetCount++;
            if (_failGet && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content) };
            response.Content.Headers.ContentLength = _content.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class GatedServeBytesHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseTransfer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _companionGetCount;

        public GatedServeBytesHandler(byte[] content) => _content = content;

        public Task DownloadStarted => _downloadStarted.Task;
        public Task SecondDownloadStarted => _secondDownloadStarted.Task;
        public int CompanionGetCount => Volatile.Read(ref _companionGetCount);
        public void ReleaseTransfer() => _releaseTransfer.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/te.safetensors", StringComparison.Ordinal))
            {
                int getCount = Interlocked.Increment(ref _companionGetCount);
                _downloadStarted.TrySetResult();
                if (getCount == 2) _secondDownloadStarted.TrySetResult();
                await _releaseTransfer.Task.WaitAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content) };
            response.Content.Headers.ContentLength = _content.Length;
            return response;
        }
    }

    // The first transfer ignores its cancellation until the test allows it to stop. A waiter released too early would
    // start its own GET while the cancelled GET is still open, so the test holds the cancelled GET open to catch that.
    private sealed class StubbornCancelHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _mayStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _companionGetCount;

        public StubbornCancelHandler(byte[] content) => _content = content;

        public Task DownloadStarted => _downloadStarted.Task;
        public Task CancelObserved => _cancelObserved.Task;
        public Task SecondDownloadStarted => _secondDownloadStarted.Task;
        public int CompanionGetCount => Volatile.Read(ref _companionGetCount);
        public void AllowCancelledTransferToStop() => _mayStop.TrySetResult();
        public void ReleaseSecondTransfer() => _releaseSecond.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/te.safetensors", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _companionGetCount) == 1)
                {
                    _downloadStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _cancelObserved.TrySetResult();
                        await _mayStop.Task;
                        throw;
                    }
                }
                else
                {
                    _secondDownloadStarted.TrySetResult();
                    await _releaseSecond.Task.WaitAsync(cancellationToken);
                }
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content) };
            response.Content.Headers.ContentLength = _content.Length;
            return response;
        }
    }

    private static ModelDownloadService Transfer(HttpMessageHandler transfer) =>
        new(new HttpClient(transfer), new HttpClient(transfer))
        {
            // The fetcher owns the two-attempt contract; the job-level retry budget would otherwise multiply it.
            TransferPolicy = new ModelDownloadTransferPolicy { MaxAttempts = 1 }
        };

    /// <summary>A registry whose one companion is pinned to the given bytes: exact size and SHA-256, immutable revision.</summary>
    private static MediaCompanionRegistry PinnedTo(byte[] content)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return MediaCompanionRegistry.Parse(
            "{\"schemaVersion\":1,\"embeddedStems\":{\"textEncoder\":[],\"vae\":[]},\"families\":[{\"id\":\"demo\",\"displayName\":\"Demo\","
            + "\"support\":\"supported\",\"pipelines\":[\"image\"],\"templateFile\":\"z-image-smoke-api.json\","
            + "\"mainLoader\":{\"node\":\"1\",\"classType\":\"UNETLoader\",\"input\":\"unet_name\",\"folder\":\"diffusion_models\"},"
            + "\"requiredNodes\":[\"UNETLoader\"],\"signature\":{\"requiredStems\":[\"a.\"]},\"companions\":[{\"role\":\"text_encoder\","
            + "\"folder\":\"text_encoders\",\"fileName\":\"te.safetensors\",\"clipType\":\"demo\",\"license\":\"example-license-1.0\","
            + "\"source\":{\"repository\":\"owner/repo\",\"path\":\"split/te.safetensors\","
            + "\"revision\":\"0123456789abcdef0123456789abcdef01234567\",\"sizeBytes\":" + content.Length + ","
            + "\"sha256\":\"" + sha256 + "\"}}]}]}");
    }

    private static MediaCompanionPlan PinnedPlan(MediaCompanionRegistry registry, string modelsRoot)
    {
        var installed = MediaCompanionResolver.ScanFolders(modelsRoot, new[] { "text_encoders" });
        var detection = new MediaFamilyDetection("demo", "Demo", "supported", null, false, false, 1);
        return MediaCompanionResolver.BuildPlan("image", "main.safetensors", detection, registry, installed);
    }

    [Fact]
    public async Task A_pinned_companion_is_downloaded_verified_and_moved_into_its_comfyui_folder()
    {
        byte[] content = Encoding.ASCII.GetBytes("synthetic companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);
        Assert.True(plan.Installable);
        MediaCompanionItem companion = Assert.Single(plan.Missing);
        Assert.Equal("example-license-1.0", companion.License);
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(new ModelDownloadRequest
        {
            RepoId = companion.Repository!,
            Revision = companion.Revision,
            FileName = companion.FileName,
            DownloadUrl = MediaCompanionFetcher.ResolveUrlFor(companion),
            SizeBytes = companion.PinnedSizeBytes ?? 0
        }));

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Transfer(new ServeBytesHandler(content)), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "text_encoders/te.safetensors" }, result.Installed);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(models, "text_encoders", "te.safetensors")));
        Assert.Contains("Retry the media job", result.Message);
    }

    [Fact]
    public async Task A_download_with_the_pinned_size_but_another_digest_is_discarded_and_named()
    {
        // Both payloads are 24 bytes, so only the SHA-256 pin can tell them apart.
        byte[] pinned = Encoding.ASCII.GetBytes("expected companion bytes");
        byte[] served = Encoding.ASCII.GetBytes("tampered companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(pinned), models);

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Transfer(new ServeBytesHandler(served)), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("fetch te.safetensors", result.FailedStep);
        Assert.Contains("SHA-256", result.Message);
        Assert.Empty(result.Installed);
        Assert.False(File.Exists(Path.Combine(models, "text_encoders", "te.safetensors")));
        Assert.False(File.Exists(Path.Combine(_dir, "managed", "te.safetensors")));
    }

    [Fact]
    public async Task A_transfer_that_keeps_failing_is_attempted_twice_then_reported_at_its_step()
    {
        byte[] content = Encoding.ASCII.GetBytes("unavailable companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);

        var handler = new ServeBytesHandler(content, failGet: true);
        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Transfer(handler), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("fetch te.safetensors", result.FailedStep);
        Assert.Contains("te.safetensors", result.Message);
        Assert.Equal(2, handler.CompanionGetCount); // a literal on purpose: the contract is two attempts per file
        Assert.Empty(result.Installed);
        Assert.False(File.Exists(Path.Combine(models, "text_encoders", "te.safetensors")));
    }

    [Fact]
    public async Task A_companion_that_appears_with_its_pinned_size_after_planning_is_not_downloaded()
    {
        // The plan was made while the file was missing. At fetch time it is in place with the pinned size, so it counts
        // as installed and no transfer starts (the NoNetworkHandler would fail the fetch if one were attempted).
        byte[] content = Encoding.ASCII.GetBytes("synthetic companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);
        Assert.Single(plan.Missing);

        Directory.CreateDirectory(Path.Combine(models, "text_encoders"));
        using (FileStream stream = File.Create(Path.Combine(models, "text_encoders", "te.safetensors")))
            stream.SetLength(content.Length);

        MediaCompanionFetchResult result = await new MediaCompanionFetcher(Downloads(), models)
            .FetchAsync(plan, Request(plan), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "text_encoders/te.safetensors" }, result.Installed);
    }

    [Fact]
    public async Task Concurrent_fetches_for_the_same_plan_transfer_once_and_the_waiter_reports_installed()
    {
        byte[] content = Encoding.ASCII.GetBytes("synthetic companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);
        var handler = new GatedServeBytesHandler(content);
        ModelDownloadService downloads = Transfer(handler);
        var firstFetcher = new MediaCompanionFetcher(downloads, models);
        var secondFetcher = new MediaCompanionFetcher(downloads, models);

        Task<MediaCompanionFetchResult> first = firstFetcher.FetchAsync(plan, Request(plan), CancellationToken.None);
        Task<MediaCompanionFetchResult>? second = null;
        bool secondWasPending = false;
        try
        {
            await handler.DownloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            second = secondFetcher.FetchAsync(plan, Request(plan), CancellationToken.None);
            secondWasPending = !second.IsCompleted;
        }
        finally
        {
            handler.ReleaseTransfer();
        }

        Assert.True(secondWasPending);
        MediaCompanionFetchResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(results, result => Assert.True(result.Ok, result.Message));
        Assert.Equal(1, handler.CompanionGetCount);
        Assert.All(results, result => Assert.Equal(new[] { "text_encoders/te.safetensors" }, result.Installed));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(models, "text_encoders", "te.safetensors")));
    }

    [Fact]
    public async Task Cancelling_a_fetch_stops_its_download_before_releasing_the_plan_waiter()
    {
        byte[] content = Encoding.ASCII.GetBytes("synthetic companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);
        var handler = new GatedServeBytesHandler(content);
        ModelDownloadService downloads = Transfer(handler);
        var firstFetcher = new MediaCompanionFetcher(downloads, models);
        var secondFetcher = new MediaCompanionFetcher(downloads, models);
        using var cancellation = new CancellationTokenSource();

        Task<MediaCompanionFetchResult> first = firstFetcher.FetchAsync(plan, Request(plan), cancellation.Token);
        Task<MediaCompanionFetchResult>? second = null;
        try
        {
            await handler.DownloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            second = secondFetcher.FetchAsync(plan, Request(plan), CancellationToken.None);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(10));
            await handler.SecondDownloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            handler.ReleaseTransfer();
        }

        MediaCompanionFetchResult result = await second!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "text_encoders/te.safetensors" }, result.Installed);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(models, "text_encoders", "te.safetensors")));
        Assert.Equal(2, handler.CompanionGetCount);
    }

    [Fact]
    public async Task A_cancelled_fetch_releases_the_plan_waiter_only_after_its_transfer_has_stopped()
    {
        byte[] content = Encoding.ASCII.GetBytes("synthetic companion bytes");
        string models = Models();
        MediaCompanionPlan plan = PinnedPlan(PinnedTo(content), models);
        var handler = new StubbornCancelHandler(content);
        ModelDownloadService downloads = Transfer(handler);
        var firstFetcher = new MediaCompanionFetcher(downloads, models);
        var secondFetcher = new MediaCompanionFetcher(downloads, models);
        using var cancellation = new CancellationTokenSource();

        Task<MediaCompanionFetchResult> first = firstFetcher.FetchAsync(plan, Request(plan), cancellation.Token);
        Task<MediaCompanionFetchResult>? second = null;
        try
        {
            await handler.DownloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
            second = secondFetcher.FetchAsync(plan, Request(plan), CancellationToken.None);
            cancellation.Cancel();
            await handler.CancelObserved.WaitAsync(TimeSpan.FromSeconds(10));

            // The cancelled GET is still open: the cancelled request must not have returned, and the waiter must not have started.
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(first.IsCompleted);
            Assert.False(handler.SecondDownloadStarted.IsCompleted);
        }
        finally
        {
            handler.AllowCancelledTransferToStop();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(20));
        await handler.SecondDownloadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        handler.ReleaseSecondTransfer();

        MediaCompanionFetchResult result = await second!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "text_encoders/te.safetensors" }, result.Installed);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(models, "text_encoders", "te.safetensors")));
        Assert.Equal(2, handler.CompanionGetCount);
    }
}
