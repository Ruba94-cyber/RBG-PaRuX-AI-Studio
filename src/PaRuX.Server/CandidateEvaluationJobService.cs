using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record CandidateEvaluationStartResult(string Outcome, string? JobId);

/// <summary>One desktop Candidate evaluation at a time; sanitized summaries are durably bounded.</summary>
internal sealed class CandidateEvaluationJobService : IHostedService, IDisposable
{
    internal const int MaximumTerminalJobs = 32;
    internal static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(7);
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceService _evidence;
    private readonly ICandidateTextQualityRunner _quality;
    private readonly ICandidateTextPerformanceRunner _performance;
    private readonly CancellationToken _stopping;
    private readonly Func<DateTime> _utcNow;
    private readonly CandidateEvaluationJobStore _summaryStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Queue<string> _terminal = new();
    private Job? _active;
    private bool _disposed;
    private bool _summaryStoreAvailable = true;

    private sealed class Job
    {
        internal required string Id;
        internal required string CandidateKey;
        internal required string ManagedFileName;
        internal required CancellationTokenSource Cancellation;
        internal required DateTime StartedUtc;
        internal Task? Task;
        internal string Status = "Running";
        internal string Phase = "Quality";
        internal DateTime? CompletedUtc;
        internal string? QualityStatus;
        internal int? QualityPassCount;
        internal int? QualityExpectedCount;
        internal string? PerformanceStatus;
        internal int? Repetitions;
        internal long? GeneratedTokenCount;
        internal double? PromptTokensPerSecond;
        internal double? DecodeTokensPerSecond;
        internal double? CompletionMilliseconds;
        internal double? TimeToFirstTokenMilliseconds;
        internal string? Capability;
        internal string? QualitySuiteId;
        internal int? QualitySuiteVersion;
        internal string? QualityProfileId;
        internal int? QualityProfileVersion;
        internal string? QualityInvocationId;
        internal string? PerformanceWorkloadId;
        internal int? PerformanceWorkloadVersion;
        internal string? Eligibility;
        internal string? ErrorCode;
        internal string? EvaluationLaunchFingerprint;
        internal List<string>? EvaluationLaunchArguments;
        internal List<string>? EvaluationLaunchEnvironmentNames;
        internal string? EvaluationLaunchRefusal;
        internal string? RequestParity;
        internal bool? CompletionLatencyIsLowerBound;
        internal List<string>? QualityFailedCaseIds;
    }

    private readonly Func<bool>? _isGamingActive;

    internal CandidateEvaluationJobService(ModelDiscoveryCatalogStore catalog, CandidateEvidenceService evidence,
        ICandidateTextQualityRunner quality, ICandidateTextPerformanceRunner performance,
        CancellationToken stopping, Func<DateTime>? utcNow = null, CandidateEvaluationJobStore? summaryStore = null,
        Func<bool>? isGamingActive = null)
    {
        _catalog = catalog;
        _evidence = evidence;
        _quality = quality;
        _performance = performance;
        _stopping = stopping;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _summaryStore = summaryStore ?? new CandidateEvaluationJobStore();
        _isGamingActive = isGamingActive;
        LoadAndRecoverSummaries();
    }

    internal void CancelForGaming()
    {
        lock (_gate)
        {
            if (_active is { } active)
            {
                try { active.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal CandidateEvaluationStartResult TryStart(string candidateKey, string managedFileName,
        CancellationToken requestCancellation, string? qualityProfileId = null)
    {
        requestCancellation.ThrowIfCancellationRequested();
        if (_isGamingActive?.Invoke() == true) return new("GamingActive", null);
        qualityProfileId ??= CandidateTextQualitySuite.ProfileId;
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(qualityProfileId,
                out CandidateEvaluationProfileDefinition profile))
            return new("Invalid", null);
        if (!ModelDiscoveryIdentity.IsSafeKey(candidateKey)
            || !ModelDownloadProvenanceManifestStore.IsSafeFileName(managedFileName))
            return new("Invalid", null);
        lock (_gate)
        {
            if (_isGamingActive?.Invoke() == true) return new("GamingActive", null);
            if (!_summaryStoreAvailable) return new("StorageUnavailable", null);
            try
            {
                if (Prune()) PersistLocked();
            }
            catch (InvalidDataException) { return new("StorageUnavailable", null); }
            if (_disposed || _stopping.IsCancellationRequested) return new("Stopping", null);
            if (_active is not null) return new("Busy", null);
            ModelDiscoveryCandidate? candidate = _catalog.Load().Candidates.SingleOrDefault(item =>
                item.Key == candidateKey && item.Lifecycle == "Candidate" && item.Disposition == "active");
            if (candidate is null || candidate.RepoFilePath.Split('/')[^1] != managedFileName)
                return new("NotFound", null);
            if (_evidence.TryCaptureProvenanceSnapshot(candidateKey, managedFileName,
                out string? provenanceRejection) is null)
                return new(provenanceRejection ?? "ProvenanceUnavailable", null);
            requestCancellation.ThrowIfCancellationRequested();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            cancellation.CancelAfter(profile.Capability == CandidateGeneralChatQualitySuite.Capability
                ? TimeSpan.FromMinutes(45) : TimeSpan.FromMinutes(11));
            var job = new Job
            {
                Id = Guid.NewGuid().ToString("N"), CandidateKey = candidateKey,
                ManagedFileName = managedFileName, Cancellation = cancellation, StartedUtc = _utcNow(),
                Capability = profile.Capability,
                QualitySuiteId = profile.QualitySuiteId,
                QualitySuiteVersion = profile.QualitySuiteVersion,
                QualityProfileId = profile.QualityProfileId,
                QualityProfileVersion = profile.QualityProfileVersion,
                PerformanceWorkloadId = profile.PerformanceWorkloadId,
                PerformanceWorkloadVersion = profile.PerformanceWorkloadVersion
            };
            _jobs.Add(job.Id, job);
            _active = job;
            try { PersistLocked(); }
            catch (InvalidDataException)
            {
                _jobs.Remove(job.Id);
                _active = null;
                cancellation.Cancel();
                cancellation.Dispose();
                return new("StorageUnavailable", null);
            }
            job.Task = Task.Run(() => ExecuteAsync(job), CancellationToken.None);
            return new("Accepted", job.Id);
        }
    }

    internal CandidateEvaluationJobStatus? Get(string id)
    {
        lock (_gate)
        {
            EnsureSummaryStoreAvailable();
            if (Prune()) PersistLocked();
            return _jobs.TryGetValue(id, out Job? job) ? Snapshot(job) : null;
        }
    }

    internal List<CandidateEvaluationJobStatus> List()
    {
        lock (_gate)
        {
            EnsureSummaryStoreAvailable();
            if (Prune()) PersistLocked();
            return _jobs.Values.Select(Snapshot).OrderByDescending(status => status.StartedUtc).ToList();
        }
    }

    internal CandidateEvaluationJobStatus? Cancel(string id)
    {
        lock (_gate)
        {
            EnsureSummaryStoreAvailable();
            if (Prune()) PersistLocked();
            if (!_jobs.TryGetValue(id, out Job? job)) return null;
            if (ReferenceEquals(_active, job) && !job.Cancellation.IsCancellationRequested)
            {
                job.Status = "Cancelling";
                InvalidDataException? persistenceFailure = null;
                try { PersistLocked(); }
                catch (InvalidDataException ex) { persistenceFailure = ex; }
                job.Cancellation.Cancel();
                if (persistenceFailure is not null) throw persistenceFailure;
            }
            return Snapshot(job);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (_gate)
        {
            _disposed = true;
            _active?.Cancellation.Cancel();
            running = _active?.Task;
        }
        if (running is not null)
        {
            try { await running.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _active?.Cancellation.Cancel();
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        string status = "Failed";
        string? errorCode = "internal_error";
        CancellationToken token = job.Cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            CandidateTextQualityRunResult quality = job.QualityProfileId == CandidateTextQualitySuite.ProfileId
                ? await _quality.RunAsync(job.CandidateKey, job.ManagedFileName, token).ConfigureAwait(false)
                : _quality is IProfiledCandidateTextQualityRunner profiled
                    ? await profiled.RunProfileAsync(job.CandidateKey, job.ManagedFileName,
                        job.QualityProfileId!, token).ConfigureAwait(false)
                    : new CandidateTextQualityRunResult("Unavailable", false, null);
            lock (_gate)
            {
                job.QualityStatus = quality.Status;
                job.QualityInvocationId = quality.InvocationId;
                job.QualityPassCount = quality.Evidence?.PassCount;
                job.QualityExpectedCount = quality.Evidence?.ExpectedCaseCount;
                RecordLaunch(job, quality.Launch, quality.LaunchRefusal);
                job.QualityFailedCaseIds = quality.Evidence is null ? null
                    : quality.Evidence.Cases.Where(item => item.Outcome != "pass").Select(item => item.CaseId).Take(64).ToList();
                PersistLocked();
            }
            token.ThrowIfCancellationRequested();
            if (!QualityPassed(quality, job.QualityProfileId!))
            {
                status = quality.Status == "Unavailable" ? "Unavailable"
                    : quality.Status == "Cancelled" ? "Cancelled" : "Failed";
                errorCode = status == "Cancelled" ? "cancelled" : "quality_not_passed";
                return;
            }
            lock (_gate)
            {
                job.Phase = "Performance";
                PersistLocked();
            }
            token.ThrowIfCancellationRequested();
            CandidateTextPerformanceRunResult performance = job.QualityProfileId != CandidateTextQualitySuite.ProfileId
                && _performance is IProfiledCandidateTextPerformanceRunner profiledPerformance
                    ? await profiledPerformance.RunProfileAsync(job.CandidateKey, job.ManagedFileName,
                        job.QualityProfileId!, token).ConfigureAwait(false)
                    : job.QualityProfileId != CandidateTextQualitySuite.ProfileId
                        ? new CandidateTextPerformanceRunResult("Unavailable", false, null, "actual_launch_unavailable")
                        : await _performance.RunAsync(job.CandidateKey, job.ManagedFileName, token).ConfigureAwait(false);
            lock (_gate)
            {
                job.PerformanceStatus = performance.Status;
                job.Repetitions = performance.Evidence?.Repetitions;
                job.GeneratedTokenCount = performance.Evidence?.TokenCount;
                job.PromptTokensPerSecond = Metric(performance.Evidence, CandidatePerformanceMetric.PromptTokensPerSecondId);
                job.DecodeTokensPerSecond = Metric(performance.Evidence, CandidatePerformanceMetric.DecodeTokensPerSecondId);
                job.CompletionMilliseconds = Metric(performance.Evidence, CandidatePerformanceMetric.CompletionMillisecondsId);
                job.TimeToFirstTokenMilliseconds = Metric(performance.Evidence, CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId);
                // A capped sample makes the completion time a lower bound; it is labelled as such and the
                // comparator already refuses to let it win (completion_latency_censored).
                job.CompletionLatencyIsLowerBound = performance.Evidence is { Samples.Count: > 0 } measured
                    ? measured.Samples.Any(sample => sample.FinishReason == "length") : null;
                if (performance.Launch is not null && job.EvaluationLaunchFingerprint is not null
                    && performance.Launch.Fingerprint != job.EvaluationLaunchFingerprint)
                    job.EvaluationLaunchRefusal = "quality_performance_launch_mismatch";
                else if (performance.Launch is not null || performance.LaunchRefusal is not null)
                    RecordLaunch(job, performance.Launch,
                        performance.LaunchRefusal ?? job.EvaluationLaunchRefusal);
                PersistLocked();
            }
            token.ThrowIfCancellationRequested();
            if (!PerformancePassed(performance))
            {
                status = performance.Status == "Unavailable" ? "Unavailable"
                    : performance.Status == "Cancelled" ? "Cancelled" : "Failed";
                errorCode = status == "Cancelled" ? "cancelled" : "performance_not_completed";
                return;
            }
            lock (_gate)
            {
                job.Phase = "Eligibility";
                PersistLocked();
            }
            token.ThrowIfCancellationRequested();
            string eligibility = await _evidence.CheckEligibilityAsync(job.CandidateKey,
                job.ManagedFileName, job.Capability!, job.QualitySuiteId!,
                job.QualitySuiteVersion!.Value,
                CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(job.QualityProfileId,
                    out CandidateEvaluationProfileDefinition selectedProfile)
                    ? selectedProfile.QualitySuiteSha256 : string.Empty,
                quality.Evidence!.Environment!, performance.Evidence!.Environment, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                job.Eligibility = eligibility;
                PersistLocked();
            }
            status = "Completed";
            errorCode = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = "Cancelled";
            errorCode = "cancelled";
        }
        catch (Exception)
        {
            // No exception text or raw model response can reach the public job summary.
            status = "Failed";
            errorCode = "internal_error";
        }
        finally
        {
            lock (_gate)
            {
                job.Status = status;
                job.Phase = "Done";
                job.ErrorCode = errorCode;
                job.CompletedUtc = _utcNow();
                _active = null; // only after both runner scopes and their child cleanup have returned.
                _terminal.Enqueue(job.Id);
                Prune();
                try { PersistLocked(); }
                catch (Exception)
                {
                    job.Status = "Unavailable";
                    job.ErrorCode = "summary_persistence_failed";
                    job.Eligibility = null;
                }
            }
            job.Cancellation.Dispose();
        }
    }

    private static void RecordLaunch(Job job, TextLaunchDescriptor? launch, string? refusal)
    {
        if (launch is not null)
        {
            job.EvaluationLaunchFingerprint = launch.Fingerprint;
            job.EvaluationLaunchArguments = launch.Arguments.ToList();
            job.EvaluationLaunchEnvironmentNames = launch.Environment.Select(entry => entry.Split('=')[0]).ToList();
            job.RequestParity = CandidateRequestParity.Version;
        }
        job.EvaluationLaunchRefusal = refusal is null ? null : CandidateEvaluationJobStore.SanitizeReason(refusal);
    }

    private static bool QualityPassed(CandidateTextQualityRunResult run, string profileId)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(profileId,
                out CandidateEvaluationProfileDefinition profile)) return false;
        CandidateQualityEvidence? e = run.Evidence;
        return run.Committed && run.Status == "Completed" && e is { RecordVersion: 3, Completed: true,
            ExecutionStatus: "Completed", Cancelled: false, Truncated: false, ExternalFallbackUsed: false }
            && Guid.TryParseExact(e.InvocationId, "N", out _)
            && profile.MatchesQuality(e)
            && e.Environment is not null && e.ExpectedCaseCount == profile.QualityExpectedCaseCount
            && e.FailCount + e.PassCount == e.ExpectedCaseCount && e.InconclusiveCount == 0
            && e.ErrorCount == 0 && e.Cases.Count == e.ExpectedCaseCount
            && e.Cases.Select(c => c.CaseId).SequenceEqual(profile.QualityCases.Select(c => c.Id))
            && e.Cases.All(c => c.FinishReason == "stop" && !c.Truncated)
            && QualityRunAboveProfileFloor(e, profile);
    }

    private static bool QualityRunAboveProfileFloor(CandidateQualityEvidence evidence,
        CandidateEvaluationProfileDefinition profile)
    {
        if (profile.MinimumRunPassCount == 0) return evidence.PassCount == evidence.ExpectedCaseCount;
        Dictionary<string, CandidateQualityCaseResult> results = evidence.Cases.ToDictionary(item => item.CaseId,
            StringComparer.Ordinal);
        return evidence.PassCount >= profile.MinimumRunPassCount
            && profile.QualityCases.GroupBy(item => item.Category, StringComparer.Ordinal)
                .All(group => group.Count(item => results[item.Id].Outcome == "pass") >= profile.MinimumPassesPerCategory)
            && profile.QualityCases.Where(item => item.Critical).All(item => results[item.Id].Outcome == "pass");
    }

    private static bool PerformancePassed(CandidateTextPerformanceRunResult run)
    {
        CandidatePerformanceEvidence? e = run.Evidence;
        return run.Committed && run.Status == "Completed" && e is { RecordVersion: 3, Completed: true,
            ExecutionStatus: "Completed", Cancelled: false, Truncated: false, ExternalFallbackUsed: false }
            && e.Capability == CandidateTextQualitySuite.Capability
            && e.WorkloadId == CandidateTextPerformanceWorkload.Id
            && e.WorkloadVersion == CandidateTextPerformanceWorkload.Version
            && e.WorkloadSha256 == CandidateTextPerformanceWorkload.Sha256
            && e.ExpectedRepetitions == CandidateTextPerformanceWorkload.Repetitions
            && e.Repetitions == e.ExpectedRepetitions && e.Samples.Count == e.ExpectedRepetitions;
    }

    private static double? Metric(CandidatePerformanceEvidence? evidence, string name)
    {
        double? value = evidence?.Metrics.FirstOrDefault(metric => metric.Name == name)?.Value;
        return value is > 0 && double.IsFinite(value.Value) ? value : null;
    }

    private bool Prune()
    {
        bool changed = false;
        DateTime cutoff = _utcNow() - TerminalRetention;
        while (_terminal.Count > 0)
        {
            string oldest = _terminal.Peek();
            if (_terminal.Count <= MaximumTerminalJobs
                && _jobs.TryGetValue(oldest, out Job? oldestJob) && oldestJob.CompletedUtc > cutoff)
                break;
            _terminal.Dequeue();
            changed |= _jobs.Remove(oldest);
        }
        return changed;
    }

    private void LoadAndRecoverSummaries()
    {
        try
        {
            List<CandidateEvaluationJobStatus> persisted = _summaryStore.Load();
            bool changed = false;
            foreach (CandidateEvaluationJobStatus status in persisted.OrderBy(item => item.CompletedUtc ?? DateTime.MaxValue)
                .ThenBy(item => item.StartedUtc))
            {
                Job job = FromSnapshot(status);
                if (job.Status is "Queued" or "Running" or "Cancelling")
                {
                    job.Status = "Interrupted";
                    job.CompletedUtc = _utcNow();
                    job.ErrorCode = "core_restart_interrupted";
                    job.Eligibility = null;
                    changed = true;
                }
                _jobs.Add(job.Id, job);
                _terminal.Enqueue(job.Id);
            }
            changed |= Prune();
            if (changed) PersistLocked();
        }
        catch (Exception ex) when (IsSummaryStoreFailure(ex))
        {
            _jobs.Clear();
            _terminal.Clear();
            _summaryStoreAvailable = false;
        }
    }

    private static Job FromSnapshot(CandidateEvaluationJobStatus status) => new()
    {
        Id = status.JobId,
        CandidateKey = status.CandidateKey,
        ManagedFileName = string.Empty,
        Cancellation = new CancellationTokenSource(),
        StartedUtc = status.StartedUtc,
        Status = status.Status,
        Phase = status.Phase,
        CompletedUtc = status.CompletedUtc,
        QualityStatus = status.QualityStatus,
        QualityPassCount = status.QualityPassCount,
        QualityExpectedCount = status.QualityExpectedCount,
        PerformanceStatus = status.PerformanceStatus,
        Repetitions = status.Repetitions,
        GeneratedTokenCount = status.GeneratedTokenCount,
        PromptTokensPerSecond = status.PromptTokensPerSecond,
        DecodeTokensPerSecond = status.DecodeTokensPerSecond,
        CompletionMilliseconds = status.CompletionMilliseconds,
        TimeToFirstTokenMilliseconds = status.TimeToFirstTokenMilliseconds,
        Capability = status.Capability,
        QualitySuiteId = status.QualitySuiteId,
        QualitySuiteVersion = status.QualitySuiteVersion,
        QualityProfileId = status.QualityProfileId,
        QualityProfileVersion = status.QualityProfileVersion,
        QualityInvocationId = status.QualityInvocationId,
        PerformanceWorkloadId = status.PerformanceWorkloadId,
        PerformanceWorkloadVersion = status.PerformanceWorkloadVersion,
        Eligibility = status.Eligibility,
        ErrorCode = status.ErrorCode,
        EvaluationLaunchFingerprint = status.EvaluationLaunchFingerprint,
        EvaluationLaunchArguments = status.EvaluationLaunchArguments?.ToList(),
        EvaluationLaunchEnvironmentNames = status.EvaluationLaunchEnvironmentNames?.ToList(),
        EvaluationLaunchRefusal = status.EvaluationLaunchRefusal,
        RequestParity = status.RequestParity,
        CompletionLatencyIsLowerBound = status.CompletionLatencyIsLowerBound,
        QualityFailedCaseIds = status.QualityFailedCaseIds?.ToList()
    };

    private void EnsureSummaryStoreAvailable()
    {
        if (!_summaryStoreAvailable)
            throw new InvalidDataException("Candidate evaluation history is unavailable; existing data was preserved.");
    }

    private void PersistLocked()
    {
        EnsureSummaryStoreAvailable();
        try { _summaryStore.Save(_jobs.Values.Select(Snapshot).ToList()); }
        catch (Exception ex) when (IsSummaryStoreFailure(ex))
        {
            _summaryStoreAvailable = false;
            throw new InvalidDataException("Candidate evaluation history could not be persisted; existing data was preserved.", ex);
        }
    }

    private static bool IsSummaryStoreFailure(Exception exception) => exception is InvalidDataException
        or IOException or UnauthorizedAccessException or SecurityException or JsonException or NotSupportedException;

    private static CandidateEvaluationJobStatus Snapshot(Job job) => new()
    {
        JobId = job.Id, CandidateKey = job.CandidateKey, Status = job.Status, Phase = job.Phase,
        StartedUtc = job.StartedUtc, CompletedUtc = job.CompletedUtc,
        QualityStatus = job.QualityStatus, QualityPassCount = job.QualityPassCount,
        QualityExpectedCount = job.QualityExpectedCount, PerformanceStatus = job.PerformanceStatus,
        Repetitions = job.Repetitions, GeneratedTokenCount = job.GeneratedTokenCount,
        PromptTokensPerSecond = job.PromptTokensPerSecond, DecodeTokensPerSecond = job.DecodeTokensPerSecond,
        CompletionMilliseconds = job.CompletionMilliseconds,
        TimeToFirstTokenMilliseconds = job.TimeToFirstTokenMilliseconds,
        Capability = job.Capability, QualitySuiteId = job.QualitySuiteId,
        QualitySuiteVersion = job.QualitySuiteVersion, QualityProfileId = job.QualityProfileId,
        QualityProfileVersion = job.QualityProfileVersion, QualityInvocationId = job.QualityInvocationId,
        PerformanceWorkloadId = job.PerformanceWorkloadId,
        PerformanceWorkloadVersion = job.PerformanceWorkloadVersion, Eligibility = job.Eligibility,
        ErrorCode = job.ErrorCode,
        EvaluationLaunchFingerprint = job.EvaluationLaunchFingerprint,
        EvaluationLaunchArguments = job.EvaluationLaunchArguments?.ToList(),
        EvaluationLaunchEnvironmentNames = job.EvaluationLaunchEnvironmentNames?.ToList(),
        EvaluationLaunchRefusal = job.EvaluationLaunchRefusal,
        RequestParity = job.RequestParity,
        CompletionLatencyIsLowerBound = job.CompletionLatencyIsLowerBound,
        QualityFailedCaseIds = job.QualityFailedCaseIds?.ToList()
    };
}
