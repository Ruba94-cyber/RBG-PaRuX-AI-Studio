using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaEditSourcePathSafetyTests
{
    [Theory]
    [InlineData(@"\\attacker-host\share\image.png")]
    [InlineData(@"//attacker-host/share/image.png")]
    [InlineData(@"\\?\C:\images\image.png")]
    [InlineData(@"   \\attacker-host\share\image.png")]
    public void SetEditSource_rejects_network_and_device_paths_before_checking_the_disk(string path)
    {
        var bridge = new MediaEditSourceBridge();
        bool raised = false;
        bridge.EditSourceRequested += (_, _) => raised = true;

        SetEditSourceResult result = bridge.SetEditSource(path, "prompt");

        Assert.False(result.Success);
        Assert.Contains("Network and device paths", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bridge.HasPendingSource);
        Assert.False(raised);
    }

    [Fact]
    public void SetEditSource_rejects_alternate_data_stream_names_even_when_the_extension_is_an_image()
    {
        var bridge = new MediaEditSourceBridge();
        string hostFile = Path.Combine(Path.GetTempPath(), $"parux_ads_{Guid.NewGuid():N}.txt");
        File.WriteAllText(hostFile, "not an image");

        try
        {
            SetEditSourceResult result = bridge.SetEditSource(hostFile + ":evil.png", "prompt");

            Assert.False(result.Success);
            Assert.Contains("Alternate data stream", result.Message, StringComparison.Ordinal);
            Assert.False(bridge.HasPendingSource);
        }
        finally
        {
            if (File.Exists(hostFile)) File.Delete(hostFile);
        }
    }

    [Fact]
    public void SetEditSource_still_accepts_a_plain_local_image()
    {
        var bridge = new MediaEditSourceBridge();
        string image = Path.Combine(Path.GetTempPath(), $"parux_plain_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(image, new byte[] { 1, 2, 3 });

        try
        {
            SetEditSourceResult result = bridge.SetEditSource(image, "prompt");

            Assert.True(result.Success);
            Assert.Equal(image, bridge.PendingEditSourcePath);
        }
        finally
        {
            if (File.Exists(image)) File.Delete(image);
        }
    }
}
