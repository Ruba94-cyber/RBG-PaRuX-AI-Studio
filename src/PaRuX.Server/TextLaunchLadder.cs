namespace PaRuX.Server;

/// <summary>
/// The bounded recovery ladder for a failed text launch. Each rung is one attempt, derived from the same primary argument
/// list, in a fixed order:
/// <list type="number">
/// <item>the primary engine with the tuned profile;</item>
/// <item>the same engine with flash attention off (only when the profile enables it);</item>
/// <item>the same engine with reduced GPU offload (only when the profile offloads layers);</item>
/// <item>the Vulkan engine, only for a CUDA primary and only when the Vulkan build is installed;</item>
/// <item>CPU, always last.</item>
/// </list>
/// A CPU-only launch is already the CPU attempt and has exactly one rung. The plan is capped at <see cref="MaxRungs"/>.
/// Pure: planning and argument transforms take plain values, so the ladder is testable without a process.
/// </summary>
internal static class TextLaunchLadder
{
    /// <summary>Hard bound on attempts per launch, so recovery always terminates.</summary>
    internal const int MaxRungs = 6;

    /// <summary>GPU layers used by the reduced-offload rung when the profile asks for every layer (the layer count is unknown here).</summary>
    internal const int ReducedGpuLayersWhenAll = 24;

    internal const string FlashAttentionFlag = "--flash-attn";
    internal const string GpuLayersFlag = "--gpu-layers";
    internal const string DeviceFlag = "--device";
    internal const string VulkanDevice = "Vulkan0";

    internal enum Engine { Cuda, Vulkan, Cpu }

    internal enum Step { Primary, UnplannedBaseline, DropFlashAttention, ReduceOffload, VulkanEngine, VulkanKeepFlash, CpuOnly }

    /// <param name="Step">Which transform this rung applies to the primary arguments.</param>
    /// <param name="Engine">The engine the rung launches, which also selects the runtime folder.</param>
    /// <param name="Label">Short name used in the status trace.</param>
    /// <param name="ProfileSuffix">Appended to the recorded profile label so the active profile says which rung is running.</param>
    internal sealed record Rung(Step Step, Engine Engine, string Label, string ProfileSuffix);

    /// <summary>
    /// Plans the rungs for one launch. <paramref name="primary"/> is <see cref="Engine.Cuda"/> for a heavy-GPU launch and
    /// <see cref="Engine.Vulkan"/> otherwise; <paramref name="cpuOnly"/> means no adapter exists and only the CPU attempt runs.
    /// </summary>
    /// <param name="hasPlannedLaunch">
    /// True when the launch planner changed the primary arguments. A second rung then re-launches the same engine from
    /// the pre-plan (known-good) arguments, and every later rung derives from those arguments instead of the plan.
    /// </param>
    internal static IReadOnlyList<Rung> Plan(Engine primary, bool cpuOnly, IReadOnlyList<string> primaryArguments, bool vulkanAvailable,
        bool hasPlannedLaunch = false, bool primaryAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(primaryArguments);
        if (cpuOnly)
        {
            return hasPlannedLaunch
                ? [new Rung(Step.Primary, Engine.Cpu, "CPU (launch plan)", string.Empty),
                   new Rung(Step.UnplannedBaseline, Engine.Cpu, "CPU (default arguments)", " (default arguments)")]
                : [new Rung(Step.Primary, Engine.Cpu, "CPU", string.Empty)];
        }

        if (!primaryAvailable)
        {
            // The primary engine is not installed at all, so its rungs could only fail to start. Go to the
            // next installed engine; the trace then reads as what happened instead of four "cannot find file".
            var fallback = new List<Rung>();
            if (primary == Engine.Cuda && vulkanAvailable)
            {
                // Vulkan is the intended engine here, not a degraded one: keep the planned flash attention and
                // only retry without it if that exact launch fails.
                fallback.Add(new Rung(Step.VulkanKeepFlash, Engine.Vulkan, "Vulkan", " (Vulkan)"));
                if (!string.Equals(ReadValue(primaryArguments, FlashAttentionFlag), "off", StringComparison.OrdinalIgnoreCase))
                    fallback.Add(new Rung(Step.VulkanEngine, Engine.Vulkan, "Vulkan without flash attention", " (Vulkan, flash attention off)"));
            }
            fallback.Add(new Rung(Step.CpuOnly, Engine.Cpu, "CPU", " (CPU fallback)"));
            return fallback;
        }

        string engineLabel = primary == Engine.Cuda ? "CUDA" : "Vulkan";
        var rungs = new List<Rung> { new(Step.Primary, primary, hasPlannedLaunch ? engineLabel + " (launch plan)" : engineLabel, string.Empty) };
        if (hasPlannedLaunch)
            rungs.Add(new Rung(Step.UnplannedBaseline, primary, engineLabel + " (default arguments)", " (default arguments)"));

        string? flash = ReadValue(primaryArguments, FlashAttentionFlag);
        if (!string.Equals(flash, "off", StringComparison.OrdinalIgnoreCase))
            rungs.Add(new Rung(Step.DropFlashAttention, primary, engineLabel + " without flash attention", " (flash attention off)"));

        string? layers = ReadValue(primaryArguments, GpuLayersFlag);
        if (!string.Equals(layers, "0", StringComparison.Ordinal))
            rungs.Add(new Rung(Step.ReduceOffload, primary, engineLabel + " with reduced GPU offload", " (reduced GPU offload)"));

        if (primary == Engine.Cuda && vulkanAvailable)
            rungs.Add(new Rung(Step.VulkanEngine, Engine.Vulkan, "Vulkan", " (Vulkan fallback)"));

        rungs.Add(new Rung(Step.CpuOnly, Engine.Cpu, "CPU", " (CPU fallback)"));
        return rungs.Count > MaxRungs ? rungs.Take(MaxRungs).ToList() : rungs;
    }

    /// <summary>
    /// The argument list one rung launches. The primary list is never modified. Each transform is applied to a copy, so the
    /// Vulkan and CPU rungs start from the tuned profile rather than from an earlier failed attempt.
    /// </summary>
    internal static List<string> ArgumentsFor(Rung rung, IReadOnlyList<string> primaryArguments, IReadOnlyList<string>? baselineArguments = null)
    {
        ArgumentNullException.ThrowIfNull(rung);
        ArgumentNullException.ThrowIfNull(primaryArguments);
        // After the planned primary has failed, every rung derives from the known-good pre-plan arguments.
        var arguments = (rung.Step == Step.Primary ? primaryArguments : baselineArguments ?? primaryArguments).ToList();
        switch (rung.Step)
        {
            case Step.Primary:
            case Step.UnplannedBaseline:
                return arguments;
            case Step.DropFlashAttention:
                return FlashOff(arguments);
            case Step.ReduceOffload:
                FlashOff(arguments);
                return SetOption(arguments, GpuLayersFlag, ReducedGpuLayers(ReadValue(arguments, GpuLayersFlag)));
            case Step.VulkanEngine:
                FlashOff(arguments);
                return SetOption(arguments, DeviceFlag, VulkanDevice);
            case Step.VulkanKeepFlash:
                return SetOption(arguments, DeviceFlag, VulkanDevice);
            case Step.CpuOnly:
                return RuntimeLaunchOptimizer.ToCpuFallbackArguments(arguments);
            default:
                throw new ArgumentOutOfRangeException(nameof(rung));
        }
    }

    /// <summary>
    /// The reduced layer count. An explicit count is halved (never below one). "all" or an absent value becomes
    /// <see cref="ReducedGpuLayersWhenAll"/>. A value that is already zero or cannot be parsed is returned unchanged.
    /// </summary>
    internal static string ReducedGpuLayers(string? current)
    {
        if (current is null || string.Equals(current, "all", StringComparison.OrdinalIgnoreCase))
            return ReducedGpuLayersWhenAll.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (int.TryParse(current, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int count) && count > 1)
            return Math.Max(1, count / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return current;
    }

    /// <summary>Turns flash attention off and, if the V cache is quantized, returns it to f16: llama.cpp refuses a
    /// quantized V cache without flash attention, so leaving it would make the rung fail before loading anything.</summary>
    private static List<string> FlashOff(List<string> arguments)
    {
        SetOption(arguments, FlashAttentionFlag, "off");
        string? v = ReadValue(arguments, "--cache-type-v");
        if (v is not null && !string.Equals(v, "f16", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(v, "f32", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(v, "bf16", StringComparison.OrdinalIgnoreCase))
            SetOption(arguments, "--cache-type-v", "f16");
        return arguments;
    }

    /// <summary>Sets <paramref name="flag"/> to <paramref name="value"/>, replacing its existing value or appending the pair.</summary>
    private static List<string> SetOption(List<string> arguments, string flag, string value)
    {
        int index = arguments.IndexOf(flag);
        if (index >= 0 && index + 1 < arguments.Count) arguments[index + 1] = value;
        else if (index >= 0) arguments.Add(value);
        else arguments.AddRange([flag, value]);
        return arguments;
    }

    /// <summary>The value following <paramref name="option"/>, or null when the option is absent.</summary>
    private static string? ReadValue(IReadOnlyList<string> arguments, string option)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
            if (arguments[i] == option) return arguments[i + 1];
        return null;
    }
}
