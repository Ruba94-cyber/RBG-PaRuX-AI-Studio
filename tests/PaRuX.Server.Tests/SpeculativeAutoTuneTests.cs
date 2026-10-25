using System.Text;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Decision logic for the speculative-decoding auto-tune: an optimisation is enabled only on a
/// measured gain of at least 5%, never when the model has no self-contained MTP head, and the
/// embedded MTP candidate needs no draft model path. Uses fake measurements and tiny fake GGUFs.
/// </summary>
public sealed class SpeculativeAutoTuneTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-autotune-" + Guid.NewGuid().ToString("N"));

    public SpeculativeAutoTuneTests()
    {
        Directory.CreateDirectory(_directory);
        GgufMetadataReader.InvalidateCache();
    }

    public void Dispose()
    {
        GgufMetadataReader.InvalidateCache();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static void Str(BinaryWriter w, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    private string WriteGguf(string name, params string[] tensorNames)
    {
        string path = Path.Combine(_directory, name);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x46554747u); w.Write(3u); w.Write((ulong)tensorNames.Length); w.Write(1ul);
        Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen35");
        ulong offset = 0;
        foreach (string t in tensorNames)
        {
            Str(w, t); w.Write(1u); w.Write(8ul); w.Write(0u); w.Write(offset); offset += 8;
        }
        long pad = (32 - stream.Position % 32) % 32;
        w.Write(new byte[pad]);
        w.Write(new byte[8 * tensorNames.Length]);
        return path;
    }

    [Fact]
    public void Gguf_with_nextn_tensors_reports_embedded_mtp_head()
    {
        string withMtp = WriteGguf("mtp.gguf", "blk.0.attn_q.weight", "blk.2.nextn.eh_proj.weight");
        string without = WriteGguf("plain.gguf", "blk.0.attn_q.weight", "blk.1.ffn_up.weight");
        Assert.True(GgufMetadataReader.Read(withMtp).HasEmbeddedMtpHead);
        Assert.False(GgufMetadataReader.Read(without).HasEmbeddedMtpHead);
    }

    [Fact]
    public void Embedded_mtp_candidate_uses_the_model_itself_and_no_draft_path()
    {
        var baseline = new LlamaPerformanceProfileDto { Id = "base" };
        List<LlamaPerformanceProfileDto> withMtp = RuntimeOptimizationService.BuildSpeculativeCandidates(
            baseline, Path.Combine(_directory, "missing.gguf"), "chat", hasEmbeddedMtpHead: true);
        LlamaPerformanceProfileDto mtp = Assert.Single(withMtp, c => c.SpeculativeType == "draft-mtp");
        Assert.True(string.IsNullOrEmpty(mtp.SpeculativeDraftModelPath));
        Assert.True(mtp.SpeculativeDraftMax > 0);

        var without = RuntimeOptimizationService.BuildSpeculativeCandidates(
            baseline, Path.Combine(_directory, "missing.gguf"), "chat", hasEmbeddedMtpHead: false);
        Assert.DoesNotContain(without, c => c.SpeculativeType == "draft-mtp");
    }

    private static RuntimeOptimizationServerResult Result(string spec, double decode, double ttft = 300, double? accept = null) => new()
    {
        Status = "completed",
        Profile = new LlamaPerformanceProfileDto { SpeculativeType = spec },
        DecodeTokensPerSecond = decode,
        PromptTokensPerSecond = 1000,
        TimeToFirstTokenMilliseconds = ttft,
        SustainedDecodeTokensPerSecond = decode,
        DraftTokens = accept is null ? 0 : 100,
        DraftTokensAccepted = accept is null ? 0 : (int)accept.Value,
        DraftAcceptancePercent = accept
    };

    [Theory]
    [InlineData(52.4, false)]   // +4.8%: below the 5% bar, keep the previous plan
    [InlineData(54.0, true)]    // +8%
    [InlineData(80.0, true)]
    public void Speculative_mode_is_enabled_only_at_five_percent_measured_gain(double decode, bool expectedEnabled)
    {
        RuntimeOptimizationServerResult baseline = Result("none", 50);
        RuntimeOptimizationServerResult spec = Result("draft-mtp", decode, accept: 70);
        RuntimeOptimizationService.Score(new[] { baseline, spec }, "turbo");
        RuntimeOptimizationServerResult winner = RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, spec }, baseline);
        Assert.Equal(expectedEnabled, !ReferenceEquals(winner, baseline));
    }

    [Fact]
    public void Speculative_mode_that_changes_the_calibration_output_is_never_enabled()
    {
        RuntimeOptimizationServerResult baseline = Result("none", 50);
        baseline.OutputDigest = "aaaa";
        RuntimeOptimizationServerResult changed = Result("ngram-mod", 150, accept: 90);
        changed.OutputDigest = "bbbb";
        RuntimeOptimizationService.Score(new[] { baseline, changed }, "turbo");
        Assert.Same(baseline, RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, changed }, baseline));

        RuntimeOptimizationServerResult identical = Result("draft-mtp", 150, accept: 90);
        identical.OutputDigest = "aaaa";
        RuntimeOptimizationService.Score(new[] { baseline, identical }, "turbo");
        Assert.Same(identical, RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, identical }, baseline));
    }

    [Fact]
    public void Output_digest_ignores_nothing_but_is_stable_and_order_sensitive()
    {
        Assert.Equal(LlamaServerPerformanceProbe.DigestOutput("a", "b"), LlamaServerPerformanceProbe.DigestOutput("a", "b"));
        Assert.NotEqual(LlamaServerPerformanceProbe.DigestOutput("a", "b"), LlamaServerPerformanceProbe.DigestOutput("b", "a"));
        Assert.Null(LlamaServerPerformanceProbe.DigestOutput(null, ""));
    }

    [Fact]
    public void Dedicated_nvidia_cuda_launch_uses_the_measured_wide_batch_not_the_igpu_128_ubatch()
    {
        // Measured on the RTX 5080 with the installed CUDA build: ubatch 128 -> 4564 tok/s prefill on the
        // 4B model, 1024 -> 9199, 2048 -> 9751; the 27B model 1679 -> 2128 -> 2204.
        Assert.Equal((2048, 1024), LocalAiProcessManager.DefaultBatchSizes(heavyCuda: true, fallbackBatch: 512, fallbackUbatch: 128));
        // iGPU / Vulkan / CPU paths keep their measured small values.
        Assert.Equal((512, 128), LocalAiProcessManager.DefaultBatchSizes(heavyCuda: false, fallbackBatch: 512, fallbackUbatch: 128));
        Assert.Equal((256, 64), LocalAiProcessManager.DefaultBatchSizes(heavyCuda: false, fallbackBatch: 256, fallbackUbatch: 64));
    }

    [Fact]
    public void Embedded_mtp_also_races_the_combined_mtp_plus_ngram_chain()
    {
        var baseline = new LlamaPerformanceProfileDto { Id = "base" };
        var list = RuntimeOptimizationService.BuildSpeculativeCandidates(
            baseline, Path.Combine(_directory, "missing.gguf"), "coding", hasEmbeddedMtpHead: true);
        LlamaPerformanceProfileDto chain = Assert.Single(list, c => c.SpeculativeType == "draft-mtp,ngram-mod");
        Assert.True(string.IsNullOrEmpty(chain.SpeculativeDraftModelPath));
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(chain, hasCuda: true);
        int at = args.IndexOf("--spec-type");
        Assert.True(at >= 0);
        Assert.Equal("draft-mtp,ngram-mod", args[at + 1]);
    }
}
