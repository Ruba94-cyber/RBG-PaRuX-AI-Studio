using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateTextPerformanceRunnerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Revision = new('a', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-performance-runner", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly ModelDiscoveryCandidate _candidate;
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceService _service;
    private readonly CandidateEvidenceStore _ledger;

    public CandidateTextPerformanceRunnerTests()
    {
        _models = Path.Combine(_root, "models");
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_models);
        _candidate = new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider, ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = "org/model", Revision = Revision, RepoFilePath = "weights/model.gguf",
            Key = ModelDiscoveryIdentity.Key("org/model", Revision, "weights/model.gguf"),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/model", Revision, "weights/model.gguf"),
            DeclaredLicense = "MIT",
            FirstSeenUtc = Now, LastSeenUtc = Now
        };
        _catalog = new ModelDiscoveryCatalogStore(state);
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        _ledger = new CandidateEvidenceStore(state);
        _service = new CandidateEvidenceService(_catalog, _ledger, _models, () => Now);
        File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-1");
        ModelDownloadProvenanceManifestStore.Upsert(_models, "model.gguf", new ModelDownloadProvenance
        {
            RepoId = _candidate.RepoId, Revision = Revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + Revision,
            RepoFilePath = _candidate.RepoFilePath, PinnedSourceUrl = _candidate.PinnedUrl,
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT", MetadataObservedUtc = Now.AddSeconds(-1),
            ComputedSha256 = Hash("payload-1"), IntegrityStatus = "unavailable", SizeBytes = 9,
            FileLastWriteUtc = Now, CompletedUtc = Now
        });
    }

    [Fact]
    public async Task ThreeSerialSamplesCommitMeasuredAggregatesWithoutResponseText()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.Committed);
        Assert.Equal(3, runtime.Calls);
        Assert.True(runtime.Disposed);
        Assert.Equal(Path.GetFullPath(Path.Combine(_models, "model.gguf")), runtime.ModelPath);
        Assert.Equal(90, run.Evidence!.TokenCount);
        Assert.Equal(3, run.Evidence.Repetitions);
        Assert.Equal(new[] { 1, 2, 3 }, run.Evidence.Samples.Select(s => s.Repetition));
        Assert.Equal(200, run.Evidence.Metrics.Single(m => m.Name == "prompt_tok_per_s").Value);
        Assert.Equal(100, run.Evidence.Metrics.Single(m => m.Name == "decode_tok_per_s").Value);
        Assert.Equal(450, run.Evidence.Metrics.Single(m => m.Name == "completion_ms").Value);
        Assert.Equal(90, run.Evidence.Metrics.Single(m => m.Name == "ttft_ms").Value);
        string ledger = File.ReadAllText(Path.Combine(_root, "state", CandidateEvidenceStore.FileName));
        Assert.DoesNotContain(CandidateTextPerformanceWorkload.Prompt, ledger);
        Assert.DoesNotContain("payload-1", ledger);
    }

    [Fact]
    public async Task LicenseDriftDuringRuntimeResolutionPreventsPerformanceRuntimeStart()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()))
        {
            OnResolve = count =>
            {
                if (count != 1) return;
                ModelDiscoveryCatalog catalog = _catalog.Load();
                catalog.Candidates.Single().DeclaredLicense = "Apache-2.0";
                _catalog.Save(catalog);
            }
        };

        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);

        Assert.Equal("Unavailable", run.Status);
        Assert.False(run.Committed);
        Assert.False(runtime.Started);
        Assert.Empty(run.Evidence!.Samples);
    }

    [Fact]
    public void SseParserMeasuresFirstVisibleContentAndStreamCompletionSeparately()
    {
        var parser = new CandidatePerformanceStreamParser("alias");
        parser.AcceptLine("data: {\"model\":\"alias\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"}}]}", 5);
        parser.AcceptLine("", 5);
        parser.AcceptLine("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"plan\"}}]}", 10);
        parser.AcceptLine("", 10);
        parser.AcceptLine("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}", 20);
        parser.AcceptLine("", 20);
        parser.AcceptLine("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" tail\"},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":20,\"prompt_ms\":100,\"predicted_n\":30,\"predicted_ms\":300}}", 40);
        parser.AcceptLine("", 40);
        parser.AcceptLine("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":30,\"total_tokens\":50}}", 45);
        parser.AcceptLine("", 45);
        parser.AcceptLine("data: [DONE]", 50);
        parser.AcceptLine("", 50);
        TextPerformanceCompletion measured = parser.Complete(60);
        Assert.Equal(20, measured.TimeToFirstTokenMilliseconds);
        Assert.Equal(60, measured.CompletionMilliseconds);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SseParserRejectsMissingVisibleContentOrTruncatedEnd(bool includeVisible, bool includeDone)
    {
        var parser = new CandidatePerformanceStreamParser("alias");
        string delta = includeVisible ? "\"content\":\"answer\"" : "\"reasoning_content\":\"plan\"";
        parser.AcceptLine("data: {\"model\":\"alias\",\"choices\":[{\"index\":0,\"delta\":{" + delta + "}}]}", 10);
        parser.AcceptLine("", 10);
        parser.AcceptLine("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":20,\"prompt_ms\":100,\"predicted_n\":30,\"predicted_ms\":300}}", 20);
        parser.AcceptLine("", 20);
        if (includeDone) { parser.AcceptLine("data: [DONE]", 30); parser.AcceptLine("", 30); }
        Assert.Throws<InvalidDataException>(() => parser.Complete(50));
    }

    [Fact]
    public void SseParserRejectsMalformedJsonAndMissingFinishReason()
    {
        var malformed = new CandidatePerformanceStreamParser("alias");
        malformed.AcceptLine("data: {bad", 1);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => malformed.AcceptLine("", 1));

        var unfinished = new CandidatePerformanceStreamParser("alias");
        unfinished.AcceptLine("data: {\"model\":\"alias\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}],\"timings\":{\"prompt_n\":20,\"prompt_ms\":100,\"predicted_n\":30,\"predicted_ms\":300}}", 10);
        unfinished.AcceptLine("", 10);
        unfinished.AcceptLine("data: [DONE]", 20);
        unfinished.AcceptLine("", 20);
        Assert.Throws<InvalidDataException>(() => unfinished.Complete(30));
    }

    [Fact]
    public void SseParserRejectsDataAfterDoneAndInvalidTerminalUsageFrames()
    {
        var afterDone = new CandidatePerformanceStreamParser("alias");
        afterDone.AcceptLine("data: [DONE]", 1);
        afterDone.AcceptLine("", 1);
        Assert.Throws<InvalidDataException>(() => afterDone.AcceptLine("data: {}", 2));

        var prematureUsage = new CandidatePerformanceStreamParser("alias");
        prematureUsage.AcceptLine("data: {\"choices\":[],\"usage\":{}}", 1);
        Assert.Throws<InvalidDataException>(() => prematureUsage.AcceptLine("", 1));
    }

    [Fact]
    public async Task BoundedReadStreamRejectsAnOversizedSingleLineAtTheByteLimit()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 1024));
        using var source = new MemoryStream(bytes);
        using var bounded = new BoundedReadStream(source, 16);
        using var reader = new StreamReader(bounded, Encoding.UTF8, false, 128, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(async () => { await reader.ReadLineAsync(); });
        Assert.Equal(17, source.Position);
    }

    [Fact]
    public async Task BoundedReadStreamAcceptsExactLimitAtEndOfStream()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        using var source = new MemoryStream(bytes);
        using var bounded = new BoundedReadStream(source, 16);
        using var reader = new StreamReader(bounded, Encoding.UTF8, false, 128, leaveOpen: true);

        Assert.Equal(new string('x', 16), await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Theory]
    [InlineData(0, 100, 30, 300, 450)]
    [InlineData(20, 0, 30, 300, 450)]
    [InlineData(20, 100, 0, 300, 450)]
    [InlineData(20, 100, 30, 0, 450)]
    [InlineData(20, 100, 30, 300, 0)]
    [InlineData(20, 100, 30, 300, double.NaN)]
    public async Task InvalidMeasurementsNeverComplete(int prompt, double promptMs, int generated,
        double decodeMs, double completionMs)
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(new TextPerformanceCompletion(
            prompt, promptMs, generated, decodeMs, completionMs, 10)));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.True(runtime.Disposed);
        Assert.False(run.Evidence!.Completed);
    }

    [Fact]
    public async Task MissingRuntimeAndGamingAreUnavailableWithoutChild()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid())) { Available = false };
        Assert.Equal("Unavailable", (await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None)).Status);
        Assert.Equal("Unavailable", (await Runner(runtime, () => true).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None)).Status);
        Assert.False(runtime.Started);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotStartOrCommit()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", cts.Token);
        Assert.Equal("Cancelled", run.Status);
        Assert.False(run.Committed);
        Assert.False(runtime.Started);
    }

    [Theory]
    [InlineData("hardware")]
    [InlineData("runtime")]
    [InlineData("binary")]
    [InlineData("configuration")]
    [InlineData("workload")]
    public async Task EnvironmentDriftAfterSamplesFails(string field)
    {
        string? runtimeDrift = null;
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()))
        { OnResolve = count => { if (count == 2) runtimeDrift = field; } };
        runtime.Drift = () => runtimeDrift;
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.False(run.Evidence!.Completed);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task RequestTimeoutFailsAndDisposesChild()
    {
        var runtime = new FakeRuntime((_, _) => throw new OperationCanceledException());
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task ByteChangeBetweenSnapshotAndLeaseBlocksStart()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()))
        { OnResolve = count => { if (count == 1) File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-2"); } };
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.False(run.Committed);
        Assert.False(runtime.Started);
    }

    [Fact]
    public async Task LeaseBlocksModificationDuringChildAndDisposesIt()
    {
        bool blocked = false;
        var runtime = new FakeRuntime((_, _) =>
        {
            try { using var writer = new FileStream(Path.Combine(_models, "model.gguf"),
                FileMode.Open, FileAccess.Write, FileShare.None); }
            catch (IOException) { blocked = true; }
            return Task.FromResult(Valid());
        });
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.True(blocked);
        Assert.True(runtime.Disposed);
        Assert.True(run.Committed);
    }

    [Fact]
    public async Task CancellationAndFailureDisposeChildWithoutPositiveEvidence()
    {
        using var cts = new CancellationTokenSource();
        var cancel = new FakeRuntime((_, ct) => { cts.Cancel(); return Task.FromCanceled<TextPerformanceCompletion>(ct); });
        CandidateTextPerformanceRunResult cancelled = await Runner(cancel).RunAsync(_candidate.Key,
            "model.gguf", cts.Token);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.True(cancel.Disposed);
        var failed = new FakeRuntime((_, _) => throw new IOException("sensitive response"));
        CandidateTextPerformanceRunResult error = await Runner(failed).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", error.Status);
        Assert.True(failed.Disposed);
        Assert.DoesNotContain("sensitive response", File.ReadAllText(
            Path.Combine(_root, "state", CandidateEvidenceStore.FileName)));
    }

    [Fact]
    public async Task LateCancellationOrGamingCannotCommitPositiveEvidence()
    {
        using var cts = new CancellationTokenSource();
        var cancel = new FakeRuntime((_, _) => Task.FromResult(Valid()))
        { OnResolve = count => { if (count == 2) cts.Cancel(); } };
        CandidateTextPerformanceRunResult cancelled = await Runner(cancel).RunAsync(_candidate.Key,
            "model.gguf", cts.Token);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.False(cancelled.Evidence!.Completed);
        bool gaming = false;
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Valid()))
        { OnResolve = count => { if (count == 2) gaming = true; } };
        CandidateTextPerformanceRunResult stopped = await Runner(runtime, () => gaming).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Cancelled", stopped.Status);
        Assert.False(stopped.Evidence!.Completed);
    }

    private const string CapturedAlias = "parux-performance-test";

    private static TextPerformanceCompletion ParseCapture(string fileName, Func<string, string>? mutate = null)
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "performance-stream", fileName));
        if (mutate is not null) text = mutate(text);
        var parser = new CandidatePerformanceStreamParser(CapturedAlias);
        double t = 0;
        foreach (string line in text.Split('\n')) parser.AcceptLine(line.TrimEnd('\r'), t += 1);
        return parser.Complete(t + 1);
    }

    [Fact]
    public void RealQwen35CaptureEndingAtTheTokenBoundParsesAsTruthfulLength()
    {
        // Raw SSE from llama.cpp b10549 text-cuda, Qwen3.5-4B Q4_K_M: the paragraph exceeds max_tokens=96.
        TextPerformanceCompletion measured = ParseCapture("qwen3.5-4b-q4km-b10549-length.sse");
        Assert.Equal("length", measured.FinishReason);
        Assert.Equal(96, measured.GeneratedTokens);
        Assert.Equal(43, measured.PromptTokens);
    }

    [Fact]
    public void RealQwen3CaptureStillParsesAsStop()
    {
        TextPerformanceCompletion measured = ParseCapture("qwen3-4b-q4km-b10549-stop.sse");
        Assert.Equal("stop", measured.FinishReason);
        Assert.Equal(49, measured.GeneratedTokens);
    }

    [Fact]
    public void LengthEndBelowTheExactTokenBoundFailsClosed()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ParseCapture(
            "qwen3.5-4b-q4km-b10549-length.sse",
            text => text.Replace("\"predicted_n\":96", "\"predicted_n\":95")
                .Replace("\"completion_tokens\":96", "\"completion_tokens\":95")));
        Assert.Equal("length_not_at_bound", PerformanceStreamException.CodeOf(ex));
    }

    [Theory]
    [InlineData("\"predicted_n\":96", "\"predicted_n\":95")]
    [InlineData("\"completion_tokens\":96", "\"completion_tokens\":95")]
    public void LengthWithUsageAndTimingsDisagreeingFailsClosed(string from, string to)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ParseCapture(
            "qwen3.5-4b-q4km-b10549-length.sse", text => text.Replace(from, to)));
        Assert.Equal("usage_timings_disagree", PerformanceStreamException.CodeOf(ex));
    }

    [Fact]
    public void UnknownFinishReasonFailsClosedWithCode()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ParseCapture(
            "qwen3.5-4b-q4km-b10549-length.sse", text => text.Replace("\"finish_reason\":\"length\"", "\"finish_reason\":\"content_filter\"")));
        Assert.Equal("finish_reason_invalid", PerformanceStreamException.CodeOf(ex));
    }

    [Fact]
    public async Task LengthBoundedSamplesCommitWithTruthfulFinishReason()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(new TextPerformanceCompletion(
            43, 280, CandidateTextPerformanceWorkload.MaxTokens, 560, 900, 100, "length")));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.Committed);
        Assert.All(run.Evidence!.Samples, s => Assert.Equal("length", s.FinishReason));
        Assert.Null(run.Evidence.FailureReason);
        Assert.Contains("\"finishReason\": \"length\"",
            File.ReadAllText(Path.Combine(_root, "state", CandidateEvidenceStore.FileName)));
    }

    [Fact]
    public async Task LengthBelowTheBoundIsNeverAcceptedByTheRunner()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(new TextPerformanceCompletion(
            43, 280, 30, 560, 900, 100, "length")));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.Equal("measurement_invalid", run.Evidence!.FailureReason);
    }

    [Fact]
    public async Task FailedRunPersistsBoundedReasonCodeWithoutExceptionText()
    {
        var runtime = new FakeRuntime((_, _) => throw PerformanceStreamException.Create(
            "finish_reason_invalid", "secret model text /private/path"));
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.True(run.Committed);
        Assert.Equal("finish_reason_invalid", run.Evidence!.FailureReason);
        string ledger = File.ReadAllText(Path.Combine(_root, "state", CandidateEvidenceStore.FileName));
        Assert.Contains("\"failureReason\": \"finish_reason_invalid\"", ledger);
        Assert.DoesNotContain("secret model text", ledger);
        Assert.Equal("finish_reason_invalid",
            _ledger.Load().Performance.Single().FailureReason);
    }

    [Fact]
    public async Task RequestTimeoutRecordsTimeoutCode()
    {
        var runtime = new FakeRuntime((_, _) => throw new OperationCanceledException());
        CandidateTextPerformanceRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("request_timeout", run.Evidence!.FailureReason);
    }

    [Fact]
    public void LengthWithoutUsageCompletionTokensFailsClosed()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ParseCapture(
            "qwen3.5-4b-q4km-b10549-length.sse",
            text => text.Replace(",\"usage\":{\"completion_tokens\":96,", ",\"usage\":{")));
        Assert.Equal("length_not_at_bound", PerformanceStreamException.CodeOf(ex));
    }

    [Fact]
    public void StopWithUsageDisagreeingWithTimingsFailsClosed()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ParseCapture("qwen3-4b-q4km-b10549-stop.sse",
            text => text.Replace("\"completion_tokens\":49", "\"completion_tokens\":48")));
        Assert.Equal("usage_timings_disagree", PerformanceStreamException.CodeOf(ex));
    }

    [Fact]
    public void RepeatedFinishFrameAndContentAfterFinishFailClosed()
    {
        string finish = "data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"delta\":{}}],\"model\":\"" + CapturedAlias + "\"}\n\n";
        string late = "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"x\"}}],\"model\":\"" + CapturedAlias + "\"}\n\n";
        InvalidDataException repeated = Assert.Throws<InvalidDataException>(() => ParseCapture("qwen3-4b-q4km-b10549-stop.sse",
            text => text.Replace("data: {\"choices\":[],", finish + "data: {\"choices\":[],")));
        Assert.Equal("finish_reason_repeated", PerformanceStreamException.CodeOf(repeated));
        InvalidDataException after = Assert.Throws<InvalidDataException>(() => ParseCapture("qwen3-4b-q4km-b10549-stop.sse",
            text => text.Replace("data: {\"choices\":[],", late + "data: {\"choices\":[],")));
        Assert.Equal("content_after_finish", PerformanceStreamException.CodeOf(after));
    }

    private CandidateTextPerformanceRunner Runner(FakeRuntime runtime, Func<bool>? gaming = null) =>
        new(_service, runtime, gaming ?? (() => false));

    private static TextPerformanceCompletion Valid() => new(20, 100, 30, 300, 450, 90);
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeRuntime : ITextPerformanceRuntime, ITextPerformanceSession
    {
        private readonly Func<int, CancellationToken, Task<TextPerformanceCompletion>> _measure;
        private int _resolves;
        public bool Available { get; init; } = true;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public string? ModelPath { get; private set; }
        public Action<int>? OnResolve { get; init; }
        public Func<string?>? Drift { get; set; }
        public FakeRuntime(Func<int, CancellationToken, Task<TextPerformanceCompletion>> measure) => _measure = measure;
        public Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken _)
        {
            OnResolve?.Invoke(++_resolves);
            string? drift = Drift?.Invoke();
            return Task.FromResult<TextQualityRuntimeSpec?>(Available ? new("fake-llama-server.exe", "text-vulkan",
                new CandidateEvaluationEnvironment
                {
                    HardwareFingerprint = drift == "hardware" ? "gpu-other" : "gpu-test",
                    RuntimeFingerprint = drift == "runtime" ? "text-vulkan:other" : "text-vulkan:test",
                    BinaryFingerprint = drift == "binary" ? "sha256:other" : "sha256:test",
                    ConfigurationFingerprint = drift == "configuration" ? "sha256:other" : "sha256:test-config",
                    WorkloadFingerprint = drift == "workload" ? "performance-workload-sha256:other"
                        : CandidateTextPerformanceWorkload.WorkloadFingerprint
                }) : null);
        }
        public Task<ITextPerformanceSession> StartAsync(TextQualityRuntimeSpec _, string modelPath,
            CancellationToken __)
        {
            Started = true; ModelPath = modelPath;
            return Task.FromResult<ITextPerformanceSession>(this);
        }
        public Task<TextPerformanceCompletion> MeasureAsync(CancellationToken cancellationToken) =>
            _measure(++Calls, cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
