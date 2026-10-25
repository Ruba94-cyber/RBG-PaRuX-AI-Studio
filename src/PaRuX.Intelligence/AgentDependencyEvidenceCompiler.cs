using System.Text;
using System.Text.Json;

namespace PaRuX.Intelligence;

/// <summary>
/// Compiles direct predecessor results into a bounded, path-free evidence handoff for the next
/// agent in a DAG. Dependency ordering alone is not enough: a review/test task that starts after a
/// coding task must know what the predecessor actually produced. This compiler carries only durable
/// result evidence, never a predecessor transcript or filesystem path.
/// </summary>
public static class AgentDependencyEvidenceCompiler
{
    public const int DefaultMaximumCharacters = 20_000;
    public const int MaximumCharactersPerDependency = 8_000;

    public sealed record Input(
        string Id,
        string? Role,
        string? VerificationStatus,
        string? ResultJson);

    public sealed record Compiled(string PromptText, int DependencyCount, int UsedCharacters);

    private static readonly string[] SafeResultFields =
    {
        "response", "summary", "diff", "goalReported", "stepsTaken", "branch",
        "analysisMode", "workspaceAuthority", "repairAttempt", "checkpointSessionId",
        "sandbox", "capabilitiesUsed", "parentTaskId", "inheritedWorkspaceFromTaskId"
    };

    public static Compiled Compile(IEnumerable<Input>? dependencies, int maximumCharacters = DefaultMaximumCharacters)
    {
        int limit = Math.Clamp(maximumCharacters, 1_000, 24_000);
        var builder = new StringBuilder();
        int included = 0;

        foreach (Input dependency in dependencies ?? Enumerable.Empty<Input>())
        {
            if (string.IsNullOrWhiteSpace(dependency.Id)) continue;
            string evidence = ExtractSafeEvidence(dependency.ResultJson);
            if (string.IsNullOrWhiteSpace(evidence)) continue;

            string header = "[DEPENDENCY RESULT " + dependency.Id.Trim()
                + " · role=" + (string.IsNullOrWhiteSpace(dependency.Role) ? "unknown" : dependency.Role!.Trim())
                + (string.IsNullOrWhiteSpace(dependency.VerificationStatus)
                    ? string.Empty : " · verification=" + dependency.VerificationStatus!.Trim())
                + "]\n";

            string separator = builder.Length > 0 ? Environment.NewLine + "---" + Environment.NewLine : string.Empty;
            int remaining = limit - builder.Length - separator.Length;
            if (remaining <= header.Length + 64) break;
            int evidenceLimit = Math.Min(MaximumCharactersPerDependency, remaining - header.Length - 2);
            if (evidence.Length > evidenceLimit)
                evidence = evidence[..Math.Max(1, evidenceLimit - 28)] + "\n… (dependency evidence cut)";

            builder.Append(separator).Append(header).Append(evidence.Trim());
            included++;
            if (builder.Length >= limit) break;
        }

        string text = builder.ToString();
        return new Compiled(text, included, text.Length);
    }

    internal static string ExtractSafeEvidence(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return string.Empty;
        string raw = resultJson.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return Bound(root.GetString() ?? string.Empty, MaximumCharactersPerDependency);
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            var output = new StringBuilder();
            foreach (string field in SafeResultFields)
            {
                if (!root.TryGetProperty(field, out JsonElement value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                string rendered = value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.GetRawText();
                if (string.IsNullOrWhiteSpace(rendered)) continue;
                if (output.Length > 0) output.AppendLine();
                output.Append(field).Append(": ").Append(rendered.Trim());
                if (output.Length >= MaximumCharactersPerDependency) break;
            }
            return Bound(output.ToString(), MaximumCharactersPerDependency);
        }
        catch (JsonException)
        {
            // Old task records may predate structured ResultJson. Keep their bounded textual
            // evidence rather than throwing, but scrub obvious path-bearing labels so a legacy
            // result cannot reintroduce workstation paths into a cross-agent prompt.
            if (raw.Contains("worktree", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("workspace", StringComparison.OrdinalIgnoreCase))
                return "Legacy predecessor result exists but was withheld because it contains path-bearing fields.";
            return Bound(raw, MaximumCharactersPerDependency);
        }
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..Math.Max(1, maximum - 24)] + "\n… (evidence truncated)";
}
