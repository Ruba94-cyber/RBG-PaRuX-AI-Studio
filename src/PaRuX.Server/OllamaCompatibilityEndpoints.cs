using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Ollama-shaped provider surface over PaRuX's authoritative OpenAI-compatible local serving path.
/// The adapter is semantic, not cosmetic: every non-null field is either translated deliberately or
/// rejected with a useful 400. This prevents a client from asking for tools, vision, JSON Schema or
/// a residency behavior and receiving an ordinary text completion that silently ignored the intent.
/// </summary>
public static class OllamaCompatibilityEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int CompatibilitySchemaVersion = 1;
    private const int MaximumImagesPerMessage = 4;
    private const int MaximumImageBase64Characters = 16 * 1024 * 1024;

    public static void MapOllamaCompatibilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/version", () => Results.Ok(new
        {
            version = ParuxVersion(),
            parux_ollama_compatibility = CompatibilitySchemaVersion
        }));

        // PaRuX-specific additive discovery endpoint. Native Ollama clients never need to call it,
        // while integrations that care about exact semantics can feature-detect without guessing.
        app.MapGet("/api/parux/capabilities", () => Results.Ok(BuildCapabilityContract()));

        app.MapGet("/api/tags", (LocalApiServerService localApi) =>
        {
            object models = localApi.ListModels() is { } listing
                ? BuildTags(listing)
                : new { models = Array.Empty<object>() };
            return Results.Ok(models);
        });

        app.MapPost("/api/show", async (HttpRequest request, HttpResponse httpResponse, LocalApiServerService localApi, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, httpResponse, ct);
            if (body == null) return Results.Empty;
            if (!string.IsNullOrWhiteSpace(body) && !IsJsonObject(body))
                return Results.BadRequest(new { error = "request body must be a JSON object", parux_ollama_compatibility = CompatibilitySchemaVersion });
            string name = ReadString(body, "name") ?? ReadString(body, "model") ?? string.Empty;
            LocalApiServerService.LocalApiModel? model = localApi.GetModel(name);
            if (model == null && name.Length > 0)
                return Results.NotFound(new { error = "model '" + name + "' not found" });

            return Results.Ok(new
            {
                license = string.Empty,
                modelfile = string.Empty,
                parameters = string.Empty,
                template = string.Empty,
                // Official SDKs require this key even when the logical alias has no exposed
                // architecture metadata. Do not invent physical-model parameters for an alias.
                model_info = new Dictionary<string, object>(),
                details = new { family = "parux", format = "gguf", parameter_size = string.Empty },
                capabilities = BuildShowCapabilities(name, model)
            });
        });

        app.MapPost("/api/chat", async (
            HttpRequest request, HttpResponse response, LocalApiServerService localApi, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, response, ct);
            if (body == null) return;
            await AnswerAsync(body, response, localApi, generate: false, ct);
        });

        app.MapPost("/api/generate", async (
            HttpRequest request, HttpResponse response, LocalApiServerService localApi, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, response, ct);
            if (body == null) return;
            await AnswerAsync(body, response, localApi, generate: true, ct);
        });

        app.MapGet("/api/ps", () =>
        {
            var models = new List<object>();
            if (HotModelPoolCatalog.IsActive)
            {
                foreach (HotModelPoolCatalog.Entry entry in HotModelPoolCatalog.GetActiveEntries())
                {
                    string name = Path.GetFileNameWithoutExtension(entry.ModelPath);
                    long size = 0L;
                    try { if (File.Exists(entry.ModelPath)) size = new FileInfo(entry.ModelPath).Length; } catch { }
                    models.Add(new
                    {
                        name = entry.Alias,
                        model = name,
                        size = size,
                        digest = entry.Alias,
                        details = new { parent_model = string.Empty, format = "gguf", family = "parux", families = new[] { "parux" }, parameter_size = string.Empty, quantization_level = string.Empty },
                        expires_at = DateTime.UtcNow.AddMinutes(45).ToString("o"),
                        size_vram = entry.EstimatedResidentBytes
                    });
                }
            }
            else
            {
                string activePath = string.Empty;
                try { if (File.Exists(LocalAiPaths.ActiveModelPathFile)) activePath = File.ReadAllText(LocalAiPaths.ActiveModelPathFile).Trim(); } catch { }
                if (!string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath))
                {
                    long size = 0L;
                    try { size = new FileInfo(activePath).Length; } catch { }
                    string name = Path.GetFileNameWithoutExtension(activePath);
                    models.Add(new
                    {
                        name = "local-default",
                        model = name,
                        size = size,
                        digest = name,
                        details = new { parent_model = string.Empty, format = "gguf", family = "parux", families = new[] { "parux" }, parameter_size = string.Empty, quantization_level = string.Empty },
                        expires_at = DateTime.UtcNow.AddMinutes(45).ToString("o"),
                        size_vram = HotModelPoolCatalog.EstimateResidentBytes(activePath)
                    });
                }
            }
            return Results.Ok(new { models });
        });

        app.MapPost("/api/embeddings", async (
            HttpRequest request, HttpResponse response, LocalApiEmbeddingsService embeddingsApi, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, response, ct);
            if (body == null) return Results.Empty;
            return HandleEmbeddings(body, embeddingsApi);
        });

        app.MapPost("/api/embed", async (
            HttpRequest request, HttpResponse response, LocalApiEmbeddingsService embeddingsApi, CancellationToken ct) =>
        {
            string? body = await ReadBodyAsync(request, response, ct);
            if (body == null) return Results.Empty;
            return HandleEmbed(body, embeddingsApi);
        });
    }

    internal static object BuildCapabilityContract() => new
    {
        schema_version = CompatibilitySchemaVersion,
        protocol = "ollama-compatible",
        implementation = "PaRuX",
        behavior = "supported-or-explicit-error",
        chat = new
        {
            streaming = true,
            tools = "buffered",
            streaming_tools = false,
            vision_base64_images = true,
            format_json = true,
            format_json_schema = true,
            think_boolean = true,
            think_levels = false,
            keep_alive = false,
            logprobs = true
        },
        generate = new
        {
            streaming = true,
            vision_base64_images = true,
            format_json = true,
            format_json_schema = true,
            think_boolean = true,
            think_levels = false,
            suffix = false,
            raw = false,
            keep_alive = false,
            logprobs = true
        },
        embeddings = true,
        embed = true,
        ps = true,
        options = new[] { "temperature", "top_p", "seed", "stop", "num_predict" },
        unsupported_status = 400
    };

    public static IReadOnlyList<string> BuildShowCapabilities(
        string? modelId,
        LocalApiServerService.LocalApiModel? model)
    {
        if (model == null)
            return Array.Empty<string>();

        return BuildShowCapabilities(
            modelId,
            model.ModelPath ?? model.real_model,
            FindProjectorCandidates(modelId, model.ModelPath));
    }

    public static IReadOnlyList<string> BuildShowCapabilities(
        string? modelId,
        string? modelPath,
        IEnumerable<string>? projectorCandidates)
    {
        string modelFileName = string.IsNullOrWhiteSpace(modelPath) ? string.Empty : Path.GetFileName(modelPath);
        string category = ModelCategoryClassifier.Classify(
            modelId ?? string.Empty,
            new[] { modelFileName },
            tags: null);

        if (category == ModelCategoryClassifier.Embedding)
            return new[] { "embedding" };
        if (category == ModelCategoryClassifier.Reranker)
            return Array.Empty<string>();

        // Vision is decided by the projector manifest family plus a managed projector pairing, not by
        // the single-winner name category: a 4B "Thinking" or natively multimodal model whose name
        // classifies as reasoning/general is still vision-capable once its projector is paired.
        IReadOnlyList<string> nameCapabilities = ModelCategoryClassifier.CapabilitiesOf(modelFileName, null, modelId);
        var capabilities = new List<string> { "completion", "chat" };
        if (HasPairedVisionProjector(modelPath, projectorCandidates))
            capabilities.Add("vision");
        capabilities.Add("tools");
        if (nameCapabilities.Contains(ModelCategoryClassifier.Reasoning))
            capabilities.Add("thinking");
        return capabilities;
    }

    public static string[] FindProjectorCandidates(string? modelId, string? modelPath)
    {
        if (VisionProjectorManifest.Default.FamilyFor(modelPath) is not { VisionCapable: true })
            return Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return Array.Empty<string>();

        string? directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
                .Where(GgufFileNaming.IsProjector)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasPairedVisionProjector(string? modelPath, IEnumerable<string>? projectorCandidates)
    {
        VisionProjectorManifest manifest = VisionProjectorManifest.Default;
        VisionFamily? family = manifest.FamilyFor(modelPath);
        if (family is not { VisionCapable: true })
            return false;

        string[] matchingProjectors = (projectorCandidates ?? Array.Empty<string>())
            .Where(GgufFileNaming.IsProjector)
            .Where(path => string.Equals(
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetDirectoryName(modelPath) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => manifest.FamilyFor(path)?.Id == family.Id)
            .ToArray();
        return manifest.ChooseProjector(matchingProjectors) != null;
    }

    private static readonly HashSet<string> EmbeddingFields = new(StringComparer.Ordinal)
    { "model", "input", "prompt" };

    /// <summary>
    /// The embedding service takes only a model and text. Every other Ollama embedding field
    /// (truncate, options, keep_alive, dimensions, unknown names) would change the result or
    /// residency, so a non-null value is rejected instead of silently ignored.
    /// </summary>
    internal static IResult? RejectUnsupportedEmbeddingFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
        if (root == null) return null;
        foreach ((string name, JsonNode? value) in root)
        {
            if (value == null || EmbeddingFields.Contains(name)) continue;
            string guidance = name switch
            {
                "keep_alive" => "PaRuX owns model residency",
                "truncate" => "PaRuX does not alter input truncation per request",
                "dimensions" => "the embedding dimension is fixed by the local embedding model",
                "options" => "embedding runtime options are not configurable per request",
                _ => "this field is not supported"
            };
            return Results.BadRequest(new
            {
                error = Unsupported("embedding request field '" + name + "'", guidance).Message,
                parux_ollama_compatibility = CompatibilitySchemaVersion
            });
        }
        return null;
    }

    private static IResult HandleEmbeddings(string body, LocalApiEmbeddingsService embeddingsApi)
    {
        if (RejectUnsupportedEmbeddingFields(body) is IResult rejected) return rejected;
        string? prompt;
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "request body must be a JSON object", parux_ollama_compatibility = CompatibilitySchemaVersion });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("prompt", out JsonElement promptElement))
            {
                prompt = promptElement.ValueKind == JsonValueKind.String
                    ? promptElement.GetString()
                    : promptElement.ToString();
            }
            else if (root.TryGetProperty("input", out JsonElement inputElement))
            {
                prompt = inputElement.ValueKind == JsonValueKind.String
                    ? inputElement.GetString()
                    : inputElement.ToString();
            }
            else
            {
                return Results.BadRequest(new
                {
                    error = "prompt is required",
                    parux_ollama_compatibility = CompatibilitySchemaVersion
                });
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid request body" });
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.BadRequest(new
            {
                error = "prompt cannot be empty",
                parux_ollama_compatibility = CompatibilitySchemaVersion
            });
        }

        var openAiRequest = new JsonObject
        {
            ["model"] = LocalApiEmbeddingsService.ModelId,
            ["input"] = prompt
        };

        LocalApiCompletionResult result = embeddingsApi.CreateEmbeddings(openAiRequest.ToJsonString());
        if (result.StatusCode != StatusCodes.Status200OK)
        {
            string message = ExtractErrorMessage(result.Body);
            return Results.Json(new
            {
                error = message,
                parux_ollama_compatibility = CompatibilitySchemaVersion
            }, statusCode: result.StatusCode);
        }

        try
        {
            using JsonDocument responseDoc = JsonDocument.Parse(result.Body);
            JsonElement embedding = responseDoc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding");

            return Results.Ok(new
            {
                embedding = JsonSerializer.Deserialize<float[]>(embedding.GetRawText(), JsonOptions)
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Failed to parse embedding response: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult HandleEmbed(string body, LocalApiEmbeddingsService embeddingsApi)
    {
        if (RejectUnsupportedEmbeddingFields(body) is IResult rejected) return rejected;
        string? model = null;
        JsonNode? inputNode;
        try
        {
            JsonNode? parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (parsed is not JsonObject obj)
                return Results.BadRequest(new { error = "invalid request body" });

            if (obj["model"] is JsonNode modelNode
                && (modelNode is not JsonValue modelValue || !modelValue.TryGetValue(out model) || string.IsNullOrWhiteSpace(model)))
                return Results.BadRequest(new { error = "model must be a non-empty string", parux_ollama_compatibility = CompatibilitySchemaVersion });
            model ??= "parux-embedding";
            if (!obj.TryGetPropertyValue("input", out inputNode) || inputNode == null)
            {
                return Results.BadRequest(new
                {
                    error = "input is required",
                    parux_ollama_compatibility = CompatibilitySchemaVersion
                });
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid request body" });
        }

        var openAiRequest = new JsonObject
        {
            ["model"] = LocalApiEmbeddingsService.ModelId,
            ["input"] = inputNode.DeepClone()
        };

        LocalApiCompletionResult result = embeddingsApi.CreateEmbeddings(openAiRequest.ToJsonString());
        if (result.StatusCode != StatusCodes.Status200OK)
        {
            string message = ExtractErrorMessage(result.Body);
            return Results.Json(new
            {
                error = message,
                parux_ollama_compatibility = CompatibilitySchemaVersion
            }, statusCode: result.StatusCode);
        }

        try
        {
            using JsonDocument responseDoc = JsonDocument.Parse(result.Body);
            var embeddingsList = new List<float[]>();
            if (responseDoc.RootElement.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("embedding", out JsonElement emb))
                    {
                        float[]? vector = JsonSerializer.Deserialize<float[]>(emb.GetRawText(), JsonOptions);
                        if (vector != null) embeddingsList.Add(vector);
                    }
                }
            }

            int promptTokens = 0;
            if (responseDoc.RootElement.TryGetProperty("usage", out JsonElement usage)
                && usage.TryGetProperty("prompt_tokens", out JsonElement pt))
            {
                promptTokens = pt.GetInt32();
            }

            return Results.Ok(new
            {
                model = string.IsNullOrWhiteSpace(model) ? LocalApiEmbeddingsService.ModelId : model,
                embeddings = embeddingsList,
                total_duration = 0L,
                load_duration = 0L,
                prompt_eval_count = promptTokens
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Failed to parse embed response: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static string ExtractErrorMessage(string responseBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "unknown error";
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement msg))
                    return msg.GetString() ?? "unknown error";
            }
        }
        catch { }
        return responseBody;
    }

    private static async Task AnswerAsync(
        string body, HttpResponse response, LocalApiServerService localApi, bool generate, CancellationToken ct)
    {
        string model = ReadString(body, "model") ?? string.Empty;
        bool stream = ReadBool(body, "stream") ?? true;
        bool includeThinking = ReadThinkBoolean(body) ?? false;

        string openAiRequest;
        try
        {
            openAiRequest = ToOpenAiRequest(body, model, generate);
            if (stream && RequestContainsTools(openAiRequest))
                throw Unsupported("streaming tool calls", "set stream=false for Ollama tool calling on this PaRuX version");
        }
        catch (OllamaCompatibilityException ex)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new
            {
                error = ex.Message,
                parux_ollama_compatibility = CompatibilitySchemaVersion
            }, cancellationToken: ct);
            return;
        }
        catch (JsonException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new { error = "invalid request body" }, cancellationToken: ct);
            return;
        }

        if (!stream)
        {
            LocalApiCompletionResult buffered = await localApi.CompleteAsync(openAiRequest, ct);
            if (buffered.StatusCode >= 400)
            {
                response.StatusCode = ClientFacingStatus(buffered.StatusCode, buffered.Body);
                response.ContentType = "application/json";
                await response.WriteAsync(OllamaErrorJson(buffered.Body), ct);
                return;
            }

            string frame;
            try { frame = FrameFromOpenAi(model, DateTime.UtcNow.ToString("o"), buffered.Body, generate, includeThinking); }
            catch (OllamaCompatibilityException ex)
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                await response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken: ct);
                return;
            }

            response.ContentType = "application/json";
            await response.WriteAsync(frame, ct);
            return;
        }

        response.ContentType = "application/x-ndjson";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var translator = new OllamaStreamTranslator(response.Body, model, generate, includeThinking);
        LocalApiCompletionResult streamed = await localApi.StreamAsync(WithStreaming(openAiRequest), translator, ct);

        if (streamed.StatusCode >= 400 && !translator.WroteAnyContent)
        {
            response.StatusCode = ClientFacingStatus(streamed.StatusCode, streamed.Body);
            response.ContentType = "application/json";
            await response.WriteAsync(OllamaErrorJson(streamed.Body), ct);
            return;
        }

        if (streamed.StatusCode >= 400 && translator.WroteAnyContent)
        {
            await translator.WriteErrorAsync(ExtractErrorMessage(streamed.Body), ct);
            return;
        }

        await translator.WriteDoneAsync(ct);
    }

    /// <summary>
    /// The resident provider reports "image input is not supported" as an HTTP 500 when the loaded
    /// model has no vision projector. That is a client-visible capability mismatch, not a server
    /// fault, so surface it as the same explicit 400 every other unsupported feature uses.
    /// </summary>
    internal static int ClientFacingStatus(int upstreamStatus, string upstreamBody) =>
        upstreamStatus == StatusCodes.Status500InternalServerError
        && upstreamBody.Contains("image input is not supported", StringComparison.OrdinalIgnoreCase)
            ? StatusCodes.Status400BadRequest
            : upstreamStatus;

    private static bool RequestContainsTools(string openAiRequest)
    {
        using JsonDocument document = JsonDocument.Parse(openAiRequest);
        return document.RootElement.TryGetProperty("tools", out JsonElement tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
    }

    private static string WithStreaming(string openAiRequest)
    {
        JsonNode? node = JsonNode.Parse(openAiRequest);
        if (node is not JsonObject rewritten) throw new JsonException("Translated request is not an object.");
        rewritten["stream"] = true;
        rewritten["stream_options"] = new JsonObject { ["include_usage"] = true };
        return rewritten.ToJsonString();
    }

    /// <summary>
    /// Translate Ollama request semantics to the OpenAI-compatible contract already served by
    /// PaRuX/llama-server. Non-null unknown fields and unsupported options fail closed.
    /// </summary>
    internal static string ToOpenAiRequest(string body, string model, bool generate)
    {
        JsonNode? parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (parsed is not JsonObject root)
            throw new OllamaCompatibilityException("request body must be a JSON object");

        ValidateTopLevel(root, generate);
        var request = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "local-default" : model.Trim(),
            ["stream"] = false
        };

        if (generate)
            request["messages"] = TranslateGenerateMessages(root);
        else
            request["messages"] = TranslateChatMessages(root);

        TranslateTools(root, request, generate);
        TranslateFormat(root, request);
        TranslateThinking(root, request);
        TranslateLogprobs(root, request);
        TranslateOptions(root, request);
        return request.ToJsonString();
    }

    private static void ValidateTopLevel(JsonObject root, bool generate)
    {
        HashSet<string> allowed = generate
            ? new(StringComparer.Ordinal)
            {
                "model", "prompt", "system", "images", "format", "stream", "think", "raw",
                "suffix", "keep_alive", "options", "logprobs", "top_logprobs"
            }
            : new(StringComparer.Ordinal)
            {
                "model", "messages", "tools", "format", "stream", "think", "keep_alive",
                "options", "logprobs", "top_logprobs"
            };

        foreach ((string name, JsonNode? value) in root)
        {
            if (allowed.Contains(name) || value == null) continue;
            throw Unsupported("request field '" + name + "'");
        }

        if (root["model"] != null) _ = ReadOptionalString(root, "model");
        if (root["stream"] != null && (root["stream"] is not JsonValue streamValue
            || !streamValue.TryGetValue(out bool _)))
            throw new OllamaCompatibilityException("'stream' must be a boolean");

        if (root["keep_alive"] != null)
            throw Unsupported("keep_alive", "PaRuX owns model residency and does not silently unload/reload a resident per Ollama request");

        if (generate)
        {
            if (root["suffix"] != null)
            {
                string? suffix = ReadOptionalString(root, "suffix");
                if (!string.IsNullOrEmpty(suffix)) throw Unsupported("generate suffix/FIM");
            }
            if (root["raw"] != null)
            {
                if (root["raw"] is not JsonValue rawValue || !rawValue.TryGetValue(out bool raw))
                    throw new OllamaCompatibilityException("'raw' must be a boolean");
                if (raw) throw Unsupported("raw prompt mode", "PaRuX keeps its local provider chat-template boundary authoritative");
            }
        }
    }

    private static void ValidateSupportedFields(JsonObject value, HashSet<string> supported, string scope)
    {
        foreach ((string name, JsonNode? child) in value)
        {
            if (!supported.Contains(name) && child != null)
                throw Unsupported(scope + " field '" + name + "'");
        }
    }

    private static JsonArray TranslateGenerateMessages(JsonObject root)
    {
        var messages = new JsonArray();
        if (root["system"] != null) _ = ReadOptionalString(root, "system");
        if (root["prompt"] != null) _ = ReadOptionalString(root, "prompt");
        if (root["images"] != null && root["images"] is not JsonArray)
            throw new OllamaCompatibilityException("'images' must be an array of base64 strings");
        if (root["system"] is JsonValue systemValue && systemValue.TryGetValue(out string? system)
            && !string.IsNullOrEmpty(system))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        string prompt = root["prompt"] is JsonValue promptValue && promptValue.TryGetValue(out string? value)
            ? value ?? string.Empty
            : string.Empty;
        var user = new JsonObject { ["role"] = "user" };
        user["content"] = BuildContent(prompt, root["images"] as JsonArray, "generate.images");
        messages.Add(user);
        return messages;
    }

    private static JsonArray TranslateChatMessages(JsonObject root)
    {
        if (root["messages"] is not JsonArray source || source.Count == 0)
            throw new OllamaCompatibilityException("'messages' must be a non-empty array");

        var output = new JsonArray();
        var pendingToolIds = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        var usedToolIds = new HashSet<string>(StringComparer.Ordinal);
        int generatedToolId = 0;

        foreach (JsonNode? node in source)
        {
            if (node is not JsonObject message)
                throw new OllamaCompatibilityException("each Ollama message must be a JSON object");
            string role = RequireString(message, "role").ToLowerInvariant();
            ValidateMessageFields(message, role);
            if (role is not ("system" or "user" or "assistant" or "tool"))
                throw new OllamaCompatibilityException("unsupported Ollama message role '" + role + "'");

            if (role == "tool")
            {
                string toolName = RequireString(message, "tool_name");
                if (!pendingToolIds.TryGetValue(toolName, out Queue<string>? queue) || queue.Count == 0)
                    throw new OllamaCompatibilityException("tool result for '" + toolName + "' has no preceding matching assistant tool call");
                output.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = queue.Dequeue(),
                    ["content"] = ReadOptionalString(message, "content") ?? string.Empty
                });
                continue;
            }

            if (message["images"] != null && message["images"] is not JsonArray)
                throw new OllamaCompatibilityException("message.images must be an array of base64 strings");
            if (message["tool_calls"] != null && message["tool_calls"] is not JsonArray)
                throw new OllamaCompatibilityException("message.tool_calls must be an array");

            JsonArray? images = message["images"] as JsonArray;
            string content = ReadOptionalString(message, "content") ?? string.Empty;
            var translated = new JsonObject
            {
                ["role"] = role,
                ["content"] = BuildContent(content, images, "messages.images")
            };

            string? thinking = ReadOptionalString(message, "thinking");
            if (!string.IsNullOrEmpty(thinking))
            {
                throw Unsupported("assistant thinking history", "PaRuX does not guess a provider-specific reasoning-history field");
            }

            if (message["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                if (role != "assistant")
                    throw new OllamaCompatibilityException("tool_calls are valid only on assistant messages");
                JsonArray translatedCalls = TranslateAssistantToolCalls(calls, pendingToolIds, usedToolIds, ref generatedToolId);
                translated["tool_calls"] = translatedCalls;
                if (content.Length == 0 && images == null) translated["content"] = null;
            }

            output.Add(translated);
        }

        return output;
    }

    private static void ValidateMessageFields(JsonObject message, string role)
    {
        HashSet<string> allowed = role == "tool"
            ? new(StringComparer.Ordinal) { "role", "content", "tool_name" }
            : role == "assistant"
                ? new(StringComparer.Ordinal) { "role", "content", "images", "tool_calls", "thinking" }
                : new(StringComparer.Ordinal) { "role", "content", "images" };
        foreach ((string name, JsonNode? value) in message)
        {
            if (!allowed.Contains(name) && value != null)
                throw Unsupported("message field '" + name + "'");
        }
    }

    private static JsonArray TranslateAssistantToolCalls(
        JsonArray calls,
        Dictionary<string, Queue<string>> pendingToolIds,
        HashSet<string> usedToolIds,
        ref int generatedToolId)
    {
        var result = new JsonArray();
        foreach (JsonNode? callNode in calls)
        {
            if (callNode is not JsonObject call || call["function"] is not JsonObject function)
                throw new OllamaCompatibilityException("each tool_call must contain a function object");
            ValidateSupportedFields(call, new HashSet<string>(StringComparer.Ordinal) { "id", "type", "function" }, "tool_call");
            string? type = ReadOptionalString(call, "type");
            if (type != null && !string.Equals(type, "function", StringComparison.Ordinal))
                throw Unsupported("tool_call type '" + type + "'");
            ValidateSupportedFields(function, new HashSet<string>(StringComparer.Ordinal) { "name", "arguments" }, "tool_call.function");
            string name = RequireString(function, "name");
            JsonNode? argumentsNode = function["arguments"];
            string arguments = argumentsNode switch
            {
                JsonObject or JsonArray => argumentsNode.ToJsonString(),
                JsonValue value when value.TryGetValue(out string? text) => text ?? "{}",
                null => "{}",
                _ => throw new OllamaCompatibilityException("tool-call arguments must be a JSON object/array or JSON string")
            };

            // Preserve valid client IDs, while keeping generated IDs collision-free across the
            // complete translated transcript (including calls whose tool result already arrived).
            string? suppliedId = ReadOptionalString(call, "id");
            if (call["id"] != null && string.IsNullOrWhiteSpace(suppliedId))
                throw new OllamaCompatibilityException("tool_call id must be a non-empty string when supplied");
            if (suppliedId != null && suppliedId.Length > 256)
                throw new OllamaCompatibilityException("tool_call id must be at most 256 characters");

            string id;
            if (suppliedId != null)
            {
                id = suppliedId;
                if (!usedToolIds.Add(id))
                    throw new OllamaCompatibilityException("tool_call id '" + id + "' is duplicated in the Ollama transcript");
            }
            else
            {
                do
                {
                    id = "call_ollama_" + (++generatedToolId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (!usedToolIds.Add(id));
            }
            if (!pendingToolIds.TryGetValue(name, out Queue<string>? queue))
            {
                queue = new Queue<string>();
                pendingToolIds[name] = queue;
            }
            queue.Enqueue(id);

            result.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["arguments"] = arguments
                }
            });
        }
        return result;
    }

    private static JsonNode BuildContent(string text, JsonArray? images, string field)
    {
        if (images == null || images.Count == 0) return JsonValue.Create(text)!;
        if (images.Count > MaximumImagesPerMessage)
            throw new OllamaCompatibilityException(field + " supports at most " + MaximumImagesPerMessage + " images per message");

        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(text))
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        foreach (JsonNode? imageNode in images)
        {
            if (imageNode is not JsonValue imageValue || !imageValue.TryGetValue(out string? base64)
                || string.IsNullOrWhiteSpace(base64))
                throw new OllamaCompatibilityException(field + " must contain non-empty base64 strings");
            if (base64.Length > MaximumImageBase64Characters)
                throw new OllamaCompatibilityException(field + " image exceeds the bounded PaRuX compatibility payload limit");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new OllamaCompatibilityException(field + " contains invalid base64 image data"); }
            string mediaType = DetectImageMediaType(bytes)
                ?? throw new OllamaCompatibilityException(field + " supports PNG, JPEG, GIF or WebP image data");

            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:" + mediaType + ";base64," + base64 }
            });
        }
        return parts;
    }

    private static string? DetectImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return "image/gif";
        if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes[..4]) == "RIFF" && Encoding.ASCII.GetString(bytes.Slice(8, 4)) == "WEBP")
            return "image/webp";
        return null;
    }

    private static void TranslateTools(JsonObject root, JsonObject request, bool generate)
    {
        if (root["tools"] == null) return;
        if (generate) throw Unsupported("tools on /api/generate");
        if (root["tools"] is not JsonArray tools) throw new OllamaCompatibilityException("'tools' must be an array");

        var translated = new JsonArray();
        foreach (JsonNode? node in tools)
        {
            if (node is not JsonObject tool || tool["function"] is not JsonObject function)
                throw new OllamaCompatibilityException("each tool must contain a function object");
            ValidateSupportedFields(tool, new HashSet<string>(StringComparer.Ordinal) { "type", "function" }, "tool");
            ValidateSupportedFields(function, new HashSet<string>(StringComparer.Ordinal)
                { "name", "description", "parameters" }, "tool.function");
            string type = ReadOptionalString(tool, "type") ?? "function";
            if (!string.Equals(type, "function", StringComparison.Ordinal))
                throw Unsupported("tool type '" + type + "'");
            string name = RequireString(function, "name");
            var openAiFunction = new JsonObject { ["name"] = name };
            string? description = ReadOptionalString(function, "description");
            if (!string.IsNullOrWhiteSpace(description)) openAiFunction["description"] = description;
            if (function["parameters"] is JsonNode parameters)
            {
                if (parameters is not JsonObject)
                    throw new OllamaCompatibilityException("tool.function.parameters must be a JSON Schema object");
                openAiFunction["parameters"] = parameters.DeepClone();
            }
            translated.Add(new JsonObject { ["type"] = "function", ["function"] = openAiFunction });
        }
        request["tools"] = translated;
    }

    private static void TranslateFormat(JsonObject root, JsonObject request)
    {
        JsonNode? format = root["format"];
        if (format == null) return;
        if (format is JsonValue value && value.TryGetValue(out string? text))
        {
            if (!string.Equals(text, "json", StringComparison.OrdinalIgnoreCase))
                throw Unsupported("format '" + text + "'", "use 'json' or a JSON Schema object");
            request["response_format"] = new JsonObject { ["type"] = "json_object" };
            return;
        }
        if (format is JsonObject schema)
        {
            var structured = new LocalStructuredOutputRequest("ollama_response", schema.ToJsonString(), Strict: true);
            try { request["response_format"] = StructuredOutputJson.BuildOpenAiResponseFormat(structured); }
            catch (InvalidDataException ex) { throw new OllamaCompatibilityException("invalid Ollama JSON Schema format: " + ex.Message); }
            return;
        }
        throw new OllamaCompatibilityException("'format' must be 'json' or a JSON Schema object");
    }

    private static void TranslateThinking(JsonObject root, JsonObject request)
    {
        JsonNode? think = root["think"];
        if (think == null) return;
        if (think is JsonValue value && value.TryGetValue(out bool enabled))
        {
            request["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = enabled };
            return;
        }
        if (think is JsonValue stringValue && stringValue.TryGetValue(out string? level))
            throw Unsupported("think level '" + level + "'", "boolean think=true/false is supported; PaRuX does not invent a mapping for model-specific Ollama effort levels");
        throw new OllamaCompatibilityException("'think' must be a boolean on this PaRuX compatibility version");
    }

    private static void TranslateLogprobs(JsonObject root, JsonObject request)
    {
        if (root["logprobs"] is JsonNode logprobs)
        {
            if (logprobs is not JsonValue logValue || !logValue.TryGetValue(out bool enabled))
                throw new OllamaCompatibilityException("'logprobs' must be a boolean");
            request["logprobs"] = enabled;
        }
        if (root["top_logprobs"] is JsonNode top)
        {
            if (top is not JsonValue topValue || !topValue.TryGetValue(out int count) || count < 0)
                throw new OllamaCompatibilityException("'top_logprobs' must be a non-negative integer");
            request["top_logprobs"] = count;
        }
    }

    private static void TranslateOptions(JsonObject root, JsonObject request)
    {
        if (root["options"] == null) return;
        if (root["options"] is not JsonObject options)
            throw new OllamaCompatibilityException("'options' must be an object");

        HashSet<string> supported = new(StringComparer.Ordinal)
            { "temperature", "top_p", "seed", "stop", "num_predict" };
        foreach ((string name, JsonNode? value) in options)
        {
            if (value == null) continue;
            if (!supported.Contains(name))
                throw Unsupported("option '" + name + "'");
        }

        CopyNumber(options, request, "temperature", "temperature");
        CopyNumber(options, request, "top_p", "top_p");
        CopyInteger(options, request, "seed", "seed", allowZero: true);
        CopyInteger(options, request, "num_predict", "max_tokens", allowZero: false);

        if (options["stop"] is JsonNode stop)
        {
            if (stop is JsonValue stopValue && stopValue.TryGetValue(out string? stopText))
                request["stop"] = stopText;
            else if (stop is JsonArray stopArray && stopArray.All(item => item is JsonValue v && v.TryGetValue(out string? _)))
                request["stop"] = stopArray.DeepClone();
            else
                throw new OllamaCompatibilityException("options.stop must be a string or an array of strings");
        }
    }

    private static void CopyNumber(JsonObject source, JsonObject destination, string sourceName, string destinationName)
    {
        if (source[sourceName] == null) return;
        if (source[sourceName] is not JsonValue value || !value.TryGetValue(out double number) || !double.IsFinite(number))
            throw new OllamaCompatibilityException("options." + sourceName + " must be a finite number");
        destination[destinationName] = number;
    }

    private static void CopyInteger(JsonObject source, JsonObject destination, string sourceName, string destinationName, bool allowZero)
    {
        if (source[sourceName] == null) return;
        if (source[sourceName] is not JsonValue value || !value.TryGetValue(out int number) || (allowZero ? number < 0 : number <= 0))
            throw new OllamaCompatibilityException("options." + sourceName + (allowZero ? " must be a non-negative integer" : " must be a positive integer"));
        destination[destinationName] = number;
    }

    private static string FrameFromOpenAi(
        string model, string createdAt, string openAiBody, bool generate, bool includeThinking)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(openAiBody); }
        catch (JsonException) { throw new OllamaCompatibilityException("local model returned unparseable OpenAI response JSON"); }
        if (parsed is not JsonObject root || root["choices"] is not JsonArray choices || choices.Count == 0
            || choices[0] is not JsonObject choice)
            throw new OllamaCompatibilityException("local model returned an unexpected OpenAI response shape");

        JsonObject? message = choice["message"] as JsonObject;
        string content = message?["content"] is JsonValue contentValue && contentValue.TryGetValue(out string? text)
            ? text ?? string.Empty : string.Empty;
        string? thinking = includeThinking ? ReadReasoning(message) : null;
        string? doneReason = choice["finish_reason"] is JsonValue finishValue && finishValue.TryGetValue(out string? finish)
            ? finish : null;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = createdAt,
            ["done"] = true
        };
        if (!string.IsNullOrWhiteSpace(doneReason)) payload["done_reason"] = doneReason;

        if (generate)
        {
            payload["response"] = content;
            if (!string.IsNullOrEmpty(thinking)) payload["thinking"] = thinking;
        }
        else
        {
            var ollamaMessage = new JsonObject { ["role"] = "assistant", ["content"] = content };
            if (!string.IsNullOrEmpty(thinking)) ollamaMessage["thinking"] = thinking;
            if (message?["tool_calls"] is JsonArray openAiCalls && openAiCalls.Count > 0)
                ollamaMessage["tool_calls"] = TranslateOpenAiToolCalls(openAiCalls);
            payload["message"] = ollamaMessage;
        }

        CopyUsage(root, payload);
        return payload.ToJsonString();
    }

    private static JsonArray TranslateOpenAiToolCalls(JsonArray calls)
    {
        var result = new JsonArray();
        int index = 0;
        foreach (JsonNode? node in calls)
        {
            if (node is not JsonObject call || call["function"] is not JsonObject function)
                throw new OllamaCompatibilityException("local model returned a malformed tool call");
            string name = RequireString(function, "name");
            string argumentText = ReadOptionalString(function, "arguments") ?? "{}";
            JsonNode? arguments;
            try { arguments = JsonNode.Parse(argumentText); }
            catch (JsonException) { throw new OllamaCompatibilityException("local model returned non-JSON tool-call arguments for '" + name + "'"); }
            arguments ??= new JsonObject();

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["index"] = index++,
                    ["name"] = name,
                    ["arguments"] = arguments.DeepClone()
                }
            });
        }
        return result;
    }

    private static string? ReadReasoning(JsonObject? message)
    {
        if (message == null) return null;
        foreach (string key in new[] { "reasoning_content", "reasoning", "thinking" })
        {
            if (message[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text))
                return text;
        }
        return null;
    }

    private static void CopyUsage(JsonObject root, JsonObject payload)
    {
        if (root["usage"] is not JsonObject usage) return;
        if (usage["prompt_tokens"] is JsonValue prompt && prompt.TryGetValue(out int promptTokens))
            payload["prompt_eval_count"] = promptTokens;
        if (usage["completion_tokens"] is JsonValue completion && completion.TryGetValue(out int completionTokens))
            payload["eval_count"] = completionTokens;
    }

    internal static string ExtractAnswer(string openAiResponseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(openAiResponseBody);
            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return string.Empty;
            JsonElement first = choices[0];
            if (first.TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            if (first.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? string.Empty;
            return string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static object BuildTags(object listing)
    {
        string serialized = JsonSerializer.Serialize(listing, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(serialized);
        var models = new List<object>();
        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in data.EnumerateArray())
            {
                string id = entry.TryGetProperty("id", out JsonElement idElement)
                    ? idElement.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0) continue;
                models.Add(new
                {
                    name = id,
                    model = id,
                    modified_at = DateTime.UtcNow.ToString("o"),
                    size = 0L,
                    digest = string.Empty,
                    details = new { family = "parux", format = "gguf", parameter_size = string.Empty }
                });
            }
        }
        return new { models };
    }

    private static string ParuxVersion() =>
        typeof(OllamaCompatibilityEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static Task<string?> ReadBodyAsync(HttpRequest request, HttpResponse response, CancellationToken ct) =>
        CompatRequestBody.ReadAsync(request, async (status, message) =>
        {
            response.StatusCode = status;
            await response.WriteAsJsonAsync(new { error = message, parux_ollama_compatibility = CompatibilitySchemaVersion }, cancellationToken: ct);
        }, ct);

    /// <summary>Ollama's own error shape is {"error":"message"}; the provider's OpenAI-shaped body is reduced to its message.</summary>
    private static string OllamaErrorJson(string upstreamBody) =>
        new JsonObject { ["error"] = ExtractErrorMessage(upstreamBody) }.ToJsonString();

    private static bool IsJsonObject(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadString(string body, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty(property, out JsonElement element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool? ReadBool(string body, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(property, out JsonElement element)) return null;
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static bool? ReadThinkBoolean(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("think", out JsonElement element)) return null;
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static string RequireString(JsonObject obj, string name)
    {
        string? value = ReadOptionalString(obj, name);
        if (string.IsNullOrWhiteSpace(value)) throw new OllamaCompatibilityException("'" + name + "' must be a non-empty string");
        return value;
    }

    private static string? ReadOptionalString(JsonObject obj, string name)
    {
        if (obj[name] == null) return null;
        if (obj[name] is JsonValue value && value.TryGetValue(out string? text)) return text;
        throw new OllamaCompatibilityException("'" + name + "' must be a string");
    }

    private static OllamaCompatibilityException Unsupported(string feature, string? guidance = null) =>
        new("unsupported Ollama feature: " + feature + (string.IsNullOrWhiteSpace(guidance) ? string.Empty : "; " + guidance));

    internal sealed class OllamaCompatibilityException : Exception
    {
        internal OllamaCompatibilityException(string message) : base(message) { }
    }
}
