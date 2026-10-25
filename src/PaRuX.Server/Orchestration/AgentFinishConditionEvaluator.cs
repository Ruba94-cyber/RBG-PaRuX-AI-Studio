using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Machine-checkable completion predicates evaluated only after the independent Verifier has
/// accepted the model result. The grammar is deliberately non-executable and bounded.
/// </summary>
public static class AgentFinishConditionEvaluator
{
    public sealed record Evaluation(bool Satisfied, string Reason);

    public static Evaluation Evaluate(AgentFinishConditionDto? condition, string? resultJson)
    {
        if (condition == null) return new Evaluation(true, "No finish condition was requested.");
        string content = ExtractResponse(resultJson);
        string type = (condition.Type ?? string.Empty).Trim().ToLowerInvariant();
        StringComparison comparison = condition.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return type switch
        {
            AgentFinishConditionType.NonEmpty =>
                string.IsNullOrWhiteSpace(content)
                    ? new Evaluation(false, "Verified result is empty.")
                    : new Evaluation(true, "Verified result is non-empty."),

            AgentFinishConditionType.Contains =>
                content.Contains(condition.Value ?? string.Empty, comparison)
                    ? new Evaluation(true, "Verified result contains the required value.")
                    : new Evaluation(false, "Verified result does not contain the required value."),

            AgentFinishConditionType.Equals =>
                string.Equals(content.Trim(), (condition.Value ?? string.Empty).Trim(), comparison)
                    ? new Evaluation(true, "Verified result equals the required value.")
                    : new Evaluation(false, "Verified result does not equal the required value."),

            AgentFinishConditionType.JsonPropertyExists => EvaluateJsonProperty(content, condition.Value),
            _ => new Evaluation(false, "Unsupported finish condition type.")
        };
    }

    private static Evaluation EvaluateJsonProperty(string content, string? propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return new Evaluation(false, "JSON property path is empty.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement current = document.RootElement;
            foreach (string segment in propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return new Evaluation(false, "Required JSON property does not exist: " + propertyPath);
            }
            return new Evaluation(true, "Required JSON property exists: " + propertyPath);
        }
        catch (JsonException)
        {
            return new Evaluation(false, "Verified result is not valid JSON for json-property-exists.");
        }
    }

    private static string ExtractResponse(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("response", out JsonElement response)
                && response.ValueKind == JsonValueKind.String)
                return response.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        return resultJson;
    }
}
