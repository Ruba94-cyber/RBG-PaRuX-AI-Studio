using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateTextQualityRunnerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Revision = new('a', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-quality-runner", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly ModelDiscoveryCandidate _candidate;
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceService _service;
    private readonly CandidateEvidenceStore _ledger;

    public CandidateTextQualityRunnerTests()
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
        string hash = Hash("payload-1");
        ModelDownloadProvenanceManifestStore.Upsert(_models, "model.gguf", new ModelDownloadProvenance
        {
            RepoId = _candidate.RepoId, Revision = Revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + Revision,
            RepoFilePath = _candidate.RepoFilePath, PinnedSourceUrl = _candidate.PinnedUrl,
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT", MetadataObservedUtc = Now.AddSeconds(-1),
            ComputedSha256 = hash, IntegrityStatus = "unavailable", SizeBytes = 9,
            FileLastWriteUtc = Now, CompletedUtc = Now
        });
    }

    [Fact]
    public void SuiteIdentityAndValidatorsAreFixed()
    {
        Assert.Equal(4, CandidateTextQualitySuite.Cases.Count);
        Assert.Equal(64, CandidateTextQualitySuite.Sha256.Length);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId, CandidateTextQualitySuite.ProfileId);
        Assert.Equal(1, CandidateTextQualitySuite.ProfileVersion);
        Assert.Equal(64, CandidateTextQualitySuite.ProfileSha256.Length);
        Assert.StartsWith("quality-workload-sha256:", CandidateTextQualitySuite.WorkloadFingerprint);
        Assert.NotEqual(CandidateTextQualitySuite.Sha256, CandidateTextQualitySuite.WorkloadFingerprint);
        Assert.True(CandidateTextQualitySuite.Validate(CandidateTextQualitySuite.Cases[3], "{\"count\":2,\"ok\":true}"));
        Assert.False(CandidateTextQualitySuite.Validate(CandidateTextQualitySuite.Cases[3], "{\"ok\":true,\"count\":2,\"extra\":0}"));
    }

    [Fact]
    public void CompletionParserRequiresOneNonemptyChoiceAndPreservesFinishReason()
    {
        static TextQualityCompletion Parse(string json) => LlamaTextQualitySession.ParseCompletion(
            Encoding.UTF8.GetBytes(json));
        Assert.Equal("length", Parse("{\"choices\":[{\"message\":{\"content\":\"PARUX\"},\"finish_reason\":\"length\"}]}").FinishReason);
        Assert.Equal("", Parse("{\"choices\":[{\"message\":{\"content\":\"PARUX\"}}]}").FinishReason);
        Assert.Throws<InvalidDataException>(() => Parse("{\"choices\":[]}"));
        Assert.Throws<InvalidDataException>(() => Parse("{\"choices\":[{\"message\":{\"content\":\"x\"}},{\"message\":{\"content\":\"y\"}}]}"));
        Assert.Throws<InvalidDataException>(() => Parse("{\"choices\":[{\"message\":{\"content\":\" \"}}]}"));
    }

    [Fact]
    public async Task AllStopResponsesCommitOnlyBoundedMetadataAndUseExactManagedPath()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")));
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.Committed);
        Assert.Equal(Path.GetFullPath(Path.Combine(_models, "model.gguf")), runtime.ModelPath);
        Assert.True(runtime.Disposed);
        Assert.True(Guid.TryParseExact(run.InvocationId, "N", out _));
        Assert.Equal(run.InvocationId, run.Evidence!.InvocationId);
        Assert.Equal(3, run.Evidence.RecordVersion);
        Assert.Equal(CandidateTextQualitySuite.ProfileId, run.Evidence.QualityProfileId);
        Assert.Equal(CandidateTextQualitySuite.ProfileVersion, run.Evidence.QualityProfileVersion);
        Assert.Equal(CandidateTextQualitySuite.ProfileSha256, run.Evidence.QualityProfileSha256);
        Assert.Equal(4, run.Evidence!.PassCount);
        Assert.All(run.Evidence.Cases, c => Assert.Equal("stop", c.FinishReason));
        string ledger = File.ReadAllText(Path.Combine(_root, "state", CandidateEvidenceStore.FileName));
        Assert.DoesNotContain("Reply with exactly", ledger);
        Assert.DoesNotContain("PARUX", ledger);
        Assert.DoesNotContain("payload-1", ledger);
    }

    [Fact]
    public async Task General_chat_profile_executes_all_heldout_cases_and_persists_exact_scope()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")));
        CandidateTextQualityRunResult run = await Runner(runtime).RunProfileAsync(_candidate.Key, "model.gguf",
            CandidateGeneralChatQualitySuite.ProfileId, CancellationToken.None);

        Assert.Equal("Completed", run.Status);
        Assert.True(run.Committed);
        Assert.Equal(30, runtime.CompletedCaseIds.Count);
        Assert.Equal(CandidateGeneralChatQualitySuite.Cases.Select(item => item.Id), runtime.CompletedCaseIds);
        Assert.Equal(CandidateGeneralChatQualitySuite.Capability, run.Evidence?.Capability);
        Assert.Equal(CandidateGeneralChatQualitySuite.Id, run.Evidence?.SuiteId);
        Assert.Equal(CandidateGeneralChatQualitySuite.Sha256, run.Evidence?.SuiteSha256);
        Assert.Equal(CandidateGeneralChatQualitySuite.ProfileId, run.Evidence?.QualityProfileId);
        Assert.Equal(CandidateGeneralChatQualitySuite.ProfileSha256, run.Evidence?.QualityProfileSha256);
        Assert.Equal(CandidateGeneralChatQualitySuite.WorkloadFingerprint,
            run.Evidence?.Environment?.WorkloadFingerprint);
        Assert.Equal(30, run.Evidence?.ExpectedCaseCount);
        Assert.All(run.Evidence!.Cases, item => Assert.Equal("pass", item.Outcome));
        Assert.Single(_ledger.Load().Quality);
        Assert.Equal(30, _ledger.Load().Quality.Single().Cases.Count);
        // Evidence is attributed to the planned actual AUTO launch, and the session received that launch.
        Assert.Equal(FakeRuntime.FakeLaunch.Fingerprint, run.Evidence.Environment!.ConfigurationFingerprint);
        Assert.Equal(FakeRuntime.FakeLaunch.Fingerprint, run.Launch?.Fingerprint);
        Assert.NotNull(runtime.StartedSpec?.Launch);
        Assert.True(CandidateLaunchScope.IsActualLaunchEvidence(run.Evidence.Environment.ConfigurationFingerprint,
            run.Evidence.Environment.ConfigurationFingerprint));
        Assert.All(CandidateEvaluationProfileCatalog.GeneralChat.QualityCases, item => Assert.True(item.ProductionRequestParity));
    }

    [Fact]
    public async Task General_chat_profile_refuses_without_an_exact_actual_launch_and_reports_why()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
        { ActualLaunchRefusal = "launch_descriptor_invalid_environment" };
        CandidateTextQualityRunResult run = await Runner(runtime).RunProfileAsync(_candidate.Key, "model.gguf",
            CandidateGeneralChatQualitySuite.ProfileId, CancellationToken.None);
        Assert.Equal("Unavailable", run.Status);
        Assert.Equal("launch_descriptor_invalid_environment", run.LaunchRefusal);
        Assert.False(runtime.Started);
        Assert.Null(run.Launch);

        // The historical smoke profile keeps its fixed evaluator launch and never claims an AUTO launch.
        var smoke = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")));
        CandidateTextQualityRunResult fixedRun = await Runner(smoke).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Completed", fixedRun.Status);
        Assert.Null(smoke.StartedSpec?.Launch);
        Assert.False(TextLaunchDescriptor.IsActualLaunchFingerprint(fixedRun.Evidence?.Environment?.ConfigurationFingerprint));
    }

    [Fact]
    public async Task General_chat_reliability_counts_mandatory_failure_and_rejects_stale_runtime_scope()
    {
        CandidateQualityEvidence? last = null;
        string missedMandatory = CandidateGeneralChatQualitySuite.Cases.First(item => item.Mandatory).Id;
        for (int runIndex = 0; runIndex < CandidateGeneralChatQualitySuite.MinimumFreshProcessRuns; runIndex++)
        {
            bool failMandatory = runIndex == 0;
            var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(
                failMandatory && item.Id == missedMandatory ? "wrong answer" : item.Expected, "stop")));
            CandidateTextQualityRunResult run = await Runner(runtime).RunProfileAsync(_candidate.Key, "model.gguf",
                CandidateGeneralChatQualitySuite.ProfileId, CancellationToken.None);
            Assert.True(run.Committed);
            last = run.Evidence;
        }

        CandidateQualityReliabilityCohort cohort = _service.BuildQualityReliabilityCohort(last!.Identity,
            last.Environment!, CandidateGeneralChatQualitySuite.MinimumSuccessRateBasisPoints,
            CandidateGeneralChatQualitySuite.MinimumFreshProcessRuns);
        Assert.True(cohort.AccountingComplete);
        Assert.Equal(5, cohort.AttemptCount);
        Assert.Equal(4, cohort.SuccessfulRunCount);
        Assert.Equal(8000, cohort.ReliabilityBasisPoints);
        Assert.False(cohort.ReliabilityBasisPoints >= CandidateGeneralChatQualitySuite.MinimumReliabilityBasisPoints);

        CandidateEvaluationEnvironment stale = CandidateEvaluationProfileCatalog.WithQualityWorkload(
            last.Environment!, CandidateEvaluationProfileCatalog.GeneralChat);
        stale.ConfigurationFingerprint = "sha256:stale-configuration";
        CandidateQualityReliabilityCohort staleCohort = _service.BuildQualityReliabilityCohort(last.Identity,
            stale, CandidateGeneralChatQualitySuite.MinimumSuccessRateBasisPoints,
            CandidateGeneralChatQualitySuite.MinimumFreshProcessRuns);
        Assert.Equal(0, staleCohort.AttemptCount);
        Assert.Null(staleCohort.ReliabilityBasisPoints);
    }

    [Fact]
    public async Task LicenseDriftDuringRuntimeResolutionPreventsQualityRuntimeStart()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
        {
            OnResolve = count =>
            {
                if (count != 1) return;
                ModelDiscoveryCatalog catalog = _catalog.Load();
                catalog.Candidates.Single().DeclaredLicense = "Apache-2.0";
                _catalog.Save(catalog);
            }
        };

        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key,
            "model.gguf", CancellationToken.None);

        Assert.Equal("Unavailable", run.Status);
        Assert.False(run.Committed);
        Assert.False(runtime.Started);
        Assert.Empty(run.Evidence!.Cases);
    }

    [Fact]
    public async Task EachQualityInvocationPersistsANewUniqueId()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")));
        CandidateTextQualityRunResult first = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        CandidateTextQualityRunResult second = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);

        Assert.NotEqual(first.InvocationId, second.InvocationId);
        string[] ids = _ledger.Load().Quality.Select(item => item.InvocationId).ToArray();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(first.InvocationId, ids);
        Assert.Contains(second.InvocationId, ids);
    }

    [Fact]
    public async Task CancellationBeforeSnapshotStillReturnsInvocationId()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        CandidateTextQualityRunResult run = await Runner(new FakeRuntime((item, _) =>
            Task.FromResult(new TextQualityCompletion(item.Expected, "stop"))))
            .RunAsync(_candidate.Key, "model.gguf", cts.Token);

        Assert.Equal("Cancelled", run.Status);
        Assert.False(run.Committed);
        Assert.Null(run.Evidence);
        Assert.True(Guid.TryParseExact(run.InvocationId, "N", out _));
    }

    [Theory]
    [InlineData("length", "inconclusive", true)]
    [InlineData("", "inconclusive", false)]
    [InlineData("other", "error", false)]
    public async Task OnlyStopCanPass(string finish, string outcome, bool truncated)
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, finish)));
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.True(run.Committed);
        Assert.Equal(outcome, run.Evidence!.Cases[0].Outcome);
        Assert.Equal(truncated, run.Evidence.Cases[0].Truncated);
        Assert.Equal(0, run.Evidence.PassCount);
    }

    [Fact]
    public async Task MissingRuntimeAndGamingAreUnavailableWithoutSession()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
            { Available = false };
        CandidateTextQualityRunResult missing = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Unavailable", missing.Status);
        Assert.True(missing.Committed);
        Assert.False(runtime.Started);
        CandidateTextQualityRunResult gaming = await Runner(runtime, () => true).RunAsync(
            _candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Unavailable", gaming.Status);
        Assert.False(runtime.Started);
    }

    [Fact]
    public async Task CancellationDisposesChildAndCannotPass()
    {
        using var cts = new CancellationTokenSource();
        var runtime = new FakeRuntime((_, ct) =>
        {
            cts.Cancel();
            return Task.FromCanceled<TextQualityCompletion>(ct);
        });
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", cts.Token);
        Assert.Equal("Cancelled", run.Status);
        Assert.True(runtime.Disposed);
        Assert.NotEqual(4, run.Evidence!.PassCount);
    }

    [Fact]
    public async Task CancellationAfterFinalResponseCannotCommitCompletedEvidence()
    {
        using var cts = new CancellationTokenSource();
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
        {
            OnResolve = count => { if (count == 2) cts.Cancel(); }
        };
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", cts.Token);
        Assert.Equal("Cancelled", run.Status);
        Assert.True(run.Committed); // A cancelled audit record may be kept, but it is never positive evidence.
        Assert.False(run.Evidence!.Completed);
        Assert.True(run.Evidence.Cancelled);
        Assert.Equal("Cancelled", _ledger.Load().Quality.Single().ExecutionStatus);
    }

    [Fact]
    public async Task GamingActivatedAfterFinalResponseCannotCommitCompletedEvidence()
    {
        bool gaming = false;
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
        {
            OnResolve = count => { if (count == 2) gaming = true; }
        };
        CandidateTextQualityRunResult run = await Runner(runtime, () => gaming)
            .RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Cancelled", run.Status);
        Assert.False(run.Evidence!.Completed);
        Assert.True(run.Evidence.Cancelled);
    }

    [Fact]
    public async Task ModelFileCannotBeWrittenWhileRuntimeUsesIt()
    {
        string modelPath = Path.Combine(_models, "model.gguf");
        bool writeBlocked = false;
        var runtime = new FakeRuntime((item, _) =>
        {
            try
            {
                using var writer = new FileStream(modelPath, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch (IOException) { writeBlocked = true; }
            return Task.FromResult(new TextQualityCompletion(item.Expected, "stop"));
        });
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.True(writeBlocked);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.Committed);
        Assert.Equal(Hash("payload-1"), run.Evidence!.Identity.LocalSha256);
    }

    [Fact]
    public async Task ByteChangeBetweenSnapshotAndLeasePreventsRunnerStart()
    {
        var runtime = new FakeRuntime((item, _) => Task.FromResult(new TextQualityCompletion(item.Expected, "stop")))
        {
            OnResolve = count =>
            {
                if (count == 1) File.WriteAllText(Path.Combine(_models, "model.gguf"), "payload-2");
            }
        };
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.False(run.Committed);
        Assert.Empty(_ledger.Load().Quality);
        Assert.False(runtime.Started);
    }

    [Fact]
    public async Task RuntimeFailureDisposesChildAndRecordsFailure()
    {
        var runtime = new FakeRuntime((_, _) => throw new IOException("sensitive diagnostic"));
        CandidateTextQualityRunResult run = await Runner(runtime).RunAsync(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Failed", run.Status);
        Assert.True(runtime.Disposed);
        Assert.True(run.Committed);
        Assert.Equal(1, run.Evidence!.ErrorCount);
        Assert.DoesNotContain("sensitive diagnostic", File.ReadAllText(
            Path.Combine(_root, "state", CandidateEvidenceStore.FileName)));
    }

    private CandidateTextQualityRunner Runner(FakeRuntime runtime, Func<bool>? gaming = null) =>
        new(_service, runtime, gaming ?? (() => false));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeRuntime : ITextQualityRuntime, ITextQualitySession
    {
        private readonly Func<CandidateTextQualitySuite.Case, CancellationToken, Task<TextQualityCompletion>> _complete;
        private int _resolveCount;
        public bool Available { get; set; } = true;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public string? ModelPath { get; private set; }
        public Action<int>? OnResolve { get; init; }
        public List<string> CompletedCaseIds { get; } = new();

        public FakeRuntime(Func<CandidateTextQualitySuite.Case, CancellationToken, Task<TextQualityCompletion>> complete)
            => _complete = complete;

        public Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken _)
        {
            OnResolve?.Invoke(++_resolveCount);
            return Task.FromResult<TextQualityRuntimeSpec?>(Available ? new("fake-llama-server.exe", "text-vulkan", new CandidateEvaluationEnvironment
            {
                HardwareFingerprint = "gpu-test", RuntimeFingerprint = "text-vulkan:test",
                BinaryFingerprint = "sha256:test", ConfigurationFingerprint = "sha256:test-config",
                WorkloadFingerprint = CandidateTextQualitySuite.WorkloadFingerprint
            }) : null);
        }

        public string? ActualLaunchRefusal { get; set; }
        public TextQualityRuntimeSpec? StartedSpec { get; private set; }
        internal static readonly TextLaunchDescriptor FakeLaunch = TextLaunchDescriptor.Create("text-vulkan",
            new[] { "--model", "C:/m.gguf", "--ctx-size", "32768", "--jinja" }, null).Descriptor!;

        public Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath, CancellationToken _)
        {
            OnResolve?.Invoke(++_resolveCount);
            if (!Available || ActualLaunchRefusal is not null)
                return Task.FromResult(new TextQualityRuntimeResolution(null, ActualLaunchRefusal ?? "launch_descriptor_unavailable"));
            TextLaunchDescriptor launch = FakeLaunch with { ModelPath = modelPath };
            return Task.FromResult(new TextQualityRuntimeResolution(new("fake-llama-server.exe", "text-vulkan",
                new CandidateEvaluationEnvironment
                {
                    HardwareFingerprint = "gpu-test", RuntimeFingerprint = "text-vulkan:test",
                    BinaryFingerprint = "sha256:test", ConfigurationFingerprint = launch.Fingerprint,
                    WorkloadFingerprint = CandidateGeneralChatQualitySuite.WorkloadFingerprint
                }, new EvaluationLaunch(new[] { "--ctx-size", "32768", "--jinja" },
                    new Dictionary<string, string>(), launch, modelPath)), null));
        }

        public Task<ITextQualitySession> StartAsync(TextQualityRuntimeSpec spec, string modelPath, CancellationToken __)
        {
            StartedSpec = spec;
            Started = true;
            ModelPath = modelPath;
            return Task.FromResult<ITextQualitySession>(this);
        }

        public Task<TextQualityCompletion> CompleteAsync(CandidateTextQualitySuite.Case item,
            CancellationToken cancellationToken)
        {
            CompletedCaseIds.Add(item.Id);
            return _complete(item, cancellationToken);
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
