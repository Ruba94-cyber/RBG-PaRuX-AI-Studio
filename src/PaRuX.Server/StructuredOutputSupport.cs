using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Engine-neutral constrained-output request. Product layers provide JSON Schema here and backend
/// adapters decide whether they can truthfully honor it. This type deliberately contains no
/// llama.cpp/OpenAI/ExLlama-specific field names.
/// </summary>
public sealed record LocalStructuredOutputRequest(
    string Name,
    string JsonSchema,
    bool Strict = true)
{
    public const int MaximumSchemaCharacters = 64_000;

    public LocalStructuredOutputRequest Validate()
    {
        string name = (Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64 || !StructuredOutputJson.NamePattern.IsMatch(name))
            throw new InvalidDataException("Structured-output name must be 1..64 characters using letters, digits, '_' or '-'.");

        string schema = (JsonSchema ?? string.Empty).Trim();
        if (schema.Length is < 2 or > MaximumSchemaCharacters)
            throw new InvalidDataException($"Structured-output JSON Schema must be 2..{MaximumSchemaCharacters} characters.");

        try
        {
            JsonNode? parsed = JsonNode.Parse(schema);
            if (parsed is not JsonObject)
                throw new InvalidDataException("Structured-output JSON Schema root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Structured-output JSON Schema is invalid JSON.", ex);
        }

        return this with { Name = name, JsonSchema = schema };
    }
}

/// <summary>
/// Provider mapping for schema-constrained generation. The public contract stays engine-neutral;
/// only this boundary emits OpenAI-compatible response_format vocabulary used by current
/// llama-server and probed USER_MANAGED providers.
/// </summary>
public static class StructuredOutputJson
{
    internal static readonly Regex NamePattern = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    public static JsonObject BuildOpenAiResponseFormat(LocalStructuredOutputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LocalStructuredOutputRequest validated = request.Validate();
        JsonNode schema = JsonNode.Parse(validated.JsonSchema)!
            .DeepClone();

        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = validated.Name,
                ["strict"] = validated.Strict,
                ["schema"] = schema
            }
        };
    }

    public static bool IsValidJsonPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            using JsonDocument _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
