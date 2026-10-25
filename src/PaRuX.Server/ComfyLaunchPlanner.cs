using System.Globalization;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>What the installed ComfyUI's <c>main.py --help</c> says it accepts. Only flags found here are ever emitted.</summary>
internal sealed record ComfyCapabilities(IReadOnlySet<string> Flags, IReadOnlySet<string> FastOptions)
{
    internal bool Supports(string flag) => Flags.Contains(flag);
    internal static readonly ComfyCapabilities None =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

internal static class ComfyHelpParser
{
    private static readonly Regex FlagToken = new(@"(?<![\w-])(--[a-z0-9][a-z0-9_-]*)", RegexOptions.Compiled);
    private static readonly Regex FastLine = new(@"valid optimizations:\s*(?<list>[a-z0-9_\s]+?)(?:\r?\n\s*--|\r?\n\s*\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Extracts every long option and the <c>--fast</c> sub-options from argparse help text. Pure; unknown text yields
    /// an empty capability set, which makes the planner emit no tuning flags at all (the safe direction).</summary>
    internal static ComfyCapabilities Parse(string? helpText)
    {
        if (string.IsNullOrWhiteSpace(helpText)) return ComfyCapabilities.None;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FlagToken.Matches(helpText)) flags.Add(m.Groups[1].Value);
        var fast = new HashSet<string>(StringComparer.Ordinal);
        Match f = FastLine.Match(helpText);
        if (f.Success)
            foreach (string option in f.Groups["list"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (option.All(c => char.IsLetterOrDigit(c) || c == '_')) fast.Add(option);
        return new ComfyCapabilities(flags, fast);
    }
}

/// <summary>Facts about the GPU the plan is made for. Free VRAM is what is free right now (other residents already subtracted).</summary>
internal sealed record ComfyGpuFacts(
    string Name, long TotalVramBytes, long FreeVramBytes, double? ComputeCapability, string? DriverVersion, bool IsNvidia, bool IsWindows)
{
    internal bool BlackwellOrAda => IsNvidia && ComputeCapability is >= 8.9;
    internal bool AmpereOrNewer => IsNvidia && ComputeCapability is >= 8.0;
}

/// <summary>Sizes of the weights the workload loads. A zero means unknown, which is planned for conservatively.</summary>
internal sealed record ComfyModelFootprint(long DiffusionBytes, long TextEncoderBytes, long VaeBytes, int OutputWidth, int OutputHeight)
{
    internal long WeightBytes => DiffusionBytes + TextEncoderBytes + VaeBytes;
}

/// <summary>What is installed beside ComfyUI in its Python environment and whether it works (importable on this GPU).</summary>
internal sealed record ComfyEnvironmentFacts(bool TorchHasCuda, bool SageAttentionWorks, bool XformersWorks, bool FlashAttentionWorks);

/// <summary>"Safe" changes only how memory is placed and never the numerics, so a plan made with it must reproduce the same image.
/// "Aggressive" may enable lower-precision kernels; it is only ever adopted after a measured win AND an equivalence check.</summary>
internal enum ComfyPlanTier { Safe, Aggressive }

internal sealed record ComfyPlanDecision(string Flag, string? Value, string Reason, bool ChangesNumerics);

/// <summary>Launch-time flags plus workflow-level hints (text-encoder device, VAE tiling) that are not command-line options.</summary>
internal sealed record ComfyLaunchPlan(
    ComfyPlanTier Tier,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<ComfyPlanDecision> Decisions,
    string TextEncoderDevice,
    bool TiledVaeDecode)
{
    /// <summary>Stable text of the tuning flags; the identity used by the optimisation store and the benchmark history.</summary>
    internal string Key => string.Join(' ', Arguments);
    internal bool ChangesNumerics => Decisions.Any(d => d.ChangesNumerics);
}

/// <summary>
/// ComfyUI counterpart of the llama.cpp launch planner. Pure and deterministic: the same facts always yield the same arguments,
/// every emitted flag carries a reason, and a flag the installed ComfyUI does not list is never emitted. It reasons from free
/// VRAM and the model footprint; it does not claim the result is fastest - the calibration measures that.
/// </summary>
internal static class ComfyLaunchPlanner
{
    internal const long GiB = 1L << 30;
    /// <summary>Activations + sampler workspace beyond the weights, scaled by resolution (1024x1024 ~ 2 GiB).</summary>
    internal static long ActivationReserveBytes(ComfyModelFootprint m)
    {
        double megapixels = Math.Max(0.25, (double)Math.Max(1, m.OutputWidth) * Math.Max(1, m.OutputHeight) / 1_000_000.0);
        return (long)(GiB * Math.Max(1.0, 2.0 * megapixels / 1.05));
    }

    internal static ComfyLaunchPlan Plan(
        ComfyGpuFacts gpu, ComfyModelFootprint model, ComfyEnvironmentFacts env, ComfyCapabilities caps, ComfyPlanTier tier)
    {
        var args = new List<string>();
        var decisions = new List<ComfyPlanDecision>();
        void Emit(string flag, string? value, string reason, bool numerics = false)
        {
            if (!caps.Supports(flag)) return; // the installed ComfyUI does not know it: say nothing rather than break startup
            args.Add(flag);
            if (value is not null) args.Add(value);
            decisions.Add(new ComfyPlanDecision(flag, value, reason, numerics));
        }

        if (!gpu.IsNvidia || !env.TorchHasCuda)
        {
            decisions.Add(new ComfyPlanDecision("(none)", null,
                !env.TorchHasCuda ? "PyTorch in this ComfyUI has no CUDA support, so no GPU tuning applies" : "not an NVIDIA GPU: no CUDA-specific tuning", false));
            return new ComfyLaunchPlan(tier, args, decisions, "default", false);
        }

        long free = Math.Max(0, gpu.FreeVramBytes);
        long weights = model.WeightBytes;
        long reserve = ActivationReserveBytes(model);
        string gb(long b) => (b / (double)GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

        // ---- VRAM placement ------------------------------------------------------------------------------------------
        // DynamicVRAM (--enable-dynamic-vram) already streams weights on demand and makes --lowvram a no-op, so the placement
        // choice is between "everything resident" (no dynamic streaming) and "dynamic" (streaming, the PaRuX legacy default).
        bool unknown = model.DiffusionBytes <= 0;
        bool fitsResident = !unknown && weights + reserve + GiB <= free;
        string textEncoderDevice = "default";
        if (fitsResident)
        {
            Emit("--disable-dynamic-vram", null,
                $"weights {gb(weights)} + working set {gb(reserve)} fit in {gb(free)} free VRAM: keep everything resident instead of streaming");
            Emit(weights + reserve + 3 * GiB <= free ? "--gpu-only" : "--highvram", null,
                weights + reserve + 3 * GiB <= free
                    ? $"{gb(free - weights - reserve)} spare after weights: text encoder and VAE stay on the GPU too"
                    : "keep models in VRAM after use (no per-job unload/reload)");
        }
        else if (unknown)
        {
            Emit("--enable-dynamic-vram", null, "model size unknown: dynamic VRAM is the safe streaming default");
        }
        else
        {
            Emit("--enable-dynamic-vram", null,
                $"weights {gb(weights)} + working set {gb(reserve)} exceed {gb(free)} free VRAM: stream weights on demand");
            if (model.DiffusionBytes + reserve > free)
            {
                Emit("--lowvram", null, $"diffusion weights {gb(model.DiffusionBytes)} alone do not fit in {gb(free)}: low-VRAM mode");
                textEncoderDevice = "cpu";
            }
            else if (model.DiffusionBytes + model.TextEncoderBytes + reserve > free)
            {
                textEncoderDevice = "cpu";
            }
        }
        if (textEncoderDevice == "cpu")
            decisions.Add(new ComfyPlanDecision("text-encoder-device", "cpu",
                $"text encoder {gb(model.TextEncoderBytes)} + diffusion {gb(model.DiffusionBytes)} + working set do not fit together in {gb(free)}: encode on CPU/offload", false));
        if (!decisions.Any(d => d.Flag is "--gpu-only" or "--highvram" or "--lowvram" or "--enable-dynamic-vram" or "--disable-dynamic-vram"))
            decisions.Add(new ComfyPlanDecision("(vram-mode)", null, "no VRAM placement flag is supported by this ComfyUI: left to its default", false));

        // Display + other apps: leave headroom. 1 GiB matches the shipped default; small cards keep more.
        double reserveGb = gpu.TotalVramBytes <= 12 * GiB ? 1.5 : 1.0;
        Emit("--reserve-vram", reserveGb.ToString("0.0", CultureInfo.InvariantCulture),
            $"keep {reserveGb:0.0} GB free for Windows and other apps on a {gb(gpu.TotalVramBytes)} card");

        // ---- allocator -----------------------------------------------------------------------------------------------
        if (gpu.IsWindows && gpu.ComputeCapability is >= 7.0)
            Emit("--cuda-malloc", null, "async CUDA allocator reduces fragmentation and allocation stalls on this Windows NVIDIA GPU");

        // ---- attention backend ---------------------------------------------------------------------------------------
        if (caps.Supports("--use-pytorch-cross-attention") && !env.SageAttentionWorks)
            Emit("--use-pytorch-cross-attention", null,
                env.XformersWorks ? "PyTorch scaled-dot-product attention (fused flash/cuDNN kernels); xformers adds nothing on a current PyTorch"
                                  : "PyTorch scaled-dot-product attention is the supported fused backend here");
        if (tier == ComfyPlanTier.Aggressive && env.SageAttentionWorks)
            Emit("--use-sage-attention", null, "SageAttention is installed and imports on this GPU: 8-bit attention, verified for equivalence before adoption", numerics: true);

        // ---- precision / speed ---------------------------------------------------------------------------------------
        if (gpu.AmpereOrNewer && !gpu.BlackwellOrAda)
            decisions.Add(new ComfyPlanDecision("(precision)", null, "Ampere: ComfyUI already selects bf16 for VAE/UNet; no extra flag", false));
        if (tier == ComfyPlanTier.Aggressive && gpu.BlackwellOrAda && caps.Supports("--fast"))
        {
            var options = new List<string>();
            if (caps.FastOptions.Contains("fp16_accumulation")) options.Add("fp16_accumulation");
            if (caps.FastOptions.Contains("fp8_matrix_mult")) options.Add("fp8_matrix_mult");
            if (options.Count > 0)
            {
                args.Add("--fast");
                args.AddRange(options);
                decisions.Add(new ComfyPlanDecision("--fast", string.Join(' ', options),
                    "Ada/Blackwell have FP8 tensor cores and fast fp16 accumulation: lower-precision matmuls, verified for equivalence before adoption", true));
            }
        }

        // ---- VAE -----------------------------------------------------------------------------------------------------
        if (gpu.ComputeCapability is < 7.0)
            Emit("--fp32-vae", null, "pre-Volta GPUs have no fast half-precision VAE path", numerics: true);
        long pixels = (long)Math.Max(1, model.OutputWidth) * Math.Max(1, model.OutputHeight);
        long vaeWorkingSet = pixels * 200; // ~200 B/pixel for decode activations at fp16/bf16
        bool tile = pixels > 0 && free > 0 && model.DiffusionBytes > 0 && !fitsResident && vaeWorkingSet > Math.Max(0, free - model.DiffusionBytes);
        if (tile)
            decisions.Add(new ComfyPlanDecision("vae-tiling", "on",
                $"VAE decode of {model.OutputWidth}x{model.OutputHeight} needs about {gb(vaeWorkingSet)} beyond the resident diffusion weights: decode in tiles", false));
        return new ComfyLaunchPlan(tier, args, decisions, textEncoderDevice, tile);
    }

    /// <summary>The arguments PaRuX has always used. A calibration races every plan against this, and it is what runs when nothing was measured.</summary>
    internal static ComfyLaunchPlan Legacy(ComfyCapabilities caps)
    {
        var args = new List<string>();
        var decisions = new List<ComfyPlanDecision>();
        if (caps.Supports("--enable-dynamic-vram")) { args.Add("--enable-dynamic-vram"); decisions.Add(new("--enable-dynamic-vram", null, "shipped PaRuX default", false)); }
        if (caps.Supports("--reserve-vram")) { args.Add("--reserve-vram"); args.Add("1.0"); decisions.Add(new("--reserve-vram", "1.0", "shipped PaRuX default", false)); }
        return new ComfyLaunchPlan(ComfyPlanTier.Safe, args, decisions, "default", false);
    }
}
