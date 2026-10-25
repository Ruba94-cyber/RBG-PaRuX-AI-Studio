using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The coding agent's working memory - the execution evidence replayed into the next prompt - used
/// to be a hard-coded 2,500 characters, independent of the context window.
///
/// Measured on the physical PC (2026-09-21): a single observation was allowed 4,000 characters and
/// one artifact read of a 154-line file returned 5,930, so not even one complete read survived
/// into the following step. The agent re-read the same file until its repeat guard and exploration
/// budget stopped it. Raising the physical window from 8,192 to 32,768 tokens (a real 4x, verified
/// through <c>/props</c>) changed nothing, because this budget was a constant: the larger window
/// bought more fresh repository text and no additional recall.
/// </summary>
public sealed class CodingAgentRecentEvidenceBudgetTests
{
    /// <summary>The value the budget was pinned at before it started scaling.</summary>
    private const int PreviousFixedBudget = 2_500;

    /// <summary>Observed size of one real artifact read that had to survive a step.</summary>
    private const int ObservedArtifactReadCharacters = 5_930;

    [Fact]
    public void An_unknown_context_keeps_the_previous_conservative_budget()
    {
        Assert.Equal(
            PreviousFixedBudget,
            CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(contextTokens: 0, PreviousFixedBudget));
    }

    [Theory]
    [InlineData(8_192)]
    [InlineData(16_384)]
    [InlineData(32_768)]
    [InlineData(131_072)]
    public void A_real_window_never_gives_less_recall_than_the_old_constant(int contextTokens)
    {
        int budget = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(contextTokens, PreviousFixedBudget);

        Assert.True(budget >= PreviousFixedBudget,
            "Context " + contextTokens + " produced " + budget
            + " characters of working memory, less than the pre-scaling floor.");
    }

    [Fact]
    public void A_bigger_window_actually_buys_more_recall()
    {
        int small = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(8_192, PreviousFixedBudget);
        int large = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(32_768, PreviousFixedBudget);

        Assert.True(large > small * 2,
            "Quadrupling the window moved working memory only from " + small + " to " + large
            + " characters - this is the regression that made the context fix useless to the agent.");
    }

    /// <summary>
    /// The concrete failure: at the window the coding role actually ran with, one whole artifact
    /// read has to fit, or the agent cannot remember the file it just opened.
    /// </summary>
    [Theory]
    [InlineData(16_384)]
    [InlineData(32_768)]
    public void One_whole_observed_file_read_fits_at_a_realistic_window(int contextTokens)
    {
        int budget = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(contextTokens, PreviousFixedBudget);

        Assert.True(budget >= ObservedArtifactReadCharacters,
            "Working memory is " + budget + " characters, so the " + ObservedArtifactReadCharacters
            + "-character read that triggered the loop still would not survive one step.");
    }

    [Fact]
    public void Recall_stays_bounded_so_the_prompt_cannot_become_all_transcript()
    {
        int budget = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(1_000_000, PreviousFixedBudget);

        Assert.Equal(CodingAgentPromptBudgetPolicy.MaximumRecentEvidenceCharacters, budget);
    }

    /// <summary>
    /// Repository evidence and working memory share one window, so the repository budget has to
    /// shrink as recall grows - and must still stay inside its own documented floor.
    /// </summary>
    [Fact]
    public void Repository_evidence_still_fits_beside_the_larger_recall()
    {
        const int contextTokens = 32_768;
        int recall = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(contextTokens, PreviousFixedBudget);

        int repository = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
            contextTokens, suggestedCharacters: 96_000,
            taskCharacters: 1_200, systemCharacters: 6_000, recentEvidenceCharacters: recall);

        Assert.True(repository >= 3_000, "Repository evidence collapsed to " + repository + " characters.");
        Assert.True(recall + repository < (contextTokens - 2_048) * 3,
            "Working memory plus repository evidence must stay inside the usable window.");
    }
}
