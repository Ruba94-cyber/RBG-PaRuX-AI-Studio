using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalAiStateTests
{
    [Theory]
    [InlineData("Qwen3.8-27B Standard GPU", "Qwen3.8-27B · Standard")]
    [InlineData("Qwen3.8-27B Uncensored GPU", "Qwen3.8-27B · Uncensored")]
    [InlineData("Qwen3.5-4B Uncensored media assistant", "Media prompt assistant · Qwen3.5-4B")]
    [InlineData("Llama-3.1-8B Standard GPU", "Llama-3.1-8B · Standard")]
    [InlineData("Role: Coding", "Role: Coding · Standard")]
    public void Header_identifies_the_actual_role_without_misrepresenting_media_quality(string profile, string expected)
    {
        var state = new LocalAiState { ActiveProfile = profile, Healthy = true };

        Assert.Equal(expected, state.ShortProfileLabel);
    }
}
