using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Validates and forwards the OpenAI sampling/length fields of the app chat route. These were
/// previously dropped silently; now each is either sent to the runtime or rejected with a message.
/// </summary>
internal static class ChatSamplingOptions
{
    public const int MaximumStopSequences = 4;

    /// <summary>The effective token cap: max_completion_tokens wins over the deprecated max_tokens.</summary>
    public static int? EffectiveMaxTokens(ChatCompletionRequest request) =>
        request.MaxCompletionTokens ?? request.MaxTokens;

    /// <summary>Returns null when valid, otherwise a user-facing message naming the offending field.</summary>
    public static string? Validate(ChatCompletionRequest request)
    {
        if (request.MaxTokens is <= 0) return "\"max_tokens\" must be a positive integer.";
        if (request.MaxCompletionTokens is <= 0) return "\"max_completion_tokens\" must be a positive integer.";
        if (request.Temperature is { } t && (double.IsNaN(t) || t < 0 || t > 2))
            return "\"temperature\" must be between 0 and 2.";
        if (request.TopP is { } p && (double.IsNaN(p) || p < 0 || p > 1))
            return "\"top_p\" must be between 0 and 1.";
        if (request.Stop is { } stop)
        {
            switch (stop.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                case JsonValueKind.String:
                    break;
                case JsonValueKind.Array:
                    if (stop.GetArrayLength() > MaximumStopSequences)
                        return "\"stop\" accepts at most " + MaximumStopSequences + " sequences.";
                    foreach (JsonElement item in stop.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.String)
                            return "\"stop\" must be a string or an array of strings.";
                    break;
                default:
                    return "\"stop\" must be a string or an array of strings.";
            }
        }
        return null;
    }

    public static void Apply(JsonObject payload, ChatCompletionRequest request)
    {
        if (EffectiveMaxTokens(request) is { } max) payload["max_tokens"] = max;
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["top_p"] = topP;
        if (request.Seed is { } seed) payload["seed"] = seed;
        if (request.Stop is { ValueKind: JsonValueKind.String or JsonValueKind.Array } stop)
            payload["stop"] = JsonNode.Parse(stop.GetRawText());
    }
}
