using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The data root is where conversations, memory, the RAG index, settings, paired-device
/// credentials, downloaded models and installed runtimes live, so every component has to agree on
/// it. These assert the naming rule without touching the real %LOCALAPPDATA%, and guard the test
/// suite's own isolation from the developer's installation.
/// </summary>
public sealed class PaRuXDataRootTests
{
    [Fact]
    public void The_production_location_is_PaRuX_under_LocalApplicationData()
    {
        // Asserted without reading PaRuXDataRoot.Path, which the suite has already redirected.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "PaRuX"), PaRuXDataRoot.DefaultLocation());
    }

    [Fact]
    public void The_test_run_is_isolated_from_the_real_data_root()
    {
        // If this ever fails, the suite is writing into the developer's installation again.
        string resolved = PaRuXDataRoot.Path;

        Assert.NotEqual(PaRuXDataRoot.DefaultLocation(), resolved);
        Assert.Equal(Path.Combine(resolved, "server", "x.db"), PaRuXDataRoot.Combine("server", "x.db"));
    }
}
