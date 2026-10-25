using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentExecutionBudgetTests
{
    [Fact]
    public void From_preserves_effective_execution_profile_agent_limits()
    {
        var status = ValidStatus();
        status.ActiveQualityProfile = ExecutionQualityProfile.MaximumQuality;
        status.Effective.ModelKey = "model-x";
        status.Effective.ToolObservationCharacters = 48_000;
        status.Effective.RepositoryEvidenceCharacters = 160_000;
        status.Effective.PreferredContextTokens = 65_536;
        status.Effective.DelegationThreshold = 55;
        status.Effective.ToolStrategy = "parallel-evidence";
        status.Effective.StructuredOutputStrategy = "validate-repair";
        status.Effective.Budget.MaxWallClockMinutes = 120;
        status.Effective.Budget.MaxToolSteps = 20_000;
        status.Effective.Budget.ContextFraction = 0.62;
        status.Effective.Budget.MaxDirectSubagents = 6;
        status.Effective.Budget.MaxDelegationDepth = 3;

        AgentExecutionBudget budget = AgentExecutionBudget.From(status);

        Assert.Equal(ExecutionQualityProfile.MaximumQuality, budget.QualityProfile);
        Assert.Equal("model-x", budget.ModelKey);
        Assert.Equal(120, budget.MaxWallClockMinutes);
        Assert.Equal(20_000, budget.MaxToolSteps);
        Assert.Equal(48_000, budget.ToolObservationCharacters);
        Assert.Equal(160_000, budget.RepositoryEvidenceCharacters);
        Assert.Equal(65_536, budget.PreferredContextTokens);
        Assert.Equal(0.62, budget.ContextFraction, 6);
        Assert.Equal(55, budget.DelegationThreshold);
        Assert.Equal("parallel-evidence", budget.ToolStrategy);
        Assert.Equal("validate-repair", budget.StructuredOutputStrategy);
        Assert.True(budget.AllowsStructuredProtocolRepair);
        Assert.Equal(6, budget.MaxDirectSubagents);
        Assert.Equal(3, budget.MaxDelegationDepth);
    }

    [Fact]
    public void Parent_continuation_keeps_the_existing_tool_step_and_fan_out_limits()
    {
        ExecutionProfileStatus status = ValidStatus();
        status.Effective.Budget.MaxToolSteps = 7;
        status.Effective.Budget.MaxDirectSubagents = 2;
        AgentExecutionBudget budget = AgentExecutionBudget.From(status);

        Assert.Equal(7, budget.MaxToolSteps);
        Assert.Equal(2, budget.MaxDirectSubagents);
    }

    [Fact]
    public void Repository_evidence_is_also_bounded_by_context_fraction()
    {
        // 8,192 tokens * 4 chars/token * 50% = 16,384 characters, lower than the explicit 96k cap.
        Assert.Equal(16_384, AgentExecutionBudget.ComputeRepositoryEvidenceBudget(96_000, 8_192, 0.5));
        Assert.Equal(8_000, AgentExecutionBudget.ComputeRepositoryEvidenceBudget(8_000, 2_048, 0.10));
        Assert.Equal(240_000, AgentExecutionBudget.ComputeRepositoryEvidenceBudget(240_000, 1_048_576, 0.80));
    }

    [Fact]
    public void From_allows_zero_delegation_to_disable_subagents()
    {
        var status = ValidStatus();
        status.ActiveQualityProfile = ExecutionQualityProfile.Fast;
        status.Effective.ModelKey = "no-delegation";
        status.Effective.ToolObservationCharacters = 6_000;
        status.Effective.Budget.MaxWallClockMinutes = 15;
        status.Effective.Budget.MaxToolSteps = 120;
        status.Effective.Budget.MaxDirectSubagents = 0;
        status.Effective.Budget.MaxDelegationDepth = 0;

        AgentExecutionBudget budget = AgentExecutionBudget.From(status);

        Assert.Equal(0, budget.MaxDirectSubagents);
        Assert.Equal(0, budget.MaxDelegationDepth);
        Assert.False(budget.AllowsDelegation("implement a complex cross-repository refactor and verify it", "coding"));
    }

    [Fact]
    public void Delegation_threshold_uses_the_same_task_intelligence_signal_as_routing()
    {
        var lowThresholdStatus = ValidStatus();
        lowThresholdStatus.Effective.DelegationThreshold = 0;
        AgentExecutionBudget permissive = AgentExecutionBudget.From(lowThresholdStatus);
        Assert.True(permissive.AllowsDelegation("summarize this", "documentation"));

        var highThresholdStatus = ValidStatus();
        highThresholdStatus.Effective.DelegationThreshold = 100;
        AgentExecutionBudget restrictive = AgentExecutionBudget.From(highThresholdStatus);
        Assert.False(restrictive.AllowsDelegation("summarize this", "documentation"));
    }

    [Fact]
    public void Tool_strategy_parallelization_is_explicit_and_adaptive_is_complexity_gated()
    {
        ExecutionProfileStatus status = ValidStatus();
        status.Effective.ToolStrategy = "parallel-evidence";
        AgentExecutionBudget parallel = AgentExecutionBudget.From(status);
        Assert.False(parallel.ShouldParallelizeEvidence("tiny", "documentation", independentSourceCount: 1));
        Assert.True(parallel.ShouldParallelizeEvidence("tiny", "documentation", independentSourceCount: 2));

        status = ValidStatus();
        status.Effective.ToolStrategy = "serial-evidence";
        AgentExecutionBudget serial = AgentExecutionBudget.From(status);
        Assert.False(serial.ShouldParallelizeEvidence("complex cross repository security review with tests", "security", 3));

        status = ValidStatus();
        status.Effective.ToolStrategy = "adaptive";
        AgentExecutionBudget adaptive = AgentExecutionBudget.From(status);
        Assert.False(adaptive.ShouldParallelizeEvidence("summarize this", "documentation", 3));
        Assert.True(adaptive.ShouldParallelizeEvidence(
            "Perform a complex cross-repository architecture and security review, verify regressions and tests", "security", 3));
    }

    [Fact]
    public void Structured_output_strategy_controls_bounded_protocol_repair()
    {
        ExecutionProfileStatus status = ValidStatus();
        status.Effective.StructuredOutputStrategy = "schema-first";
        Assert.False(AgentExecutionBudget.From(status).AllowsStructuredProtocolRepair);

        status = ValidStatus();
        status.Effective.StructuredOutputStrategy = "validate-repair";
        Assert.True(AgentExecutionBudget.From(status).AllowsStructuredProtocolRepair);
    }

    [Fact]
    public void Remaining_counts_from_execution_pass_start_and_never_goes_negative()
    {
        AgentExecutionBudget budget = ValidBudget(maxMinutes: 30);
        DateTime started = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromMinutes(20), budget.Remaining(started, started.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, budget.Remaining(started, started.AddMinutes(31)));
    }

    [Theory]
    [InlineData(0, 10_000, 6, 3, 12_000)]
    [InlineData(30, 0, 6, 3, 12_000)]
    [InlineData(30, 20_001, 6, 3, 12_000)]
    [InlineData(30, 10, -1, 3, 12_000)]
    [InlineData(30, 10, 7, 3, 12_000)]
    [InlineData(30, 10, 6, -1, 12_000)]
    [InlineData(30, 10, 6, 4, 12_000)]
    [InlineData(30, 10, 6, 3, 1_999)]
    [InlineData(30, 10, 6, 3, 64_001)]
    public void From_rejects_corrupt_or_out_of_range_profile_values(
        int wallClock, int tools, int subagents, int depth, int observationCharacters)
    {
        ExecutionProfileStatus status = ValidStatus();
        status.Effective.ToolObservationCharacters = observationCharacters;
        status.Effective.Budget.MaxWallClockMinutes = wallClock;
        status.Effective.Budget.MaxToolSteps = tools;
        status.Effective.Budget.MaxDirectSubagents = subagents;
        status.Effective.Budget.MaxDelegationDepth = depth;

        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));
    }

    [Fact]
    public void From_rejects_invalid_context_and_strategy_fields()
    {
        ExecutionProfileStatus status = ValidStatus();
        status.Effective.RepositoryEvidenceCharacters = 7_999;
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));

        status = ValidStatus();
        status.Effective.PreferredContextTokens = 2_047;
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));

        status = ValidStatus();
        status.Effective.Budget.ContextFraction = 0.09;
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));

        status = ValidStatus();
        status.Effective.DelegationThreshold = 101;
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));

        status = ValidStatus();
        status.Effective.ToolStrategy = "pretend-parallel";
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));

        status = ValidStatus();
        status.Effective.StructuredOutputStrategy = "hope-for-json";
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));
    }

    [Fact]
    public void From_rejects_unknown_quality_instead_of_silently_normalizing_it()
    {
        ExecutionProfileStatus status = ValidStatus();
        status.ActiveQualityProfile = "turbo";
        Assert.Throws<InvalidDataException>(() => AgentExecutionBudget.From(status));
    }

    private static ExecutionProfileStatus ValidStatus() => new()
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "m",
            ToolObservationCharacters = 12_000,
            RepositoryEvidenceCharacters = 96_000,
            PreferredContextTokens = 65_536,
            DelegationThreshold = 65,
            ToolStrategy = "adaptive",
            StructuredOutputStrategy = "schema-first",
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 30,
                MaxToolSteps = 10,
                ContextFraction = 0.50,
                MaxDirectSubagents = 6,
                MaxDelegationDepth = 3
            }
        }
    };

    private static AgentExecutionBudget ValidBudget(int maxMinutes) => new(
        ExecutionQualityProfile.Balanced,
        "m",
        maxMinutes,
        10,
        12_000,
        96_000,
        65_536,
        0.50,
        65,
        "adaptive",
        "schema-first",
        6,
        3);
}
