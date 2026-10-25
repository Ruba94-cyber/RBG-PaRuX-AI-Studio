using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SpeechVoiceSelectorTests
{
    [Theory]
    [InlineData("Buongiorno! Oggi il cielo è sereno e la temperatura è di ventidue gradi.", "if_sara")]  // live regression
    [InlineData("The quick brown fox jumps over the lazy dog near the river bank.", "af_heart")]
    [InlineData("Hoy el día está muy bonito y gracias por todo.", "ef_dora")]
    [InlineData("Je vous remercie pour cette très belle journée.", "ff_siwis")]
    [InlineData("こんにちは、元気ですか", "jf_alpha")]
    [InlineData("你好，今天天气很好", "zf_xiaobei")]
    [InlineData("", "af_heart")]
    public void Unchosen_voice_follows_the_text_language(string text, string expected) =>
        Assert.Equal(expected, SpeechVoiceSelector.Resolve(null, text));

    [Fact]
    public void An_explicit_voice_is_always_respected() =>
        Assert.Equal("im_nicola", SpeechVoiceSelector.Resolve(" im_nicola ", "The weather is nice today"));
}
