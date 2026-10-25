using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SpeechInstallPackagesTests
{
    [Fact]
    public void Install_list_provisions_the_spacy_model_kokoro_english_needs_without_pip()
    {
        Assert.Contains(SpeechService.SpacyModelRequirement, SpeechService.SpeechPackages);
        Assert.Contains("en_core_web_sm-3.8.0", SpeechService.SpacyModelRequirement);
        Assert.StartsWith("https://github.com/explosion/spacy-models/releases/download/",
            SpeechService.SpacyModelRequirement.Split('@')[1].Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_list_keeps_the_original_speech_dependencies()
    {
        foreach (string required in new[] { "transformers", "soundfile", "numpy", "librosa", "kokoro", "misaki[en]" })
            Assert.Contains(required, SpeechService.SpeechPackages);
    }
}
