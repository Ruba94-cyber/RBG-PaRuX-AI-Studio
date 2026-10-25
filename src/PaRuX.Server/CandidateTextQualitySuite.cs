using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Versioned, objective smoke check for one narrow text capability.</summary>
internal static class CandidateTextQualitySuite
{
    internal const string Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability;
    internal const string Id = CandidateEvaluationScope.ChatInstructionFollowingSuiteId;
    internal const int Version = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion;
    internal const string ProfileId = CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId;
    internal const int ProfileVersion = CandidateEvaluationScope.ChatInstructionFollowingQualityProfileVersion;
    internal const string SystemPrompt = "Follow the user's instruction exactly. Return only the requested answer.";
    internal const int Seed = 17;
    internal const int MaxTokens = 48;
    internal const string RequestConfiguration = "temperature=0|seed=17|max-tokens=48|stream=false";

    internal sealed record Case(string Id, string Prompt, string Expected, bool Critical, bool Json,
        string? SystemPrompt = null, int? SeedOverride = null, int? MaxTokensOverride = null,
        string Category = "instruction-following", bool ProductionRequestParity = false);

    internal static readonly IReadOnlyList<Case> Cases = Array.AsReadOnly(new[]
    {
        new Case("literal", "Reply with exactly PARUX in uppercase. No other text.", "PARUX", true, false),
        new Case("arithmetic", "Compute 3 + 4. Reply with the digit only.", "7", true, false),
        new Case("selection", "From red, blue, green, reply with the second word only.", "blue", false, false),
        new Case("json", "Return only a JSON object with boolean ok=true and integer count=2. No other keys.", "{\"ok\":true,\"count\":2}", true, true)
    });

    // Canonical lines deliberately include validator type, criticality, prompts and expected answers.
    internal static string Sha256 { get; } = ComputeHash();

    private static string ComputeHash()
    {
        IEnumerable<string> lines = new[] { Capability, Id, Version.ToString(), SystemPrompt }
            .Concat(Cases.Select(c => string.Join("|", c.Id, c.Critical ? "critical" : "ordinary",
                c.Json ? "json-strict" : "exact", c.Prompt, c.Expected)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))
            .ToLowerInvariant();
    }

    internal static string WorkloadFingerprint { get; } = "quality-workload-sha256:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Sha256 + "\n" + RequestConfiguration))).ToLowerInvariant();

    // This fingerprint binds the exact prompt and generation settings. It is intentionally
    // separate from the environment workload fingerprint so the profile scope is explicit.
    internal static string ProfileSha256 { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(SystemPrompt + "\n" + RequestConfiguration))).ToLowerInvariant();

    internal static bool Validate(Case item, string content)
    {
        if (!item.Json) return content.Trim() == item.Expected;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2) return false;
            return root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("count", out JsonElement count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out int value) && value == 2;
        }
        catch (JsonException) { return false; }
    }
}
