using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Server;

namespace PaRuX.Server.Orchestration;

/// <summary>Real ILocalCompletionClient - a single non-streaming chat completion against the
/// local llama-server instance LocalRuntimeService manages, same endpoint/shape ChatProxyService
/// uses for the interactive chat path. Model is set to "local-default" (ChatCompletionRequest's
/// own default) rather than resolving an AiProfileService role, since agent work should use
/// whatever model is currently active - matching how ordinary chat behaves.
///
/// Structured completions remain engine-neutral above this class. Managed llama-server receives
/// the OpenAI-compatible response_format mapping; USER_MANAGED providers receive it only after a
/// bounded live capability probe proves that endpoint can actually honor JSON Schema.
/// </summary>
public sealed class LlamaServerCompletionClient : ILocalCompletionClient
{
    private static readonly HttpClient Http = new(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Upper bound on a single orchestration completion. Comfortably above any task
    /// graph, verdict or extraction this client is asked for, while still guaranteeing the request
    /// terminates instead of generating until the context is exhausted.</summary>
    internal const int MaxOrchestrationTokens = 2048;

    private readonly LocalRuntimeService _runtime;

    public LlamaServerCompletionClient(LocalRuntimeService runtime) => _runtime = runtime;

    public Task<CompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken) =>
        CompleteCoreAsync(systemPrompt, userPrompt, structuredOutput: null, structuredOutputApplied: false, cancellationToken);

    public async Task<CompletionResult> CompleteStructuredAsync(
        string systemPrompt,
        string userPrompt,
        LocalStructuredOutputRequest structuredOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(structuredOutput);
        LocalStructuredOutputRequest validated = structuredOutput.Validate();

        if (_runtime.UsesExternalProvider)
        {
            ExternalTextProviderService.StructuredOutputProbeEvidence evidence =
                await ExternalTextProviderService.ProbeStructuredOutputAsync(
                    _runtime.ProviderEndpoint, cancellationToken).ConfigureAwait(false);
            if (!evidence.Supported)
            {
                CompletionResult fallback = await CompleteCoreAsync(
                    systemPrompt, userPrompt, structuredOutput: null,
                    structuredOutputApplied: false, cancellationToken).ConfigureAwait(false);
                return fallback with { StructuredOutputApplied = false };
            }
        }

        return await CompleteCoreAsync(
            systemPrompt, userPrompt, validated,
            structuredOutputApplied: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompletionResult> CompleteCoreAsync(
        string systemPrompt,
        string userPrompt,
        LocalStructuredOutputRequest? structuredOutput,
        bool structuredOutputApplied,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            // "local-default" is only a model the provider knows on a single-model launch. While a
            // hot model pool is resident, llama-server exposes exclusively the pool's hashed
            // aliases and rejects that id with HTTP 400 "model 'local-default' not found", which
            // kills the orchestration call outright. Address the pool's own default in that case.
            ["model"] = HotModelPoolCatalog.TryGetDefaultAlias(out string poolAlias)
                ? poolAlias
                : "local-default",
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            },
            // This client serves orchestration utilities - planner task graphs, verifier verdicts,
            // eval answers, research extraction - where the returned *content* is the artifact and
            // the model's private deliberation is not consumed by anything.
            //
            // Leaving thinking on here is not a quality/speed trade, it is a hang. Measured on the
            // 4B fallback model over Vulkan, asking for a two-sentence summary of supplied text:
            // with thinking the request had produced no content after 10 minutes and was abandoned
            // (a shorter capped run showed the entire budget consumed by reasoning_content with
            // content empty and finish_reason "length", which every caller here reads as failure);
            // with thinking disabled the same request answered correctly in 12.6 s. An answer that
            // never arrives has no quality to protect.
            //
            // User-facing chat does NOT use this client - ChatProxyService proxies the provider
            // directly - so reasoning remains fully intact where a user can actually see it.
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            // Safety net so a runaway generation cannot occupy the runtime indefinitely even if a
            // template ignores the flag above. Generous enough not to truncate a real answer.
            ["max_tokens"] = MaxOrchestrationTokens
        };
        if (structuredOutput != null)
            payload["response_format"] = StructuredOutputJson.BuildOpenAiResponseFormat(structuredOutput);

        using var request = new HttpRequestMessage(HttpMethod.Post, _runtime.ProviderEndpoint + "/v1/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        // No-op unless the endpoint is genuinely remote and the user stored a key.
        ExternalTextProviderService.ApplyRemoteAuthorization(request, _runtime.ProviderEndpoint);

        // Keep the shared text reader for the complete provider request, not only startup. A queued
        // media writer may wait behind this orchestration answer, but it can never stop llama-server
        // while the answer is still being generated or parsed.
        await using MediaExecutionCoordinator.Lease textLease = await _runtime
            .AcquireTextInferenceAsync("orchestration:completion", cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CompletionResult.Failure(
                "Could not reach the local model provider: " + ex.Message,
                structuredOutputApplied);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return CompletionResult.Failure(
                    $"Model provider returned HTTP {(int)response.StatusCode}: {Trim(detail)}",
                    structuredOutputApplied);
            }

            // vNext Phase 55 (failure injection, directive section 119: "worker crash") - a real
            // gap found by reasoning through what happens on an unexpected response shape (e.g. a
            // 200 with "choices":[]): the JsonArray indexer below throws for an out-of-range
            // index, which is neither a JsonException nor OperationCanceledException. Wrapping the
            // whole extraction keeps this method's contract honest: bad provider output becomes a
            // Failure result rather than stranding an orchestration task.
            try
            {
                JsonNode? resultNode = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                string? content = resultNode?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(content))
                    return CompletionResult.Failure("The model returned an empty response.", structuredOutputApplied);
                if (structuredOutputApplied && !StructuredOutputJson.IsValidJsonPayload(content))
                    return CompletionResult.Failure(
                        "The provider claimed structured output but returned invalid JSON.",
                        structuredOutputApplied: true);
                return CompletionResult.Ok(content, structuredOutputApplied);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CompletionResult.Failure(
                    "Could not parse the model response: " + ex.Message,
                    structuredOutputApplied);
            }
        }
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 300 ? value : value[..300] + "…";
    }
}
