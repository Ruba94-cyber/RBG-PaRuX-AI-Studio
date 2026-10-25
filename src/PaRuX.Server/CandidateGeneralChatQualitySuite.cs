using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Frozen representative general-chat qualification suite. It is separate from the 4-case
/// instruction-following smoke and is scoped only to standard × balanced routing.
/// </summary>
internal static class CandidateGeneralChatQualitySuite
{
    internal const string Capability = CandidateEvaluationScope.GeneralChatCapability;
    internal const string Id = CandidateEvaluationScope.GeneralChatSuiteId;
    internal const int Version = CandidateEvaluationScope.GeneralChatSuiteVersion;
    internal const string ProfileId = CandidateEvaluationScope.GeneralChatQualityProfileId;
    internal const int ProfileVersion = CandidateEvaluationScope.GeneralChatQualityProfileVersion;
    internal const string RouteRole = CandidateEvaluationScope.GeneralChatRouteRole;
    internal const string AutoProfile = CandidateEvaluationScope.GeneralChatAutoProfile;
    internal const int MinimumSuccessRateBasisPoints = CandidateEvaluationScope.GeneralChatMinimumSuccessRateBasisPoints;
    internal const int MinimumReliabilityBasisPoints = CandidateEvaluationScope.GeneralChatMinimumReliabilityBasisPoints;
    internal const int MinimumFreshProcessRuns = CandidateEvaluationScope.GeneralChatMinimumFreshProcessRuns;
    internal const int MinimumPassCount = 27;
    internal const int MinimumPassesPerCategory = CandidateEvaluationScope.GeneralChatMinimumPassesPerCategory;
    internal const int Seed = 29;
    internal const int MaxTokens = 96;
    // v3: production request parity (see CandidateRequestParity). The frozen per-case reasoning
    // decisions are part of this string, so any policy change re-versions the suite identity.
    // Lazy: Cases is declared below, and static initializers run in textual order.
    internal static string RequestConfiguration => RequestConfigurationValue.Value;
    private static readonly Lazy<string> RequestConfigurationValue = new(() => "request-parity=" + CandidateRequestParity.Version
        + "|sampling=launch-defaults|seed=none|answer-max-tokens=96|max-tokens=answer+thinking-budget|stream=false"
        + "|reasoning=" + CandidateRequestParity.Describe(Cases.Select(item => item.Prompt)));
    internal const string SystemPrompt = "Answer accurately and concisely. Follow the requested format. Use only supplied context when the question is grounded in context.";
    // Bump this revision whenever Validate, NormalizeLabel or JsonEqual changes meaning. Evidence
    // from an older evaluator must not be reinterpreted under the same suite/workload identity.
    internal const int EvaluatorSemanticsRevision = 2;
    internal const string EvaluatorSemanticsDescription =
        "input=known-case+reject-null-or-whitespace;exact=ordinal;" +
        "normalized=NFC+trim+strip-one-final-period-exclamation-question+collapse-whitespace+ordinal-ignore-case;" +
        "json=parse+same-kind+objects-exact-unique-keys-order-independent+arrays-order-dependent+" +
        "strings-ordinal+numbers-invariant-decimal-value+true-false-null-same-token;" +
        "ocean=one-physical-line+optional-the+normalized-label;" +
        "colors=one-physical-line+one-terminal-mark+exactly-two-comma-fields+trim+ordinal-ignore-case;" +
        "affirmative=normalized-single-yes;french-days=normalized-sept-or-7";

    internal enum Category { InstructionFollowing, Grounding, Multilingual, StructuredAnswer, BasicReasoning }
    internal enum ValidationMode { ExactText, NormalizedText, Json, OceanOneLine, CommaSeparatedColors, Affirmative, FrenchDayCount }
    internal sealed record Case(string Id, Category Category, string Prompt, string Expected,
        bool Mandatory, ValidationMode Mode = ValidationMode.NormalizedText)
    {
        internal bool Json => Mode == ValidationMode.Json;
    }

    // These cases are frozen qualification checks, not optimizer-training examples. Any changes
    // require a new suite version and hash; do not tune prompts against candidate outputs.
    internal static readonly IReadOnlyList<Case> Cases = Array.AsReadOnly(new[]
    {
        new Case("if-exact-token", Category.InstructionFollowing, "Reply with exactly the word amber in lowercase. No other text.", "amber", true, ValidationMode.ExactText),
        new Case("if-one-line", Category.InstructionFollowing, "Name the largest ocean. Reply with one line and no explanation.", "Pacific Ocean", false, ValidationMode.OceanOneLine),
        new Case("if-order", Category.InstructionFollowing, "Repeat these words in reverse order only: cedar, birch, maple.", "maple, birch, cedar", false, ValidationMode.ExactText),
        new Case("if-count", Category.InstructionFollowing, "Give exactly two colors separated by a comma: red then violet.", "red, violet", false, ValidationMode.CommaSeparatedColors),
        new Case("if-avoid-extra", Category.InstructionFollowing, "Answer yes or no only: Is 12 an even number?", "yes", false, ValidationMode.Affirmative),
        new Case("if-brevity", Category.InstructionFollowing, "In one word, name the season after spring.", "summer", false),

        new Case("ground-city", Category.Grounding, "Context: The archive is stored in Bergen. Question: In which city is the archive stored? Reply with the city only.", "Bergen", true),
        new Case("ground-date", Category.Grounding, "Context: The trial begins on 14 May. Question: On what date does the trial begin? Reply with the date only.", "14 May", false),
        new Case("ground-unknown", Category.Grounding, "Context: The parcel contains two books. Question: What is the parcel's tracking number? If the context does not say, reply unknown.", "unknown", false),
        new Case("ground-attribute", Category.Grounding, "Context: Mira chose the copper key; Oren chose the glass key. Question: Which key did Oren choose? Reply with the material only.", "glass", false),
        new Case("ground-count", Category.Grounding, "Context: The team has 4 analysts and 3 designers. Question: How many designers are on the team? Reply with the number only.", "3", false),
        new Case("ground-no-inference", Category.Grounding, "Context: The cafe is beside the library. Question: Is the cafe open today? If the context does not say, reply unknown.", "unknown", false),

        new Case("multi-spanish", Category.Multilingual, "Responde en español con el nombre de la capital de Perú y nada más.", "Lima", true),
        new Case("multi-french", Category.Multilingual, "Réponds en français avec le nombre de jours dans une semaine, rien d'autre.", "sept", false, ValidationMode.FrenchDayCount),
        new Case("multi-italian", Category.Multilingual, "In italiano, traduci 'good morning'. Rispondi solo con la traduzione.", "buongiorno", false),
        new Case("multi-german", Category.Multilingual, "Antworte auf Deutsch nur mit der Farbe des Himmels an einem klaren Tag.", "blau", false),
        new Case("multi-portuguese", Category.Multilingual, "Em português, escreva apenas o plural de 'flor'.", "flores", false),
        new Case("multi-dutch", Category.Multilingual, "Antwoord in het Nederlands met alleen het woord voor 'water'.", "water", false),

        new Case("struct-object", Category.StructuredAnswer, "Return only JSON with exactly these fields: ok=true and count=3.", "{\"ok\":true,\"count\":3}", true, ValidationMode.Json),
        new Case("struct-array", Category.StructuredAnswer, "Return only a JSON array of the integers 2, 4, 6 in that order.", "[2,4,6]", false, ValidationMode.Json),
        new Case("struct-string", Category.StructuredAnswer, "Return only JSON with exactly one field named color whose string value is teal.", "{\"color\":\"teal\"}", false, ValidationMode.Json),
        new Case("struct-nested", Category.StructuredAnswer, "Return only JSON with fields item='pen' and meta.count=2.", "{\"item\":\"pen\",\"meta\":{\"count\":2}}", false, ValidationMode.Json),
        new Case("struct-bool", Category.StructuredAnswer, "Return only a JSON object with fields ready=false and retries=0.", "{\"ready\":false,\"retries\":0}", false, ValidationMode.Json),
        new Case("struct-no-extra", Category.StructuredAnswer, "Return only JSON with exactly one field: code with integer value 8.", "{\"code\":8}", false, ValidationMode.Json),

        new Case("reason-sum", Category.BasicReasoning, "A box has 5 red and 2 blue balls. How many balls are there? Reply with the number only.", "7", true),
        new Case("reason-sequence", Category.BasicReasoning, "What number comes next: 3, 6, 9, 12? Reply with the number only.", "15", false),
        new Case("reason-comparison", Category.BasicReasoning, "A 2 kg parcel and a 5 kg parcel are weighed. Which is heavier? Reply with its weight only.", "5 kg", false),
        new Case("reason-logic", Category.BasicReasoning, "All tulips in a tray are yellow. A flower is a tulip from that tray. What color is it? Reply with the color only.", "yellow", false),
        new Case("reason-time", Category.BasicReasoning, "A meeting starts at 10:00 and lasts 90 minutes. What time does it end? Reply HH:MM only.", "11:30", false),
        new Case("reason-remainder", Category.BasicReasoning, "Divide 17 apples equally among 5 people. How many apples remain? Reply with the number only.", "2", false)
    });

    internal static string Sha256 { get; } = ComputeSha256(EvaluatorSemanticsRevision, EvaluatorSemanticsDescription);

    internal static string WorkloadFingerprint => "quality-workload-sha256:" + Hash(Sha256 + "\n" + RequestConfiguration);
    internal static string ProfileSha256 => Hash(SystemPrompt + "\n" + RequestConfiguration);

    internal static string ComputeSha256(int evaluatorSemanticsRevision, string evaluatorSemanticsDescription)
    {
        string[] scope =
        {
            Capability, Id, Version.ToString(System.Globalization.CultureInfo.InvariantCulture), ProfileId,
            ProfileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), RouteRole, AutoProfile,
            evaluatorSemanticsRevision.ToString(CultureInfo.InvariantCulture), evaluatorSemanticsDescription,
            MinimumSuccessRateBasisPoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinimumReliabilityBasisPoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinimumFreshProcessRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinimumPassCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinimumPassesPerCategory.ToString(System.Globalization.CultureInfo.InvariantCulture), SystemPrompt,
            RequestConfiguration
        };
        IEnumerable<string> caseLines = Cases.Select(c => string.Join("|", c.Id, c.Category, c.Mandatory,
            c.Mode, c.Prompt, c.Expected));
        return Hash(string.Join("\n", scope.Concat(caseLines)));
    }

    internal static bool Validate(Case item, string content)
    {
        if (!Cases.Contains(item) || string.IsNullOrWhiteSpace(content)) return false;
        if (item.Mode == ValidationMode.ExactText) return content == item.Expected;
        if (item.Mode == ValidationMode.NormalizedText)
            return string.Equals(NormalizeLabel(content), NormalizeLabel(item.Expected),
                StringComparison.OrdinalIgnoreCase);
        if (item.Mode == ValidationMode.OceanOneLine)
        {
            if (!IsOnePhysicalLine(content)) return false;
            string answer = NormalizeLabel(content);
            return string.Equals(answer, item.Expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(answer, "The " + item.Expected, StringComparison.OrdinalIgnoreCase);
        }
        if (item.Mode == ValidationMode.CommaSeparatedColors)
        {
            if (!IsOnePhysicalLine(content)) return false;
            string[] colors = NormalizeLabel(content).Split(',');
            return colors.Length == 2
                && string.Equals(colors[0].Trim(), "red", StringComparison.OrdinalIgnoreCase)
                && string.Equals(colors[1].Trim(), "violet", StringComparison.OrdinalIgnoreCase);
        }
        if (item.Mode == ValidationMode.Affirmative)
            return string.Equals(NormalizeLabel(content), item.Expected, StringComparison.OrdinalIgnoreCase);
        if (item.Mode == ValidationMode.FrenchDayCount)
        {
            string answer = NormalizeLabel(content);
            return string.Equals(answer, item.Expected, StringComparison.OrdinalIgnoreCase) || answer == "7";
        }
        try
        {
            using JsonDocument actual = JsonDocument.Parse(content);
            using JsonDocument expected = JsonDocument.Parse(item.Expected);
            return JsonEqual(actual.RootElement, expected.RootElement);
        }
        catch (JsonException) { return false; }
    }

    internal static bool MeetsSingleRunFloor(IReadOnlyDictionary<string, bool> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count != Cases.Count || Cases.Any(item => !outcomes.ContainsKey(item.Id))) return false;
        return outcomes.Count(pair => pair.Value) >= MinimumPassCount
            && Cases.GroupBy(item => item.Category).All(group => group.Count(item => outcomes[item.Id]) >= MinimumPassesPerCategory)
            && Cases.Where(item => item.Mandatory).All(item => outcomes[item.Id]);
    }

    private static bool JsonEqual(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        if (actual.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] actualProperties = actual.EnumerateObject().ToArray();
            JsonProperty[] expectedProperties = expected.EnumerateObject().ToArray();
            return actualProperties.Length == expectedProperties.Length
                && actualProperties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == actualProperties.Length
                && expectedProperties.All(property => actual.TryGetProperty(property.Name, out JsonElement value)
                    && JsonEqual(value, property.Value));
        }
        if (actual.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] actualItems = actual.EnumerateArray().ToArray();
            JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
            return actualItems.Length == expectedItems.Length
                && actualItems.Zip(expectedItems).All(pair => JsonEqual(pair.First, pair.Second));
        }
        return actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString() == expected.GetString(),
            JsonValueKind.Number => decimal.TryParse(actual.GetRawText(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal actualNumber)
                && decimal.TryParse(expected.GetRawText(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal expectedNumber)
                && actualNumber == expectedNumber,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => actual.GetRawText() == expected.GetRawText(),
            _ => false
        };
    }

    private static string NormalizeLabel(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length > 0 && normalized[^1] is '.' or '!' or '?')
            normalized = normalized[..^1].TrimEnd();
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsOnePhysicalLine(string value) =>
        value.IndexOfAny(['\r', '\n', '\u0085', '\u2028', '\u2029']) < 0;

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
