using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Transfer engine behaviour against a real loopback HTTP server: stalls, connect timeouts, Retry-After,
/// classified refusals, the redirect allow-list, explicit pause/resume, the Gaming gate and speed/ETA.
/// Deterministic: no internet, an isolated temporary models directory and short policy windows.
/// </summary>
public sealed class ModelDownloadTransferEngineTests
{
    private static readonly string Revision = new('d', 40);

    /// <summary>Scripted per-request behaviour for the loopback source. Requests are numbered per GET.</summary>
    private sealed class Plan
    {
        public int Status { get; init; } = 200;
        public string? RedirectTo { get; init; }
        public Dictionary<string, string> Headers { get; } = new();
        public int HeaderDelayMs { get; init; }
        /// <summary>Send only this many body bytes, then hold the connection open for <see cref="HoldMs"/>.</summary>
        public long? StopAfterBytes { get; init; }
        public int HoldMs { get; init; }
        public int ChunkBytes { get; init; } = 8192;
        public int ChunkDelayMs { get; init; }
        /// <summary>Body length this response declares and sends, when it differs from the whole file.</summary>
        public long? ContentLength { get; init; }
    }

    private sealed record Exchange(string Method, string Path, long? RangeFrom, string? Authorization);

    /// <summary>Loopback source backed by HttpListener on 127.0.0.1 with a random port (retried on clashes).</summary>
    private sealed class LoopbackModelServer : IDisposable
    {
        private HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private int _gets;

        public LoopbackModelServer(byte[] content)
        {
            Content = content;
            for (int attempt = 0; ; attempt++)
            {
                int port = Random.Shared.Next(20000, 60000);
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Port = port;
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    // The runtime closes a listener whose Start failed (port taken), so the next port needs a fresh one.
                    _listener = new HttpListener();
                }
            }
            _ = Task.Run(Loop);
        }

        public byte[] Content { get; }
        public int Port { get; }
        public Func<int, string, Plan> Planner { get; set; } = (_, _) => new Plan();
        /// <summary>Digest reported on every response as X-Linked-ETag, the way the hub reports it for LFS files. Null omits it.</summary>
        public string? LinkedSha256 { get; set; }
        public List<Exchange> Exchanges { get; } = new();

        public IReadOnlyList<Exchange> Gets()
        {
            lock (_gate) return Exchanges.Where(e => e.Method == "GET").ToList();
        }

        public IReadOnlyList<Exchange> Snapshot()
        {
            lock (_gate) return Exchanges.ToList();
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Serve(context));
            }
        }

        private async Task Serve(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                long? rangeFrom = null;
                string? range = request.Headers["Range"];
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal)
                    && long.TryParse(range[6..].TrimEnd('-'), out long parsedFrom))
                    rangeFrom = parsedFrom;

                int getNumber;
                string path = request.Url!.AbsolutePath;
                lock (_gate)
                {
                    getNumber = request.HttpMethod == "GET" ? ++_gets : 0;
                    Exchanges.Add(new Exchange(request.HttpMethod, path, rangeFrom, request.Headers["Authorization"]));
                }
                Plan plan = request.HttpMethod == "GET" ? Planner(getNumber, path) : new Plan();
                if (plan.HeaderDelayMs > 0) await Task.Delay(plan.HeaderDelayMs, _stop.Token);
                foreach (KeyValuePair<string, string> header in plan.Headers)
                    response.Headers[header.Key] = header.Value;
                if (LinkedSha256 is not null) response.Headers["X-Linked-ETag"] = "\"" + LinkedSha256 + "\"";

                if (plan.RedirectTo != null)
                {
                    response.StatusCode = 302;
                    response.Headers["Location"] = plan.RedirectTo;
                    response.Close();
                    return;
                }
                if (plan.Status >= 400)
                {
                    response.StatusCode = plan.Status;
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }

                bool ranged = rangeFrom is > 0 && rangeFrom.Value < Content.Length;
                long start = ranged ? rangeFrom!.Value : 0;
                response.StatusCode = ranged ? 206 : 200;
                long length = plan.ContentLength ?? (Content.Length - start);
                response.ContentLength64 = length;
                if (ranged) response.Headers["Content-Range"] = $"bytes {start}-{Content.Length - 1}/{Content.Length}";
                if (request.HttpMethod == "HEAD")
                {
                    response.Close();
                    return;
                }

                long limit = Math.Min(plan.StopAfterBytes ?? long.MaxValue, length);
                long sent = 0;
                for (long offset = start; offset < Content.Length && sent < limit;)
                {
                    int count = (int)Math.Min(Math.Min(plan.ChunkBytes, Content.Length - offset), limit - sent);
                    await response.OutputStream.WriteAsync(Content.AsMemory((int)offset, count));
                    await response.OutputStream.FlushAsync();
                    offset += count;
                    sent += count;
                    if (plan.ChunkDelayMs > 0) await Task.Delay(plan.ChunkDelayMs);
                }
                if (plan.StopAfterBytes is long && plan.HoldMs > 0) await Task.Delay(plan.HoldMs);
                response.Close();
            }
            catch
            {
                try { response.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    /// <summary>Rewrites the validated huggingface.co URL to the loopback source. Redirects are not followed
    /// inside the handler, so the service sees every hop and applies its own allow-list.</summary>
    private sealed class LoopbackRewriteHandler : DelegatingHandler
    {
        private readonly int _port;

        public LoopbackRewriteHandler(int port) : base(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            _port = port;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{_port}{request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>An isolated models directory for one test (the production path is never touched).</summary>
    private sealed class TempModels : IDisposable
    {
        private readonly IDisposable _scope;

        public TempModels()
        {
            Root = Path.Combine(Path.GetTempPath(), "parux-transfer-" + Guid.NewGuid().ToString("N"));
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

    private static ModelDownloadTransferPolicy Policy(TimeSpan? stall = null, TimeSpan? connect = null, int maxAttempts = 3) => new()
    {
        StallTimeout = stall ?? TimeSpan.FromMilliseconds(400),
        ConnectTimeout = connect ?? TimeSpan.FromSeconds(5),
        MaxAttempts = maxAttempts,
        RetryBaseDelay = TimeSpan.FromMilliseconds(40),
        RetryMaxDelay = TimeSpan.FromMilliseconds(200),
    };

    private static ModelDownloadService CreateService(LoopbackModelServer server, ModelDownloadTransferPolicy policy,
        Func<bool>? gaming = null, string? hubToken = null)
    {
        Func<string?>? tokenProvider = hubToken is null ? null : () => hubToken;
        return new ModelDownloadService(new HttpClient(new LoopbackRewriteHandler(server.Port)),
            new HttpClient(new LoopbackRewriteHandler(server.Port)), hubTokenProvider: tokenProvider)
        {
            TransferPolicy = policy,
            GamingActive = gaming ?? (() => false),
        };
    }

    private static ModelDownloadRequest Request(string fileName) => new()
    {
        RepoId = "test/transfer-engine",
        Revision = Revision,
        FileName = fileName,
        DownloadUrl = "https://huggingface.co/test/transfer-engine/resolve/" + Revision + "/" + fileName,
        SizeBytes = 0
    };

    private static byte[] RandomBytes(int size, int seed)
    {
        byte[] bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            T? value = probe();
            if (value != null) return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition was not reached within " + timeout.TotalSeconds + " s.");
    }

    private static Task<ModelDownloadStatus> WaitForStatusAsync(ModelDownloadService service, string id,
        Func<ModelDownloadStatus, bool> match, TimeSpan timeout) =>
        WaitForAsync(() => service.Get(id) is { } status && match(status) ? status : null, timeout);

    [Fact]
    public async Task A_stalled_attempt_is_transient_and_the_retry_resumes_from_the_partial()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(256 * 1024, 11);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (getNumber, _) => getNumber == 1 ? new Plan { StopAfterBytes = 64 * 1024, HoldMs = 5000 } : new Plan()
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("stall.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));

        Assert.Equal("completed", done.Status);
        Assert.Equal(2, done.Attempt);
        Assert.Null(done.FailureClass);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "stall.gguf")));
        Assert.Contains(server.Gets(), e => e.RangeFrom == 64 * 1024);
    }

    [Fact]
    public async Task A_slow_transfer_with_gaps_shorter_than_the_stall_window_completes_however_long_it_takes()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(256 * 1024, 12);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (_, _) => new Plan { ChunkBytes = 8192, ChunkDelayMs = 60 }
        };
        ModelDownloadService service = CreateService(server, Policy(stall: TimeSpan.FromMilliseconds(400)));

        var clock = Stopwatch.StartNew();
        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("slow.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(60));
        clock.Stop();

        Assert.Equal("completed", done.Status);
        Assert.Equal(1, done.Attempt);
        // Several times longer than the stall window: there is no whole-transfer deadline.
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(1.5), "the transfer must outlast the stall window");
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "slow.gguf")));
    }

    [Fact]
    public async Task A_source_that_never_answers_fails_as_a_bounded_transient_connect_timeout()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(64 * 1024, 13))
        {
            Planner = (_, _) => new Plan { HeaderDelayMs = 4000 }
        };
        ModelDownloadService service = CreateService(server,
            Policy(connect: TimeSpan.FromMilliseconds(300), maxAttempts: 1));

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("timeout.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("transient", done.FailureClass);
        Assert.Contains("timed out", done.Error);
        Assert.False(string.IsNullOrWhiteSpace(done.Remediation));
        Assert.True(done.RetryRecommended);
    }

    [Fact]
    public async Task A_429_with_Retry_After_waits_at_least_that_long_and_then_completes()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 14);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (getNumber, _) => getNumber == 1
                ? new Plan { Status = 429, Headers = { ["Retry-After"] = "1" } }
                : new Plan()
        };
        ModelDownloadService service = CreateService(server, Policy());

        var clock = Stopwatch.StartNew();
        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("limited.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));
        clock.Stop();

        Assert.Equal("completed", done.Status);
        Assert.Equal(2, done.Attempt);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), "Retry-After must be honoured");
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "limited.gguf")));
    }

    [Fact]
    public async Task A_missing_file_is_a_permanent_failure_with_one_attempt_and_no_retry_offer()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(4096, 15))
        {
            Planner = (_, _) => new Plan { Status = 404 }
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("gone.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("permanent", done.FailureClass);
        Assert.Equal(404, done.HttpStatusCode);
        Assert.Equal(1, done.Attempt);
        Assert.False(done.RetryRecommended);
        Assert.Single(server.Gets());
    }

    [Fact]
    public async Task A_gated_file_without_a_token_is_an_auth_failure_that_names_the_fix()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(4096, 16))
        {
            Planner = (_, _) => new Plan { Status = 403 }
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("gated.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("auth", done.FailureClass);
        Assert.Equal(403, done.HttpStatusCode);
        Assert.Contains("access token", done.Remediation);
        Assert.Contains("Token required", done.Error);
    }

    [Fact]
    public async Task A_redirect_outside_the_hub_allow_list_is_refused_before_any_connection_is_made()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(4096, 17))
        {
            Planner = (_, _) => new Plan { RedirectTo = "https://evil.example.test/evil-file" }
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("redirect.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("permanent", done.FailureClass);
        Assert.Contains("allow-list", done.Error);
        Assert.DoesNotContain(server.Snapshot(), e => e.Path == "/evil-file");
    }

    [Fact]
    public async Task Pause_keeps_the_partial_as_an_exact_prefix_and_resume_continues_byte_exact()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(512 * 1024, 18);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (getNumber, _) => getNumber == 1 ? new Plan { ChunkDelayMs = 30 } : new Plan()
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("pause.gguf"), CancellationToken.None);
        await WaitForStatusAsync(service, started.Id, s => s.BytesDownloaded > 64 * 1024, TimeSpan.FromSeconds(20));
        Assert.True(service.Pause(started.Id));

        ModelDownloadStatus paused = await WaitForStatusAsync(service, started.Id,
            s => s.Status == "paused", TimeSpan.FromSeconds(10));
        Assert.Null(paused.Error);
        Assert.Null(paused.FailureClass);

        string partialPath = Path.Combine(models.Root, "pause.gguf.partial");
        long frozen = new FileInfo(partialPath).Length;
        await Task.Delay(300);
        Assert.Equal(frozen, new FileInfo(partialPath).Length);
        byte[] prefix = await File.ReadAllBytesAsync(partialPath);
        Assert.True(prefix.AsSpan().SequenceEqual(content.AsSpan(0, prefix.Length)), "partial must be an exact prefix");
        Assert.False(File.Exists(Path.Combine(models.Root, "pause.gguf")));

        ModelDownloadStatus resumed = await service.CreateDownloadAsync(
            new ModelDownloadRequest { ResumeFromDownloadId = started.Id }, CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, resumed.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));

        Assert.Equal("completed", done.Status);
        Assert.Contains(server.Gets(), e => e.RangeFrom == frozen);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "pause.gguf")));
        Assert.Equal("paused", service.Get(started.Id)!.Status);
    }

    [Fact]
    public async Task A_queued_job_does_not_start_while_Gaming_holds_the_gate()
    {
        using var models = new TempModels();
        var gate = new StrongBox<bool>(true);
        using var server = new LoopbackModelServer(RandomBytes(64 * 1024, 19));
        ModelDownloadService service = CreateService(server, Policy(), () => gate.Value);

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("gaming.gguf"), CancellationToken.None);
        ModelDownloadStatus paused = await WaitForStatusAsync(service, started.Id,
            s => s.Status == "paused", TimeSpan.FromSeconds(10));

        Assert.Equal("Paused for Gaming", paused.Error);
        Assert.Equal(0, paused.BytesDownloaded);
        Assert.Empty(server.Gets());
        Assert.False(File.Exists(Path.Combine(models.Root, "gaming.gguf.partial")));
    }

    [Fact]
    public async Task Speed_and_ETA_are_reported_while_bytes_flow_and_the_job_completes_afterwards()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(512 * 1024, 20);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (_, _) => new Plan { ChunkDelayMs = 40 }
        };
        ModelDownloadService service = CreateService(server, Policy(stall: TimeSpan.FromSeconds(5)));

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("speed.gguf"), CancellationToken.None);
        ModelDownloadStatus measuring = await WaitForAsync(() =>
            service.Get(started.Id) is { Status: "downloading", SpeedBytesPerSecond: > 0 } s ? s : null,
            TimeSpan.FromSeconds(20));

        Assert.NotNull(measuring.EtaSeconds);
        Assert.True(measuring.TotalBytes > measuring.BytesDownloaded);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", done.Status);
        Assert.Null(done.SpeedBytesPerSecond);
        Assert.Null(done.EtaSeconds);
    }

    [Fact]
    public async Task The_saved_token_goes_to_the_hub_host_and_never_to_a_redirected_file_host()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 21);
        using var server = new LoopbackModelServer(content)
        {
            Planner = (getNumber, _) => getNumber == 1
                ? new Plan { RedirectTo = "https://cdn-lfs.hf.co/cdn/gated-file" }
                : new Plan()
        };
        ModelDownloadService service = CreateService(server, Policy(), hubToken: "hf_TestToken123");

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("gated-token.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(20));

        Assert.Equal("completed", done.Status);
        IReadOnlyList<Exchange> exchanges = server.Snapshot();
        Assert.Contains(exchanges, e => e.Path.StartsWith("/test/transfer-engine/", StringComparison.Ordinal)
            && e.Authorization == "Bearer hf_TestToken123");
        Assert.Contains(exchanges, e => e.Path == "/cdn/gated-file" && e.Authorization is null);
        Assert.DoesNotContain(exchanges, e => e.Path.Contains("hf_TestToken123", StringComparison.Ordinal));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "gated-token.gguf")));
    }

    [Fact]
    public async Task A_refused_token_on_a_gated_file_is_an_auth_failure_that_offers_retry_once_a_token_is_saved()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(4096, 22))
        {
            Planner = (_, _) => new Plan { Status = 403 }
        };
        ModelDownloadService service = CreateService(server, Policy(), hubToken: "hf_RefusedToken");

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("refused.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("auth", done.FailureClass);
        Assert.Equal(403, done.HttpStatusCode);
        Assert.Contains("token refused", done.Error);
        Assert.True(done.RetryRecommended);
        Assert.Single(server.Gets());
    }

    [Fact]
    public async Task A_shared_bandwidth_limit_paces_the_transfer_and_is_applied_to_the_next_chunk_at_once()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(512 * 1024, 23);
        using var server = new LoopbackModelServer(content);
        ModelDownloadService service = CreateService(server, Policy());
        service.SetBandwidthLimitMegabytesPerSecond(0.5);

        var clock = Stopwatch.StartNew();
        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("paced.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));
        clock.Stop();

        Assert.Equal("completed", done.Status);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.7),
            $"512 KiB at 0.5 MiB/s must take about a second (took {clock.Elapsed.TotalMilliseconds:F0} ms, attempt {done.Attempt})");
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "paced.gguf")));
    }

    [Fact]
    public async Task Admission_refuses_a_download_that_cannot_fit_on_the_model_drive_and_names_the_bytes()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(256 * 1024, 24));
        var service = new ModelDownloadService(new HttpClient(new LoopbackRewriteHandler(server.Port)),
            new HttpClient(new LoopbackRewriteHandler(server.Port)),
            destinationSpaceProvider: new DelegateSpace(() => 512L << 20))
        {
            TransferPolicy = Policy()
        };

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateDownloadAsync(Request("nospace.gguf"), CancellationToken.None));
        Assert.Contains("Not enough free space", refusal.Message);
        // The free figure is written in the host culture ("0.5" or "0,5"), so both separators are accepted.
        Assert.Matches(@"but 0[.,]5 GB is free", refusal.Message);
        Assert.Empty(service.List());
        Assert.False(File.Exists(Path.Combine(models.Root, "nospace.gguf.partial")));
    }

    [Fact]
    public async Task Free_space_lost_while_streaming_fails_as_a_disk_class_and_keeps_the_partial()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(256 * 1024, 25));
        int probes = 0;
        var service = new ModelDownloadService(new HttpClient(new LoopbackRewriteHandler(server.Port)),
            new HttpClient(new LoopbackRewriteHandler(server.Port)),
            destinationSpaceProvider: new DelegateSpace(() => Interlocked.Increment(ref probes) <= 2 ? 1L << 40 : 100L * 1024))
        {
            TransferPolicy = new ModelDownloadTransferPolicy
            {
                StallTimeout = TimeSpan.FromSeconds(5),
                FreeSpaceReserveBytes = 1L << 20,
                FreeSpaceCheckIntervalBytes = 16L << 10,
                RetryBaseDelay = TimeSpan.FromMilliseconds(40),
            }
        };

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("lowdisk.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(20));

        Assert.Equal("failed", done.Status);
        Assert.Equal("disk", done.FailureClass);
        Assert.Contains("Free space", done.Remediation);
        Assert.Equal(1, done.Attempt);
        Assert.False(File.Exists(Path.Combine(models.Root, "lowdisk.gguf")));
        Assert.True(new FileInfo(Path.Combine(models.Root, "lowdisk.gguf.partial")).Length >= 16 * 1024);
    }

    [Fact]
    public async Task A_managed_budget_that_runs_out_while_streaming_fails_as_a_disk_class_and_keeps_the_partial()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(256 * 1024, 27));
        int readings = 0;
        var service = new ModelDownloadService(new HttpClient(new LoopbackRewriteHandler(server.Port)),
            new HttpClient(new LoopbackRewriteHandler(server.Port)),
            destinationSpaceProvider: new DelegateSpace(() => 1L << 40))
        {
            TransferPolicy = new ModelDownloadTransferPolicy
            {
                StallTimeout = TimeSpan.FromSeconds(5),
                BudgetCheckIntervalBytes = 16L << 10,
                RetryBaseDelay = TimeSpan.FromMilliseconds(40),
            },
            // The first reading is the admission check and still fits; every reading taken while bytes stream reports the budget spent.
            BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
            {
                LimitBytes = AiStorageBudget.LimitBytes,
                WithinBudget = Interlocked.Increment(ref readings) == 1,
            }),
        };

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("overbudget.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(20));

        Assert.Equal("failed", done.Status);
        Assert.Equal("disk", done.FailureClass);
        Assert.Contains("budget", done.Error);
        Assert.Equal(1, done.Attempt);
        Assert.True(readings >= 2, "the budget must be re-read while the file streams, not only at admission");
        Assert.False(File.Exists(Path.Combine(models.Root, "overbudget.gguf")));
        Assert.True(new FileInfo(Path.Combine(models.Root, "overbudget.gguf.partial")).Length >= 16 * 1024);
    }

    [Fact]
    public async Task A_verified_file_already_in_place_is_answered_without_a_transfer()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 28);
        using var server = new LoopbackModelServer(content) { LinkedSha256 = Sha256Hex(content) };
        ModelDownloadService service = CreateService(server, Policy());
        Assert.Equal("completed", (await DownloadToCompletionAsync(service, "inplace.gguf")).Status);
        int gets = server.Exchanges.FindAll(e => e.Method == "GET").Count;

        ModelDownloadStatus again = await service.CreateDownloadAsync(Request("inplace.gguf"), CancellationToken.None);

        Assert.Equal("completed", again.Status);
        Assert.Equal("verified", again.VerificationStatus);
        Assert.Equal(content.Length, again.BytesDownloaded);
        Assert.Equal(gets, server.Exchanges.FindAll(e => e.Method == "GET").Count);
        Assert.True(File.Exists(Path.Combine(models.Root, "inplace.gguf")));
    }

    [Fact]
    public async Task A_verified_file_whose_bytes_changed_since_verification_is_downloaded_again()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 29);
        using var server = new LoopbackModelServer(content) { LinkedSha256 = Sha256Hex(content) };
        ModelDownloadService service = CreateService(server, Policy());
        Assert.Equal("completed", (await DownloadToCompletionAsync(service, "changed.gguf")).Status);
        int gets = server.Exchanges.FindAll(e => e.Method == "GET").Count;
        // Same length, later last-write time: the stored provenance no longer describes these bytes.
        string path = Path.Combine(models.Root, "changed.gguf");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        ModelDownloadStatus again = await service.CreateDownloadAsync(Request("changed.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, again.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("completed", done.Status);
        Assert.Equal(gets + 1, server.Exchanges.FindAll(e => e.Method == "GET").Count);
    }

    [Fact]
    public async Task A_verified_file_is_still_answered_from_its_provenance_when_the_hub_reports_no_digest_now()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 30);
        using var server = new LoopbackModelServer(content) { LinkedSha256 = Sha256Hex(content) };
        ModelDownloadService service = CreateService(server, Policy());
        Assert.Equal("completed", (await DownloadToCompletionAsync(service, "offline.gguf")).Status);
        int gets = server.Exchanges.FindAll(e => e.Method == "GET").Count;
        server.LinkedSha256 = null;

        ModelDownloadStatus again = await service.CreateDownloadAsync(Request("offline.gguf"), CancellationToken.None);

        Assert.Equal("completed", again.Status);
        Assert.Equal("verified", again.VerificationStatus);
        Assert.Equal(gets, server.Exchanges.FindAll(e => e.Method == "GET").Count);
    }

    [Fact]
    public async Task A_file_copied_into_the_folder_without_a_provenance_record_is_downloaded_not_trusted()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(64 * 1024, 31);
        using var server = new LoopbackModelServer(content) { LinkedSha256 = Sha256Hex(content) };
        await File.WriteAllBytesAsync(Path.Combine(models.Root, "handcopy.gguf"), content);
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus done = await DownloadToCompletionAsync(service, "handcopy.gguf");

        Assert.Equal("completed", done.Status);
        Assert.Equal(1, server.Exchanges.FindAll(e => e.Method == "GET").Count);
    }

    /// <summary>Downloads one file through the real loopback path and returns its final status.</summary>
    private static async Task<ModelDownloadStatus> DownloadToCompletionAsync(ModelDownloadService service, string fileName)
    {
        ModelDownloadStatus started = await service.CreateDownloadAsync(Request(fileName), CancellationToken.None);
        return await WaitForStatusAsync(service, started.Id, s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));
    }

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    [Fact]
    public async Task A_source_that_delivers_fewer_bytes_than_it_declared_never_completes_the_file()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(64 * 1024, 26))
        {
            Planner = (_, _) => new Plan { ContentLength = 16 * 1024 }
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("short.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(15));

        Assert.Equal("failed", done.Status);
        Assert.Equal("permanent", done.FailureClass);
        Assert.Contains("declared", done.Error);
        Assert.False(File.Exists(Path.Combine(models.Root, "short.gguf")));
    }

    [Fact]
    public async Task A_connection_dropped_mid_body_is_transient_and_the_retry_resumes_from_the_partial()
    {
        using var models = new TempModels();
        byte[] content = RandomBytes(256 * 1024, 30);
        using var server = new LoopbackModelServer(content)
        {
            // Declares the whole body, sends 64 KiB, then closes the connection: the usual shape of a network drop.
            Planner = (getNumber, _) => getNumber == 1 ? new Plan { StopAfterBytes = 64 * 1024 } : new Plan()
        };
        ModelDownloadService service = CreateService(server, Policy());

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("dropped.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(30));

        Assert.True(done.Status == "completed", $"status {done.Status}, failure class {done.FailureClass}: {done.Error}");
        Assert.Equal(2, done.Attempt);
        Assert.Null(done.FailureClass);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(models.Root, "dropped.gguf")));
        // Resumed from whatever reached the disk before the reset: the exact cut point depends on the socket, the offset does not.
        Assert.Contains(server.Gets(), e => e.RangeFrom is > 0);
    }

    [Fact]
    public async Task A_transient_failure_stops_when_the_attempt_budget_is_spent_and_offers_retry()
    {
        using var models = new TempModels();
        using var server = new LoopbackModelServer(RandomBytes(4096, 31))
        {
            Planner = (_, _) => new Plan { Status = 503 }
        };
        ModelDownloadService service = CreateService(server, Policy(maxAttempts: 3));

        ModelDownloadStatus started = await service.CreateDownloadAsync(Request("busy.gguf"), CancellationToken.None);
        ModelDownloadStatus done = await WaitForStatusAsync(service, started.Id,
            s => s.Status is "completed" or "failed", TimeSpan.FromSeconds(20));

        Assert.Equal("failed", done.Status);
        Assert.Equal("transient", done.FailureClass);
        Assert.Equal(3, done.Attempt);
        Assert.Equal(3, done.MaxAttempts);
        Assert.Equal(3, server.Gets().Count);
        Assert.Contains("retries were exhausted", done.Remediation);
        Assert.True(done.RetryRecommended);
        Assert.False(File.Exists(Path.Combine(models.Root, "busy.gguf")));
    }

    /// <summary>Reports a free-space figure chosen by the test (the real drive is never probed here).</summary>
    private sealed class DelegateSpace(Func<long> freeBytes) : IModelDestinationSpaceProvider
    {
        public long? GetAvailableBytes(string destinationDirectory) => freeBytes();
    }
}

/// <summary>Pure policy and classification rules, checked without any network.</summary>
public sealed class ModelDownloadTransferRulesTests
{
    [Fact]
    public void Speed_is_a_moving_average_that_stays_unknown_until_the_window_is_long_enough_and_then_stale()
    {
        var meter = new ModelDownloadSpeedMeter();
        DateTime t0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        meter.Record(0, t0);
        meter.Record(100, t0.AddSeconds(1));
        Assert.Null(meter.SpeedBytesPerSecond(t0.AddSeconds(1)));
        meter.Record(200, t0.AddSeconds(2));
        meter.Record(300, t0.AddSeconds(3));
        Assert.Equal(100d, meter.SpeedBytesPerSecond(t0.AddSeconds(3)));
        Assert.Null(meter.SpeedBytesPerSecond(t0.AddSeconds(20)));
        Assert.Equal(5d, ModelDownloadSpeedMeter.EstimateSeconds(500, 100d));
        Assert.Null(ModelDownloadSpeedMeter.EstimateSeconds(500, null));
    }

    [Fact]
    public void Backoff_grows_exponentially_with_jitter_is_capped_and_honours_Retry_After_up_to_its_cap()
    {
        var policy = new ModelDownloadTransferPolicy();
        var random = new Random(7);
        Assert.InRange(ModelDownloadRetrySchedule.DelayAfter(1, policy, null, random).TotalMilliseconds, 1600d, 2400d);
        Assert.InRange(ModelDownloadRetrySchedule.DelayAfter(3, policy, null, random).TotalMilliseconds, 6400d, 9600d);
        Assert.InRange(ModelDownloadRetrySchedule.DelayAfter(12, policy, null, random).TotalMilliseconds, 48000d, 72000d);
        Assert.True(ModelDownloadRetrySchedule.DelayAfter(1, policy, TimeSpan.FromSeconds(30), random) >= TimeSpan.FromSeconds(30));
        Assert.Equal(policy.RetryAfterCap, ModelDownloadRetrySchedule.DelayAfter(1, policy, TimeSpan.FromHours(3), random));
    }

    [Theory]
    [InlineData(401, false, "auth", "access token")]
    [InlineData(403, true, "auth", "refused")]
    [InlineData(404, false, "permanent", "not found")]
    [InlineData(429, false, "transient", "temporarily")]
    [InlineData(503, false, "transient", "temporarily")]
    [InlineData(418, false, "permanent", "refused")]
    public void HTTP_refusals_map_to_one_failure_class_with_actionable_text(int status, bool tokenSent, string failureClass, string fragment)
    {
        ModelDownloadFailure failure = ModelDownloadFailure.FromHttpStatus((HttpStatusCode)status, tokenSent, TimeSpan.FromSeconds(3));
        Assert.Equal(failureClass, failure.FailureClass);
        Assert.Equal(status, failure.HttpStatusCode);
        Assert.Contains(fragment, failure.Message + " " + failure.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(failureClass == "transient", failure.Retryable);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(401, true)]
    [InlineData(403, false)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    [InlineData(418, false)]
    public void CivitAI_refusals_name_CivitAI_and_never_Hugging_Face(int status, bool tokenSent)
    {
        ModelDownloadFailure failure = ModelDownloadFailure.FromHttpStatus((HttpStatusCode)status, tokenSent, null, civitAi: true);
        string text = failure.Message + " " + failure.Remediation;
        Assert.Contains("CivitAI", text);
        Assert.DoesNotContain("Hugging Face", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("huggingface", text, StringComparison.OrdinalIgnoreCase);
        if (status == 401) Assert.Contains("API key", text);
    }

    [Fact]
    public void Hugging_Face_401_text_is_unchanged_when_the_source_is_not_CivitAI()
    {
        ModelDownloadFailure failure = ModelDownloadFailure.FromHttpStatus(HttpStatusCode.Unauthorized, false, null);
        Assert.Contains("Hugging Face repository needs an access token", failure.Message);
    }

    [Theory]
    [InlineData("https://huggingface.co/a/b/resolve/x/f.gguf", true)]
    [InlineData("https://cdn-lfs.hf.co/x", true)]
    [InlineData("https://cas-bridge.xethub.hf.co/x", true)]
    [InlineData("https://huggingface.co.evil.example/x", false)]
    [InlineData("https://evilhuggingface.co/x", false)]
    [InlineData("http://huggingface.co/x", false)]
    [InlineData("https://example.test/x", false)]
    public void Transfer_hosts_are_limited_to_the_hub_and_its_file_domains(string url, bool allowed) =>
        Assert.Equal(allowed, ModelDownloadHostPolicy.IsAllowedTransferHost(new Uri(url)));

    [Fact]
    public void Only_the_official_hub_host_may_receive_the_access_token()
    {
        Assert.True(ModelDownloadHostPolicy.IsOfficialHubHost("huggingface.co"));
        Assert.False(ModelDownloadHostPolicy.IsOfficialHubHost("cdn-lfs.hf.co"));
    }

    [Fact]
    public void Unknown_size_admission_reserves_the_safe_default_and_declared_sizes_reserve_only_what_is_missing()
    {
        var policy = new ModelDownloadTransferPolicy();
        Assert.Equal(policy.UnknownSizeReserveBytes, ModelDownloadService.AdmissionBytes(0, 0, policy));
        Assert.Equal(policy.UnknownSizeReserveBytes - 1024, ModelDownloadService.AdmissionBytes(0, 1024, policy));
        Assert.Equal(900L, ModelDownloadService.AdmissionBytes(1000, 100, policy));
        Assert.Equal(0L, ModelDownloadService.AdmissionBytes(100, 1000, policy));
    }

    [Fact]
    public async Task The_shared_limiter_paces_the_second_chunk_behind_the_first()
    {
        var limiter = new ModelDownloadRateLimiter();
        limiter.SetMegabytesPerSecond(1);
        await limiter.WaitForAsync(1024 * 1024, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        await limiter.WaitForAsync(256 * 1024, CancellationToken.None);

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.7), "256 KiB behind 1 MiB at 1 MiB/s must wait about 0.9 s");
        Assert.Equal(1d, limiter.MegabytesPerSecond);
    }

    [Fact]
    public void The_bandwidth_limit_is_remembered_across_restarts_and_out_of_range_values_are_refused()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "parux-download-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ModelDownloadService(stateDirectory: stateDirectory);
            Assert.Equal(0d, service.GetTransferSettings().BandwidthLimitMegabytesPerSecond);

            service.SetBandwidthLimitMegabytesPerSecond(2.5);
            Assert.Equal(2.5d, new ModelDownloadService(stateDirectory: stateDirectory).GetTransferSettings().BandwidthLimitMegabytesPerSecond);

            Assert.Throws<ArgumentOutOfRangeException>(() => service.SetBandwidthLimitMegabytesPerSecond(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.SetBandwidthLimitMegabytesPerSecond(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.SetBandwidthLimitMegabytesPerSecond(20000));
            Assert.Equal(2.5d, service.GetTransferSettings().BandwidthLimitMegabytesPerSecond);
        }
        finally
        {
            try { Directory.Delete(stateDirectory, recursive: true); } catch { }
        }
    }
}
