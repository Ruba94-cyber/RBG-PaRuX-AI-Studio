using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The hardware probe must not fail (and so must not stop all automatic maintenance) when the media runtime is installed.</summary>
public sealed class AutoMaintenanceRuntimeFingerprintTests
{
    [Fact]
    public void A_media_backend_is_recorded_as_installed_instead_of_throwing()
    {
        Assert.Equal("media-comfyui@installed", AutoMaintenanceComposition.RuntimeFingerprintToken("media-comfyui"));
    }

    [Theory]
    [InlineData("text-cuda")]
    [InlineData("text-vulkan")]
    public void Text_backends_keep_their_build_tag(string backend)
    {
        string token = AutoMaintenanceComposition.RuntimeFingerprintToken(backend);
        Assert.StartsWith(backend + "@", token);
        Assert.NotEqual(backend + "@installed", token);
    }
}
