using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PaRuXLocalAiStateRootTests : IDisposable
{
    private const string OverrideName = "PARUX_LOCALAI_STATE_DIR";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideName);

    [Fact]
    public void Absolute_process_override_is_normalized_and_used()
    {
        string overrideRoot = Path.Combine(Path.GetTempPath(), "parux-localai-state", "..", "isolated-state");
        Environment.SetEnvironmentVariable(OverrideName, overrideRoot);

        Assert.Equal(Path.GetFullPath(overrideRoot), PaRuXLocalAiStateRoot.Path);
    }

    [Fact]
    public void Relative_process_override_is_ignored()
    {
        Environment.SetEnvironmentVariable(OverrideName, "relative-state");

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAI");
        Assert.Equal(expected, PaRuXLocalAiStateRoot.Path);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(OverrideName, _previousOverride);
}
