using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record TextQualityRuntimeSpec(string Executable, string Backend,
    CandidateEvaluationEnvironment Environment, EvaluationLaunch? Launch = null);

/// <summary>An evaluation launch reproducing the production planner's exact fresh-exact launch: the planned
/// semantic arguments, the explicit environment overrides and the ld1 descriptor they must reproduce.</summary>
internal sealed record EvaluationLaunch(IReadOnlyList<string> SessionArguments,
    IReadOnlyDictionary<string, string> EnvironmentOverrides, TextLaunchDescriptor Descriptor, string ModelPath);

/// <summary>Spec or a bounded refusal reason (for diagnostics); never both.</summary>
internal sealed record TextQualityRuntimeResolution(TextQualityRuntimeSpec? Spec, string? Refusal);
internal sealed record TextQualityCompletion(string Content, string FinishReason);

internal interface ITextQualitySession : IAsyncDisposable
{
    Task<TextQualityCompletion> CompleteAsync(CandidateTextQualitySuite.Case item, CancellationToken cancellationToken);
}

internal interface ITextQualityRuntime
{
    Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken);
    Task<ITextQualitySession> StartAsync(TextQualityRuntimeSpec spec, string modelPath, CancellationToken cancellationToken);

    /// <summary>The actual AUTO launch for this exact model, via the single production planner. A runtime
    /// that cannot plan it refuses; evidence is then never produced under an approximation.</summary>
    Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath, CancellationToken cancellationToken) =>
        Task.FromResult(new TextQualityRuntimeResolution(null, "actual_launch_unavailable"));
}

internal sealed record CandidateTextQualityRunResult(string Status, bool Committed,
    CandidateQualityEvidence? Evidence, string? InvocationId = null, string? LaunchRefusal = null,
    TextLaunchDescriptor? Launch = null);

internal interface ICandidateTextQualityRunner
{
    Task<CandidateTextQualityRunResult> RunAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken);
}

internal interface IProfiledCandidateTextQualityRunner : ICandidateTextQualityRunner
{
    Task<CandidateTextQualityRunResult> RunProfileAsync(string candidateKey, string managedFileName,
        string qualityProfileId, CancellationToken cancellationToken);
}

/// <summary>Runs fixed objective checks against only the exact active managed Candidate bytes.</summary>
internal sealed class CandidateTextQualityRunner : IProfiledCandidateTextQualityRunner
{
    private readonly CandidateEvidenceService _evidence;
    private readonly ITextQualityRuntime _runtime;
    private readonly Func<bool> _gamingActive;

    internal CandidateTextQualityRunner(CandidateEvidenceService evidence, ITextQualityRuntime runtime,
        Func<bool> gamingActive)
    {
        _evidence = evidence;
        _runtime = runtime;
        _gamingActive = gamingActive;
    }

    public Task<CandidateTextQualityRunResult> RunAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken) => RunProfileAsync(candidateKey, managedFileName,
            CandidateTextQualitySuite.ProfileId, cancellationToken);

    private sealed class LaunchCapture
    {
        internal string? Refusal;
        internal TextLaunchDescriptor? Launch;
    }

    public async Task<CandidateTextQualityRunResult> RunProfileAsync(string candidateKey, string managedFileName,
        string qualityProfileId, CancellationToken cancellationToken)
    {
        var capture = new LaunchCapture();
        CandidateTextQualityRunResult result = await RunProfileCoreAsync(candidateKey, managedFileName,
            qualityProfileId, capture, cancellationToken).ConfigureAwait(false);
        return result with { LaunchRefusal = capture.Refusal, Launch = capture.Launch };
    }

    private async Task<CandidateTextQualityRunResult> RunProfileCoreAsync(string candidateKey, string managedFileName,
        string qualityProfileId, LaunchCapture capture, CancellationToken cancellationToken)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(qualityProfileId,
                out CandidateEvaluationProfileDefinition profile))
            return new("Unavailable", false, null);
        string invocationId = Guid.NewGuid().ToString("N");
        if (cancellationToken.IsCancellationRequested) return new("Cancelled", false, null, invocationId);
        CandidateEvidenceIdentity? identity;
        try
        {
            identity = await _evidence.TryCaptureSnapshotAsync(
                candidateKey, managedFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new("Cancelled", false, null, invocationId);
        }
        if (identity is null) return new("Unavailable", false, null, invocationId);
        var record = new CandidateQualityEvidence
        {
            RecordVersion = 3, InvocationId = invocationId,
            Identity = identity, Capability = profile.Capability,
            SuiteId = profile.QualitySuiteId, SuiteVersion = profile.QualitySuiteVersion,
            SuiteSha256 = profile.QualitySuiteSha256,
            QualityProfileId = profile.QualityProfileId,
            QualityProfileVersion = profile.QualityProfileVersion,
            QualityProfileSha256 = profile.QualityProfileSha256,
            ExpectedCaseCount = profile.QualityExpectedCaseCount, ExecutionStatus = "Unavailable"
        };
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(profile.Capability == CandidateGeneralChatQualitySuite.Capability
            ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(4));
        using var monitorCts = new CancellationTokenSource();
        Task monitor = MonitorGamingAsync(runCts, monitorCts.Token);
        try
        {
            if (_gamingActive()) return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            string modelPath = Path.GetFullPath(Path.Combine(_evidence.ModelsDirectory, identity.ManagedFileName));
            if (profile.ActualLaunchScope
                && CandidateRequestParity.CheckLive(profile.QualityCases, modelPath) is { } parityRefusal)
            {
                capture.Refusal = parityRefusal;
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            }
            (TextQualityRuntimeSpec? resolvedSpec, string? launchRefusal) =
                await ResolveSpecAsync(profile, modelPath, runCts.Token).ConfigureAwait(false);
            TextQualityRuntimeSpec? spec = ForQualityProfile(resolvedSpec, profile);
            if (spec is null || !CandidateEvidenceStore.ValidEnvironment(spec.Environment)
                || spec.Environment.WorkloadFingerprint != profile.QualityWorkloadFingerprint
                || profile.ActualLaunchScope != (spec.Launch is not null))
            {
                capture.Refusal = launchRefusal ?? "evaluation_environment_unavailable";
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            }
            record.Environment = spec.Environment;
            capture.Launch = spec.Launch?.Descriptor;
            // Hold a read-only Windows share lease for the complete child lifetime. This prevents replacement,
            // deletion, or writes after the recorded hash while the runtime loads/uses the exact managed bytes.
            await using var modelLease = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string lockedHash = Convert.ToHexString(await SHA256.HashDataAsync(modelLease, runCts.Token)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (lockedHash != identity.LocalSha256)
                return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
            if (!_evidence.TryBeginEvaluation(identity,
                () => _runtime.StartAsync(spec, modelPath, runCts.Token),
                out Task<ITextQualitySession>? startTask) || startTask is null)
                return await CommitAsync(record, "Unavailable", runCts.Token).ConfigureAwait(false);
            await using (ITextQualitySession session = await startTask.ConfigureAwait(false))
            {
                for (int index = 0; index < profile.QualityCases.Count; index++)
                {
                    CandidateTextQualitySuite.Case item = profile.QualityCases[index];
                    runCts.Token.ThrowIfCancellationRequested();
                    if (_gamingActive()) throw new OperationCanceledException(runCts.Token);
                    CandidateQualityCaseResult result;
                    try
                    {
                        using var caseCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
                        caseCts.CancelAfter(TimeSpan.FromSeconds(45));
                        TextQualityCompletion completion = await session.CompleteAsync(item, caseCts.Token)
                            .ConfigureAwait(false);
                        string reason = completion.FinishReason;
                        bool validReason = reason is "stop" or "length";
                        result = new CandidateQualityCaseResult
                        {
                            CaseId = item.Id, Critical = item.Critical,
                            FinishReason = validReason ? reason : string.IsNullOrEmpty(reason) ? "missing" : "invalid",
                            Truncated = reason == "length",
                            Outcome = reason == "stop" && !string.IsNullOrWhiteSpace(completion.Content)
                                ? ValidateAnswer(profile, index, item, completion.Content) ? "pass" : "fail"
                                : reason == "length" || string.IsNullOrEmpty(reason) ? "inconclusive" : "error"
                        };
                    }
                    catch (OperationCanceledException) when (!runCts.IsCancellationRequested)
                    {
                        result = new CandidateQualityCaseResult
                        { CaseId = item.Id, Critical = item.Critical, FinishReason = "error", Outcome = "error" };
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                        or JsonException or TimeoutException)
                    {
                        result = new CandidateQualityCaseResult
                        { CaseId = item.Id, Critical = item.Critical, FinishReason = "error", Outcome = "error" };
                    }
                    record.Cases.Add(result);
                    if (result.Outcome == "error") break;
                }
            }
            TextQualityRuntimeSpec? after = ForQualityProfile(
                (await ResolveSpecAsync(profile, modelPath, runCts.Token).ConfigureAwait(false)).Spec, profile);
            if (after is null || after.Executable != spec.Executable
                || after.Backend != spec.Backend
                || !SameEnvironment(after.Environment, spec.Environment))
                return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
            record.PassCount = record.Cases.Count(c => c.Outcome == "pass");
            record.FailCount = record.Cases.Count(c => c.Outcome == "fail");
            record.InconclusiveCount = record.Cases.Count(c => c.Outcome == "inconclusive");
            record.ErrorCount = record.Cases.Count(c => c.Outcome == "error");
            record.Truncated = record.Cases.Any(c => c.Truncated);
            string status = record.Cases.Count == record.ExpectedCaseCount ? "Completed" : "Failed";
            return await CommitAsync(record, status, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!runCts.IsCancellationRequested)
        {
            return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CommitAsync(record, "Cancelled", runCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or HttpRequestException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return await CommitAsync(record, "Failed", runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            monitorCts.Cancel();
            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task<(TextQualityRuntimeSpec? Spec, string? Refusal)> ResolveSpecAsync(
        CandidateEvaluationProfileDefinition profile, string modelPath, CancellationToken cancellationToken)
    {
        if (!profile.ActualLaunchScope)
            return (await _runtime.ResolveAsync(cancellationToken).ConfigureAwait(false), null);
        TextQualityRuntimeResolution resolution = await _runtime.ResolveForModelAsync(modelPath, cancellationToken)
            .ConfigureAwait(false);
        return (resolution.Spec, resolution.Refusal);
    }

    private static TextQualityRuntimeSpec? ForQualityProfile(TextQualityRuntimeSpec? runtime,
        CandidateEvaluationProfileDefinition profile)
    {
        if (runtime is null || runtime.Environment is null) return null;
        CandidateEvaluationEnvironment source = runtime.Environment;
        return new TextQualityRuntimeSpec(runtime.Executable, runtime.Backend, new CandidateEvaluationEnvironment
        {
            HardwareFingerprint = source.HardwareFingerprint,
            RuntimeFingerprint = source.RuntimeFingerprint,
            BinaryFingerprint = source.BinaryFingerprint,
            ConfigurationFingerprint = source.ConfigurationFingerprint,
            WorkloadFingerprint = profile.QualityWorkloadFingerprint
        }, runtime.Launch);
    }

    private static bool ValidateAnswer(CandidateEvaluationProfileDefinition profile, int index,
        CandidateTextQualitySuite.Case item, string content) =>
        profile.Capability == CandidateGeneralChatQualitySuite.Capability
            ? CandidateGeneralChatQualitySuite.Validate(CandidateGeneralChatQualitySuite.Cases[index], content)
            : CandidateTextQualitySuite.Validate(item, content);

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
        && a.BinaryFingerprint == b.BinaryFingerprint
        && a.ConfigurationFingerprint == b.ConfigurationFingerprint
        && a.WorkloadFingerprint == b.WorkloadFingerprint;

    private async Task<CandidateTextQualityRunResult> CommitAsync(CandidateQualityEvidence record, string status,
        CancellationToken runToken)
    {
        // Gaming/caller/overall timeout cancellation must win over an all-pass commit, including the narrow
        // window after the final response and during the identity recheck.
        if (status == "Completed" && (runToken.IsCancellationRequested || _gamingActive())) status = "Cancelled";
        record.ExecutionStatus = status;
        record.Completed = status == "Completed";
        record.Cancelled = status == "Cancelled";
        record.PassCount = record.Cases.Count(c => c.Outcome == "pass");
        record.FailCount = record.Cases.Count(c => c.Outcome == "fail");
        record.InconclusiveCount = record.Cases.Count(c => c.Outcome == "inconclusive");
        record.ErrorCount = record.Cases.Count(c => c.Outcome == "error");
        record.Truncated = record.Cases.Any(c => c.Truncated);
        // This second capture detects byte changes after inference; TryCommitQualityAsync rechecks.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var positiveCommitCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        positiveCommitCts.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken commitToken = status == "Completed" ? positiveCommitCts.Token : timeoutCts.Token;
        try
        {
            CandidateEvidenceIdentity? current = await _evidence.TryCaptureSnapshotAsync(
                record.Identity.CandidateKey, record.Identity.ManagedFileName, commitToken).ConfigureAwait(false);
            if (current is null || current.LocalSha256 != record.Identity.LocalSha256)
                return new(status, false, record, record.InvocationId);
            if (status == "Completed" && (runToken.IsCancellationRequested || _gamingActive()))
            {
                record.ExecutionStatus = "Cancelled";
                record.Completed = false;
                record.Cancelled = true;
                return new("Cancelled", false, record, record.InvocationId);
            }
            bool committed = await _evidence.TryCommitQualityAsync(record, commitToken,
                record.Completed ? _gamingActive : null).ConfigureAwait(false);
            if (!committed && record.Completed && (runToken.IsCancellationRequested || _gamingActive()))
            {
                record.ExecutionStatus = "Cancelled";
                record.Completed = false;
                record.Cancelled = true;
                return new("Cancelled", false, record, record.InvocationId);
            }
            return new(status, committed, record, record.InvocationId);
        }
        catch (OperationCanceledException) when (status == "Completed")
        {
            record.ExecutionStatus = "Cancelled";
            record.Completed = false;
            record.Cancelled = true;
            return new("Cancelled", false, record, record.InvocationId);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return new(status, false, record, record.InvocationId);
        }
    }
}

/// <summary>PaRuX-owned GPU runtime, with no interactive-slot or external-service dependency.</summary>
internal sealed class LlamaCandidateTextQualityRuntime : ITextQualityRuntime
{
    private readonly LocalAiProcessManager? _planner;

    public LlamaCandidateTextQualityRuntime() { }

    public LlamaCandidateTextQualityRuntime(LocalAiProcessManager planner) => _planner = planner;

    public async Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath,
        CancellationToken cancellationToken)
    {
        if (_planner is null) return new(null, "actual_launch_unavailable");
        // Same planner, label and prefix as the exact standard-role Candidate load.
        LocalAiProcessManager.FreshExactTextLaunchPlan plan = await _planner.PlanFreshExactTextLaunchDetailsAsync(
            modelPath, RuntimeManagerService.RoleDisplayLabel(CandidateGeneralChatQualitySuite.RouteRole),
            cancellationToken).ConfigureAwait(false);
        if (plan.Descriptor.Descriptor is not { } descriptor || plan.Executable is null)
            return new(null, "launch_descriptor_" + (plan.Descriptor.Reason ?? "unavailable"));
        // The backend in the descriptor must be the one the executable path actually belongs to.
        if (TextLaunchDescriptor.BackendForRuntimeDirectory(Path.GetDirectoryName(plan.Executable)) != descriptor.Backend)
            return new(null, "backend_executable_mismatch");
        CandidateLaunchHost? host = await CandidateLaunchHostAttestation.CaptureAsync(descriptor.Backend,
            new GpuInventoryService().GetStatus(), cancellationToken).ConfigureAwait(false);
        if (host is null) return new(null, "host_attestation_unavailable");
        return new(new TextQualityRuntimeSpec(plan.Executable, descriptor.Backend, new CandidateEvaluationEnvironment
        {
            HardwareFingerprint = host.HardwareFingerprint,
            RuntimeFingerprint = host.RuntimeFingerprint,
            BinaryFingerprint = host.BinaryFingerprint,
            ConfigurationFingerprint = descriptor.Fingerprint,
            WorkloadFingerprint = CandidateGeneralChatQualitySuite.WorkloadFingerprint
        }, new EvaluationLaunch(SessionArguments(plan.Arguments), plan.EnvironmentOverrides, descriptor, modelPath)), null);
    }

    /// <summary>The planned argv without the per-launch values the session supplies itself.</summary>
    internal static IReadOnlyList<string> SessionArguments(IReadOnlyList<string> planned)
    {
        var result = new List<string>(planned.Count);
        for (int index = 0; index < planned.Count; index++)
        {
            string argument = planned[index];
            if (argument is "--model" or "--alias" or "--port" or "--host" or "--ui-config-file") { index++; continue; }
            if (argument == "--ui") continue;
            result.Add(argument);
        }
        return result;
    }

    public async Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken)
    {
        GpuInventoryStatus inventory = new GpuInventoryService().GetStatus();
        GpuAdapterStatus[] adapters = inventory.Adapters.Where(a => !a.IsSoftware).ToArray();
        if (!inventory.Available || adapters.Length == 0) return null;
        bool cuda = adapters.Any(a => a.VendorId == 0x10de && a.DedicatedVideoMemoryBytes >= LocalAiProcessManager.HeavyGpuDedicatedVramThresholdBytes);
        string backend = cuda ? "text-cuda" : "text-vulkan";
        string directory = cuda ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
        string executable = Path.Combine(directory, LocalAiPaths.LlamaServerExecutable);
        // v2 identity: adapters + driver + CPU + OS build + runtime dependency bytes, shared with
        // admission. Missing attestation fails closed (no evidence is better than evidence that
        // cannot distinguish a driver/OS/runtime change).
        CandidateLaunchHost? host = await CandidateLaunchHostAttestation.CaptureAsync(backend, inventory,
            cancellationToken).ConfigureAwait(false);
        if (host is null) return null;
        return new TextQualityRuntimeSpec(executable, backend, new CandidateEvaluationEnvironment
        {
            HardwareFingerprint = host.HardwareFingerprint,
            RuntimeFingerprint = host.RuntimeFingerprint,
            BinaryFingerprint = host.BinaryFingerprint,
            ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint,
            WorkloadFingerprint = CandidateTextQualitySuite.WorkloadFingerprint
        });
    }

    public async Task<ITextQualitySession> StartAsync(TextQualityRuntimeSpec spec, string modelPath,
        CancellationToken cancellationToken) => await LlamaTextQualitySession.StartAsync(spec, modelPath,
            cancellationToken).ConfigureAwait(false);
}

internal sealed class LlamaTextQualitySession : ITextQualitySession, ITextPerformanceSession
{
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumPerformanceStreamBytes = 1024 * 1024;
    private static readonly string[] RuntimeArguments =
    {
        "--host", "127.0.0.1", "--parallel", "1", "--ctx-size", "2048", "--gpu-layers", "-1",
        "--jinja", "--reasoning", "off", "--sleep-idle-seconds", "-1"
    };
    internal static string ConfigurationFingerprint { get; } = "sha256:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", RuntimeArguments)))).ToLowerInvariant();
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _alias;
    private readonly Task _stdoutDrain;
    private readonly Task _stderrDrain;

    private LlamaTextQualitySession(Process process, HttpClient http, int port, string alias)
    {
        _process = process;
        _http = http;
        _baseUrl = "http://127.0.0.1:" + port;
        _alias = alias;
        _stdoutDrain = DrainAsync(process.StandardOutput);
        _stderrDrain = DrainAsync(process.StandardError);
    }

    internal static async Task<LlamaTextQualitySession> StartAsync(TextQualityRuntimeSpec spec, string modelPath,
        CancellationToken cancellationToken, string aliasPrefix = "parux-quality-")
    {
        cancellationToken.ThrowIfCancellationRequested();
        int port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string alias = aliasPrefix + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo
        {
            FileName = spec.Executable, WorkingDirectory = Path.GetDirectoryName(spec.Executable)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        IEnumerable<string> semantic = spec.Launch?.SessionArguments ?? (IEnumerable<string>)RuntimeArguments;
        IEnumerable<string> ephemeral = spec.Launch is null
            ? new[] { "--model", modelPath, "--alias", alias, "--port", port.ToString() }
            : new[] { "--model", modelPath, "--alias", alias, "--port", port.ToString(), "--host", "127.0.0.1" };
        foreach (string arg in ephemeral.Concat(semantic))
            start.ArgumentList.Add(arg);
        if (spec.Launch is { } launch)
        {
            foreach ((string name, string value) in launch.EnvironmentOverrides) start.Environment[name] = value;
            // Evaluator-side fence: the exact argv/environment about to start must reproduce the planned
            // ld1 descriptor for the same model file, or no evidence can be attributed to that launch.
            TextLaunchDescriptor? actual = TextLaunchDescriptor.Create(spec.Backend, start.ArgumentList.ToArray(),
                TextLaunchDescriptor.RelevantEnvironment(start.Environment)).Descriptor;
            if (actual is null || actual.Fingerprint != launch.Descriptor.Fingerprint
                || !string.Equals(Path.GetFullPath(launch.ModelPath), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase)
                || TextLaunchDescriptor.BackendForRuntimeDirectory(start.WorkingDirectory) != launch.Descriptor.Backend)
                throw new InvalidDataException("evaluation_launch_mismatch");
        }
        var process = new Process { StartInfo = start };
        var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        LlamaTextQualitySession? session = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Local quality runtime failed to start.");
            ChildProcessJob.Assign(process);
            session = new LlamaTextQualitySession(process, http, port, alias);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(TimeSpan.FromMinutes(2));
            await session.WaitHealthyAsync(startupCts.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                http.Dispose();
            }
            throw;
        }
    }

    private async Task WaitHealthyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new IOException("Local quality runtime exited during startup.");
            try
            {
                using HttpResponseMessage health = await _http.GetAsync(_baseUrl + "/health", cancellationToken)
                    .ConfigureAwait(false);
                if (health.IsSuccessStatusCode)
                {
                    using HttpResponseMessage models = await _http.GetAsync(_baseUrl + "/v1/models", cancellationToken)
                        .ConfigureAwait(false);
                    if (models.IsSuccessStatusCode)
                    {
                        byte[] bytes = await ReadBoundedAsync(models, cancellationToken).ConfigureAwait(false);
                        using JsonDocument doc = JsonDocument.Parse(bytes);
                        if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                            && data.ValueKind == JsonValueKind.Array && data.EnumerateArray().Any(m =>
                                m.TryGetProperty("id", out JsonElement id) && id.GetString() == _alias))
                            return;
                    }
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TextQualityCompletion> CompleteAsync(CandidateTextQualitySuite.Case item,
        CancellationToken cancellationToken)
    {
        if (_process.HasExited) throw new IOException("Local quality runtime exited.");
        string body;
        if (item.ProductionRequestParity)
        {
            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = _alias,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = item.SystemPrompt ?? CandidateTextQualitySuite.SystemPrompt },
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = item.Prompt }),
                ["stream"] = false
            };
            CandidateRequestParity.Apply(payload, CandidateRequestParity.Frozen(item.Prompt),
                item.MaxTokensOverride ?? CandidateTextQualitySuite.MaxTokens,
                await SupportedEffortsAsync(cancellationToken).ConfigureAwait(false));
            body = payload.ToJsonString();
        }
        else
        {
            body = JsonSerializer.Serialize(new
            {
                model = _alias,
                messages = new[] { new { role = "system", content = item.SystemPrompt ?? CandidateTextQualitySuite.SystemPrompt },
                    new { role = "user", content = item.Prompt } },
                temperature = 0, seed = item.SeedOverride ?? CandidateTextQualitySuite.Seed,
                max_tokens = item.MaxTokensOverride ?? CandidateTextQualitySuite.MaxTokens, stream = false
            });
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("Local quality runtime request failed.");
        byte[] bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseCompletion(bytes);
    }

    public async Task<TextPerformanceCompletion> MeasureAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited) throw new IOException("Local performance runtime exited.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/chat/completions")
        {
            Content = new StringContent(await PerformancePayloadAsync(cancellationToken).ConfigureAwait(false),
                Encoding.UTF8, "application/json")
        };
        var timer = Stopwatch.StartNew();
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("Local performance runtime request failed.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var boundedStream = new BoundedReadStream(stream, MaximumPerformanceStreamBytes);
        using var reader = new StreamReader(boundedStream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var parser = new CandidatePerformanceStreamParser(_alias, CandidateTextPerformanceWorkload.MaximumGeneratedTokens);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            parser.AcceptLine(line, timer.Elapsed.TotalMilliseconds);
        }
        timer.Stop();
        return parser.Complete(timer.Elapsed.TotalMilliseconds);
    }

    /// <summary>Workload v3: production request parity (no sampling/seed override, production reasoning fields).</summary>
    private async Task<string> PerformancePayloadAsync(CancellationToken cancellationToken)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = _alias,
            ["messages"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = CandidateTextPerformanceWorkload.SystemPrompt },
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = CandidateTextPerformanceWorkload.Prompt }),
            ["stream"] = true,
            ["stream_options"] = new System.Text.Json.Nodes.JsonObject { ["include_usage"] = true }
        };
        CandidateRequestParity.Apply(payload, CandidateRequestParity.Frozen(CandidateTextPerformanceWorkload.Prompt),
            CandidateTextPerformanceWorkload.MaxTokens, await SupportedEffortsAsync(cancellationToken).ConfigureAwait(false));
        return payload.ToJsonString();
    }

    private IReadOnlyCollection<string>? _supportedEfforts;
    private bool _supportedEffortsRead;

    /// <summary>The served template's reasoning-effort vocabulary, read once per session exactly as the
    /// production chat path resolves it.</summary>
    private async Task<IReadOnlyCollection<string>?> SupportedEffortsAsync(CancellationToken cancellationToken)
    {
        if (_supportedEffortsRead) return _supportedEfforts;
        _supportedEfforts = await ReasoningEffortCompatibility.GetSupportedAsync(_http, _baseUrl, cancellationToken)
            .ConfigureAwait(false);
        _supportedEffortsRead = true;
        return _supportedEfforts;
    }

    internal static TextQualityCompletion ParseCompletion(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() != 1) throw new InvalidDataException("Invalid local quality response.");
        JsonElement choice = choices[0];
        if (!choice.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString())) throw new InvalidDataException("Empty local quality response.");
        string reason = choice.TryGetProperty("finish_reason", out JsonElement finish)
            && finish.ValueKind == JsonValueKind.String ? finish.GetString() ?? "" : "";
        return new TextQualityCompletion(content.GetString()!, reason);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Local quality response too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { _process.Dispose(); }
        try { await Task.WhenAll(_stdoutDrain, _stderrDrain).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
