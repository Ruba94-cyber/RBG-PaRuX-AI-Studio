using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Characterization tests for the public API of ActiveModelSubstitution. These pin the observable
/// behaviour of the pure decision function as it exists today, independent of the defect-regression
/// suite in ActiveModelTruthTests.
/// </summary>
public sealed class ActiveModelSubstitutionCharacterizationTests
{
    private const string Configured = @"C:\LocalAI\models\Qwen3.8-27B-Q4_K_M.gguf";
    private const string Fallback = @"C:\LocalAI\models\Qwen3.5-4B-Q4_K_M.gguf";

    [Fact]
    public void The_shared_None_sentinel_reports_no_substitution_no_vision_loss_and_no_note()
    {
        Assert.False(ActiveModelSubstitution.None.IsSubstituted);
        Assert.False(ActiveModelSubstitution.None.LostVision);
        Assert.Null(ActiveModelSubstitution.None.Note);
    }

    [Theory]
    [InlineData(null, @"C:\x\model.gguf")]
    [InlineData(@"C:\x\model.gguf", null)]
    [InlineData("", @"C:\x\model.gguf")]
    [InlineData(@"C:\x\model.gguf", "")]
    [InlineData("   ", @"C:\x\model.gguf")]
    [InlineData(@"C:\x\model.gguf", "   ")]
    public void A_missing_or_blank_side_yields_exactly_the_shared_none_sentinel(
        string? configuredPath, string? loadedPath)
    {
        ActiveModelSubstitution.Result result =
            ActiveModelSubstitution.Evaluate(configuredPath, loadedPath, true, false, false);

        Assert.Same(ActiveModelSubstitution.None, result);
    }

    [Fact]
    public void A_substitution_is_detected_when_the_configured_and_loaded_paths_differ()
    {
        ActiveModelSubstitution.Result result =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, false, false, false);

        Assert.True(result.IsSubstituted);
        Assert.False(result.LostVision);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void The_note_names_both_models_with_only_their_file_names()
    {
        ActiveModelSubstitution.Result result =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, false, false, false);

        Assert.Contains("Qwen3.8-27B-Q4_K_M.gguf", result.Note!);
        Assert.Contains("Qwen3.5-4B-Q4_K_M.gguf", result.Note!);
        // The note must carry the leaf file names, not the directory the models live in.
        Assert.DoesNotContain(@"C:\LocalAI\models", result.Note!);
    }

    [Fact]
    public void The_note_explains_the_fallback_only_when_no_capable_gpu_was_detected()
    {
        ActiveModelSubstitution.Result noGpu =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, false, false, gpuDetected: false);
        ActiveModelSubstitution.Result withGpu =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, false, false, gpuDetected: true);

        Assert.Contains("12GB VRAM", noGpu.Note!);
        Assert.DoesNotContain("12GB VRAM", withGpu.Note!);
    }

    [Fact]
    public void The_note_appends_the_text_only_warning_only_when_vision_was_lost()
    {
        ActiveModelSubstitution.Result lost =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, configuredHasVision: true, loadedHasVision: false, false);
        ActiveModelSubstitution.Result kept =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, configuredHasVision: true, loadedHasVision: true, false);
        ActiveModelSubstitution.Result neverHadVision =
            ActiveModelSubstitution.Evaluate(Configured, Fallback, configuredHasVision: false, loadedHasVision: false, false);

        Assert.Contains("text-only", lost.Note!);
        Assert.Contains("image input is unavailable", lost.Note!);
        Assert.DoesNotContain("text-only", kept.Note!);
        Assert.DoesNotContain("text-only", neverHadVision.Note!);
        Assert.True(lost.LostVision);
        Assert.False(kept.LostVision);
        Assert.False(neverHadVision.LostVision);
    }

    [Fact]
    public void Two_spellings_of_the_same_file_are_not_a_substitution()
    {
        string canonical = Path.Combine(Path.GetTempPath(), "parux", "Qwen3.8-27B-Q4_K_M.gguf");
        string redundantHop = Path.Combine(Path.GetTempPath(), "parux", "..", "parux", "Qwen3.8-27B-Q4_K_M.gguf");

        Assert.Same(
            ActiveModelSubstitution.None,
            ActiveModelSubstitution.Evaluate(redundantHop, canonical, true, true, true));
    }

    [Fact]
    public void Casing_is_decided_by_the_filesystem_not_by_the_string()
    {
        string canonical = Path.Combine(Path.GetTempPath(), "parux", "Qwen3.8-27B-Q4_K_M.gguf");
        bool caseSensitive =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux);

        ActiveModelSubstitution.Result result = ActiveModelSubstitution.Evaluate(
            canonical, canonical.ToUpperInvariant(), true, true, true);

        Assert.Equal(caseSensitive, result.IsSubstituted);
    }
}
