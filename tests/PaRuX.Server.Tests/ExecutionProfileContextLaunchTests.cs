using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionProfileContextLaunchTests
{
    [Fact]
    public void Fast_profile_caps_a_larger_tuned_context()
    {
        var runtime = new LlamaPerformanceProfileDto { ContextSize = 65_536 };
        ModelExecutionProfile execution = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Fast);

        ContextPreferenceLaunch.ApplyExecutionProfileCap(runtime, execution);

        Assert.Equal(16_384, runtime.ContextSize);
    }

    [Fact]
    public void Quality_profile_never_expands_a_context_selected_by_benchmark_tuning()
    {
        var runtime = new LlamaPerformanceProfileDto { ContextSize = 32_768 };
        ModelExecutionProfile execution = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Overnight);

        ContextPreferenceLaunch.ApplyExecutionProfileCap(runtime, execution);

        Assert.Equal(32_768, runtime.ContextSize);
    }

    [Fact]
    public void Model_specific_preferred_context_can_apply_a_stricter_cap()
    {
        var runtime = new LlamaPerformanceProfileDto { ContextSize = 32_768 };
        ModelExecutionProfile execution = ExecutionProfileStore.BuildDefault("model", ExecutionQualityProfile.Balanced);
        execution.PreferredContextTokens = 8_192;

        ContextPreferenceLaunch.ApplyExecutionProfileCap(runtime, execution);

        Assert.Equal(8_192, runtime.ContextSize);
    }
}
