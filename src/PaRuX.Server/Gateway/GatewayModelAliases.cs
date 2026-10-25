using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server.Gateway;

/// <summary>
/// Virtual model ids exposed identically through every protocol adapter (OpenAI Chat, OpenAI
/// Responses, Anthropic Messages). A virtual id names a routing intent, not a physical model:
/// clients keep "parux-auto" while PaRuX chooses and swaps the physical model behind it.
/// </summary>
public static class GatewayModelAliases
{
    public const string Auto = "parux-auto";
    public const string Code = "parux-code";
    public const string Reasoning = "parux-reasoning";
    public const string Fast = "parux-fast";
    public const string Vision = "parux-vision";

    /// <summary>The virtual ids listed by /v1/models, with the role each one pins (null = automatic).</summary>
    public static readonly IReadOnlyList<(string Id, string? Role, string Description)> Virtual = new[]
    {
        (Auto, (string?)null, "PaRuX automatic routing: the best local model for the request and this hardware"),
        (Code, (string?)"coding", "Best measured local coding/tool-use model"),
        (Reasoning, (string?)"reasoning", "Local reasoning model"),
        (Fast, (string?)"fast", "Fastest capable local model"),
        (Vision, (string?)"vision", "Local vision-capable model")
    };

    public sealed record Resolution(string? Role, bool Automatic, string Source);

    /// <summary>
    /// Maps a client-supplied model id to a PaRuX role. Returns null only for an id PaRuX cannot
    /// honour. Vendor ids that agent clients send by default (claude-*, gpt-*, o*-series, codex-*)
    /// resolve to <paramref name="vendorDefault"/> so a client works without per-model configuration;
    /// the requested id is still echoed back to the client unchanged.
    /// </summary>
    public static Resolution? Resolve(string? modelId, string vendorDefault = Auto)
    {
        string id = (modelId ?? string.Empty).Trim();
        if (id.Length == 0) return new Resolution("standard", false, "default");

        foreach ((string virtualId, string? role, _) in Virtual)
            if (string.Equals(id, virtualId, StringComparison.OrdinalIgnoreCase))
                return new Resolution(role, role == null, "alias:" + virtualId);

        string bare = id.StartsWith("parux-", StringComparison.OrdinalIgnoreCase) ? id["parux-".Length..] : id;
        string? namedRole = LocalAiPaths.NormalizeTextRole(bare);
        if (namedRole != null) return new Resolution(namedRole, false, "role:" + namedRole);

        if (IsVendorModelId(id) && !string.Equals(vendorDefault, id, StringComparison.OrdinalIgnoreCase))
        {
            Resolution? fallback = Resolve(vendorDefault, Auto);
            return fallback == null ? null : fallback with { Source = "vendor-default:" + vendorDefault };
        }
        return null;
    }

    internal static bool IsVendorModelId(string id)
    {
        string lower = id.ToLowerInvariant();
        return lower.StartsWith("claude", StringComparison.Ordinal)
            || lower.StartsWith("gpt-", StringComparison.Ordinal)
            || lower.StartsWith("codex", StringComparison.Ordinal)
            || lower.StartsWith("chatgpt", StringComparison.Ordinal)
            || (lower.Length >= 2 && lower[0] == 'o' && char.IsDigit(lower[1]));
    }

    /// <summary>
    /// For "parux-auto": a request that declares tools comes from an agent loop (Claude Code, Codex,
    /// OpenCode), so it routes to the measured coding/tool-use model rather than a chat model that
    /// writes good prose but unreliable tool calls. Anything else goes to Core's automatic router
    /// with the latest user text.
    /// </summary>
    public static RouteRequest AutoRouteRequest(JsonObject chatRequest)
    {
        if (chatRequest["tools"] is JsonArray { Count: > 0 })
            return new RouteRequest { Role = "coding", TaskType = "agentic-coding" };
        return new RouteRequest { Role = null, PromptText = LastUserText(chatRequest) };
    }

    internal static string? LastUserText(JsonObject chatRequest)
    {
        if (chatRequest["messages"] is not JsonArray messages) return null;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || message["role"]?.GetValue<string>() != "user") continue;
            JsonNode? content = message["content"];
            if (content is JsonValue value && value.TryGetValue(out string? text)) return Clip(text);
            if (content is JsonArray parts)
                return Clip(string.Join("\n", parts.OfType<JsonObject>()
                    .Where(part => part["type"]?.GetValue<string>() == "text")
                    .Select(part => part["text"]?.GetValue<string>() ?? string.Empty)));
        }
        return null;
    }

    private static string? Clip(string? text) => text == null ? null : text.Length <= 4000 ? text : text[..4000];
}
