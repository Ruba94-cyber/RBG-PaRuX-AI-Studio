using System.Reflection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real BIC-13 evidence run: the pinned Unsloth runtime resolved a TRL whose SFTConfig has no
/// max_seq_length (renamed max_length), so every SFT, continued-pretraining and Vision SFT launch died with
/// a TypeError. The trainer must adapt to either name instead of passing it straight to SFTConfig.
/// </summary>
public sealed class TrainingScriptTrlCompatibilityTests
{
    private static string Script() =>
        (string)typeof(TrainingLabService)
            .GetField("TrainScriptSource", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    [Fact]
    public void Trainer_script_never_constructs_SFTConfig_directly()
    {
        string script = Script();
        Assert.DoesNotContain("args=SFTConfig(", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("args=sft_config(").Length - 1);
    }

    [Fact]
    public void Trainer_script_maps_max_seq_length_to_max_length_when_the_installed_trl_requires_it()
    {
        string script = Script();
        Assert.Contains("inspect.signature(SFTConfig.__init__)", script, StringComparison.Ordinal);
        Assert.Contains("kwargs[\"max_length\"] = kwargs.pop(\"max_seq_length\")", script, StringComparison.Ordinal);
    }
}
