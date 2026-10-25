using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The prompt budget has to withhold enough of the window for the model's own output, including
/// the hidden reasoning a reasoning model spends before it answers.
///
/// Measured on the physical PC (2026-09-21) at n_ctx 8,192 with the coding launch's 4,096-token
/// reasoning budget: system prompt ~5,200 characters, action schema ~2,500, repository evidence
/// 6,600 and working memory 4,600 left roughly 1,800 tokens for generation. The agent returned
/// "invalid action protocol - missing action (model returned an empty response)" twice in a row
/// and the run ended. The old policy reserved a flat 2,048 tokens and did not count the schema.
/// </summary>
public sealed class CodingAgentGenerationReserveTests
{
    /// <summary>Measured sizes of the fixed per-step cost in the shipped agent.</summary>
    private const int SystemPromptCharacters = 5_200;
    private const int ActionSchemaCharacters = 2_500;
    private const int FixedPromptCharacters = SystemPromptCharacters + ActionSchemaCharacters;
    private const int CodingReasoningBudget = 4_096;

    [Fact]
    public void The_reserve_covers_hidden_reasoning_plus_the_visible_answer()
    {
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(CodingReasoningBudget);

        Assert.True(reserve > CodingReasoningBudget,
            "A reserve of " + reserve + " tokens does not even cover the " + CodingReasoningBudget
            + "-token reasoning budget, so the visible answer has nowhere to go.");
    }

    [Fact]
    public void A_model_without_a_reasoning_budget_keeps_the_old_reserve()
    {
        Assert.Equal(
            CodingAgentPromptBudgetPolicy.DefaultGenerationReserveTokens,
            CodingAgentPromptBudgetPolicy.GenerationReserveTokens(0));
    }

    /// <summary>
    /// The exact configuration that produced empty answers must now be reported as too small
    /// rather than silently clamped to the floors and overfilled.
    /// </summary>
    [Fact]
    public void The_window_that_produced_empty_answers_is_reported_as_too_small()
    {
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(CodingReasoningBudget);
        int recall = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(8_192, 2_500);

        Assert.True(CodingAgentPromptBudgetPolicy.IsContextTooSmall(
            contextTokens: 8_192, taskCharacters: 1_200,
            systemCharacters: FixedPromptCharacters,
            recentEvidenceCharacters: recall, generationReserveTokens: reserve));
    }

    [Fact]
    public void A_window_with_real_headroom_is_not_reported_as_too_small()
    {
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(CodingReasoningBudget);
        int recall = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(32_768, 2_500);

        Assert.False(CodingAgentPromptBudgetPolicy.IsContextTooSmall(
            contextTokens: 32_768, taskCharacters: 1_200,
            systemCharacters: FixedPromptCharacters,
            recentEvidenceCharacters: recall, generationReserveTokens: reserve));
    }

    /// <summary>
    /// The whole prompt - fixed cost, evidence and recall - plus the reserve must fit the window.
    /// This is the invariant the empty-answer failure violated.
    /// </summary>
    [Theory]
    [InlineData(16_384)]
    [InlineData(32_768)]
    [InlineData(65_536)]
    public void The_assembled_prompt_plus_the_reserve_fits_the_window(int contextTokens)
    {
        const int taskCharacters = 1_200;
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(CodingReasoningBudget);
        int recall = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(contextTokens, 2_500);
        int repository = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
            contextTokens, suggestedCharacters: 96_000, taskCharacters,
            FixedPromptCharacters, recall, reserve);

        long promptCharacters = FixedPromptCharacters + taskCharacters + recall + repository + 2_000;
        long promptTokens = promptCharacters / 3;

        Assert.True(promptTokens + reserve <= contextTokens,
            "Prompt is about " + promptTokens + " tokens and the reserve is " + reserve
            + ", which exceeds the " + contextTokens + "-token window.");
    }

    /// <summary>The five-argument overload every existing caller uses must keep its behaviour.</summary>
    [Fact]
    public void The_legacy_overload_still_uses_the_default_reserve()
    {
        Assert.Equal(
            CodingAgentPromptBudgetPolicy.RepositoryCharacters(
                32_768, 96_000, 1_200, FixedPromptCharacters, 7_000,
                CodingAgentPromptBudgetPolicy.DefaultGenerationReserveTokens),
            CodingAgentPromptBudgetPolicy.RepositoryCharacters(
                32_768, 96_000, 1_200, FixedPromptCharacters, 7_000));
    }
}
