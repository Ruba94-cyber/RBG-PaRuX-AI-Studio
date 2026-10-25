using PaRuX.Services;
using Xunit;

public sealed class CodingAgentPromptBudgetPolicyTests
{
    [Theory]
    [InlineData(16_384, false, 8_192)]
    [InlineData(8_192, false, 8_192)]
    [InlineData(4_096, false, 4_096)]
    [InlineData(0, false, 8_192)]
    [InlineData(16_384, true, 16_384)]
    [InlineData(0, true, 8_192)]
    public void Initial_prompt_budget_is_conservative_until_the_coding_model_is_resolved(
        int residentTokens, bool reflectsCodingModel, int expected)
    {
        Assert.Equal(expected, CodingAgentPromptBudgetPolicy.InitialPromptContextTokens(
            residentTokens, reflectsCodingModel));
    }

    [Fact]
    public void LargeTaskReducesRepositoryEvidenceInAnEightThousandTokenWindow()
    {
        int allowed = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
            8_192, 9_338, taskCharacters: 7_635, systemCharacters: 3_200,
            recentEvidenceCharacters: 2_500);

        Assert.Equal(3_097, allowed);
    }

    [Fact]
    public void LargeWindowRetainsTheUsefulRepositoryBudget()
    {
        int allowed = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
            65_536, 50_000, taskCharacters: 7_635, systemCharacters: 3_200,
            recentEvidenceCharacters: 6_000);

        Assert.Equal(50_000, allowed);
    }

    [Fact]
    public void FinalPromptAdmissionRejectsOverlongActualRequest()
    {
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(4_096);
        Assert.False(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
            8_192, reserve, systemCharacters: 5_000,
            schemaCharacters: 2_500, userCharacters: 9_000));
        Assert.True(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
            32_768, reserve, systemCharacters: 5_000,
            schemaCharacters: 2_500, userCharacters: 9_000));
    }

    [Fact]
    public void FinalPromptAdmissionIsOverflowSafeAndHandlesUnknownContext()
    {
        Assert.True(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
            0, 10_000, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.False(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
            8_192, 16_384, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.True(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
            65_536, 4_864, 6_000, 2_000, 16_000));
    }

    [Fact]
    public void RecoveryBudgetsCompactOnlyReplayAndRepositoryEvidence()
    {
        Assert.Equal((2_500, 20_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(0, 12_000, 20_000));
        Assert.Equal((0, 20_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(1, 12_000, 20_000));
        Assert.Equal((0, 6_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(2, 12_000, 20_000));
        Assert.Equal((0, 3_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(3, 12_000, 20_000));
        Assert.Equal((0, 3_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(3, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CodingAgentPromptBudgetPolicy.RecoveryBudgets(4, 12_000, 20_000));
    }

    [Fact]
    public void RecoveryNeverIncreasesSmallEvidenceBudgets()
    {
        Assert.Equal((2_000, 3_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(0, 2_000, 3_000));
        Assert.Equal((0, 3_000), CodingAgentPromptBudgetPolicy.RecoveryBudgets(2, 2_000, 3_000));
    }

    [Fact]
    public void BoundedRecoveryCanFitWithoutTruncatingTaskOrSystemProtocol()
    {
        // Synthetic prompt layout: the full replay and repository packet do not fit;
        // the third, repository-reduced projection does. No original evidence is deleted.
        const int window = 16_384;
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(4_096);
        const int immutablePrompt = 13_000; // system + schema + task + framing
        bool fitted = false;
        int fittedAt = -1;
        for (int pass = 0; pass < 4; pass++)
        {
            var reduced = CodingAgentPromptBudgetPolicy.RecoveryBudgets(pass, 12_000, 20_000);
            if (!CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
                    window, reserve, immutablePrompt, 0,
                    reduced.RecentCharacters + reduced.RepositoryCharacters))
                continue;
            fitted = true;
            fittedAt = pass;
            break;
        }

        Assert.True(fitted);
        Assert.Equal(2, fittedAt);
    }

    [Fact]
    public void SmallWindowNeverPretendsUncompressibleProtocolFits()
    {
        int reserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(4_096);
        for (int pass = 0; pass < 4; pass++)
        {
            var reduced = CodingAgentPromptBudgetPolicy.RecoveryBudgets(pass, 12_000, 20_000);
            Assert.False(CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
                8_192, reserve, systemCharacters: 12_000,
                schemaCharacters: 2_500,
                userCharacters: 1_000 + reduced.RecentCharacters + reduced.RepositoryCharacters));
        }
    }
    [Theory]
    [InlineData("HTTP 400: context_length_exceeded")]
    [InlineData("Failed: maximum context length is 8192 tokens")]
    [InlineData("The prompt exceeds available input capacity")]
    [InlineData("prompt too long")]
    [InlineData("n_ctx exceeded on llama-server")]
    [InlineData("Context window exceeded")]
    [InlineData("context shift is disabled")]
    public void OnlyExplicitContextOverflowErrorsTriggerTheSingleRecoveryRetry(string error)
    {
        Assert.True(CodingAgentPromptBudgetPolicy.IsProviderContextOverflow(error));
    }

    [Theory]
    [InlineData("HTTP 400: malformed request")]
    [InlineData("HTTP 401: missing token")]
    [InlineData("HTTP 429: rate limit exceeded")]
    [InlineData("Connection refused")]
    [InlineData("max_tokens output length reached")]
    [InlineData("")]
    public void OtherProviderFailuresNeverTriggerContextRecovery(string error)
    {
        Assert.False(CodingAgentPromptBudgetPolicy.IsProviderContextOverflow(error));
    }

}
