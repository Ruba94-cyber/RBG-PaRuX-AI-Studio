using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX ships only CUDA and Vulkan runtime builds and pins --device Vulkan0 on the non-CUDA path,
/// so a machine with no usable Vulkan device had no text inference at all. The shipped Vulkan build
/// already bundles a CPU backend, so the fallback reuses it rather than adding another download.
///
/// That "no accelerator present" machine cannot be reproduced on a development box that has one,
/// which is exactly why the decision is a pure function: the argument rewriting is verified here
/// directly instead of being asserted from an environment that can never exercise it.
/// </summary>
public sealed class CpuFallbackArgumentTests
{
    private static List<string> RealVulkanLaunchArguments()
    {
        var profile = new LlamaPerformanceProfileDto
        {
            Id = "adaptive-default",
            DisplayName = "Adaptive safe default",
            BatchSize = 256,
            UBatchSize = 64,
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            FlashAttention = "on",
            GpuLayers = -1,
            SplitMode = "none",
            MainGpu = 0,
            ContextSize = 8192,
            FitTargetMib = 1024,
            ParallelSlots = 1,
            ContinuousBatching = true,
            SlotPromptSimilarity = 0.10,
            ReasoningEffort = "low",
            ReasoningBudget = 768,
            LoadMode = "auto",
            Device = "Vulkan0"
        };
        return RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: false);
    }

    [Fact]
    public void Cpu_fallback_removes_every_accelerator_placement_flag_and_its_value()
    {
        List<string> gpu = RealVulkanLaunchArguments();
        Assert.Contains("--device", gpu);   // guards the premise of this test

        List<string> cpu = RuntimeLaunchOptimizer.ToCpuFallbackArguments(gpu);

        foreach (string flag in new[] { "--device", "--tensor-split", "--main-gpu", "--fit", "--fit-target" })
            Assert.DoesNotContain(flag, cpu);

        // A dropped flag must take its value with it, or the value becomes a stray positional
        // argument that llama-server rejects.
        Assert.DoesNotContain("Vulkan0", cpu);
        Assert.DoesNotContain("--split-mode", cpu);
        Assert.DoesNotContain("none", cpu);
    }

    [Fact]
    public void Cpu_fallback_pins_all_layers_to_the_cpu_exactly_once()
    {
        List<string> cpu = RuntimeLaunchOptimizer.ToCpuFallbackArguments(RealVulkanLaunchArguments());

        int index = cpu.IndexOf("--gpu-layers");
        Assert.NotEqual(-1, index);
        Assert.Equal("0", cpu[index + 1]);
        Assert.Equal(1, cpu.Count(a => a == "--gpu-layers"));
    }

    [Fact]
    public void Cpu_fallback_preserves_the_model_and_serving_arguments_that_still_apply()
    {
        var original = new List<string>
        {
            "--model", "C:/models/m.gguf", "--alias", "local", "--host", "127.0.0.1", "--port", "8080",
            "--ctx-size", "8192", "--batch-size", "256", "--cache-type-k", "q8_0",
            "--device", "Vulkan0", "--gpu-layers", "all", "--fit", "on", "--fit-target", "1024"
        };

        List<string> cpu = RuntimeLaunchOptimizer.ToCpuFallbackArguments(original);

        foreach (string kept in new[] { "--model", "C:/models/m.gguf", "--alias", "local",
                                        "--host", "127.0.0.1", "--port", "8080",
                                        "--ctx-size", "8192", "--batch-size", "256",
                                        "--cache-type-k", "q8_0" })
            Assert.Contains(kept, cpu);

        Assert.DoesNotContain("all", cpu);   // the dropped --gpu-layers value
        Assert.DoesNotContain("1024", cpu);  // the dropped --fit-target value
    }

    [Fact]
    public void Cpu_fallback_adds_thread_counts_without_overriding_explicit_ones()
    {
        List<string> withExplicit = RuntimeLaunchOptimizer.ToCpuFallbackArguments(
            new List<string> { "--model", "m.gguf", "--threads", "3", "--threads-batch", "5" });
        Assert.Equal(1, withExplicit.Count(a => a == "--threads"));
        Assert.Equal("3", withExplicit[withExplicit.IndexOf("--threads") + 1]);
        Assert.Equal("5", withExplicit[withExplicit.IndexOf("--threads-batch") + 1]);

        List<string> withoutExplicit = RuntimeLaunchOptimizer.ToCpuFallbackArguments(
            new List<string> { "--model", "m.gguf" });
        Assert.Contains("--threads", withoutExplicit);
        Assert.Contains("--threads-batch", withoutExplicit);
    }

    [Fact]
    public void Cpu_fallback_produces_well_formed_flag_value_pairs()
    {
        List<string> cpu = RuntimeLaunchOptimizer.ToCpuFallbackArguments(RealVulkanLaunchArguments());

        // Every "--flag" that takes a value must still be followed by a non-flag token; a dropped
        // value or an orphaned flag is exactly how this rewrite could break a real launch.
        for (int i = 0; i < cpu.Count; i++)
        {
            if (!cpu[i].StartsWith("--", StringComparison.Ordinal)) continue;
            bool valueless = cpu[i] is "--cont-batching" or "--no-cont-batching" or "--jinja"
                or "--ui" or "--cpu-strict" or "--backend-sampling" or "--reasoning-preserve";
            if (valueless) continue;
            Assert.True(i + 1 < cpu.Count, $"trailing flag with no value: {cpu[i]}");
            Assert.False(cpu[i + 1].StartsWith("--", StringComparison.Ordinal),
                $"flag {cpu[i]} lost its value (followed by {cpu[i + 1]})");
        }
    }
}
