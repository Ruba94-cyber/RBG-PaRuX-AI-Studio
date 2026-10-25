using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record LocalInferenceChatPreparation(
    LocalInferenceEngineSelection? Selection,
    string Workload,
    AdaptiveReasoningPolicy.Decision Reasoning,
    string? FallbackReason);

public sealed record LocalInferenceChatStreamResult(
    bool Completed,
    bool CanFallbackToBaseline,
    string? Error);

/// <summary>
/// Adapts the engine-neutral serving boundary to PaRuX's existing OpenAI-compatible SSE Chat
/// contract. Runtime mutation and inference are deliberately different scheduling domains:
/// loading/switching a direct engine owns the process-wide exclusive GPU writer, while an already
/// resident engine streams under a shared text-reader lease. This preserves batching/concurrent
/// turns without ever replacing an engine underneath an admitted response.
/// </summary>
public sealed class LocalInferenceChatBridge
{
    private readonly LocalInferenceEngineCoordinator _engines;
    private readonly LocalRuntimeService _runtime;

    public LocalInferenceChatBridge(
        LocalInferenceEngineCoordinator engines,
        LocalRuntimeService runtime)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<LocalInferenceChatPreparation> PrepareAsync(
        ChatCompletionRequest request,
        string? selectedGgufPath,
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        bool hasRetrievalContext,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            selectedGgufPath,
            manualModelLock,
            hasVisualInput,
            requiresToolCalling,
            requiresProviderFallback,
            hasRetrievalContext,
            requiresStructuredOutput: false,
            cancellationToken);

    public async Task<LocalInferenceChatPreparation> PrepareAsync(
        ChatCompletionRequest request,
        string? selectedGgufPath,
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        bool hasRetrievalContext,
        bool requiresStructuredOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AdaptiveReasoningPolicy.Decision reasoning = AdaptiveReasoningPolicy.Decide(request);
        string workload = InferWorkload(request, hasRetrievalContext);

        LocalInferenceEngineSelection? selection = null;
        if (!_runtime.UsesExternalProvider)
        {
            selection = await _engines.TrySelectAutoAsync(
                selectedGgufPath,
                workload,
                manualModelLock,
                hasVisualInput,
                requiresToolCalling,
                requiresProviderFallback,
                requiresStructuredOutput,
                cancellationToken).ConfigureAwait(false);
        }

        if (selection == null)
        {
            await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            return new LocalInferenceChatPreparation(null, workload, reasoning, null);
        }

        try
        {
            // Engine EnsureReady can stop llama.cpp and make a different resident claim VRAM. That
            // is a runtime transition, never shared inference. Existing text readers drain first;
            // once this writer queues, new readers wait instead of being admitted onto an engine
            // that is about to disappear.
            await using MediaExecutionCoordinator.Lease transition = await MediaExecutionCoordinator.Current
                .AcquireAsync("text-transition:" + selection.Engine.Descriptor.Id, cancellationToken)
                .ConfigureAwait(false);
            await selection.Engine.EnsureReadyAsync(selection.Variant.StableKey, cancellationToken)
                .ConfigureAwait(false);
            return new LocalInferenceChatPreparation(selection, workload, reasoning, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _engines.ReportServingFailureAsync(selection, ex.Message, CancellationToken.None).ConfigureAwait(false);
            try { await selection.Engine.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            return new LocalInferenceChatPreparation(
                null,
                workload,
                reasoning,
                "Alternative local engine load failed; PaRuX restored llama.cpp: " + Trim(ex.Message));
        }
    }

    public async Task<LocalInferenceChatStreamResult> StreamAsync(
        LocalInferenceChatPreparation preparation,
        ChatCompletionRequest request,
        HttpResponse downstream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(downstream);
        LocalInferenceEngineSelection selection = preparation.Selection
            ?? throw new InvalidOperationException("Direct-engine stream requested without an engine selection.");

        LocalInferenceRequest localRequest = BuildEngineRequest(preparation, request, stream: true);

        bool emittedModelDelta = false;
        try
        {
            // There is intentionally a small gap between PrepareAsync's transition and here so
            // unrelated work is not blocked while context/tool preparation happens. Acquire a read
            // lease now, then verify the exact resident again under that lease. If another legitimate
            // transition won the gap, fail before emitting bytes and let the baseline retry rather
            // than accidentally generating on the wrong model.
            await using MediaExecutionCoordinator.Lease inference = await _runtime
                .AcquireTextInferenceAsync("direct-engine:" + selection.Engine.Descriptor.Id, cancellationToken)
                .ConfigureAwait(false);
            LocalInferenceEngineStatus resident = await selection.Engine.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!resident.Healthy
                || !string.Equals(resident.ActiveModelVariantId, selection.Variant.StableKey, StringComparison.OrdinalIgnoreCase))
            {
                string changed = "The selected direct engine was replaced before inference began; no output was emitted.";
                await _engines.ReportServingFailureAsync(selection, changed, CancellationToken.None).ConfigureAwait(false);
                return new LocalInferenceChatStreamResult(false, true, changed);
            }

            await foreach (LocalInferenceChunk chunk in selection.Engine.StreamAsync(
                localRequest, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    await _engines.ReportServingFailureAsync(selection, chunk.Error, CancellationToken.None).ConfigureAwait(false);
                    if (!emittedModelDelta)
                        return new LocalInferenceChatStreamResult(false, true, chunk.Error);
                    await WriteErrorAsync(downstream, chunk.Error, cancellationToken).ConfigureAwait(false);
                    return new LocalInferenceChatStreamResult(false, false, chunk.Error);
                }

                if (!string.IsNullOrEmpty(chunk.Reasoning))
                {
                    emittedModelDelta = true;
                    await WriteDeltaAsync(downstream, "reasoning_content", chunk.Reasoning, cancellationToken).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    emittedModelDelta = true;
                    await WriteDeltaAsync(downstream, "content", chunk.Content, cancellationToken).ConfigureAwait(false);
                }
                if (chunk.Completed)
                {
                    await _engines.ReportServingSuccessAsync(selection, CancellationToken.None).ConfigureAwait(false);
                    await WriteDoneAsync(downstream, cancellationToken).ConfigureAwait(false);
                    return new LocalInferenceChatStreamResult(true, false, null);
                }
            }

            const string premature = "The selected local inference engine ended before a completion marker.";
            await _engines.ReportServingFailureAsync(selection, premature, CancellationToken.None).ConfigureAwait(false);
            if (!emittedModelDelta)
                return new LocalInferenceChatStreamResult(false, true, premature);
            await WriteErrorAsync(downstream, premature, cancellationToken).ConfigureAwait(false);
            return new LocalInferenceChatStreamResult(false, false, premature);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _engines.ReportServingFailureAsync(selection, ex.Message, CancellationToken.None).ConfigureAwait(false);
            try { await selection.Engine.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (!emittedModelDelta)
                return new LocalInferenceChatStreamResult(false, true, ex.Message);
            await WriteErrorAsync(downstream, ex.Message, cancellationToken).ConfigureAwait(false);
            return new LocalInferenceChatStreamResult(false, false, ex.Message);
        }
    }

    internal static string InferWorkload(ChatCompletionRequest request, bool hasRetrievalContext)
    {
        string prompt = request.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        if (string.Equals(AdaptiveReasoningPolicy.InferRole(prompt), "coding", StringComparison.Ordinal))
            return "coding";
        return hasRetrievalContext ? "long-context" : "chat";
    }

    internal static int RecommendMaxOutputTokens(
        string workload,
        AdaptiveReasoningPolicy.Decision reasoning)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        int answerBudget = workload == "chat" ? 4096 : 8192;
        return Math.Clamp(answerBudget + Math.Max(0, reasoning.Budget), 4096, 24_576);
    }

    internal static LocalInferenceRequest BuildEngineRequest(
        LocalInferenceChatPreparation preparation,
        ChatCompletionRequest request,
        bool stream)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(request);
        LocalInferenceEngineSelection selection = preparation.Selection
            ?? throw new InvalidOperationException("Direct-engine request built without an engine selection.");
        return new LocalInferenceRequest(
            ModelVariantId: selection.Variant.StableKey,
            Messages: request.Messages.Select(message => (message.Role, message.Content ?? string.Empty)).ToList(),
            MaxOutputTokens: RecommendMaxOutputTokens(preparation.Workload, preparation.Reasoning),
            Temperature: request.PreferLowLatency ? 0.2 : 0.7,
            Stream: stream,
            Workload: preparation.Workload,
            EnableThinking: preparation.Reasoning.Budget > 0,
            StructuredOutput: StructuredOutputRequestTransport.TryRead(request));
    }

    private static async Task WriteDeltaAsync(
        HttpResponse downstream,
        string property,
        string value,
        CancellationToken cancellationToken)
    {
        object delta = property == "reasoning_content"
            ? new { reasoning_content = value }
            : new { content = value };
        string line = "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta } }
        }) + "\n\n";
        await downstream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await downstream.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpResponse downstream,
        string? error,
        CancellationToken cancellationToken)
    {
        string bounded = Trim(error);
        string line = "data: " + JsonSerializer.Serialize(new
        {
            error = new { message = bounded, type = "local_inference_engine_error" }
        }) + "\n\n";
        await downstream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await WriteDoneAsync(downstream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDoneAsync(HttpResponse downstream, CancellationToken cancellationToken)
    {
        await downstream.WriteAsync("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
        await downstream.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Trim(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 700 ? value : value[..700] + "…";
    }
}
