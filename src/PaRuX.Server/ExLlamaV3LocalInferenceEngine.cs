using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed class ExLlamaWorkerEvent
{
    public int ApiVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? Error { get; set; }
    public int PromptTokens { get; set; }
    public int GeneratedTokens { get; set; }
}

/// <summary>
/// Direct ExLlamaV3 serving adapter behind PaRuX's engine-neutral boundary. A single PaRuX-owned
/// Python worker keeps one exact EXL3 model resident and exchanges bounded JSONL over redirected
/// stdio. Generation is serialized. GPU text-reader authority is owned by the serving transport
/// (the interactive chat endpoint), not duplicated here: nested reader acquisition would deadlock
/// if a writer queued between the outer transport lease and this engine's stream start.
/// </summary>
public sealed class ExLlamaV3LocalInferenceEngine : ILocalInferenceEngine, ILocalInferencePromptTokenCounter, IAsyncDisposable
{
    internal const int WorkerApiVersion = 2;
    private const int MaximumOutputTokens = 32_768;
    private readonly string _packTag;
    private readonly string _pythonPath;
    private readonly string _workerPath;
    private readonly ModelDownloadService _models;
    private readonly LocalInferenceEngineRegistry _registry;
    private readonly LocalAiProcessManager _runtime;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly Func<GpuInventoryStatus> _detectGpuInventory;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task<string>? _stderrDrain;
    private string? _activeVariantKey;
    private string? _lastError;

    public ExLlamaV3LocalInferenceEngine(
        string packTag,
        ExLlamaV3ManagedPackService packs,
        ModelDownloadService models,
        LocalInferenceEngineRegistry registry,
        LocalAiProcessManager runtime,
        string? workerPath = null,
        Func<GpuInventoryStatus>? detectGpuInventory = null)
    {
        if (packs == null) throw new ArgumentNullException(nameof(packs));
        _packTag = ManagedRuntimeEngineRaceService.ValidatePackTag(packTag);
        _pythonPath = packs.RequireInstalledPython(_packTag);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workerPath = Path.GetFullPath(workerPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "exllamav3-host",
            "parux_exllamav3_worker.py"));
        _detectGpuInventory = detectGpuInventory ?? (() => new GpuInventoryService().GetStatus());
    }

    public LocalInferenceEngineDescriptor Descriptor { get; } = new(
        LocalInferenceEngineRegistry.ExLlamaV3Id,
        "ExLlamaV3",
        EngineImplementationState.Integrated,
        new HashSet<ManagedModelFormat> { ManagedModelFormat.Exl3 },
        LocalInferenceCapabilities.Text,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nvidia" },
        PaRuXManaged: true,
        SupportsManagedUpdates: true,
        Notes: "Direct PaRuX-owned EXL3 text serving with structured reasoning deltas, serialized generation, exact rendered-prompt token counting and destructive cancellation safety. Extra capabilities remain disabled until implemented and verified.");

    public async Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool installed = File.Exists(_pythonPath) && File.Exists(_workerPath);
            bool healthy = installed && _process is { HasExited: false } && _stdin != null && _stdout != null;
            return new LocalInferenceEngineStatus(
                Descriptor.Id,
                installed,
                healthy,
                _packTag,
                healthy ? _activeVariantKey : null,
                _lastError);
        }
        finally { _stateGate.Release(); }
    }

    public async Task EnsureReadyAsync(string modelVariantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelVariantId))
            throw new InvalidDataException("ExLlamaV3 requires an exact Model Hub variant id.");
        // Refused before the variant lookup and before any worker starts: EXL3 cannot run without CUDA.
        ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(_detectGpuInventory());
        if (_runtime.GamingActive)
            throw new InvalidOperationException("ExLlamaV3 serving is blocked while Gaming Mode is active.");

        ManagedModelVariantIdentity variant = ResolveVariant(modelVariantId);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false }
                && _stdin != null
                && _stdout != null
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

        // The interactive transport already owns one shared GPU reader for the entire turn. Keep
        // this engine focused on its own serialized generation gate. Acquiring a second reader here
        // would not add safety and can deadlock writer-priority scheduling if media queues between
        // the outer reader and this point.
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool completedCleanly = false;
        try
        {
            await EnsureReadyAsync(request.ModelVariantId, cancellationToken).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString("N");
            StreamWriter input;
            StreamReader output;

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_process is not { HasExited: false } || _stdin == null || _stdout == null)
                    throw new InvalidOperationException("ExLlamaV3 worker is not ready.");
                input = _stdin;
                output = _stdout;
            }
            finally { _stateGate.Release(); }

            string command = JsonSerializer.Serialize(new
            {
                id = requestId,
                op = "generate",
                messages = request.Messages.Select(message => new
                {
                    role = NormalizeRole(message.Role),
                    content = message.Content ?? string.Empty
                }).ToList(),
                max_new_tokens = request.MaxOutputTokens,
                temperature = request.Temperature,
                workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload),
                enable_thinking = request.EnableThinking
            });
            await input.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    throw new InvalidOperationException("ExLlamaV3 worker exited before completing the request.");

                ExLlamaWorkerEvent evt = ParseWorkerEvent(line);
                if (!string.IsNullOrEmpty(evt.Id)
                    && !string.Equals(evt.Id, requestId, StringComparison.Ordinal))
                    throw new InvalidDataException("ExLlamaV3 worker returned an event for a different request id.");

                switch (evt.Type)
                {
                    case "delta":
                        if (!string.IsNullOrEmpty(evt.Content))
                            yield return new LocalInferenceChunk(evt.Content, null, Completed: false);
                        break;
                    case "reasoning":
                        if (!string.IsNullOrEmpty(evt.Content))
                            yield return new LocalInferenceChunk(null, evt.Content, Completed: false);
                        break;
                    case "done":
                        completedCleanly = true;
                        yield return new LocalInferenceChunk(null, null, Completed: true);
                        yield break;
                    case "error":
                        _lastError = string.IsNullOrWhiteSpace(evt.Error)
                            ? "ExLlamaV3 worker generation failed."
                            : evt.Error;
                        yield return new LocalInferenceChunk(null, null, Completed: true, Error: _lastError);
                        yield break;
                    case "pong":
                        break;
                    default:
                        throw new InvalidDataException("ExLlamaV3 worker returned an unsupported event type: " + evt.Type);
                }
            }
        }
        finally
        {
            if (!completedCleanly)
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await StopWorkerUnsafeAsync().ConfigureAwait(false); }
                finally { _stateGate.Release(); }
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
        bool protocolClean = false;
        try
        {
            await EnsureReadyAsync(request.ModelVariantId, cancellationToken).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString("N");
            StreamWriter input;
            StreamReader output;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_process is not { HasExited: false } || _stdin == null || _stdout == null)
                    throw new InvalidOperationException("ExLlamaV3 worker is not ready.");
                input = _stdin;
                output = _stdout;
            }
            finally { _stateGate.Release(); }

            string command = JsonSerializer.Serialize(new
            {
                id = requestId,
                op = "count_prompt",
                messages = request.Messages.Select(message => new
                {
                    role = NormalizeRole(message.Role),
                    content = message.Content ?? string.Empty
                }).ToList(),
                enable_thinking = request.EnableThinking
            });
            await input.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);

            string? line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                throw new InvalidOperationException("ExLlamaV3 worker exited while counting the rendered prompt.");
            ExLlamaWorkerEvent evt = ParseWorkerEvent(line);
            if (!string.Equals(evt.Id, requestId, StringComparison.Ordinal))
                throw new InvalidDataException("ExLlamaV3 prompt counter returned an event for a different request id.");
            if (evt.Type == "error")
                throw new InvalidOperationException(evt.Error ?? "ExLlamaV3 prompt counting failed.");
            if (!string.Equals(evt.Type, "prompt_count", StringComparison.Ordinal) || evt.PromptTokens <= 0)
                throw new InvalidDataException("ExLlamaV3 worker did not return a valid prompt_count terminal.");
            protocolClean = true;
            return evt.PromptTokens;
        }
        finally
        {
            if (!protocolClean)
            {
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await StopWorkerUnsafeAsync().ConfigureAwait(false); }
                finally { _stateGate.Release(); }
            }
            _generationGate.Release();
        }
    }

    internal async Task WaitForGenerationIdleAsync(CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _generationGate.Release();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopWorkerUnsafeAsync().ConfigureAwait(false); }
        finally { _stateGate.Release(); }
    }

    internal ManagedModelVariantIdentity ResolveVariant(string stableKey)
    {
        IReadOnlyList<ManagedModelCatalogEntry> catalog = new MultiFormatModelCatalog()
            .List(_models.ListManagedModels(), _registry);
        ManagedModelCatalogEntry? entry = catalog.FirstOrDefault(item =>
            string.Equals(item.Variant.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            throw new InvalidDataException("ExLlamaV3 model variant is not registered in PaRuX Model Hub: " + stableKey);
        if (!entry.StructureValidated || entry.Variant.Format != ManagedModelFormat.Exl3)
            throw new InvalidDataException("ExLlamaV3 serving requires a structurally validated EXL3 Model Hub variant.");
        return entry.Variant;
    }

    private async Task StartWorkerUnsafeAsync(
        ManagedModelVariantIdentity variant,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_pythonPath))
            throw new FileNotFoundException("PaRuX-managed ExLlamaV3 Python runtime is missing.", _pythonPath);
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("PaRuX ExLlamaV3 serving worker is missing.", _workerPath);
        if (!Directory.Exists(variant.ArtifactPath))
            throw new DirectoryNotFoundException("EXL3 model directory is missing: " + variant.ArtifactPath);
        if (_runtime.GamingActive)
            throw new InvalidOperationException("ExLlamaV3 serving is blocked while Gaming Mode is active.");

        // A runtime that survives the stop keeps its VRAM and PID record, so the worker is not started beside it.
        bool textStopped = _runtime.StopText();
        bool mediaStopped = _runtime.StopMedia();
        if (!textStopped || !mediaStopped)
            throw new InvalidOperationException("The resident local runtime did not stop, so the ExLlamaV3 worker was not started.");

        var start = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(_workerPath);
        start.ArgumentList.Add(Path.GetFullPath(variant.ArtifactPath));
        ManagedPythonRuntime.ApplyEnvironment(start);

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start the PaRuX ExLlamaV3 serving worker.");
            ChildProcessJob.Assign(process);
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
                throw new TimeoutException("ExLlamaV3 model load exceeded the 10 minute safety timeout.");
            }
            if (line == null)
                throw new InvalidOperationException("ExLlamaV3 worker exited during model load: " + await ReadStderrUnsafeAsync().ConfigureAwait(false));
            ExLlamaWorkerEvent ready = ParseWorkerEvent(line);
            if (!string.Equals(ready.Type, "ready", StringComparison.Ordinal))
                throw new InvalidDataException("ExLlamaV3 worker did not complete its ready handshake.");

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
        _activeVariantKey = null;

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
        if (stderr != null)
        {
            try { _ = await stderr.ConfigureAwait(false); } catch { }
        }
    }

    private async Task<string> ReadStderrUnsafeAsync()
    {
        if (_stderrDrain == null) return string.Empty;
        try
        {
            string text = await _stderrDrain.ConfigureAwait(false);
            text = text.Trim();
            return text.Length <= 2000 ? text : text[^2000..];
        }
        catch { return string.Empty; }
    }

    internal static ExLlamaWorkerEvent ParseWorkerEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidDataException("ExLlamaV3 worker returned an empty protocol line.");
        ExLlamaWorkerEvent evt;
        try
        {
            evt = JsonSerializer.Deserialize<ExLlamaWorkerEvent>(line, WorkerJson)
                ?? throw new InvalidDataException("ExLlamaV3 worker returned an empty event.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ExLlamaV3 worker returned invalid JSON.", ex);
        }
        if (evt.ApiVersion != WorkerApiVersion)
            throw new InvalidDataException("Unsupported ExLlamaV3 worker protocol version: " + evt.ApiVersion);
        if (string.IsNullOrWhiteSpace(evt.Type))
            throw new InvalidDataException("ExLlamaV3 worker event has no type.");
        if ((evt.Type == "delta" || evt.Type == "reasoning") && evt.Content == null)
            throw new InvalidDataException("ExLlamaV3 streaming event is missing content.");
        return evt;
    }

    private static void ValidateRequest(LocalInferenceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelVariantId))
            throw new InvalidDataException("Model variant id is required.");
        if (request.StructuredOutput != null)
            throw new NotSupportedException("ExLlamaV3 does not currently implement schema-constrained structured output. PaRuX will not silently ignore the schema.");
        if (request.Messages == null || request.Messages.Count == 0 || request.Messages.Count > 256)
            throw new InvalidDataException("ExLlamaV3 inference requires 1..256 messages.");
        if (request.MaxOutputTokens is < 1 or > MaximumOutputTokens)
            throw new InvalidDataException($"maxOutputTokens must be in 1..{MaximumOutputTokens}.");
        if (!double.IsFinite(request.Temperature) || request.Temperature is < 0 or > 2)
            throw new InvalidDataException("temperature must be finite and in 0..2.");
        _ = RuntimePerformanceTuner.NormalizeWorkload(request.Workload);
        long total = 0;
        foreach ((string role, string content) in request.Messages)
        {
            _ = NormalizeRole(role);
            total += content?.Length ?? 0;
            if (total > 1_000_000)
                throw new InvalidDataException("ExLlamaV3 message payload exceeds the PaRuX safety bound.");
        }
    }

    private static string NormalizeRole(string role)
    {
        string value = (role ?? string.Empty).Trim().ToLowerInvariant();
        return value is "system" or "user" or "assistant" or "tool"
            ? value
            : throw new InvalidDataException("Unsupported ExLlamaV3 message role: " + value);
    }

    private static readonly JsonSerializerOptions WorkerJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stateGate.Dispose();
        _generationGate.Dispose();
    }
}
