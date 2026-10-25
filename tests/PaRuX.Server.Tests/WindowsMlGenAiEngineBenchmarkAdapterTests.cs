using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlGenAiEngineBenchmarkAdapterTests
{
    [Fact]
    public void Long_context_prompt_contains_real_large_context_and_verifiable_markers()
    {
        string prompt = WindowsMlGenAiEngineBenchmarkAdapter.BuildLongContextPrompt(sustained: false);

        Assert.True(prompt.Length > 40_000, "Long-context workload must contain a real large payload, not a short question about long context.");
        Assert.Contains("Record 0017", prompt, StringComparison.Ordinal);
        Assert.Contains("Record 0211", prompt, StringComparison.Ordinal);
        Assert.Contains("Record 0487", prompt, StringComparison.Ordinal);
        Assert.Contains("report", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sustained_duration_is_configurable_and_bounded()
    {
        string? previous = Environment.GetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS", "12");
            Assert.Equal(TimeSpan.FromSeconds(12), WindowsMlGenAiEngineBenchmarkAdapter.SustainedValidationDuration());

            Environment.SetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS", "1");
            Assert.Equal(TimeSpan.FromSeconds(5), WindowsMlGenAiEngineBenchmarkAdapter.SustainedValidationDuration());

            Environment.SetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS", "5000");
            Assert.Equal(TimeSpan.FromSeconds(900), WindowsMlGenAiEngineBenchmarkAdapter.SustainedValidationDuration());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_ENGINE_RACE_SUSTAINED_SECONDS", previous);
        }
    }
}