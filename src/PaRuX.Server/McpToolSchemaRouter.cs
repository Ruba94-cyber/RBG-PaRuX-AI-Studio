using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Prompt-aware, deterministic MCP schema selection. This runs only after Skill allowlists have
/// already been applied by McpClientRegistry, so it can narrow tool exposure but can never widen
/// a skill's permissions. The purpose is latency/context efficiency: large MCP installations no
/// longer force every tool schema into every local-model turn.
///
/// Correctness wins over token savings. Small tool sets are kept intact, and when there is no
/// meaningful lexical routing signal the original schemas are returned unchanged rather than
/// guessing and hiding a tool the model may need.
/// </summary>
public static class McpToolSchemaRouter
{
    public const int DefaultActivationToolCount = 8;
    public const int DefaultMaximumSelectedTools = 12;
    public const int DefaultMaximumSelectedCharacters = 8_000;

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}][\p{L}\p{N}_-]*", RegexOptions.Compiled);
    private static readonly Regex CamelBoundaryRegex = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "into", "use", "using", "tool", "tools",
        "please", "can", "you", "vorrei", "puoi", "con", "per", "una", "uno", "del", "della", "delle",
        "dei", "gli", "che", "come", "questo", "questa", "fai", "fare", "usa", "usare"
    };

    public sealed record Decision(
        JsonArray Schemas,
        bool Routed,
        int OriginalCount,
        int SelectedCount,
        int SelectedCharacters,
        string Reason);

    private sealed record Candidate(JsonNode Node, int OriginalIndex, int Score, int Length, string Name);

    public static Decision Route(
        string? query,
        JsonArray schemas,
        int activationToolCount = DefaultActivationToolCount,
        int maximumSelectedTools = DefaultMaximumSelectedTools,
        int maximumSelectedCharacters = DefaultMaximumSelectedCharacters)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        long started = Stopwatch.GetTimestamp();
        activationToolCount = Math.Max(1, activationToolCount);
        maximumSelectedTools = Math.Max(1, maximumSelectedTools);
        maximumSelectedCharacters = Math.Max(256, maximumSelectedCharacters);

        int originalCharacters = schemas.Sum(SerializedLength);
        if (schemas.Count <= activationToolCount || schemas.Count <= maximumSelectedTools && originalCharacters <= maximumSelectedCharacters)
        {
            return Record(new Decision(Clone(schemas), false, schemas.Count, schemas.Count, originalCharacters,
                "Tool set is already small enough; preserving every allowed MCP schema."), originalCharacters, started);
        }

        HashSet<string> queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
        {
            return Record(new Decision(Clone(schemas), false, schemas.Count, schemas.Count, originalCharacters,
                "No useful routing tokens were present; preserving every allowed MCP schema."), originalCharacters, started);
        }

        var candidates = new List<Candidate>();
        int bestScore = 0;
        for (int index = 0; index < schemas.Count; index++)
        {
            JsonNode? schema = schemas[index];
            if (schema == null) continue;
            string name = schema["function"]?["name"]?.GetValue<string>() ?? string.Empty;
            string description = schema["function"]?["description"]?.GetValue<string>() ?? string.Empty;
            int score = Score(query ?? string.Empty, queryTokens, name, description);
            bestScore = Math.Max(bestScore, score);
            candidates.Add(new Candidate(schema, index, score, SerializedLength(schema), name));
        }

        // A weak or nonexistent match is not enough evidence to hide tools. This is intentionally
        // conservative for local models whose user wording may not resemble an MCP author's names.
        if (bestScore < 8)
        {
            return Record(new Decision(Clone(schemas), false, schemas.Count, schemas.Count, originalCharacters,
                "No high-confidence MCP relevance match; preserving every allowed schema."), originalCharacters, started);
        }

        List<Candidate> ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.OriginalIndex)
            .ToList();

        var selected = new List<Candidate>();
        int usedCharacters = 0;
        foreach (Candidate candidate in ordered)
        {
            if (candidate.Score <= 0) continue;
            if (selected.Count >= maximumSelectedTools) break;
            if (usedCharacters + candidate.Length > maximumSelectedCharacters && selected.Count > 0) continue;
            selected.Add(candidate);
            usedCharacters += candidate.Length;
        }

        // Preserve a small amount of discovery headroom for tools that did not lexically match.
        // This protects chained workflows where the second tool is not obvious from the initial
        // user wording, while still avoiding the old 20-30 schema prompt fan-out.
        int discoveryFloor = Math.Min(4, maximumSelectedTools);
        foreach (Candidate candidate in candidates.OrderBy(candidate => candidate.OriginalIndex))
        {
            if (selected.Count >= discoveryFloor || selected.Count >= maximumSelectedTools) break;
            if (selected.Contains(candidate)) continue;
            if (usedCharacters + candidate.Length > maximumSelectedCharacters && selected.Count > 0) continue;
            selected.Add(candidate);
            usedCharacters += candidate.Length;
        }

        if (selected.Count == 0)
        {
            return Record(new Decision(Clone(schemas), false, schemas.Count, schemas.Count, originalCharacters,
                "Routing produced no safe selection; preserving every allowed schema."), originalCharacters, started);
        }

        // Stable score order is useful to the model: highest-confidence tools arrive first.
        var routed = new JsonArray();
        foreach (Candidate candidate in selected
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.OriginalIndex))
            routed.Add(candidate.Node.DeepClone());

        return Record(new Decision(
            routed,
            true,
            schemas.Count,
            routed.Count,
            routed.Sum(SerializedLength),
            "High-confidence lexical MCP routing reduced the tool-schema prompt while preserving discovery headroom."), originalCharacters, started);
    }

    private static Decision Record(Decision decision, int originalCharacters, long startedTimestamp)
    {
        McpToolUseTelemetry.RecordRouting(
            decision.Routed,
            decision.OriginalCount,
            decision.SelectedCount,
            originalCharacters,
            decision.SelectedCharacters,
            decision.Reason,
            (long)Math.Max(0d, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds));
        return decision;
    }

    private static int Score(string query, HashSet<string> queryTokens, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description)) return 0;
        int score = 0;
        string normalizedQuery = Normalize(query);
        string normalizedName = Normalize(CamelBoundaryRegex.Replace(name ?? string.Empty, " "));
        if (normalizedName.Length >= 3 && normalizedQuery.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))
            score += 28;

        HashSet<string> nameTokens = Tokenize(CamelBoundaryRegex.Replace(name ?? string.Empty, " "));
        HashSet<string> descriptionTokens = Tokenize(description);
        foreach (string token in queryTokens)
        {
            if (nameTokens.Contains(token)) score += 12;
            else if (descriptionTokens.Contains(token)) score += 4;
            else
            {
                // Prefix matching catches common inflections without introducing fuzzy-match noise.
                if (nameTokens.Any(candidate => PrefixRelated(token, candidate))) score += 6;
                else if (descriptionTokens.Any(candidate => PrefixRelated(token, candidate))) score += 2;
            }
        }
        return score;
    }

    private static bool PrefixRelated(string a, string b)
    {
        if (a.Length < 5 || b.Length < 5) return false;
        int common = 0;
        int max = Math.Min(a.Length, b.Length);
        while (common < max && char.ToLowerInvariant(a[common]) == char.ToLowerInvariant(b[common])) common++;
        return common >= 5;
    }

    private static HashSet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        foreach (Match match in TokenRegex.Matches(text))
        {
            string token = match.Value.Trim('_', '-').ToLowerInvariant();
            if (token.Length < 3 || StopWords.Contains(token)) continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        bool previousSpace = false;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                sb.Append(' ');
                previousSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static int SerializedLength(JsonNode? node) => node?.ToJsonString().Length ?? 0;

    private static JsonArray Clone(JsonArray schemas)
    {
        var clone = new JsonArray();
        foreach (JsonNode? schema in schemas) clone.Add(schema?.DeepClone());
        return clone;
    }
}
