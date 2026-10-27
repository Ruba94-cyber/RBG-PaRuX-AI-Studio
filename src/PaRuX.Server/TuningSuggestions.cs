using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>What the text suggestions need to know. Everything is measured or read locally; nothing here is guessed from the network.</summary>
internal sealed record TextTuningFacts(
    string ModelFileName, long ModelBytes, string Backend, bool HasNvidiaGpu, long VramBytes, long SystemRamBytes,
    double? MeasuredTokensPerSecond, int ContextSize, long? KvBytesAtContext, bool HasLocalDraft, string Workload);

/// <summary>What the media suggestions need to know (from the calibration snapshot and its measured candidates).</summary>
internal sealed record MediaTuningFacts(
    string ModelLabel, string GpuName, bool IsNvidia, bool TorchHasCuda, long VramBytes, long DiffusionBytes,
    long? PeakVramBytes, double? BaselineWarmSeconds, int OutputWidth, int OutputHeight);

/// <summary>
/// Turns "no faster profile on the current settings" into concrete next steps. Pure: no network, no download, no process.
/// Repository listings are passed in by the caller. Gains are always labelled as estimates unless they were measured here.
/// </summary>
internal static class TuningSuggestions
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static string OptimalText(double? tokensPerSecond) =>
        tokensPerSecond is > 0
            ? "Already optimal for this PC (measured " + tokensPerSecond.Value.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s)"
            : "Already optimal for this PC (no faster setting than the defaults was measured)";

    internal static string MediaOptimalText(double? warmSeconds) =>
        warmSeconds is > 0
            ? "Already optimal for this PC (measured " + warmSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s per image)"
            : "Already optimal for this PC (no faster launch flags than the defaults were measured)";

    private static string Gib(long bytes) => (bytes / (double)GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    /// <summary>Weights plus the usual runtime overhead, the same rule of thumb the launch planner uses for a quick fit check.</summary>
    internal static long EstimatedResidentBytes(long modelBytes) => (long)(modelBytes * 1.16) + 768L * 1024 * 1024;

    // ---------------------------------------------------------------- text

    internal static IReadOnlyList<ModelTuningSuggestionDto> ForText(
        TextTuningFacts f, IReadOnlyList<(string FileName, long SizeBytes)>? repoFiles, string? repoId, bool final = true)
    {
        var list = new List<ModelTuningSuggestionDto>();
        bool gpu = f.VramBytes > 0;
        bool fitsGpu = gpu && EstimatedResidentBytes(f.ModelBytes) + (f.KvBytesAtContext ?? 0) <= (long)(f.VramBytes * 0.92);
        bool tight = gpu ? !fitsGpu : f.SystemRamBytes > 0 && EstimatedResidentBytes(f.ModelBytes) + (f.KvBytesAtContext ?? 0) > (long)(f.SystemRamBytes * 0.6);

        if (repoFiles is { Count: > 0 } && repoId is not null)
        {
            if (SmallerQuant(f, repoFiles, repoId, gpu, fitsGpu) is { } quant) list.Add(quant);
            if (!f.HasLocalDraft && DraftHeadProposals.Pick(repoFiles, f.ModelFileName) is { } head)
                list.Add(new ModelTuningSuggestionDto
                {
                    Kind = "draft_model",
                    Title = "Add a speculative-decoding draft head",
                    Detail = $"{head.FileName} ({head.SizeBytes / 1048576.0:0} MB) from the same repository can be installed next to this model; PaRuX tests it automatically and keeps it only if output stays identical and it is faster.",
                    ExpectedGain = "typically 1.3x to 2x faster decoding on code and structured text (estimate until measured here)",
                    ActionKind = "download_file", RepoId = repoId, FileName = head.FileName, SizeBytes = head.SizeBytes
                });
        }

        if (tight && f.ContextSize >= 16384 && f.KvBytesAtContext is > 0)
        {
            int half = Math.Max(8192, f.ContextSize / 2);
            long saved = f.KvBytesAtContext.Value - (long)(f.KvBytesAtContext.Value * (half / (double)f.ContextSize));
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "lower_context",
                Title = $"Lower the context window to {half:N0} tokens",
                Detail = $"The model and its {f.ContextSize:N0}-token KV cache are close to this PC's memory. Halving the window frees about {Gib(saved)} (estimate) and lets more of the model stay on the fast memory.",
                ExpectedGain = "frees " + Gib(saved) + "; faster only if the model currently spills to the CPU (estimate)",
                ActionKind = "info"
            });
        }

        if (string.Equals(f.Backend, "text-vulkan", StringComparison.OrdinalIgnoreCase) && f.HasNvidiaGpu)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "switch_backend",
                Title = "Use the CUDA runtime build",
                Detail = "This NVIDIA GPU is currently served by the Vulkan build. Install the CUDA build in Runtime Manager; PaRuX re-tests the model on it automatically.",
                ExpectedGain = "often noticeably faster prompt processing and decoding on NVIDIA GPUs (estimate)",
                ActionKind = "open_runtime_manager"
            });
        else if (!gpu && f.Backend.Length > 0)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "switch_backend",
                Title = "No usable GPU memory was detected",
                Detail = "The model runs on the CPU. A smaller quantization is the biggest lever on a CPU; a GPU runtime (CUDA or Vulkan) in Runtime Manager helps if a supported GPU is installed.",
                ExpectedGain = "depends on the GPU (estimate)",
                ActionKind = "open_runtime_manager"
            });

        if (final && list.Count == 0)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "info",
                Title = "Nothing else to gain on this PC right now",
                Detail = "PaRuX measured KV cache, batch sizes, GPU offload, flash attention, threads, serving concurrency and speculative decoding for this model. It tests again after a runtime update or when the hardware changes.",
                ExpectedGain = string.Empty,
                ActionKind = "info"
            });
        return list;
    }

    private static ModelTuningSuggestionDto? SmallerQuant(
        TextTuningFacts f, IReadOnlyList<(string FileName, long SizeBytes)> repoFiles, string repoId, bool gpu, bool fitsGpu)
    {
        string? current = GgufFileNaming.ParseQuantization(f.ModelFileName);
        if (current is null || f.ModelBytes <= 0) return null;
        string stem = StemWithoutQuant(f.ModelFileName, current);
        var options = repoFiles
            .Where(r => r.SizeBytes > 0 && r.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                && !GgufFileNaming.IsAuxiliaryHead(r.FileName) && !GgufFileNaming.IsProjector(r.FileName)
                && !(GgufFileNaming.TryParseSplitPart(r.FileName, out GgufFileNaming.SplitPart part) && part.Total > 1)
                && GgufFileNaming.ParseQuantization(r.FileName) is { } q && !IsTooLossy(q)
                && !string.Equals(q, current, StringComparison.OrdinalIgnoreCase)
                && string.Equals(StemWithoutQuant(r.FileName, q), stem, StringComparison.OrdinalIgnoreCase)
                && r.SizeBytes <= f.ModelBytes * 0.88)
            .ToList();
        if (options.Count == 0) return null;
        // Prefer the largest (best quality) smaller file; if the model does not fit the GPU, the largest one that does.
        (string FileName, long SizeBytes) pick = options.OrderByDescending(o => o.SizeBytes).First();
        bool makesFit = false;
        if (gpu && !fitsGpu)
        {
            var fitting = options.Where(o => EstimatedResidentBytes(o.SizeBytes) + (f.KvBytesAtContext ?? 0) <= (long)(f.VramBytes * 0.92))
                .OrderByDescending(o => o.SizeBytes).ToList();
            if (fitting.Count > 0) { pick = fitting[0]; makesFit = true; }
        }
        string quant = GgufFileNaming.ParseQuantization(pick.FileName)!;
        double gain = (f.ModelBytes / (double)pick.SizeBytes - 1.0) * 100.0;
        return new ModelTuningSuggestionDto
        {
            Kind = "smaller_quant",
            Title = $"Try the {quant} version of this model",
            Detail = $"{pick.FileName} is {Gib(pick.SizeBytes)} instead of {Gib(f.ModelBytes)}"
                + (makesFit ? " and would fit in this GPU's memory entirely" : string.Empty)
                + ". Quality drops slightly with lower precision; PaRuX tunes it automatically once installed.",
            ExpectedGain = makesFit
                ? "fits fully in GPU memory: usually several times faster than running partly on the CPU (estimate)"
                : $"about +{gain:0}% tokens/s from the smaller file (estimate; decoding is memory-bound)",
            ActionKind = "download_file", RepoId = repoId, FileName = pick.FileName, SizeBytes = pick.SizeBytes
        };
    }

    /// <summary>Quality floor: 1-bit and 2-bit quantizations are never suggested as a "faster version".</summary>
    private static bool IsTooLossy(string quant) =>
        quant.StartsWith("Q2", StringComparison.OrdinalIgnoreCase) || quant.StartsWith("IQ1", StringComparison.OrdinalIgnoreCase)
        || quant.StartsWith("IQ2", StringComparison.OrdinalIgnoreCase);

    private static string StemWithoutQuant(string fileName, string quant)
    {
        string name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = Regex.Replace(name, @"\.gguf$", string.Empty, RegexOptions.IgnoreCase);
        name = Regex.Replace(name, Regex.Escape(quant), string.Empty, RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"[-_. ]+", "-").Trim('-').ToLowerInvariant();
    }

    // ---------------------------------------------------------------- media

    internal static IReadOnlyList<ModelTuningSuggestionDto> ForMedia(MediaTuningFacts f)
    {
        var list = new List<ModelTuningSuggestionDto>();
        string label = (f.ModelLabel ?? string.Empty).ToLowerInvariant();

        if (f.IsNvidia && !f.TorchHasCuda)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "switch_backend",
                Title = "Repair the media engine's CUDA support",
                Detail = "This ComfyUI's PyTorch cannot use the NVIDIA GPU, so images render on the CPU. Repair or update the media engine in Runtime Manager.",
                ExpectedGain = "typically 10x or more faster than CPU rendering (estimate)",
                ActionKind = "open_runtime_manager"
            });

        bool distilled = Regex.IsMatch(label, @"turbo|schnell|lightning|lcm|hyper|distill|[-_ ]4[-_ ]?step|[-_ ]8[-_ ]?step|klein");
        if (!distilled)
        {
            string variant;
            if (Regex.IsMatch(label, @"z[-_ ]?image")) variant = "the Z-Image Turbo checkpoint (about 8 steps)";
            else if (label.Contains("flux")) variant = "FLUX.1 schnell (4 steps) or a FLUX Turbo LoRA";
            else if (Regex.IsMatch(label, @"sdxl|xl")) variant = "an SDXL Lightning or Turbo checkpoint (4 to 8 steps)";
            else if (Regex.IsMatch(label, @"wan|ltx|hunyuan|video")) variant = "a step-distilled 4-step LoRA for this video model";
            else variant = "a step-distilled (Turbo, Lightning or LCM) variant of this model family";
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "media_variant",
                Title = "Use a distilled few-step variant",
                Detail = $"Launch flags are already the best measured here; the remaining lever is the number of sampling steps. Look for {variant} in Model Hub.",
                ExpectedGain = "typically 3x to 6x faster (4 to 8 steps instead of 20 to 30; estimate), with a small style change",
                ActionKind = "info"
            });
        }

        bool lowPrecisionAlready = Regex.IsMatch(label, @"fp8|nvfp4|fp4|gguf|q[2-8][-_]|int8");
        if (!lowPrecisionAlready && f.DiffusionBytes > 0 && f.VramBytes > 0 && f.DiffusionBytes > (long)(f.VramBytes * 0.55))
        {
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "media_precision",
                Title = f.IsNvidia ? "Use an fp8 checkpoint" : "Use a quantized (GGUF Q5/Q4) checkpoint",
                Detail = $"The diffusion weights take {Gib(f.DiffusionBytes)} of this GPU's {Gib(f.VramBytes)}. An 8-bit version halves that (about {Gib(f.DiffusionBytes / 2)} saved, estimate), so the model stays resident instead of streaming from system memory.",
                ExpectedGain = "saves about " + Gib(f.DiffusionBytes / 2) + (f.IsNvidia ? "; on RTX 40/50 GPUs also faster matrix math (estimate)" : " (estimate)"),
                ActionKind = "info"
            });
        }

        if (f.OutputWidth >= 1024 && f.OutputHeight >= 1024 && f.PeakVramBytes is > 0 && f.VramBytes > 0 && f.PeakVramBytes > (long)(f.VramBytes * 0.9))
        {
            int w = (int)(f.OutputWidth * 0.75) / 64 * 64, h = (int)(f.OutputHeight * 0.75) / 64 * 64;
            double saved = (1.0 - (double)w * h / ((double)f.OutputWidth * f.OutputHeight)) * 100.0;
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "media_preset",
                Title = $"Generate at {w}x{h}, then upscale",
                Detail = $"Peak memory reached {Gib(f.PeakVramBytes.Value)} of {Gib(f.VramBytes)} at {f.OutputWidth}x{f.OutputHeight}. A smaller base size keeps the run inside GPU memory; upscale afterwards for the final size.",
                ExpectedGain = $"about {saved:0}% less compute per image (estimate)",
                ActionKind = "info"
            });
        }

        if (!f.IsNvidia)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "info",
                Title = "Launch-flag tuning needs an NVIDIA GPU",
                Detail = "On this GPU the media engine keeps its defaults. The variants above are the way to make it faster here.",
                ExpectedGain = string.Empty,
                ActionKind = "info"
            });

        if (list.Count == 0)
            list.Add(new ModelTuningSuggestionDto
            {
                Kind = "info",
                Title = "Nothing else to gain on this PC right now",
                Detail = "PaRuX measured memory strategy, attention kernel, precision options and VAE handling for this GPU. It tests again after a ComfyUI or PyTorch update or a hardware change.",
                ExpectedGain = string.Empty,
                ActionKind = "info"
            });
        return list;
    }
}
