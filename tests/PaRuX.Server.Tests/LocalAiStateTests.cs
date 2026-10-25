using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalAiStateTests
{
    [Theory]
    [InlineData("Qwen3.8-27B Standard GPU", "Qwen 27B · Standard")]
    [InlineData("Qwen3.8-27B Uncensored GPU", "Qwen 27B · Uncensored")]
    [InlineData("Qwen3.5-4B Uncensored media assistant", "Media prompt assistant · Qwen 4B")]
    public void Header_identifies_the_actual_role_without_misrepresenting_media_quality(string profile, string expected)
    {
        var state = new LocalAiState { ActiveProfile = profile, Healthy = true };

        Assert.Equal(expected, state.ShortProfileLabel);
    }
}
