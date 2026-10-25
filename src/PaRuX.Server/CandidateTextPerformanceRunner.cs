using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record TextPerformanceCompletion(int PromptTokens, double PromptMilliseconds,
    int GeneratedTokens, double DecodeMilliseconds, double CompletionMilliseconds,
    double TimeToFirstTokenMilliseconds, string FinishReason = "stop");

internal interface ITextPerformanceSession : IAsyncDisposable
{
    Task<TextPerformanceCompletion> MeasureAsync(CancellationToken cancellationToken);
}

internal interface ITextPerformanceRuntime
{
    Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken);
    Task<ITextPerformanceSession> StartAsync(TextQualityRuntimeSpec spec, string modelPath,
        CancellationToken cancellationToken);

    /// <summary>The actual AUTO launch for this exact model (see ITextQualityRuntime.ResolveForModelAsync).</summary>
    Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath, CancellationToken cancellationToken) =>
        Task.FromResult(new TextQualityRuntimeResolution(null, "actual_launch_unavailable"));
}

internal sealed record CandidateTextPerformanceRunResult(string Status, bool Committed,
    CandidatePerformanceEvidence? Evidence, string? LaunchRefusal = null, TextLaunchDescriptor? Launch = null);

internal interface ICandidateTextPerformanceRunner
{
    Task<CandidateTextPerformanceRunResult> RunAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken);
}

/// <summary>Measures the shared workload under the launch scope of the selected quality profile.</summary>
internal interface IProfiledCandidateTextPerformanceRunner : ICandidateTextPerformanceRunner
{
    Task<CandidateTextPerformanceRunResult> RunProfileAsync(string candidateKey, string managedFileName,
        string qualityProfileId, CancellationToken cancellationToken);
}

internal sealed class LlamaCandidateTextPerformanceRuntime : ITextPerformanceRuntime
{
    private readonly ITextQualityRuntime _shared;

    public LlamaCandidateTextPerformanceRuntime() => _shared = new LlamaCandidateTextQualityRuntime();

    public LlamaCandidateTextPerformanceRuntime(ITextQualityRuntime shared) => _shared = shared;

    public async Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken) =>
        ForPerformance(await _shared.ResolveAsync(cancellationToken).ConfigureAwait(false));

    public async Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath,
        CancellationToken cancellationToken)
    {
        TextQualityRuntimeResolution resolution = await _shared.ResolveForModelAsync(modelPath, cancellationToken)
            .ConfigureAwait(false);
        return resolution with { Spec = ForPerformance(resolution.Spec) };
    }

    private static TextQualityRuntimeSpec? ForPerformance(TextQualityRuntimeSpec? spec)
    {
        if (spec is null) return null;
        CandidateEvaluationEnvironment e = spec.Environment;
        return spec with { Environment = new CandidateEvaluationEnvironment
        {
            HardwareFingerprint = e.HardwareFingerprint, RuntimeFingerprint = e.RuntimeFingerprint,
            BinaryFingerprint = e.BinaryFingerprint, ConfigurationFingerprint = e.ConfigurationFingerprint,
            WorkloadFingerprint = CandidateTextPerformanceWorkload.WorkloadFingerprint
        } };
    }

    public async Task<ITextPerformanceSession> StartAsync(TextQualityRuntimeSpec spec, string modelPath,
        CancellationToken cancellationToken) => await LlamaTextQualitySession.StartAsync(spec, modelPath,
            cancellationToken, "parux-performance-").ConfigureAwait(false);
}

/// <summary>Measures only the fixed local workload against the active managed Candidate bytes.</summary>
internal sealed class CandidateTextPerformanceRunner : IProfiledCandidateTextPerformanceRunner
{
    private readonly CandidateEvidenceService _evidence;
    private readonly ITextPerformanceRuntime _runtime;
    private readonly Func<bool> _gamingActive;

    internal CandidateTextPerformanceRunner(CandidateEvidenceService evidence, ITextPerformanceRuntime runtime,
        Func<bool> gamingActive)
    {
        _evidence = evidence;
        _runtime = runtime;
        _gamingActive = gamingActive;
    }

    public Task<CandidateTextPerformanceRunResult> RunAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken) => RunProfileAsync(candidateKey, managedFileName,
            CandidateTextQualitySuite.ProfileId, cancellationToken);

    public async Task<CandidateTextPerformanceRunResult> RunProfileAsync(string candidateKey, string managedFileName,
        string qualityProfileId, CancellationToken cancellationToken)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(qualityProfileId,
                out CandidateEvaluationProfileDefinition profile))
            return new("Unavailable", false, null);
        string? launchRefusal = null;
        TextLaunchDescriptor? launch = null;
        CandidateTextPerformanceRunResult result = await RunCoreAsync(candidateKey, managedFileName, profile,
            reason => launchRefusal = reason, descriptor => launch = descriptor, cancellationToken).ConfigureAwait(false);
        return result with { LaunchRefusal = launchRefusal, Launch = launch };
    }

    private async Task<CandidateTextPerformanceRunResult> RunCoreAsync(string candidateKey, string managedFileName,
        CandidateEvaluationProfileDefinition profile, Action<string> refuse, Action<TextLaunchDescriptor?> launched,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new("Cancelled", false, null);
        CandidateEvidenceIdentity? identity;
        try
        {
            identity = await _evidence.TryCaptureSnapshotAsync(candidateKey,
                managedFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new("Cancelled", false, null);
        }
        if (identity is null) return new("Unavailable", false, null);
        var record = new CandidatePerformanceEvidence
        {
            RecordVersion = 3, Identity = identity, Capability = CandidateTextQualitySuite.Capability,
            WorkloadId = CandidateTextPerformanceWorkload.Id,
            WorkloadVersion = CandidateTextPerformanceWorkload.Version,
            WorkloadSha256 = CandidateTextPerformanceWorkload.Sha256,
            ExpectedRepetitions = CandidateTextPerformanceWorkload.Repetitions,
            ExecutionStatus = "Unavailable"
        };
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(TimeSpan.FromMinutes(5));
        using var monitorCts = new CancellationTokenSource();
        Task monitor = MonitorGamingAsync(runCts, monitorCts.Token);
        try
        {
            if (_gamingActive()) return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            string modelPath = Path.GetFullPath(Path.Combine(_evidence.ModelsDirectory, identity.ManagedFileName));
            if (CandidateRequestParity.CheckLive(CandidateTextPerformanceWorkload.Prompt, modelPath) is { } parityRefusal)
            {
                refuse(parityRefusal);
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            }
            (TextQualityRuntimeSpec? spec, string? specRefusal) = await ResolveSpecAsync(profile, modelPath, runCts.Token)
                .ConfigureAwait(false);
            if (spec is null || !CandidateEvidenceStore.ValidEnvironment(spec.Environment)
                || spec.Environment.WorkloadFingerprint != CandidateTextPerformanceWorkload.WorkloadFingerprint
                || profile.ActualLaunchScope != (spec.Launch is not null))
            {
                refuse(specRefusal ?? "evaluation_environment_unavailable");
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            }
            record.Environment = spec.Environment;
            launched(spec.Launch?.Descriptor);
            await using var lease = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string lockedHash = Convert.ToHexString(await SHA256.HashDataAsync(lease, runCts.Token)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (lockedHash != identity.LocalSha256)
            {
                record.FailureReason = "model_hash_mismatch";
                return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
            }
            if (!_evidence.TryBeginEvaluation(identity,
                () => _runtime.StartAsync(spec, modelPath, runCts.Token),
                out Task<ITextPerformanceSession>? startTask) || startTask is null)
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            await using (ITextPerformanceSession session = await startTask.ConfigureAwait(false))
            {
                for (int repetition = 1; repetition <= CandidateTextPerformanceWorkload.Repetitions; repetition++)
                {
                    runCts.Token.ThrowIfCancellationRequested();
                    if (_gamingActive()) throw new OperationCanceledException(runCts.Token);
                    using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
                    requestCts.CancelAfter(TimeSpan.FromSeconds(60));
                    TextPerformanceCompletion measured = await session.MeasureAsync(requestCts.Token)
                        .ConfigureAwait(false);
                    if (!Valid(measured))
                        throw PerformanceStreamException.Create("measurement_invalid", "Invalid performance measurement.");
                    record.Samples.Add(new CandidatePerformanceSample
                    {
                        Repetition = repetition, Outcome = "pass", FinishReason = measured.FinishReason,
                        PromptTokens = measured.PromptTokens, PromptMilliseconds = measured.PromptMilliseconds,
                        GeneratedTokens = measured.GeneratedTokens, DecodeMilliseconds = measured.DecodeMilliseconds,
                        CompletionMilliseconds = measured.CompletionMilliseconds,
                        TimeToFirstTokenMilliseconds = measured.TimeToFirstTokenMilliseconds
                    });
                }
            }
            TextQualityRuntimeSpec? after = (await ResolveSpecAsync(profile, modelPath, runCts.Token).ConfigureAwait(false)).Spec;
            if (after is null || after.Executable != spec.Executable || after.Backend != spec.Backend
                || !SameEnvironment(after.Environment, spec.Environment))
            {
                record.FailureReason = "runtime_changed";
                return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
            }
            int promptTokens = record.Samples.Sum(s => s.PromptTokens);
            int generatedTokens = record.Samples.Sum(s => s.GeneratedTokens);
            double promptMs = record.Samples.Sum(s => s.PromptMilliseconds);
            double decodeMs = record.Samples.Sum(s => s.DecodeMilliseconds);
            record.Repetitions = record.Samples.Count;
            record.TokenCount = generatedTokens;
            record.Metrics = new List<CandidateMeasuredMetric>
            {
                new() { Name = CandidatePerformanceMetric.PromptTokensPerSecondId, Unit = CandidatePerformanceMetric.TokensPerSecondUnit, Value = 1000d * promptTokens / promptMs },
                new() { Name = CandidatePerformanceMetric.DecodeTokensPerSecondId, Unit = CandidatePerformanceMetric.TokensPerSecondUnit, Value = 1000d * generatedTokens / decodeMs },
                new() { Name = CandidatePerformanceMetric.CompletionMillisecondsId, Unit = CandidatePerformanceMetric.MillisecondsUnit, Value = record.Samples.Average(s => s.CompletionMilliseconds) },
                new() { Name = CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId, Unit = CandidatePerformanceMetric.MillisecondsUnit, Value = record.Samples.Average(s => s.TimeToFirstTokenMilliseconds) }
            };
            return await CommitAsync(record, "Completed", runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!runCts.IsCancellationRequested)
        {
            record.FailureReason = "request_timeout";
            return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CommitAsync(record, "Cancelled", runCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or HttpRequestException or TimeoutException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            record.FailureReason = FailureCode(ex);
            return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            monitorCts.Cancel();
            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Bounded, fixed-vocabulary reason; never exception text, which could carry paths or model output.</summary>
    internal static string FailureCode(Exception ex) => PerformanceStreamException.CodeOf(ex) is { } code ? code : ex switch
    {
        System.ComponentModel.Win32Exception => "runtime_start_failed",
        JsonException => "stream_malformed",
        InvalidDataException => "stream_invalid",
        HttpRequestException => "runtime_http_failed",
        TimeoutException => "request_timeout",
        IOException => "runtime_io_failed",
        _ => "runtime_failed"
    };

    private async Task<(TextQualityRuntimeSpec? Spec, string? Refusal)> ResolveSpecAsync(
        CandidateEvaluationProfileDefinition profile, string modelPath, CancellationToken cancellationToken)
    {
        if (!profile.ActualLaunchScope)
            return (await _runtime.ResolveAsync(cancellationToken).ConfigureAwait(false), null);
        TextQualityRuntimeResolution resolution = await _runtime.ResolveForModelAsync(modelPath, cancellationToken)
            .ConfigureAwait(false);
        return (resolution.Spec, resolution.Refusal);
    }

    private static bool Valid(TextPerformanceCompletion m) => m.PromptTokens is > 0 and <= 2048
        && m.GeneratedTokens > 0 && m.GeneratedTokens <= CandidateTextPerformanceWorkload.MaximumGeneratedTokens
        && (m.FinishReason == "stop" || (m.FinishReason == "length"
            && m.GeneratedTokens == CandidateTextPerformanceWorkload.MaximumGeneratedTokens))
        && double.IsFinite(m.PromptMilliseconds) && m.PromptMilliseconds > 0
        && double.IsFinite(m.DecodeMilliseconds) && m.DecodeMilliseconds > 0
        && double.IsFinite(m.CompletionMilliseconds) && m.CompletionMilliseconds > 0
        && double.IsFinite(m.TimeToFirstTokenMilliseconds) && m.TimeToFirstTokenMilliseconds > 0
        && m.TimeToFirstTokenMilliseconds <= m.CompletionMilliseconds;

    private async Task MonitorGamingAsync(CancellationTokenSource runCts, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && !runCts.IsCancellationRequested)
        {
            if (_gamingActive()) { runCts.Cancel(); break; }
            await Task.Delay(250, stop).ConfigureAwait(false);
        }
    }

    private static bool SameEnvironment(CandidateEvaluationEnvironment a, CandidateEvaluationEnvironment b) =>
        a.HardwareFingerprint == b.HardwareFingerprint && a.RuntimeFingerprint == b.RuntimeFingerprint
        && a.BinaryFingerprint == b.BinaryFingerprint && a.ConfigurationFingerprint == b.ConfigurationFingerprint
        && a.WorkloadFingerprint == b.WorkloadFingerprint;

    private async Task<CandidateTextPerformanceRunResult> CommitAsync(CandidatePerformanceEvidence record,
        string status, CancellationToken runToken)
    {
        if (status == "Completed" && (runToken.IsCancellationRequested || _gamingActive())) status = "Cancelled";
        record.ExecutionStatus = status;
        record.Completed = status == "Completed";
        record.Cancelled = status == "Cancelled";
        record.Repetitions = record.Samples.Count;
        record.TokenCount = record.Samples.Sum(s => (long)s.GeneratedTokens);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var positiveCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        positiveCts.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken commitToken = record.Completed ? positiveCts.Token : timeoutCts.Token;
        try
        {
            CandidateEvidenceIdentity? current = await _evidence.TryCaptureSnapshotAsync(record.Identity.CandidateKey,
                record.Identity.ManagedFileName, commitToken).ConfigureAwait(false);
            if (current is null || current.LocalSha256 != record.Identity.LocalSha256)
                return new(status, false, record);
            if (record.Completed && (runToken.IsCancellationRequested || _gamingActive()))
            {
                record.ExecutionStatus = "Cancelled"; record.Completed = false; record.Cancelled = true;
                return new("Cancelled", false, record);
            }
            bool committed = await _evidence.TryCommitPerformanceAsync(record, commitToken,
                record.Completed ? _gamingActive : null).ConfigureAwait(false);
            if (!committed && record.Completed && (runToken.IsCancellationRequested || _gamingActive()))
            {
                record.ExecutionStatus = "Cancelled"; record.Completed = false; record.Cancelled = true;
                return new("Cancelled", false, record);
            }
            return new(status, committed, record);
        }
        catch (OperationCanceledException) when (record.Completed)
        {
            record.ExecutionStatus = "Cancelled"; record.Completed = false; record.Cancelled = true;
            return new("Cancelled", false, record);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return new(status, false, record);
        }
    }
}
