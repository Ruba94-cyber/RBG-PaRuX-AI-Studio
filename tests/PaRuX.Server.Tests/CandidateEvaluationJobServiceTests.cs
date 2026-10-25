using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateEvaluationJobServiceTests : IDisposable
{
    private static readonly string Revision = new('a', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-evaluation-jobs", Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceService _evidence;
    private readonly CandidateEvaluationJobStore _jobStore;
    private readonly ModelDiscoveryCandidate _candidate;
    private DateTime _now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    public CandidateEvaluationJobServiceTests()
    {
        string models = Path.Combine(_root, "models");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(models);
        _candidate = new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider, ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = "org/model", Revision = Revision, RepoFilePath = "weights/model.gguf",
            Key = ModelDiscoveryIdentity.Key("org/model", Revision, "weights/model.gguf"),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/model", Revision, "weights/model.gguf"),
            DeclaredLicense = "MIT",
            FirstSeenUtc = _now, LastSeenUtc = _now
        };
        Directory.CreateDirectory(_state);
        _catalog = new ModelDiscoveryCatalogStore(_state);
        _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        _evidence = new CandidateEvidenceService(_catalog, new CandidateEvidenceStore(_state), models, () => _now);
        _jobStore = new CandidateEvaluationJobStore(_state);
        File.WriteAllText(Path.Combine(models, "model.gguf"), "payload-1");
        ModelDownloadProvenanceManifestStore.Upsert(models, "model.gguf", new ModelDownloadProvenance
        {
            RepoId = _candidate.RepoId, Revision = Revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + Revision,
            RepoFilePath = _candidate.RepoFilePath, PinnedSourceUrl = _candidate.PinnedUrl,
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = _now.AddSeconds(-1),
            ComputedSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload-1"))).ToLowerInvariant(),
            IntegrityStatus = "unavailable", SizeBytes = 9, FileLastWriteUtc = _now, CompletedUtc = _now
        });
    }

    [Fact]
    public async Task PassingQualityRunsPerformanceThenEligibilityAndOnlyReturnsSafeSummary()
    {
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key,
                "model.gguf", ct))!;
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.Quality(identity);
            bool committed = await _evidence.TryCommitQualityAsync(record, ct);
            return new("Completed", committed, record, record.InvocationId);
        });
        var performance = new FakePerformance(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key,
                "model.gguf", ct))!;
            CandidatePerformanceEvidence record = CandidateEvidenceServiceTests.Performance(identity);
            bool committed = await _evidence.TryCommitPerformanceAsync(record, ct);
            return new("Completed", committed, record);
        });
        using var jobs = Jobs(quality, performance);
        string id = Assert.IsType<string>(jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId);
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("Done", result.Phase);
        Assert.Equal(CandidateEligibilityOutcome.EligibleForFurtherReview, result.Eligibility);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingCapability, result.Capability);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingSuiteId, result.QualitySuiteId);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion, result.QualitySuiteVersion);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId, result.QualityProfileId);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingQualityProfileVersion, result.QualityProfileVersion);
        Assert.True(Guid.TryParseExact(result.QualityInvocationId, "N", out _));
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId, result.PerformanceWorkloadId);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion, result.PerformanceWorkloadVersion);
        Assert.Equal(4, result.QualityPassCount);
        Assert.Equal(3, result.Repetitions);
        Assert.Equal(90, result.GeneratedTokenCount);
        Assert.Equal(200, result.PromptTokensPerSecond);
        Assert.Equal(100, result.DecodeTokensPerSecond);
        Assert.Equal(1, quality.Calls);
        Assert.Equal(1, performance.Calls);
        string json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("model.gguf", json);
        Assert.DoesNotContain("payload-1", json);
        Assert.DoesNotContain("Reply with exactly", json);
    }

    [Fact]
    public async Task Job_summary_persists_actual_launch_identity_and_labels_capped_completion_as_lower_bound()
    {
        TextLaunchDescriptor launch = TextLaunchDescriptor.Create("text-cuda",
            new[] { "--model", "C:/m.gguf", "--ctx-size", "32768", "--jinja" },
            new Dictionary<string, string> { ["GGML_CUDA_P2P"] = "1" }).Descriptor!;
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key, "model.gguf", ct))!;
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.Quality(identity);
            bool committed = await _evidence.TryCommitQualityAsync(record, ct);
            return new("Completed", committed, record, record.InvocationId, null, launch);
        });
        var performance = new FakePerformance(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key, "model.gguf", ct))!;
            CandidatePerformanceEvidence record = CandidateEvidenceServiceTests.Performance(identity);
            record.Samples[1].FinishReason = "length";
            return new("Completed", true, record, null, launch);
        });
        using (var jobs = Jobs(quality, performance))
        {
            string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
            CandidateEvaluationJobStatus result = await Terminal(jobs, id);
            Assert.True(result.CompletionLatencyIsLowerBound);
            Assert.Equal(launch.Fingerprint, result.EvaluationLaunchFingerprint);
            Assert.Equal(launch.Arguments.ToArray(), result.EvaluationLaunchArguments);
            Assert.Equal(new[] { "GGML_CUDA_P2P" }, result.EvaluationLaunchEnvironmentNames);
            Assert.Equal(CandidateRequestParity.Version, result.RequestParity);
            Assert.Null(result.EvaluationLaunchRefusal);
        }
        // Restart: the summary (including the lower-bound label) is durable.
        using var reopened = Jobs(quality, performance);
        CandidateEvaluationJobStatus reloaded = Assert.Single(reopened.List());
        Assert.True(reloaded.CompletionLatencyIsLowerBound);
        Assert.Equal(launch.Fingerprint, reloaded.EvaluationLaunchFingerprint);
    }

    [Fact]
    public async Task Job_summary_surfaces_bounded_launch_refusal_reason()
    {
        var quality = new FakeQuality((_, _, _) => Task.FromResult(
            new CandidateTextQualityRunResult("Unavailable", false, null, null, "launch_descriptor_invalid_environment")));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Unavailable", result.Status);
        Assert.Equal("launch_descriptor_invalid_environment", result.EvaluationLaunchRefusal);
        Assert.Null(result.EvaluationLaunchFingerprint);
        Assert.Equal(0, performance.Calls);
    }
    [Fact]
    public async Task FailedQualitySkipsPerformanceAndEligibility()
    {
        var quality = new FakeQuality((_, _, _) => Task.FromResult(new CandidateTextQualityRunResult(
            "Failed", false, null)));
        var performance = new FakePerformance((_, _, _) => Task.FromResult(new CandidateTextPerformanceRunResult(
            "Completed", true, null)));
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("quality_not_passed", result.ErrorCode);
        Assert.Null(result.Eligibility);
        Assert.Equal(0, performance.Calls);
    }

    [Theory]
    [InlineData(false, "LicenseEvidenceMissing")]
    [InlineData(true, "LicenseMetadataChanged")]
    public void Start_rejects_missing_or_changed_license_evidence_before_any_runner(
        bool changeCandidateLicense, string expectedOutcome)
    {
        if (changeCandidateLicense)
        {
            _candidate.DeclaredLicense = "custom-research-only";
            _catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { _candidate } });
        }
        else
        {
            ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(
                ModelDownloadProvenanceManifestStore.Get(_evidence.ModelsDirectory, "model.gguf"));
            provenance.LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided;
            provenance.PublisherDeclaredLicense = null;
            provenance.MetadataObservedUtc = null;
            provenance.ReviewRequired = false;
            provenance.ReviewAcknowledged = false;
            provenance.ReviewAcknowledgedUtc = null;
            ModelDownloadProvenanceManifestStore.Upsert(_evidence.ModelsDirectory, "model.gguf", provenance);
        }

        var quality = new FakeQuality((_, _, _) => throw new InvalidOperationException("Must not start quality."));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException("Must not start performance."));
        using var jobs = Jobs(quality, performance);
        CandidateEvaluationStartResult result = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Null(result.JobId);
        Assert.Equal(0, quality.Calls);
        Assert.Equal(0, performance.Calls);
    }

    [Fact]
    public async Task CommittedQualityWithOneFailedCaseStillSkipsPerformance()
    {
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key,
                "model.gguf", ct))!;
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.Quality(identity);
            record.Cases[0].Outcome = "fail";
            record.PassCount--;
            record.FailCount++;
            bool committed = await _evidence.TryCommitQualityAsync(record, ct);
            return new("Completed", committed, record);
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Failed", result.Status);
        Assert.Equal(3, result.QualityPassCount);
        Assert.Equal(0, performance.Calls);
    }

    [Fact]
    public async Task CancellationAfterQualityReturnCannotStartPerformance()
    {
        using var host = new CancellationTokenSource();
        CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key,
            "model.gguf", CancellationToken.None))!;
        var quality = new FakeQuality((_, _, _) =>
        {
            host.Cancel();
            return Task.FromResult(new CandidateTextQualityRunResult("Completed", true,
                CandidateEvidenceServiceTests.Quality(identity)));
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance, host.Token);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        Assert.Equal("Cancelled", (await Terminal(jobs, id)).Status);
        Assert.Equal(0, performance.Calls);
    }

    [Fact]
    public async Task CancelHoldsSingleSlotUntilRunnerCleanupAndNeverStartsNextPhase()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            await cleanup.Task;
            return new("Cancelled", false, null);
        });
        var performance = new FakePerformance((_, _, _) => Task.FromResult(new CandidateTextPerformanceRunResult(
            "Completed", true, null)));
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Busy", jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).Outcome);
        Assert.Equal("Cancelling", jobs.Cancel(id)!.Status);
        Assert.Equal("Busy", jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).Outcome);
        cleanup.TrySetResult();
        Assert.Equal("Cancelled", (await Terminal(jobs, id)).Status);
        Assert.Equal(0, performance.Calls);
        CandidateEvaluationStartResult next = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("Accepted", next.Outcome);
        jobs.Cancel(next.JobId!);
        await Terminal(jobs, next.JobId!);
    }

    [Fact]
    public async Task HostStopCancelsAndWaitsForCleanup()
    {
        using var host = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance, host.Token);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Cancel();
        await jobs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Cancelled", jobs.Get(id)!.Status);
        Assert.Equal(0, performance.Calls);
        Assert.Equal("Stopping", jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).Outcome);
    }

    [Fact]
    public async Task TerminalHistoryIsLimitedTo32AndSevenDays()
    {
        var quality = new FakeQuality((_, _, _) => Task.FromResult(new CandidateTextQualityRunResult(
            "Failed", false, null)));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        var ids = new List<string>();
        for (int i = 0; i < 33; i++)
        {
            string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
            ids.Add(id);
            await Terminal(jobs, id);
        }
        Assert.Null(jobs.Get(ids[0]));
        Assert.NotNull(jobs.Get(ids[^1]));
        Assert.Equal(32, jobs.List().Count);
        _now = _now.AddDays(8);
        Assert.Null(jobs.Get(ids[^1]));
        Assert.Empty(jobs.List());
    }

    [Fact]
    public async Task CompletedSummarySurvivesServiceRecreationWithoutPersistingManagedFileName()
    {
        var quality = new FakeQuality((_, _, _) => Task.FromResult(new CandidateTextQualityRunResult(
            "Failed", false, null)));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        string id;
        using (var jobs = Jobs(quality, performance))
        {
            id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
            Assert.Equal("Failed", (await Terminal(jobs, id)).Status);
        }

        using var restored = Jobs(new FakeQuality((_, _, _) => throw new InvalidOperationException()),
            new FakePerformance((_, _, _) => throw new InvalidOperationException()));
        CandidateEvaluationJobStatus summary = Assert.Single(restored.List());
        Assert.Equal(id, summary.JobId);
        Assert.Equal("Failed", summary.Status);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingCapability, summary.Capability);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingSuiteId, summary.QualitySuiteId);
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId, summary.PerformanceWorkloadId);
        string json = File.ReadAllText(Path.Combine(_state, CandidateEvaluationJobStore.FileName));
        Assert.DoesNotContain("model.gguf", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagedFileName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedEvidenceScopeMustBeACompleteBoundedTuple()
    {
        var summary = new CandidateEvaluationJobStatus
        {
            JobId = Guid.NewGuid().ToString("N"), CandidateKey = _candidate.Key,
            Status = "Completed", Phase = "Done", StartedUtc = _now, CompletedUtc = _now
        };
        CandidateEvaluationJobStore.Validate(summary); // Legacy summaries without scope remain readable.

        summary.Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability;
        Assert.Throws<InvalidDataException>(() => CandidateEvaluationJobStore.Validate(summary));

        summary.QualitySuiteId = CandidateEvaluationScope.ChatInstructionFollowingSuiteId;
        summary.QualitySuiteVersion = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion;
        summary.PerformanceWorkloadId = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId;
        summary.PerformanceWorkloadVersion = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion;
        CandidateEvaluationJobStore.Validate(summary);

        summary.QualitySuiteId = new string('x', 129);
        Assert.Throws<InvalidDataException>(() => CandidateEvaluationJobStore.Validate(summary));
    }

    [Fact]
    public void PersistedActiveSummaryBecomesInterruptedWithoutResumingRunner()
    {
        _jobStore.Save(new[]
        {
            new CandidateEvaluationJobStatus
            {
                JobId = Guid.NewGuid().ToString("N"), CandidateKey = _candidate.Key,
                Status = "Running", Phase = "Performance", StartedUtc = _now,
                QualityStatus = "Completed", QualityPassCount = 4, QualityExpectedCount = 4,
                Eligibility = CandidateEligibilityOutcome.EligibleForFurtherReview
            }
        });
        var quality = new FakeQuality((_, _, _) => throw new InvalidOperationException("Must not resume."));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException("Must not resume."));

        using var jobs = Jobs(quality, performance);
        CandidateEvaluationJobStatus interrupted = Assert.Single(jobs.List());
        Assert.Equal("Interrupted", interrupted.Status);
        Assert.Equal("Performance", interrupted.Phase);
        Assert.Equal("core_restart_interrupted", interrupted.ErrorCode);
        Assert.Null(interrupted.Eligibility);
        Assert.NotNull(interrupted.CompletedUtc);
        Assert.Equal(0, quality.Calls);
        Assert.Equal(0, performance.Calls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"jobs\":[]}")]
    [InlineData("{\"schemaVersion\":999999999999999999999999,\"jobs\":[]}")]
    public void InvalidOrFutureSummaryStoreFailsClosedAndPreservesOriginal(string json)
    {
        string path = Path.Combine(_state, CandidateEvaluationJobStore.FileName);
        File.WriteAllText(path, json);
        var quality = new FakeQuality((_, _, _) => throw new InvalidOperationException());
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());

        using var jobs = Jobs(quality, performance);
        Assert.Equal("StorageUnavailable", jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).Outcome);
        Assert.Throws<InvalidDataException>(() => jobs.List());
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(0, quality.Calls);
        Assert.Equal(0, performance.Calls);
    }

    [Fact]
    public async Task PruneWriteFailureBecomesControlledHistoryUnavailable()
    {
        var quality = new FakeQuality((_, _, _) => Task.FromResult(new CandidateTextQualityRunResult(
            "Failed", false, null)));
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        await Terminal(jobs, id);

        string path = Path.Combine(_state, CandidateEvaluationJobStore.FileName);
        string preserved = File.ReadAllText(path);
        File.Move(path, path + ".preserved");
        Directory.CreateDirectory(path);
        _now = _now.AddDays(8);

        Assert.Throws<InvalidDataException>(() => jobs.Get(id));
        Assert.Throws<InvalidDataException>(() => jobs.List());
        Assert.Equal("StorageUnavailable", jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).Outcome);
        Assert.Equal(preserved, File.ReadAllText(path + ".preserved"));
    }

    [Fact]
    public async Task CancelStillStopsRunnerButReportsHistoryWriteFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaned.TrySetResult(); }
            throw new InvalidOperationException("unreachable");
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string path = Path.Combine(_state, CandidateEvaluationJobStore.FileName);
        File.Move(path, path + ".preserved");
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidDataException>(() => jobs.Cancel(id));
        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, quality.Calls);
        Assert.Throws<InvalidDataException>(() => jobs.Get(id));
    }

    [Fact]
    public async Task InvalidOrUnknownCandidateCannotStartAndPreAcceptedCancellationLeavesSlotFree()
    {
        using var jobs = Jobs(new FakeQuality((_, _, _) => throw new InvalidOperationException()),
            new FakePerformance((_, _, _) => throw new InvalidOperationException()));
        Assert.Equal("Invalid", jobs.TryStart("bad", "model.gguf", CancellationToken.None).Outcome);
        Assert.Equal("Invalid", jobs.TryStart(_candidate.Key, "../model.gguf", CancellationToken.None).Outcome);
        Assert.Equal("NotFound", jobs.TryStart(new string('f', 64), "model.gguf", CancellationToken.None).Outcome);
        Assert.Equal("NotFound", jobs.TryStart(_candidate.Key, "other.gguf", CancellationToken.None).Outcome);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => jobs.TryStart(_candidate.Key,
            "model.gguf", cancelled.Token));
        CandidateEvaluationStartResult afterCancelledRequest = jobs.TryStart(_candidate.Key,
            "model.gguf", CancellationToken.None);
        Assert.Equal("Accepted", afterCancelledRequest.Outcome);
        Assert.Equal("Failed", (await Terminal(jobs, afterCancelledRequest.JobId!)).Status);
    }

    [Fact]
    public void TryStart_refuses_when_gaming_is_active()
    {
        var quality = new FakeQuality((_, _, _) => throw new InvalidOperationException());
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance, isGamingActive: () => true);

        CandidateEvaluationStartResult result = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None);
        Assert.Equal("GamingActive", result.Outcome);
        Assert.Null(result.JobId);
    }

    [Fact]
    public async Task CancelForGaming_cancels_running_job()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quality = new FakeQuality(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new("Completed", true, null!);
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);

        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None).JobId!;
        await started.Task;
        jobs.CancelForGaming();
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Cancelled", result.Status);
    }

    private CandidateEvaluationJobService Jobs(ICandidateTextQualityRunner quality,
        ICandidateTextPerformanceRunner performance, CancellationToken stopping = default,
        Func<bool>? isGamingActive = null) =>
        new(_catalog, _evidence, quality, performance, stopping, () => _now, _jobStore, isGamingActive);

    private static async Task<CandidateEvaluationJobStatus> Terminal(CandidateEvaluationJobService jobs, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            CandidateEvaluationJobStatus status = jobs.Get(id)!;
            if (status.CompletedUtc is not null) return status;
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task General_chat_job_with_a_runner_that_cannot_plan_the_actual_launch_is_unavailable_not_fixed_profile()
    {
        var quality = new ProfiledFakeQuality(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key, "model.gguf", ct))!;
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.GeneralChatQuality(identity);
            bool committed = await _evidence.TryCommitQualityAsync(record, ct);
            return new("Completed", committed, record, record.InvocationId);
        });
        // A plain (non-profiled) performance runner would silently measure the fixed evaluator profile.
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException("must not run"));
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None,
            CandidateEvaluationScope.GeneralChatQualityProfileId).JobId!;
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal("Unavailable", result.PerformanceStatus);
        Assert.Equal("actual_launch_unavailable", result.EvaluationLaunchRefusal);
        Assert.Equal(0, performance.Calls);
        Assert.Null(result.Eligibility);
    }

    [Fact]
    public async Task Job_summary_lists_failed_quality_case_ids_for_each_run()
    {
        var quality = new ProfiledFakeQuality(async (_, _, ct) =>
        {
            CandidateEvidenceIdentity identity = (await _evidence.TryCaptureSnapshotAsync(_candidate.Key, "model.gguf", ct))!;
            CandidateQualityEvidence record = CandidateEvidenceServiceTests.GeneralChatQuality(identity);
            record.Cases[1].Outcome = "fail"; record.PassCount--; record.FailCount++;
            bool committed = await _evidence.TryCommitQualityAsync(record, ct);
            return new("Completed", committed, record, record.InvocationId);
        });
        var performance = new FakePerformance((_, _, _) => throw new InvalidOperationException());
        using var jobs = Jobs(quality, performance);
        string id = jobs.TryStart(_candidate.Key, "model.gguf", CancellationToken.None,
            CandidateEvaluationScope.GeneralChatQualityProfileId).JobId!;
        CandidateEvaluationJobStatus result = await Terminal(jobs, id);
        Assert.Equal(new[] { CandidateEvaluationProfileCatalog.GeneralChat.QualityCases[1].Id }, result.QualityFailedCaseIds);
    }

    private sealed class ProfiledFakeQuality : IProfiledCandidateTextQualityRunner
    {
        private readonly Func<string, string, CancellationToken, Task<CandidateTextQualityRunResult>> _run;
        internal ProfiledFakeQuality(Func<string, string, CancellationToken, Task<CandidateTextQualityRunResult>> run) => _run = run;
        public Task<CandidateTextQualityRunResult> RunAsync(string key, string file, CancellationToken ct) => _run(key, file, ct);
        public Task<CandidateTextQualityRunResult> RunProfileAsync(string key, string file, string profile, CancellationToken ct) => _run(key, file, ct);
    }

    private sealed class FakeQuality : ICandidateTextQualityRunner
    {
        private readonly Func<string, string, CancellationToken, Task<CandidateTextQualityRunResult>> _run;
        internal int Calls;
        internal FakeQuality(Func<string, string, CancellationToken, Task<CandidateTextQualityRunResult>> run) => _run = run;
        public Task<CandidateTextQualityRunResult> RunAsync(string key, string file, CancellationToken ct)
        { Calls++; return _run(key, file, ct); }
    }

    private sealed class FakePerformance : ICandidateTextPerformanceRunner
    {
        private readonly Func<string, string, CancellationToken, Task<CandidateTextPerformanceRunResult>> _run;
        internal int Calls;
        internal FakePerformance(Func<string, string, CancellationToken, Task<CandidateTextPerformanceRunResult>> run) => _run = run;
        public Task<CandidateTextPerformanceRunResult> RunAsync(string key, string file, CancellationToken ct)
        { Calls++; return _run(key, file, ct); }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
