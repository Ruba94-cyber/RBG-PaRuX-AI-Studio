using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The engine probe runs inside a Python process that is anything but quiet: Unsloth prints a banner
/// and patch notices on import, and pip/torch can add warnings of their own. Treating stdout as JSON
/// is how an installed, working training runtime came back as "not ready" with a null Unsloth
/// version - a verdict that was then recorded as a hardware limitation.
/// </summary>
public sealed class TrainingEngineProbeTests
{
    [Fact]
    public void The_probe_payload_survives_the_banner_unsloth_prints_before_it()
    {
        string stdout = string.Join('\n',
            "🦥 Unsloth: Will patch your computer to enable 2x faster free finetuning.",
            "🦥 Unsloth Zoo will now patch everything to make training faster!",
            "PARUX_ENGINE_PROBE {\"python\":\"3.11.9\",\"torch\":\"2.11.0+cu128\",\"cudaAvailable\":true,\"unsloth\":\"2026.8.19\"}");

        string payload = TrainingLabService.ExtractProbeJson(stdout);

        Assert.StartsWith("{", payload, System.StringComparison.Ordinal);
        Assert.Contains("\"unsloth\":\"2026.8.19\"", payload, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Unsloth Zoo", payload, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_output_after_the_payload_is_not_swallowed_into_it()
    {
        string stdout = "PARUX_ENGINE_PROBE {\"torch\":\"2.11.0+cu128\"}\nsome later warning line\n";

        Assert.Equal("{\"torch\":\"2.11.0+cu128\"}", TrainingLabService.ExtractProbeJson(stdout));
    }

    [Fact]
    public void A_run_that_never_reached_the_probe_reports_nothing_rather_than_guessing()
    {
        Assert.Equal(string.Empty, TrainingLabService.ExtractProbeJson("ModuleNotFoundError: No module named 'torch'"));
        Assert.Equal(string.Empty, TrainingLabService.ExtractProbeJson(string.Empty));
    }
}
