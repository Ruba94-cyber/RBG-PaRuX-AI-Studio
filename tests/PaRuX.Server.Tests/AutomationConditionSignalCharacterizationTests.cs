using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutomationConditionSignalCharacterizationTests
{
    [Fact]
    public void Signal_constants_use_snake_case_wire_names()
    {
        Assert.Equal("failed_task_count", AutomationConditionSignal.FailedTaskCount);
        Assert.Equal("running_task_count", AutomationConditionSignal.RunningTaskCount);
        Assert.Equal("completed_task_count", AutomationConditionSignal.CompletedTaskCount);
        Assert.Equal("failed_download_count", AutomationConditionSignal.FailedDownloadCount);
        Assert.Equal("active_download_count", AutomationConditionSignal.ActiveDownloadCount);
        Assert.Equal("completed_download_count", AutomationConditionSignal.CompletedDownloadCount);
        Assert.Equal("component_health", AutomationConditionSignal.ComponentHealth);
    }

    [Fact]
    public void All_contains_exactly_the_seven_documented_signals()
    {
        string[] expected =
        {
            "active_download_count", "completed_download_count", "completed_task_count",
            "component_health", "failed_download_count", "failed_task_count", "running_task_count"
        };
        Assert.Equal(expected, AutomationConditionSignal.All.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Numeric_contains_exactly_the_six_count_signals()
    {
        string[] expected =
        {
            "active_download_count", "completed_download_count", "completed_task_count",
            "failed_download_count", "failed_task_count", "running_task_count"
        };
        Assert.Equal(expected, AutomationConditionSignal.Numeric.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Numeric_is_a_proper_subset_of_All_and_excludes_component_health()
    {
        Assert.True(AutomationConditionSignal.Numeric.All(s => AutomationConditionSignal.All.Contains(s)));
        Assert.NotEqual(7, AutomationConditionSignal.Numeric.Count);
        Assert.False(AutomationConditionSignal.Numeric.Contains(AutomationConditionSignal.ComponentHealth));
        Assert.True(AutomationConditionSignal.All.Contains(AutomationConditionSignal.ComponentHealth));
    }

    [Fact]
    public void Signal_sets_are_ordinal_case_sensitive()
    {
        Assert.False(AutomationConditionSignal.All.Contains("Failed_Task_Count"));
        Assert.False(AutomationConditionSignal.All.Contains("component_health "));
        Assert.False(AutomationConditionSignal.Numeric.Contains("FAILED_TASK_COUNT"));
        Assert.True(AutomationConditionSignal.All.Contains("failed_task_count"));
    }

    [Theory]
    [InlineData(AutomationConditionSignal.FailedTaskCount)]
    [InlineData(AutomationConditionSignal.RunningTaskCount)]
    [InlineData(AutomationConditionSignal.CompletedTaskCount)]
    [InlineData(AutomationConditionSignal.FailedDownloadCount)]
    [InlineData(AutomationConditionSignal.ActiveDownloadCount)]
    [InlineData(AutomationConditionSignal.CompletedDownloadCount)]
    [InlineData(AutomationConditionSignal.ComponentHealth)]
    public void Every_signal_constant_is_registered_in_All(string signal)
    {
        Assert.True(AutomationConditionSignal.All.Contains(signal));
    }

    [Fact]
    public void Custom_condition_defaults_wire_the_canonical_signal_operator_and_value()
    {
        var config = new CustomConditionTriggerConfig();

        Assert.Equal(AutomationConditionSignal.FailedTaskCount, config.Signal);
        Assert.Equal(AutomationConditionOperator.GreaterThanOrEqual, config.Operator);
        Assert.Equal("1", config.Value);
        Assert.True(AutomationConditionSignal.Numeric.Contains(config.Signal));
    }

    [Fact]
    public void Signal_constants_are_distinct_from_the_neighbouring_operator_vocabulary()
    {
        foreach (string signal in AutomationConditionSignal.All)
            Assert.False(AutomationConditionOperator.All.Contains(signal), "signal " + signal + " collides with an operator name");
        foreach (string op in AutomationConditionOperator.Text)
            Assert.False(AutomationConditionSignal.All.Contains(op), "operator " + op + " collides with a signal name");
    }
}
