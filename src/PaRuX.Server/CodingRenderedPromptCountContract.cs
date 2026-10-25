using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// A desktop-only, side-effect-free prompt-count request. The Core, not a client,
/// chooses parux-coding and uses the same gateway preparation as actual inference.
/// Token counting may *load* the selected managed coding model but NEVER generates
/// or executes a model action. Do not log the supplied prompt or schema.
/// </summary>
public sealed class CodingRenderedPromptCountRequest
{
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
}

public static class CodingRenderedPromptCountContract
{
    public const int MaximumPromptCharacters = 250_000;
    public const int MaximumSchemaCharacters = 15_000;

    public static bool IsValid(CodingRenderedPromptCountRequest? request) =>
        request != null
        && !string.IsNullOrWhiteSpace(request.SystemPrompt)
        && !string.IsNullOrWhiteSpace(request.UserPrompt)
        && !string.IsNullOrWhiteSpace(request.JsonSchema)
        && !string.IsNullOrWhiteSpace(request.SchemaName)
        && request.SystemPrompt.Length + (long)request.UserPrompt.Length <= MaximumPromptCharacters
        && request.JsonSchema.Length <= MaximumSchemaCharacters
        && request.SchemaName.Length <= 64
        && request.SchemaName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static string BuildProviderPayload(CodingRenderedPromptCountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValid(request)) throw new ArgumentException("Invalid bounded coding prompt.", nameof(request));
        JsonNode? parsedSchema;
        try { parsedSchema = JsonNode.Parse(request.JsonSchema); }
        catch (JsonException)
        {
            // System.Text.Json reports malformed input with an internal JsonException subtype.
            // Surface the plain JsonException contract, without echoing any schema text.
            throw new JsonException("Invalid JSON schema.");
        }
        JsonNode schema = parsedSchema
            ?? throw new ArgumentException("Invalid JSON schema.", nameof(request));
        if (schema is not JsonObject)
            throw new ArgumentException("Action schema must be a JSON object.", nameof(request));
        var obj = new JsonObject
        {
            ["model"] = "parux-coding",
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = schema
                }
            }
        };
        return obj.ToJsonString();
    }
}
