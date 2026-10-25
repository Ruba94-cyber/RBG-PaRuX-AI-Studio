using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Drives the recommendation coordinator in a killable child while a separate loopback runtime process owns the
/// health endpoint. All state and model paths used here are under a unique temporary root.
/// </summary>
public sealed class CandidateRecommendationRealRuntimeKillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-real-rec-runtime", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("before_pending")]
    [InlineData("after_pending")]
    [InlineData("after_load")]
    [InlineData("before_commit")]
    [InlineData("after_commit")]
    public async Task Promote_kill_points_reconcile_against_the_owned_loopback_runtime(string crashPoint)
    {
        string state = StateRoot("promote-" + crashPoint);
        string models = Path.Combine(state, "models");
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateRecommendationStore seeded = SeedActive(state, models);

        await using RuntimeProcess runtimeProcess = await RuntimeProcess.StartAsync(proof.Incumbent, models);
        using var runtime = new LoopbackRecommendationRuntime(runtimeProcess.Port);
        var fence = new LoopbackRecommendationFence(runtime);

        var (exit, _, stderr) = await RunCoordinatorAsync(state, "promote-real", crashPoint,
            seeded.Load().Generation, runtimeProcess.Port, models);
        Assert.True(exit != 0, "coordinator did not die at the requested hook; stderr: " + stderr);
        Assert.Equal(crashPoint, File.ReadAllText(Path.Combine(state, "crashed.txt")));
        Assert.True(await runtimeProcess.ProbeHealthyAsync(), "stand-in did not answer health after coordinator death");

        CandidateRecommendationSnapshot killed = seeded.Load();
        if (crashPoint is "after_pending" or "after_load" or "before_commit")
        {
            Assert.NotNull(killed.Pending);
            Assert.Null(killed.GetActiveRecord());
        }
        else
        {
            Assert.Null(killed.Pending);
            Assert.NotNull(killed.GetActiveRecord());
            await AssertActiveWinnerHasRuntimeEvidenceAsync(killed.GetActiveRecord()!, models, runtimeProcess);
        }

        var recovery = new CandidateRecommendationService(seeded, runtime, new FakeRecProofs(null), fence, models,
            healthBudget: TimeSpan.FromSeconds(2), pinRoot: Path.Combine(state, "pins"),
            reconcileWaitBound: TimeSpan.FromSeconds(2));
        RecommendationTransitionResult result = await recovery.ReconcileAsync(CancellationToken.None);
        CandidateRecommendationSnapshot recovered = seeded.Load();
        Assert.Null(recovered.Pending);

        string actual = await ClassifyRecoveredStateAsync(result, recovered, proof.Challenger, proof.Incumbent,
            models, runtimeProcess);
        Assert.Equal(crashPoint == "after_commit" ? "committed-new" : "incumbent-restored", actual);

        long generation = recovered.Generation;
        _ = await recovery.ReconcileAsync(CancellationToken.None);
        Assert.Equal(generation, seeded.Load().Generation);
        await runtimeProcess.StopAndAssertCleanAsync();
    }

    [Theory]
    [InlineData("before_pending")]
    [InlineData("after_pending")]
    [InlineData("after_load")]
    [InlineData("before_commit")]
    [InlineData("after_commit")]
    public async Task Rollback_kill_points_reconcile_against_the_owned_loopback_runtime(string crashPoint)
    {
        string state = StateRoot("rollback-" + crashPoint);
        string models = Path.Combine(state, "models");
        CandidateUtilityComparisonProof first = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateUtilityComparisonProof second = RecFx.Proof("org/second", "second.gguf", 'c',
            "org/challenger", "challenger.gguf", 'a');
        CandidateRecommendationStore seeded = SeedRollback(state, models);

        await using RuntimeProcess runtimeProcess = await RuntimeProcess.StartAsync(second.Challenger, models);
        using var runtime = new LoopbackRecommendationRuntime(runtimeProcess.Port);
        var fence = new LoopbackRecommendationFence(runtime);

        var (exit, _, stderr) = await RunCoordinatorAsync(state, "rollback-real", crashPoint,
            seeded.Load().Generation, runtimeProcess.Port, models);
        Assert.True(exit != 0, "coordinator did not die at the requested hook; stderr: " + stderr);
        Assert.Equal(crashPoint, File.ReadAllText(Path.Combine(state, "crashed.txt")));
        Assert.True(await runtimeProcess.ProbeHealthyAsync(), "stand-in did not answer health after coordinator death");

        CandidateRecommendationSnapshot killed = seeded.Load();
        if (crashPoint is "after_pending" or "after_load" or "before_commit")
        {
            Assert.NotNull(killed.Pending);
            Assert.Null(killed.GetActiveRecord());
        }
        else
        {
            Assert.Null(killed.Pending);
            Assert.NotNull(killed.GetActiveRecord());
            await AssertActiveWinnerHasRuntimeEvidenceAsync(killed.GetActiveRecord()!, models, runtimeProcess);
        }

        var recovery = new CandidateRecommendationService(seeded, runtime, new FakeRecProofs(null), fence, models,
            healthBudget: TimeSpan.FromSeconds(2), pinRoot: Path.Combine(state, "pins"),
            reconcileWaitBound: TimeSpan.FromSeconds(2));
        RecommendationTransitionResult result = await recovery.ReconcileAsync(CancellationToken.None);
        CandidateRecommendationSnapshot recovered = seeded.Load();
        Assert.Null(recovered.Pending);

        string actual = await ClassifyRecoveredStateAsync(result, recovered, first.Challenger, second.Challenger,
            models, runtimeProcess);
        Assert.Equal(crashPoint == "after_commit" ? "committed-new" : "incumbent-restored", actual);

        long generation = recovered.Generation;
        _ = await recovery.ReconcileAsync(CancellationToken.None);
        Assert.Equal(generation, seeded.Load().Generation);
        await runtimeProcess.StopAndAssertCleanAsync();
    }

    [Fact]
    public async Task Alive_but_unhealthy_challenger_is_not_promoted()
    {
        string state = StateRoot("unhealthy-challenger");
        string models = Path.Combine(state, "models");
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateRecommendationStore seeded = SeedActive(state, models);
        string unhealthyPath = Path.GetFullPath(Path.Combine(models, proof.Challenger.Identity.ManagedFileName));

        await using RuntimeProcess runtimeProcess = await RuntimeProcess.StartAsync(proof.Incumbent, models, unhealthyPath);
        using var runtime = new LoopbackRecommendationRuntime(runtimeProcess.Port);
        (int exit, string output, string stderr) = await RunCoordinatorAsync(state, "promote-real", "none",
            seeded.Load().Generation, runtimeProcess.Port, models);

        Assert.Equal(3, exit);
        Assert.Contains("FailedRestored:challenger_unhealthy:", output, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);
        Assert.Null(seeded.Load().Pending);
        CandidateRecommendationRecord active = Assert.IsType<CandidateRecommendationRecord>(seeded.Load().GetActiveRecord());
        Assert.Equal("baseline.gguf", active.Proof.Challenger.Identity.ManagedFileName);
        RuntimeProcessStatus status = await runtimeProcess.GetStatusAsync();
        Assert.True(status.Running);
        Assert.True(status.Healthy);
        Assert.True(status.UnhealthyHealthProbeCount > 0, "coordinator never received the stand-in's unhealthy response");
        Assert.True(await runtimeProcess.ProbeHealthyAsync());
        await AssertActiveWinnerHasRuntimeEvidenceAsync(active, models, runtimeProcess);
        await runtimeProcess.StopAndAssertCleanAsync();
    }

    private string StateRoot(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "models"));
        return path;
    }

    internal static CandidateRecommendationStore SeedActive(string state, string models)
    {
        var store = new CandidateRecommendationStore(state);
        CandidateUtilityComparisonProof baseline = RecFx.Proof("org/baseline", "baseline.gguf", 'b',
            "org/prior", "prior.gguf", 'd');
        CommitProof(store, baseline, models);
        return store;
    }

    internal static CandidateRecommendationStore SeedRollback(string state, string models)
    {
        var store = new CandidateRecommendationStore(state);
        CandidateUtilityComparisonProof first = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateUtilityComparisonProof second = RecFx.Proof("org/second", "second.gguf", 'c',
            "org/challenger", "challenger.gguf", 'a');
        CommitProof(store, first, models);
        CommitProof(store, second, models);
        return store;
    }

    private static void CommitProof(CandidateRecommendationStore store, CandidateUtilityComparisonProof proof,
        string models)
    {
        CandidateLaunchAdmission AdmissionFor(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            Path.GetFullPath(Path.Combine(models, side.Identity.ManagedFileName)), DateTime.UtcNow);

        RecommendationTransitionChecks ChecksFor(CandidateProofSide side)
        {
            string expectedPath = Path.GetFullPath(Path.Combine(models, side.Identity.ManagedFileName));
            return new RecommendationTransitionChecks(
                () => new TextLaunchDescriptor(1, "text-cuda", ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty, RecFx.ActualConfig, expectedPath),
                (path, sha256) => string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sha256, side.Identity.LocalSha256, StringComparison.OrdinalIgnoreCase),
                _ => true, () => true);
        }

        long generation = store.Load().Generation;
        Guid transaction = Guid.NewGuid();
        Assert.True(store.TryBeginPending(generation, transaction, proof, AdmissionFor(proof.Incumbent),
            ChecksFor(proof.Incumbent)));
        Assert.True(store.TryCommitPending(generation + 1, transaction, AdmissionFor(proof.Challenger),
            ChecksFor(proof.Challenger)));
    }

    private static async Task<(int Exit, string Output, string Stderr)> RunCoordinatorAsync(string state, string mode,
        string crashPoint, long generation, int port, string models)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["PARUX_DATA_ROOT"] = Path.Combine(state, "data");
        foreach (string argument in new[] { HarnessDll(), state, mode, crashPoint,
                     generation.ToString(CultureInfo.InvariantCulture), port.ToString(CultureInfo.InvariantCulture), models })
            start.ArgumentList.Add(argument);
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start coordinator harness.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch
        {
            try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (child.ExitCode, await output.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string HarnessDll()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "PaRuX.Server.Tests.dll");
        Assert.True(File.Exists(dll), "test assembly not found: " + dll);
        return dll;
    }

    private static async Task<string> ClassifyRecoveredStateAsync(RecommendationTransitionResult result,
        CandidateRecommendationSnapshot snapshot, CandidateProofSide committedSide, CandidateProofSide incumbentSide,
        string models, RuntimeProcess runtime)
    {
        CandidateRecommendationRecord? active = snapshot.GetActiveRecord();
        bool committed = active is not null && active.Proof.Challenger.Identity.CandidateKey == committedSide.Identity.CandidateKey;
        bool incumbent = active is not null && active.Proof.Challenger.Identity.CandidateKey == incumbentSide.Identity.CandidateKey;
        bool refused = active is null && !string.IsNullOrWhiteSpace(result.Reason);
        Assert.Single(new[] { committed, incumbent, refused }.Where(value => value));

        if (committed || incumbent)
        {
            await AssertActiveWinnerHasRuntimeEvidenceAsync(active!, models, runtime);
            return committed ? "committed-new" : "incumbent-restored";
        }

        Assert.True(!string.IsNullOrWhiteSpace(result.Reason), "a refusal must explain why there is no Active winner");
        Assert.False(await runtime.ProbeHealthyAsync(), "a refused runtime without a winner must not report healthy");
        return "refused-with-reason";
    }

    private static async Task AssertActiveWinnerHasRuntimeEvidenceAsync(CandidateRecommendationRecord active,
        string models, RuntimeProcess runtime)
    {
        RuntimeProcessStatus status = await runtime.GetStatusAsync();
        string expectedPath = Path.GetFullPath(Path.Combine(models, active.Proof.Challenger.Identity.ManagedFileName));
        Assert.True(status.Running, "Active winner has no running stand-in");
        Assert.True(status.Healthy, "Active winner has no healthy stand-in evidence");
        Assert.Equal(expectedPath, status.ModelPath, ignoreCase: true);
        Assert.Equal(active.Proof.Challenger.Identity.LocalSha256, status.Sha256, ignoreCase: true);
        Assert.True(await runtime.ProbeHealthyAsync());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RuntimeProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private bool _cleaned;

        private RuntimeProcess(Process process, int port)
        {
            _process = process;
            Port = port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        }

        internal int Port { get; }
        private Uri BaseAddress { get; }

        internal static async Task<RuntimeProcess> StartAsync(CandidateProofSide initial, string models,
            string? unhealthyPath = null)
        {
            int port = ChooseLoopbackPort();
            string initialPath = Path.GetFullPath(Path.Combine(models, initial.Identity.ManagedFileName));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { HarnessDll(), "--recommendation-runtime",
                         port.ToString(CultureInfo.InvariantCulture), initialPath, initial.Identity.LocalSha256,
                         unhealthyPath ?? string.Empty })
                start.ArgumentList.Add(argument);

            Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start loopback runtime harness.");
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                string? ready = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Assert.Equal("READY:" + port.ToString(CultureInfo.InvariantCulture), ready);
                _ = process.StandardOutput.ReadToEndAsync();
                var runtime = new RuntimeProcess(process, port);
                await runtime.WaitUntilReachableAsync().ConfigureAwait(false);
                return runtime;
            }
            catch (Exception exception)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                string error = await stderr.ConfigureAwait(false);
                process.Dispose();
                throw new InvalidOperationException("Loopback runtime harness did not start. " + error, exception);
            }
        }

        internal async Task<RuntimeProcessStatus> GetStatusAsync() =>
            await _http.GetFromJsonAsync<RuntimeProcessStatus>(new Uri(BaseAddress, "status")).ConfigureAwait(false)
            ?? throw new InvalidDataException("Loopback runtime returned no status.");

        internal async Task<bool> ProbeHealthyAsync()
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri(BaseAddress, "health")).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        private async Task WaitUntilReachableAsync()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    _ = await GetStatusAsync().ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException exception) { last = exception; }
                catch (TaskCanceledException exception) { last = exception; }
                if (_process.HasExited) throw new InvalidOperationException("Loopback runtime exited before becoming ready.", last);
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
            throw new TimeoutException("Loopback runtime status endpoint did not become reachable.", last);
        }

        internal async Task StopAndAssertCleanAsync()
        {
            if (_cleaned) return;
            if (!_process.HasExited)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, "shutdown"));
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                Assert.True(response.IsSuccessStatusCode, "runtime rejected shutdown request");
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            Assert.True(_process.HasExited, "owned runtime process remains alive");
            Assert.Equal(0, _process.ExitCode);

            using var probe = new TcpClient(AddressFamily.InterNetwork);
            bool connected = false;
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, Port).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                connected = true;
            }
            catch (SocketException) { }
            catch (TimeoutException) { }
            Assert.False(connected, "loopback port still accepts connections after owned process cleanup");
            _cleaned = true;
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            _process.Dispose();
        }
    }

    internal sealed class LoopbackRecommendationRuntime(int port) : IRecommendationRuntime, IDisposable
    {
        private readonly Uri _baseAddress = new($"http://127.0.0.1:{port}/");
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

        public bool GamingActive => false;
        public bool StopRequested => false;
        public bool TextRuntimeRunning => ReadStatus().Running;

        public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoopWriter.Instance);
        }

        public async Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey,
            string path, string sha256, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(new Uri(_baseAddress, "load"),
                new LoadRequest(path, sha256), cancellationToken).ConfigureAwait(false);
            LoadResponse? result = await response.Content.ReadFromJsonAsync<LoadResponse>(cancellationToken)
                .ConfigureAwait(false);
            return new RuntimeLoadResult
            {
                Success = response.IsSuccessStatusCode && result?.Success == true,
                Role = role,
                Message = result?.Message ?? "load_failed",
                ModelPath = response.IsSuccessStatusCode ? Path.GetFullPath(path) : null
            };
        }

        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri(_baseAddress, "health"), cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        public bool StopText()
        {
            try
            {
                using HttpResponseMessage response = _http.PostAsync(new Uri(_baseAddress, "stop"), content: null)
                    .GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
        }

        internal RuntimeProcessStatus ReadStatus() => _http.GetFromJsonAsync<RuntimeProcessStatus>(new Uri(_baseAddress, "status"))
            .GetAwaiter().GetResult() ?? throw new InvalidDataException("Loopback runtime returned no status.");

        public void Dispose() => _http.Dispose();

        private sealed record LoadRequest(string Path, string Sha256);
        private sealed record LoadResponse(bool Success, string Message);
        private sealed class NoopWriter : IAsyncDisposable
        {
            internal static readonly NoopWriter Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class LoopbackRecommendationFence(LoopbackRecommendationRuntime runtime) : ICandidateLaunchFenceSource
    {
        public TextLaunchDescriptor? ReadVerifiedFence()
        {
            RuntimeProcessStatus status = runtime.ReadStatus();
            return status.Running && status.ModelPath is not null
                ? new TextLaunchDescriptor(1, "text-cuda", ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty, RecFx.ActualConfig, status.ModelPath)
                : null;
        }

        public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CandidateLaunchHost?>(RecFx.Host);
        }

        public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TextLaunchDescriptorResult(null, "not_used"));
        }

        public bool IsLiveExactModel(string modelPath, string expectedSha256)
        {
            RuntimeProcessStatus status = runtime.ReadStatus();
            return status.Running && string.Equals(status.ModelPath, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed record RuntimeProcessStatus(bool Running, string? ModelPath, string? Sha256, bool Healthy,
        int UnhealthyHealthProbeCount);

    private static int ChooseLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
