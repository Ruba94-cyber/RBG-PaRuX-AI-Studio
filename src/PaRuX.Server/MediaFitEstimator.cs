using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum MediaFitClass { Fits, Offload, TooLarge, Unknown }

/// <summary>Detected hardware as the estimator needs it. Null means "could not be detected", never zero.</summary>
internal sealed record MediaHardware(long? VramBytes, long? RamBytes)
{
    internal static MediaHardware From(GpuInventoryStatus? gpu, long? systemRamBytes)
    {
        long? vram = null;
        if (gpu is { Adapters.Count: > 0 })
        {
            long best = gpu.Adapters.Where(a => !a.IsSoftware).Select(a => a.DedicatedVideoMemoryBytes).DefaultIfEmpty(0).Max();
            vram = best;
        }
        return new MediaHardware(vram, systemRamBytes is > 0 ? systemRamBytes : null);
    }
}

/// <summary>What one variant needs. Weight bytes come from the exact listing when known, else from parameters x precision.</summary>
internal sealed record MediaFitInput(
    string CapabilityId, string Precision, double ParametersBillion, long? ListedWeightBytes,
    long TextEncoderBytes, long VaeBytes, double Megapixels, int Frames, double Seconds);

internal sealed record MediaFitResult(MediaFitClass Class, long RequiredVramBytes, long WeightBytes, string Reason)
{
    internal string Code => Class switch
    {
        MediaFitClass.Fits => "fits",
        MediaFitClass.Offload => "offload",
        MediaFitClass.TooLarge => "too_large",
        _ => "unknown"
    };
}

/// <summary>
/// Planning estimate of whether a media model runs on this PC: Fits (all in VRAM), Offload (weights spill to system RAM,
/// slower), TooLarge, or Unknown when the hardware needed to decide was not detected. It is arithmetic on declared sizes,
/// never a benchmark, and it errs towards the worse class: 10% of VRAM is held back and 8 GiB of RAM is left to the OS.
/// </summary>
internal static class MediaFitEstimator
{
    internal const double VramUsableFraction = 0.90;
    internal const double RamUsableFraction = 0.80;
    internal const long RamReserveBytes = 8L * 1024 * 1024 * 1024;
    internal const long MinimumUsableVramBytes = 2L * 1024 * 1024 * 1024;
    private const double Gib = 1024.0 * 1024 * 1024;

    /// <summary>Average stored bytes per parameter, scales included. Null for a precision this table does not know.</summary>
    internal static double? BytesPerParameter(string precision) => precision.Trim().ToLowerInvariant() switch
    {
        "fp32" => 4.0,
        "fp16" or "bf16" => 2.0,
        "fp8" or "fp8_scaled" or "int8" => 1.0,
        "nvfp4" or "fp4" => 0.56,
        "q8_0" => 1.0625,
        "q6_k" => 0.82,
        "q5_k_m" or "q5_0" => 0.69,
        "q4_k_m" or "q4_k_s" or "q4_0" => 0.60,
        "q3_k_m" => 0.49,
        "q2_k" => 0.33,
        _ => null
    };

    /// <summary>Working memory on top of the weights for the requested output size/length (planning heuristic).</summary>
    internal static long ActivationBytes(MediaFitInput i)
    {
        double gib = i.CapabilityId switch
        {
            ModelCapabilityIds.ImageGenerate => 1.0 + 1.0 * i.Megapixels,
            ModelCapabilityIds.ImageEdit => 1.5 + 1.5 * i.Megapixels,
            ModelCapabilityIds.VideoGenerate => 2.0 + 0.06 * i.Megapixels * Math.Max(1, i.Frames),
            "audio.music" => 1.0 + 0.02 * Math.Max(0, i.Seconds),
            ModelCapabilityIds.AudioSpeak => 0.5,
            _ => 2.0
        };
        return (long)(gib * Gib);
    }

    internal static MediaFitResult Estimate(MediaFitInput input, MediaHardware hw)
    {
        long weights;
        if (input.ListedWeightBytes is > 0) weights = input.ListedWeightBytes.Value;
        else if (BytesPerParameter(input.Precision) is { } bpp && input.ParametersBillion > 0)
            weights = (long)(input.ParametersBillion * 1e9 * bpp);
        else
            return new MediaFitResult(MediaFitClass.Unknown, 0, 0, $"Size unknown: precision \"{input.Precision}\" has no size rule and no exact listing is held.");

        long activation = ActivationBytes(input);
        // The text encoder runs first and is then released, so it is not part of the peak video memory; it is counted
        // against system RAM when weights have to spill.
        long required = weights + input.VaeBytes + activation;

        if (hw.VramBytes is null)
            return new MediaFitResult(MediaFitClass.Unknown, required, weights, $"Needs about {Gb(required)} of video memory; this PC's GPU memory could not be detected, so fit is not claimed.");
        if (hw.VramBytes < MinimumUsableVramBytes)
            return new MediaFitResult(MediaFitClass.TooLarge, required, weights, $"Needs about {Gb(required)}; no GPU with at least 2 GB of dedicated video memory was detected.");

        double usable = hw.VramBytes.Value * VramUsableFraction;
        if (required <= usable)
            return new MediaFitResult(MediaFitClass.Fits, required, weights, $"Needs about {Gb(required)} of {Gb(hw.VramBytes.Value)} video memory: fits on the GPU.");

        // Activations and the VAE must stay on the GPU; only weights can spill to system RAM.
        long mustStayOnGpu = activation + input.VaeBytes;
        if (mustStayOnGpu > usable)
            return new MediaFitResult(MediaFitClass.TooLarge, required, weights, $"Even with weights offloaded, working memory (~{Gb(mustStayOnGpu)}) exceeds the usable {Gb((long)usable)} of video memory.");
        if (hw.RamBytes is null)
            return new MediaFitResult(MediaFitClass.Unknown, required, weights, $"Needs about {Gb(required)}, more than the usable video memory; system RAM could not be detected so offloading is not claimed.");

        double spill = required - usable;
        double ramUsable = Math.Max(0, hw.RamBytes.Value - RamReserveBytes) * RamUsableFraction;
        if (spill + input.TextEncoderBytes <= ramUsable)
            return new MediaFitResult(MediaFitClass.Offload, required, weights, $"Needs about {Gb(required)}; about {Gb((long)spill)} would spill to system RAM, so it runs but slower.");
        return new MediaFitResult(MediaFitClass.TooLarge, required, weights, $"Needs about {Gb(required)}; video memory plus usable system RAM (~{Gb((long)(usable + ramUsable))}) is not enough.");
    }

    private static string Gb(long bytes) => $"{bytes / Gib:0.#} GB";
}
