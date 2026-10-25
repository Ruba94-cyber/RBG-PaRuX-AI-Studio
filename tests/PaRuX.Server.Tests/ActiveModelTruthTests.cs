using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the 2026-09-02 fix for a real defect found during a live Computer Use smoke run: the
/// runtime reported the model a role was CONFIGURED to use rather than the one it had actually
/// loaded. On a machine with no heavy GPU the flagship 27B+vision profile is deliberately replaced
/// by the text-only 4B fallback, so "standard" was reported as active and vision-capable while a
/// model that cannot see an image was serving. Anything downstream that needed vision therefore
/// failed with a misleading error instead of an honest one, and prompt-cache scoping compared
/// against a model identity that was never loaded.
///
/// Two independent halves are asserted here: that the launched arguments are read back correctly
/// (LocalAiProcessManager is the only writer of that record now), and that the substitution is
/// detected and explained rather than smoothed over.
/// </summary>
public sealed class ActiveModelTruthTests
{
    private const string Configured = @"C:\LocalAI\models\Qwen3.8-27B-Q4_K_M.gguf";
    private const string Fallback = @"C:\LocalAI\models\Qwen3.5-4B-Q4_K_M.gguf";

    // ---- The observation half: what the process was really launched with ----

    [Fact]
    public void Argument_reader_returns_the_launched_model_and_projector()
    {
        string[] args = { "--model", Configured, "--host", "127.0.0.1", "--mmproj", @"C:\mm.gguf" };
        Assert.Equal(Configured, LocalAiProcessManager.ReadArgumentValue(args, "--model"));
        Assert.Equal(@"C:\mm.gguf", LocalAiProcessManager.ReadArgumentValue(args, "--mmproj"));
    }

    [Fact]
    public void Argument_reader_reports_no_projector_for_a_text_only_launch()
    {
        string[] args = { "--model", Fallback, "--host", "127.0.0.1", "--port", "8080" };
        Assert.Equal(Fallback, LocalAiProcessManager.ReadArgumentValue(args, "--model"));
        Assert.Null(LocalAiProcessManager.ReadArgumentValue(args, "--mmproj"));
    }

    [Fact]
    public void Argument_reader_ignores_a_dangling_option_with_no_value()
    {
        // A trailing option must not be read as if the next (absent) token were its value, and
        // must not throw - this record is written on the launch hot path.
        string[] args = { "--host", "127.0.0.1", "--model" };
        Assert.Null(LocalAiProcessManager.ReadArgumentValue(args, "--model"));
    }

    [Fact]
    public void Argument_reader_takes_the_effective_last_value_when_an_option_repeats()
    {
        // llama-server applies the last occurrence, so the record has to agree with the process.
        string[] args = { "--model", Configured, "--model", Fallback };
        Assert.Equal(Fallback, LocalAiProcessManager.ReadArgumentValue(args, "--model"));
    }

    // ---- The reporting half: configured vs actually loaded ----

    [Fact]
    public void No_substitution_is_reported_when_the_configured_model_is_the_one_loaded()
    {
        ActiveModelSubstitution.Result result = ActiveModelSubstitution.Evaluate(
            Configured, Configured, configuredHasVision: true, loadedHasVision: true, gpuDetected: true);

        Assert.False(result.IsSubstituted);
        Assert.False(result.LostVision);
        Assert.Null(result.Note);
    }

    /// <summary>
    /// A redundant "../" hop is another spelling of one file, not a swapped model - asserted with a
    /// path built for whichever platform this is running on, since the Windows literal used
    /// elsewhere in this class is just an opaque string off Windows.
    /// </summary>
    [Fact]
    public void A_redundant_path_hop_to_the_same_file_is_not_a_substitution()
    {
        string canonical = Path.Combine(Path.GetTempPath(), "parux-models", "Qwen3.8-27B-Q4_K_M.gguf");
        string redundantHop = Path.Combine(
            Path.GetTempPath(), "parux-models", "..", "parux-models", "Qwen3.8-27B-Q4_K_M.gguf");

        Assert.False(ActiveModelSubstitution.Evaluate(
            redundantHop, canonical,
            configuredHasVision: true, loadedHasVision: true, gpuDetected: true).IsSubstituted);
    }

    /// <summary>
    /// Casing is where "the same file" stops being a platform-independent question. Windows and the
    /// default macOS volume treat two spellings as one file; Linux does not, and reporting "no
    /// substitution" there for two genuinely different models would be exactly the silent swap this
    /// class exists to surface. So the expectation follows the filesystem rather than the string.
    /// </summary>
    [Fact]
    public void Casing_counts_as_a_different_model_only_where_the_filesystem_says_it_does()
    {
        string canonical = Path.Combine(Path.GetTempPath(), "parux-models", "Qwen3.8-27B-Q4_K_M.gguf");
        bool caseSensitiveFilesystem = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);

        Assert.Equal(
            caseSensitiveFilesystem,
            ActiveModelSubstitution.Evaluate(
                canonical, canonical.ToUpperInvariant(),
                configuredHasVision: true, loadedHasVision: true, gpuDetected: true).IsSubstituted);
    }

    [Fact]
    public void The_no_gpu_fallback_is_reported_named_and_explained()
    {
        ActiveModelSubstitution.Result result = ActiveModelSubstitution.Evaluate(
            Configured, Fallback, configuredHasVision: true, loadedHasVision: false, gpuDetected: false);

        Assert.True(result.IsSubstituted);
        Assert.True(result.LostVision);
        Assert.NotNull(result.Note);
        // Both models named, the hardware cause stated, and the lost capability spelled out - the
        // three things the old "configured model, vision available" answer hid.
        Assert.Contains("Qwen3.8-27B-Q4_K_M.gguf", result.Note!);
        Assert.Contains("Qwen3.5-4B-Q4_K_M.gguf", result.Note!);
        Assert.Contains("12GB VRAM", result.Note!);
        Assert.Contains("text-only", result.Note!);
    }

    [Fact]
    public void A_substitution_that_keeps_vision_does_not_claim_image_input_was_lost()
    {
        ActiveModelSubstitution.Result result = ActiveModelSubstitution.Evaluate(
            Configured, Fallback, configuredHasVision: true, loadedHasVision: true, gpuDetected: true);

        Assert.True(result.IsSubstituted);
        Assert.False(result.LostVision);
        Assert.DoesNotContain("text-only", result.Note!);
        // With a capable GPU present the fallback reason does not apply, so it must not be invented.
        Assert.DoesNotContain("12GB VRAM", result.Note!);
    }

    [Fact]
    public void Nothing_is_claimed_when_the_runtime_state_is_unknown()
    {
        // No text process running, or a launch whose arguments carried no --model: the honest
        // answer is "no observation", never "no substitution happened".
        Assert.False(ActiveModelSubstitution.Evaluate(Configured, null, true, false, false).IsSubstituted);
        Assert.False(ActiveModelSubstitution.Evaluate(null, Fallback, true, false, false).IsSubstituted);
        Assert.False(ActiveModelSubstitution.Evaluate(Configured, "   ", true, false, false).IsSubstituted);
    }
}
