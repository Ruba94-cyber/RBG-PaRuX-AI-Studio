using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace PaRuX.Server;

/// <summary>
/// Resident ONNX Runtime GenAI adapter backed by a Windows ML managed execution-provider plugin.
/// The exact EP runtime identity is pinned from Engine Race evidence; provider updates therefore
/// invalidate old evidence instead of silently changing the engine underneath AUTO routing.
/// Generation remains intentionally single-flight because the worker owns one resident model/KV
/// cache runtime. Protocol v4 retains request-id cancellation/drain and adds exact rendered-prompt
/// token counting on the same resident tokenizer/template used for generation.
/// </summary>
public sealed class WindowsMlGenAiLocalInferenceEngine : ILocalInferenceEngine, ILocalInferencePromptTokenCounter, IAsyncDisposable
{
    private const int MaximumOutputTokens = 32_768;
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(8);
    private readonly string _providerName;
    private readonly string _expectedRuntimeIdentity;
    private readonly string _workerPath;
    private readonly ModelDownloadService _models;
    private readonly LocalInferenceEngineRegistry _registry;
    private readonly LocalAiProcessManager _runtime;
    private readonly WindowsMlPerformancePackService _performancePack;
    private readonly Func<string, CancellationToken, Task<WindowsMlPerformancePackStatus>> _ensureProvider;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Channel<WindowsMlGenAiWorkerEvent>> _requests =
        new(StringComparer.Ordinal);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task<string>? _stderrDrain;
    private CancellationTokenSource? _workerLifetime;
    private Task? _stdoutPump;
    private string? _activeVariantKey;
    private string? _activeDeviceIdentity;
    private bool _activeThinkingControlExposed;
    private readonly string? _adapterPin;
    private string? _lastError;
    private long _workerStarts;
    private long _warmCancellations;
    private long _forcedRestarts;

    public WindowsMlGenAiLocalInferenceEngine(
        string providerName,
        string expectedRuntimeIdentity,
        ModelDownloadService models,
        LocalInferenceEngineRegistry registry,
        LocalAiProcessManager runtime,
        string? workerPath = null)
        : this(providerName, expectedRuntimeIdentity, models, registry, runtime, workerPath, null)
    {
    }

    internal WindowsMlGenAiLocalInferenceEngine(
        string providerName,
        string expectedRuntimeIdentity,
        ModelDownloadService models,
        LocalInferenceEngineRegistry registry,
        LocalAiProcessManager runtime,
        string? workerPath,
        Func<string, CancellationToken, Task<WindowsMlPerformancePackStatus>>? ensureProvider)
    {
        _providerName = WindowsMlGenAiProtocol.ValidateProviderName(providerName);
        _expectedRuntimeIdentity = expectedRuntimeIdentity?.Trim()
            ?? throw new ArgumentNullException(nameof(expectedRuntimeIdentity));
        if (!WindowsMlGenAiProtocol.TryParseRuntimeIdentity(
                _expectedRuntimeIdentity, out string identityProvider, out _, out _, out string? adapterPin)
            || !string.Equals(identityProvider, _providerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows ML engine runtime identity does not match its provider.");
        _adapterPin = adapterPin;
        _workerPath = WindowsMlGenAiProtocol.ResolveWorkerPath(workerPath);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _performancePack = new WindowsMlPerformancePackService(_workerPath);
        _ensureProvider = ensureProvider ?? _performancePack.EnsureProviderAsync;
    }

    public LocalInferenceEngineDescriptor Descriptor { get; } = new(
        LocalInferenceEngineRegistry.WindowsMlGenAiId,
        "Windows ML GenAI",
        EngineImplementationState.Integrated,
        new HashSet<ManagedModelFormat> { ManagedModelFormat.Onnx },
        LocalInferenceCapabilities.Text,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PaRuXManaged: true,
        SupportsManagedUpdates: true,
        Notes: "Resident text-only ONNX Runtime GenAI serving over a Windows ML-managed execution provider. AUTO requires exact-model, exact-provider Engine Race evidence plus one unambiguous compatible ORT device. Request-id cancellation keeps the model warm after a drained terminal and prompt accounting uses the exact resident tokenizer/chat template; vision/tool/structured turns remain ineligible until independently implemented and verified. Provider/device discovery is not proof of physical NPU execution.");

    public async Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool healthy = IsWorkerHealthyUnsafe();
            return new LocalInferenceEngineStatus(
                Descriptor.Id,
                Installed: File.Exists(_workerPath),
                Healthy: healthy,
                InstalledVersion: _expectedRuntimeIdentity,
                ActiveModelVariantId: healthy ? _activeVariantKey : null,
                LastError: _lastError);
        }
        finally { _stateGate.Release(); }
    }

    public async Task EnsureReadyAsync(string modelVariantId, CancellationToken cancellationToken)
    {
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Windows ML GenAI serving is blocked while Gaming Mode is active.");
        ManagedModelVariantIdentity variant = ResolveVariant(modelVariantId);

        WindowsMlPerformancePackStatus status = await _ensureProvider(
            _providerName, cancellationToken).ConfigureAwait(false);
        WindowsMlProviderInfo provider = status.FindProvider(_providerName)
            ?? throw new InvalidOperationException("Windows ML provider vanished after readiness: " + _providerName + ".");
        string currentIdentity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(provider, _adapterPin);
        if (!string.Equals(currentIdentity, _expectedRuntimeIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Windows ML execution provider changed since Engine Race. Re-run the race before AUTO can use it. Expected "
                + _expectedRuntimeIdentity + ", current " + currentIdentity + ".");

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsWorkerHealthyUnsafe()
                && string.Equals(_activeVariantKey, variant.StableKey, StringComparison.OrdinalIgnoreCase))
                return;
            await StopWorkerUnsafeAsync().ConfigureAwait(false);
            await StartWorkerUnsafeAsync(variant, cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    public async IAsyncEnumerable<LocalInferenceChunk> StreamAsync(
        LocalInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? requestId = null;
        Channel<WindowsMlGenAiWorkerEvent>? events = null;
        bool protocolSynchronized = false;
        try
        {
            await EnsureReadyAsync(request.ModelVariantId, cancellationToken).ConfigureAwait(false);
            if (ThinkingDemandViolation(request) is { } thinkingViolation)
            {
                protocolSynchronized = true; // worker untouched and healthy: no protocol-loss restart
                throw thinkingViolation;
            }
            requestId = Guid.NewGuid().ToString("N");
            events = CreateRequestChannel(requestId);

            StreamWriter input = await RequireInputAsync(cancellationToken).ConfigureAwait(false);
            string command = SerializeWorkerCommand(requestId, "generate", request);
            await input.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                WindowsMlGenAiWorkerEvent evt;
                try
                {
                    evt = await events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    protocolSynchronized = await TryCancelAndDrainAsync(
                        requestId, input, events.Reader).ConfigureAwait(false);
                    if (protocolSynchronized) Interlocked.Increment(ref _warmCancellations);
                    else
                        _lastError = "Windows ML cancellation did not reach a request terminal within "
                            + CancellationDrainTimeout.TotalSeconds.ToString("0") + " seconds; worker will be restarted.";
                    throw;
                }

                switch (evt.Type)
                {
                    case "delta":
                        if (!string.IsNullOrEmpty(evt.Content))
                            yield return new LocalInferenceChunk(evt.Content, null, Completed: false);
                        break;
                    case "done":
                        protocolSynchronized = true;
                        yield return new LocalInferenceChunk(null, null, Completed: true);
                        yield break;
                    case "error":
                        protocolSynchronized = true;
                        _lastError = string.IsNullOrWhiteSpace(evt.Error)
                            ? "Windows ML GenAI worker generation failed."
                            : evt.Error;
                        yield return new LocalInferenceChunk(null, null, Completed: true, Error: _lastError);
                        yield break;
                    case "cancelled":
                        protocolSynchronized = true;
                        _lastError = "Windows ML worker cancelled the request before completion.";
                        yield return new LocalInferenceChunk(null, null, Completed: true, Error: _lastError);
                        yield break;
                    default:
                        throw new InvalidDataException("Windows ML GenAI worker returned an unsupported event type: " + evt.Type + ".");
                }
            }
        }
        finally
        {
            if (requestId != null) _requests.TryRemove(requestId, out _);
            if (!protocolSynchronized)
            {
                Interlocked.Increment(ref _forcedRestarts);
                await RestartUnsafeAfterProtocolLossAsync().ConfigureAwait(false);
            }
            _generationGate.Release();
        }
    }

    public async Task<int> CountPromptTokensAsync(
        LocalInferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? requestId = null;
        bool protocolSynchronized = false;
        try
        {
            await EnsureReadyAsync(request.ModelVariantId, cancellationToken).ConfigureAwait(false);
            if (ThinkingDemandViolation(request) is { } thinkingViolation)
            {
                protocolSynchronized = true; // worker untouched and healthy: no protocol-loss restart
                throw thinkingViolation;
            }
            requestId = Guid.NewGuid().ToString("N");
            Channel<WindowsMlGenAiWorkerEvent> events = CreateRequestChannel(requestId);
            StreamWriter input = await RequireInputAsync(cancellationToken).ConfigureAwait(false);
            string command = SerializeWorkerCommand(requestId, "count_prompt", request);
            await input.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);

            WindowsMlGenAiWorkerEvent evt = await events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (evt.Type == "error")
            {
                protocolSynchronized = true;
                throw new InvalidOperationException(evt.Error ?? "Windows ML prompt counting failed.");
            }
            if (evt.Type != "prompt_count" || evt.PromptTokens <= 0)
                throw new InvalidDataException("Windows ML worker did not return a valid prompt_count terminal.");
            protocolSynchronized = true;
            return evt.PromptTokens;
        }
        finally
        {
            if (requestId != null) _requests.TryRemove(requestId, out _);
            if (!protocolSynchronized)
            {
                Interlocked.Increment(ref _forcedRestarts);
                await RestartUnsafeAfterProtocolLossAsync().ConfigureAwait(false);
            }
            _generationGate.Release();
        }
    }

    /// <summary>True when the resident model's chat template exposes a verified thinking toggle.
    /// Models without one are still served, but only for requests that do not demand thinking=false.</summary>
    internal bool ThinkingControlExposed => _activeThinkingControlExposed;

    /// <summary>A thinking=false demand on a model without a verified toggle is refused, never
    /// silently ignored. Requests that allow thinking are always admitted.</summary>
    private Exception? ThinkingDemandViolation(LocalInferenceRequest request) =>
        !request.EnableThinking && !_activeThinkingControlExposed
            ? new NotSupportedException(
                "This ONNX model's chat template exposes no verified enable_thinking control; PaRuX will not "
                + "pretend thinking=false was applied. Re-send with thinking allowed or choose a model with the control.")
            : null;

    private Channel<WindowsMlGenAiWorkerEvent> CreateRequestChannel(string requestId)
    {
        var events = Channel.CreateUnbounded<WindowsMlGenAiWorkerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        if (!_requests.TryAdd(requestId, events))
            throw new InvalidOperationException("Windows ML request id collision.");
        return events;
    }

    private async Task<StreamWriter> RequireInputAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsWorkerHealthyUnsafe() || _stdin == null)
                throw new InvalidOperationException("Windows ML GenAI worker is not ready.");
            return _stdin;
        }
        finally { _stateGate.Release(); }
    }

    private static string SerializeWorkerCommand(string requestId, string op, LocalInferenceRequest request) =>
        JsonSerializer.Serialize(new
        {
            apiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
            id = requestId,
            op,
            messages = request.Messages.Select(message => new
            {
                role = NormalizeRole(message.Role),
                content = message.Content ?? string.Empty
            }),
            maxNewTokens = request.MaxOutputTokens,
            temperature = request.Temperature,
            workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload),
            enableThinking = request.EnableThinking
        });

    private async Task RestartUnsafeAfterProtocolLossAsync()
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await StopWorkerUnsafeAsync().ConfigureAwait(false); }
        finally { _stateGate.Release(); }
    }

    private static async Task<bool> TryCancelAndDrainAsync(
        string requestId,
        StreamWriter input,
        ChannelReader<WindowsMlGenAiWorkerEvent> events)
    {
        using var drainCts = new CancellationTokenSource(CancellationDrainTimeout);
        CancellationToken drainToken = drainCts.Token;
        try
        {
            string cancel = JsonSerializer.Serialize(new
            {
                apiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
                id = requestId,
                op = "cancel"
            });
            await input.WriteLineAsync(cancel.AsMemory(), drainToken).ConfigureAwait(false);
            await input.FlushAsync(drainToken).ConfigureAwait(false);

            while (await events.WaitToReadAsync(drainToken).ConfigureAwait(false))
            {
                while (events.TryRead(out WindowsMlGenAiWorkerEvent? evt))
                {
                    if (!string.Equals(evt.Id, requestId, StringComparison.Ordinal)) return false;
                    if (evt.Type is "done" or "cancelled" or "error") return true;
                    if (evt.Type != "delta") return false;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopWorkerUnsafeAsync().ConfigureAwait(false); }
        finally { _stateGate.Release(); }
    }

    internal async Task WaitForGenerationIdleAsync(CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _generationGate.Release();
    }

    internal long WorkerStartCount => Interlocked.Read(ref _workerStarts);
    internal long WarmCancellationCount => Interlocked.Read(ref _warmCancellations);
    internal int? WorkerProcessId { get { try { return _process is { HasExited: false } p ? p.Id : null; } catch { return null; } } }
    internal long ForcedRestartCount => Interlocked.Read(ref _forcedRestarts);

    internal ManagedModelVariantIdentity ResolveVariant(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey))
            throw new InvalidDataException("Windows ML GenAI requires an exact Model Hub variant id.");
        ManagedModelCatalogEntry? entry = new MultiFormatModelCatalog()
            .List(_models.ListManagedModels(), _registry)
            .FirstOrDefault(item => string.Equals(item.Variant.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            throw new InvalidDataException("Windows ML GenAI model variant is not registered in PaRuX Model Hub: " + stableKey + ".");
        if (!entry.StructureValidated || entry.Variant.Format != ManagedModelFormat.Onnx)
            throw new InvalidDataException("Windows ML GenAI requires a structurally validated ONNX Model Hub variant.");
        if (!WindowsMlGenAiProtocol.HasServingLayout(entry.Variant.ArtifactPath, out string reason))
            throw new InvalidDataException(reason);
        return entry.Variant;
    }

    private bool IsWorkerHealthyUnsafe() =>
        _process is { HasExited: false }
        && _stdin != null
        && _stdout != null
        && _stdoutPump is { IsCompleted: false }
        && _workerLifetime is { IsCancellationRequested: false };

    private async Task StartWorkerUnsafeAsync(
        ManagedModelVariantIdentity variant,
        CancellationToken cancellationToken)
    {
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Windows ML GenAI serving is blocked while Gaming Mode is active.");
        // A runtime that survives the stop keeps its VRAM and PID record, so the worker is not started beside it.
        bool textStopped = _runtime.StopText();
        bool mediaStopped = _runtime.StopMedia();
        if (!textStopped || !mediaStopped)
            throw new InvalidOperationException("The resident local runtime did not stop, so the Windows ML GenAI worker was not started.");

        var start = new ProcessStartInfo
        {
            FileName = _workerPath,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add(Path.GetFullPath(variant.ArtifactPath));
        start.ArgumentList.Add(_providerName);
        if (_adapterPin != null) start.ArgumentList.Add(_adapterPin);

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start the PaRuX Windows ML GenAI worker.");
            ChildProcessJob.Assign(process);
            Interlocked.Increment(ref _workerStarts);
            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            _stderrDrain = process.StandardError.ReadToEndAsync();

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyCts.CancelAfter(TimeSpan.FromMinutes(10));
            string? line;
            try { line = await _stdout.ReadLineAsync(readyCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Windows ML GenAI model load exceeded the 10 minute safety timeout.");
            }
            if (line == null)
                throw new InvalidOperationException("Windows ML GenAI worker exited during model load: " + await ReadStderrUnsafeAsync().ConfigureAwait(false));
            WindowsMlGenAiWorkerEvent ready = WindowsMlGenAiProtocol.ParseWorkerEvent(line);
            if (ready.Type == "error") throw new InvalidOperationException(ready.Error ?? "Windows ML GenAI worker failed during startup.");
            _activeDeviceIdentity = WindowsMlGenAiProtocol.ValidateReadyHandshake(
                ready, _providerName, requireUnambiguousDevice: true,
                requireThinkingControl: false, requireCancellationControl: true,
                requirePromptCountingControl: true, expectedAdapterPin: _adapterPin);
            _activeThinkingControlExposed = string.Equals(
                ready.ThinkingControl, WindowsMlGenAiProtocol.ThinkingControlJinja, StringComparison.Ordinal);

            _workerLifetime = new CancellationTokenSource();
            _stdoutPump = PumpWorkerEventsAsync(_stdout, _workerLifetime.Token);
            _activeVariantKey = variant.StableKey;
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            await StopWorkerUnsafeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpWorkerEventsAsync(StreamReader output, CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                WindowsMlGenAiWorkerEvent evt = WindowsMlGenAiProtocol.ParseWorkerEvent(line);
                if (string.IsNullOrWhiteSpace(evt.Id))
                    throw new InvalidDataException("Windows ML worker emitted a non-request event after the ready handshake.");
                if (!_requests.TryGetValue(evt.Id, out Channel<WindowsMlGenAiWorkerEvent>? channel))
                    throw new InvalidDataException(
                        "Windows ML worker emitted a stale or unknown request event: " + evt.Id + ".");
                await channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
                terminalError = new InvalidOperationException("Windows ML worker stdout closed unexpectedly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            terminalError = ex;
            _lastError = ex.Message;
        }
        finally
        {
            foreach (KeyValuePair<string, Channel<WindowsMlGenAiWorkerEvent>> pending in _requests)
            {
                if (terminalError != null) pending.Value.Writer.TryComplete(terminalError);
                else pending.Value.Writer.TryComplete();
            }
        }
    }

    private async Task StopWorkerUnsafeAsync()
    {
        Process? process = _process;
        _process = null;
        StreamWriter? input = _stdin;
        _stdin = null;
        StreamReader? output = _stdout;
        _stdout = null;
        Task<string>? stderr = _stderrDrain;
        _stderrDrain = null;
        CancellationTokenSource? lifetime = _workerLifetime;
        _workerLifetime = null;
        Task? pump = _stdoutPump;
        _stdoutPump = null;
        _activeVariantKey = null;
        _activeDeviceIdentity = null;
        _activeThinkingControlExposed = false;

        try { lifetime?.Cancel(); } catch { }
        try { input?.Dispose(); } catch { }
        try { output?.Dispose(); } catch { }
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
            try { process.Dispose(); } catch { }
        }
        if (pump != null) { try { await pump.ConfigureAwait(false); } catch { } }
        if (stderr != null) { try { _ = await stderr.ConfigureAwait(false); } catch { } }
        try { lifetime?.Dispose(); } catch { }
    }

    private async Task<string> ReadStderrUnsafeAsync()
    {
        if (_stderrDrain == null) return string.Empty;
        try
        {
            string value = (await _stderrDrain.ConfigureAwait(false)).Trim();
            return value.Length <= 2000 ? value : value[^2000..];
        }
        catch { return string.Empty; }
    }

    private static void ValidateRequest(LocalInferenceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelVariantId))
            throw new InvalidDataException("Model variant id is required.");
        if (request.StructuredOutput != null)
            throw new NotSupportedException("Windows ML GenAI constrained structured output has not been verified; PaRuX will not silently ignore the schema.");
        if (request.Messages == null || request.Messages.Count is < 1 or > 256)
            throw new InvalidDataException("Windows ML GenAI requires 1..256 messages.");
        if (request.MaxOutputTokens is < 1 or > MaximumOutputTokens)
            throw new InvalidDataException("maxOutputTokens must be in 1..32768.");
        if (!double.IsFinite(request.Temperature) || request.Temperature is < 0 or > 2)
            throw new InvalidDataException("temperature must be finite and in 0..2.");
        long total = 0;
        foreach ((string role, string content) in request.Messages)
        {
            _ = NormalizeRole(role);
            total += content?.Length ?? 0;
            if (total > 1_000_000)
                throw new InvalidDataException("Windows ML GenAI message payload exceeds the PaRuX safety bound.");
        }
    }

    private static string NormalizeRole(string? role)
    {
        string value = (role ?? string.Empty).Trim().ToLowerInvariant();
        return value is "system" or "user" or "assistant" or "tool"
            ? value
            : throw new InvalidDataException("Unsupported Windows ML GenAI message role: " + value + ".");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stateGate.Dispose();
        _generationGate.Dispose();
    }
}
