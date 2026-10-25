using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateGeneralChatQualitySuiteTests
{
    [Fact]
    public void Suite_is_frozen_versioned_and_scoped_to_standard_balanced()
    {
        Assert.Equal("chat.general.v1", CandidateGeneralChatQualitySuite.Capability);
        Assert.Equal(3, CandidateGeneralChatQualitySuite.Version);
        Assert.Equal("standard", CandidateGeneralChatQualitySuite.RouteRole);
        Assert.Equal("balanced", CandidateGeneralChatQualitySuite.AutoProfile);
        Assert.Equal(30, CandidateGeneralChatQualitySuite.Cases.Count);
        Assert.Equal(5, CandidateGeneralChatQualitySuite.Cases.Select(item => item.Category).Distinct().Count());
        Assert.All(CandidateGeneralChatQualitySuite.Cases.GroupBy(item => item.Category), group =>
            Assert.Equal(6, group.Count()));
        Assert.Equal(5, CandidateGeneralChatQualitySuite.Cases.Count(item => item.Mandatory));
        Assert.Equal(5, CandidateGeneralChatQualitySuite.Cases.Where(item => item.Mandatory)
            .Select(item => item.Category).Distinct().Count());
        Assert.Equal(9000, CandidateGeneralChatQualitySuite.MinimumSuccessRateBasisPoints);
        Assert.Equal(9500, CandidateGeneralChatQualitySuite.MinimumReliabilityBasisPoints);
        Assert.Equal(5, CandidateGeneralChatQualitySuite.MinimumFreshProcessRuns);
        Assert.Equal(27, CandidateGeneralChatQualitySuite.MinimumPassCount);
        Assert.Equal(5, CandidateGeneralChatQualitySuite.MinimumPassesPerCategory);
        Assert.Equal(2, CandidateGeneralChatQualitySuite.EvaluatorSemanticsRevision);
        Assert.Equal(64, CandidateGeneralChatQualitySuite.Sha256.Length);
        Assert.Equal(64, CandidateGeneralChatQualitySuite.ProfileSha256.Length);
        Assert.StartsWith("quality-workload-sha256:", CandidateGeneralChatQualitySuite.WorkloadFingerprint);
        Assert.NotEqual(CandidateTextQualitySuite.Sha256, CandidateGeneralChatQualitySuite.Sha256);
        Assert.NotEqual(CandidateTextQualitySuite.WorkloadFingerprint, CandidateGeneralChatQualitySuite.WorkloadFingerprint);
        Assert.NotEqual(CandidateTextQualitySuite.ProfileSha256, CandidateGeneralChatQualitySuite.ProfileSha256);
    }

    [Fact]
    public void V3_request_shape_is_production_parity_with_frozen_reasoning_decisions()
    {
        string configuration = CandidateGeneralChatQualitySuite.RequestConfiguration;
        Assert.StartsWith("request-parity=" + CandidateRequestParity.Version, configuration);
        Assert.Contains("seed=none", configuration);
        Assert.DoesNotContain("temperature=0", configuration);
        Assert.Contains("|reasoning=" + CandidateRequestParity.Describe(
            CandidateGeneralChatQualitySuite.Cases.Select(item => item.Prompt)), configuration);
        Assert.StartsWith("request-parity=" + CandidateRequestParity.Version, CandidateTextPerformanceWorkload.RequestConfiguration);
        // The production adaptive policy decides per prompt; one held-out case carries a coding keyword and is
        // therefore a thinking turn in production, so the evaluator must give it the same budget.
        Assert.Equal(new CandidateRequestParity.Decision("high", 4096),
            CandidateRequestParity.Frozen(CandidateGeneralChatQualitySuite.Cases.Single(item => item.Id == "struct-no-extra").Prompt));
        Assert.Equal(new CandidateRequestParity.Decision("low", 0), CandidateRequestParity.Frozen(CandidateTextPerformanceWorkload.Prompt));
        Assert.Equal(CandidateTextPerformanceWorkload.MaxTokens, CandidateTextPerformanceWorkload.MaximumGeneratedTokens);

        var payload = new System.Text.Json.Nodes.JsonObject();
        CandidateRequestParity.Apply(payload, new CandidateRequestParity.Decision("high", 4096), 96, new[] { "low", "medium" });
        Assert.Equal(96 + 4096, (int)payload["max_tokens"]!);
        Assert.Equal(4096, (int)payload["thinking_budget_tokens"]!);
        Assert.True((bool)payload["chat_template_kwargs"]!["enable_thinking"]!);
        Assert.Equal("medium", (string)payload["chat_template_kwargs"]!["reasoning_effort"]!);
        Assert.Null(payload["temperature"]);
        Assert.Null(payload["seed"]);
    }

    [Fact]
    public void Suite_hash_changes_when_evaluator_semantics_revision_or_description_changes()
    {
        string current = CandidateGeneralChatQualitySuite.Sha256;
        Assert.Equal(current, CandidateGeneralChatQualitySuite.ComputeSha256(
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsRevision,
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsDescription));
        Assert.NotEqual(current, CandidateGeneralChatQualitySuite.ComputeSha256(
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsRevision + 1,
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsDescription));
        Assert.NotEqual(current, CandidateGeneralChatQualitySuite.ComputeSha256(
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsRevision,
            CandidateGeneralChatQualitySuite.EvaluatorSemanticsDescription + ";changed"));
    }

    [Fact]
    public void Cases_have_checkable_grounding_language_reasoning_and_strict_structure()
    {
        Assert.True(Validate("ground-city", "Bergen"));
        Assert.True(Validate("ground-city", "bergen."));
        Assert.True(Validate("if-exact-token", "amber"));
        Assert.False(Validate("if-exact-token", "Amber"));
        Assert.False(Validate("if-exact-token", "amber\n"));
        Assert.True(Validate("if-one-line", "Pacific Ocean."));
        Assert.False(Validate("if-one-line", "Atlantic Ocean"));
        Assert.True(Validate("ground-unknown", "unknown"));
        Assert.False(Validate("ground-unknown", "The tracking number is not provided."));
        Assert.True(Validate("multi-spanish", "Lima"));
        Assert.True(Validate("multi-italian", "Buongiorno."));
        Assert.True(Validate("multi-german", "Blau"));
        Assert.True(Validate("reason-time", "11:30"));
        Assert.True(Validate("struct-object", "{\"count\":3,\"ok\":true}"));
        Assert.True(Validate("struct-object", "{\"ok\":true,\"count\":3.0}"));
        Assert.True(Validate("struct-object", "{\"ok\":true,\"count\":3e0}"));
        Assert.False(Validate("struct-object", "{\"count\":3,\"ok\":true,\"extra\":0}"));
        Assert.False(Validate("struct-object", "{\"ok\":true,\"ok\":true,\"count\":3}"));
        Assert.True(Validate("struct-array", "[2,4,6]"));
        Assert.False(Validate("struct-array", "[2,6,4]"));
    }

    [Fact]
    public void Instruction_answers_accept_only_the_formats_the_prompts_require()
    {
        Assert.True(Validate("if-one-line", "The Pacific Ocean."));
        Assert.True(Validate("if-one-line", "pacific ocean"));
        Assert.False(Validate("if-one-line", "Pacific\nOcean"));
        Assert.False(Validate("if-one-line", "Pacific\u2028Ocean"));
        Assert.False(Validate("if-one-line", "The Pacific Ocean is largest."));
        Assert.False(Validate("if-one-line", "Atlantic Ocean"));

        Assert.True(Validate("if-count", "red,violet"));
        Assert.True(Validate("if-count", "Red, violet"));
        Assert.True(Validate("if-count", "red,violet."));
        Assert.False(Validate("if-count", "violet,red"));
        Assert.False(Validate("if-count", "red,violet,blue"));
        Assert.False(Validate("if-count", "red violet"));
        Assert.False(Validate("if-count", "red,\nviolet"));
        Assert.False(Validate("if-count", "red,\u2028violet"));

        Assert.True(Validate("if-avoid-extra", "Yes"));
        Assert.True(Validate("if-avoid-extra", "Yes."));
        Assert.False(Validate("if-avoid-extra", "No"));
        Assert.False(Validate("if-avoid-extra", "Yes, 12 is even."));
        Assert.False(Validate("if-avoid-extra", "Yes.."));
    }

    [Fact]
    public void French_day_count_accepts_a_word_or_numeral_but_not_wrong_language_or_explanation()
    {
        Assert.True(Validate("multi-french", "sept"));
        Assert.True(Validate("multi-french", "7"));
        Assert.False(Validate("multi-french", "Seven jours dans une semaine."));
        Assert.False(Validate("multi-french", "Il y a sept jours."));
        Assert.False(Validate("multi-french", "seven"));
        Assert.False(Validate("reason-time", "10:30"));
    }

    [Fact]
    public void Every_run_requires_all_mandatory_classes_and_at_least_27_of_30()
    {
        Dictionary<string, bool> complete = CandidateGeneralChatQualitySuite.Cases.ToDictionary(item => item.Id, _ => true);
        Assert.True(CandidateGeneralChatQualitySuite.MeetsSingleRunFloor(complete));

        Dictionary<string, bool> belowAbsoluteFloor = complete.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (string id in belowAbsoluteFloor.Keys.Take(4).ToArray()) belowAbsoluteFloor[id] = false;
        Assert.False(CandidateGeneralChatQualitySuite.MeetsSingleRunFloor(belowAbsoluteFloor));

        Dictionary<string, bool> belowCategoryFloor = complete.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (string id in CandidateGeneralChatQualitySuite.Cases
            .Where(item => item.Category == CandidateGeneralChatQualitySuite.Category.Grounding && !item.Mandatory)
            .Take(3).Select(item => item.Id)) belowCategoryFloor[id] = false;
        Assert.Equal(27, belowCategoryFloor.Count(pair => pair.Value));
        Assert.False(CandidateGeneralChatQualitySuite.MeetsSingleRunFloor(belowCategoryFloor));

        Dictionary<string, bool> missingMandatory = complete.ToDictionary(pair => pair.Key, pair => pair.Value);
        missingMandatory[CandidateGeneralChatQualitySuite.Cases.First(item => item.Mandatory).Id] = false;
        Assert.False(CandidateGeneralChatQualitySuite.MeetsSingleRunFloor(missingMandatory));

        Dictionary<string, bool> incomplete = complete.ToDictionary(pair => pair.Key, pair => pair.Value);
        incomplete.Remove(CandidateGeneralChatQualitySuite.Cases[^1].Id);
        Assert.False(CandidateGeneralChatQualitySuite.MeetsSingleRunFloor(incomplete));
    }

    private static bool Validate(string caseId, string answer)
    {
        CandidateGeneralChatQualitySuite.Case item = CandidateGeneralChatQualitySuite.Cases.Single(c => c.Id == caseId);
        return CandidateGeneralChatQualitySuite.Validate(item, answer);
    }
}
