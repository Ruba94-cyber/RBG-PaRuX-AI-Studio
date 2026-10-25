using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Runtime Manager's physical context control promises "how many tokens the selected model can
/// attend to simultaneously in one inference". That is a PER-REQUEST quantity, but llama.cpp's
/// <c>--ctx-size</c> is the TOTAL it divides across its slots.
///
/// Measured on the physical PC (RTX 5080, 2026-09-21): the coding runtime ran
/// <c>--ctx-size 32768 --parallel 4</c> and <c>/props</c> reported <c>total_slots 4</c> with
/// <c>default_generation_settings.n_ctx 8192</c>. The coding agent read that 8192 correctly and
/// shrank its repository-evidence budget to 9,338 characters against the Execution Profile's
/// declared 96,000; it then re-read the same 154-line file until its own repeat guard and
/// exploration budget stopped it, twice, without producing a single edit. Picking a larger window
/// in the UI did not help, because the launcher treated the per-request choice as a total - the
/// control was inert.
/// </summary>
public sealed class RuntimePerRequestContextTests
{
    private static (int Total, int Slots) Launched(int perRequestContext, int profileSlots)
    {
        var profile = new LlamaPerformanceProfileDto
        {
            ContextSize = perRequestContext,
            // What ContextPreferenceLaunch sets when the user has an explicit context choice for
            // this model - the case Runtime Manager's physical-context control produces.
            ContextIsPerRequest = true,
            ParallelSlots = profileSlots,
            BatchSize = 256,
            UBatchSize = 64,
            GpuLayers = -1,
            SplitMode = "none"
        };

        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: true);
        int contextIndex = args.IndexOf("--ctx-size");
        Assert.True(contextIndex >= 0 && contextIndex + 1 < args.Count, "--ctx-size must always be passed.");

        // An omitted --parallel means the launch left slot sizing to llama.cpp, which the launcher
        // only does when the resolved count equals the count auto is assumed to pick.
        int slotIndex = args.IndexOf("--parallel");
        int slots = slotIndex >= 0 && slotIndex + 1 < args.Count
            ? int.Parse(args[slotIndex + 1])
            : RuntimeLaunchOptimizer.AssumedAutoParallelSlots;

        return (int.Parse(args[contextIndex + 1]), slots);
    }

    [Theory]
    [InlineData(8_192, 4)]
    [InlineData(16_384, 4)]
    [InlineData(32_768, 4)]
    [InlineData(65_536, 2)]
    [InlineData(131_072, 1)]
    [InlineData(262_144, 4)]
    [InlineData(32_768, 0)]     // slots left on auto
    public void A_request_sees_at_least_the_window_the_profile_asked_for(int perRequestContext, int profileSlots)
    {
        (int total, int slots) = Launched(perRequestContext, profileSlots);
        int perSlot = total / slots;

        Assert.True(perSlot >= perRequestContext,
            "The profile asked for " + perRequestContext + " tokens per inference but the launch gives "
            + perSlot + " (--ctx-size " + total + " split across " + slots
            + " slots). This is the regression that starved the coding agent.");
    }

    /// <summary>
    /// The fix must never buy the window with extra KV cache: the total stays inside the envelope
    /// the previous expression already committed to, and the per-request window is paid for by
    /// dropping slots instead. Otherwise a launch that fitted in VRAM before could stop fitting.
    /// </summary>
    [Theory]
    [InlineData(8_192, 4)]
    [InlineData(16_384, 4)]
    [InlineData(32_768, 4)]
    [InlineData(262_144, 4)]
    [InlineData(2_048, 1)]
    [InlineData(32_768, 0)]
    public void The_total_never_exceeds_the_previous_memory_envelope(int perRequestContext, int profileSlots)
    {
        int previousEnvelope = Math.Max(
            RuntimeLaunchOptimizer.MinimumTotalContextFor(profileSlots), perRequestContext);

        (int total, _) = Launched(perRequestContext, profileSlots);

        Assert.True(total <= previousEnvelope,
            "Launch asked for " + total + " total context where the previous behavior committed only "
            + previousEnvelope + " - that would allocate more KV cache than before.");
    }

    /// <summary>The shipped chat default must come out byte-identical to the pre-fix launch.</summary>
    [Fact]
    public void The_shipped_default_is_unchanged()
    {
        (int total, int slots) = Launched(perRequestContext: 8_192, profileSlots: 4);

        Assert.Equal(32_768, total);
        Assert.Equal(4, slots);
    }

    /// <summary>
    /// The concrete case from the acceptance run: 32k selected in Runtime Manager used to yield
    /// 8192 per request behind 4 slots. It must now yield the full 32,768 at the same total.
    /// </summary>
    [Fact]
    public void The_acceptance_run_regression_is_fixed()
    {
        (int total, int slots) = Launched(perRequestContext: 32_768, profileSlots: 4);

        Assert.Equal(32_768, total);
        Assert.Equal(1, slots);
        Assert.Equal(32_768, total / slots);
    }

    /// <summary>
    /// An adaptive context is a resource envelope, not a promise about one inference, so it keeps
    /// the old total-and-divide behavior. Only an explicit user preference is per-request. Without
    /// this distinction the adaptive path would start pinning slots it was deliberately leaving to
    /// llama.cpp - the iGPU measurement behind RuntimeLaunchOptimizerParallelTests.
    /// </summary>
    [Theory]
    [InlineData(16_384, 0)]
    [InlineData(32_768, 4)]
    public void An_adaptive_envelope_is_still_treated_as_a_total(int contextSize, int profileSlots)
    {
        var profile = new LlamaPerformanceProfileDto
        {
            ContextSize = contextSize,
            ContextIsPerRequest = false,
            ParallelSlots = profileSlots,
            GpuLayers = -1,
            SplitMode = "none"
        };

        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: true);
        int contextIndex = args.IndexOf("--ctx-size");

        Assert.Equal(
            Math.Max(RuntimeLaunchOptimizer.MinimumTotalContextFor(profileSlots), contextSize),
            int.Parse(args[contextIndex + 1]));
    }

    /// <summary>
    /// The flag has to be set where the user's choice enters the launch profile, or the fix above
    /// never engages in the product.
    /// </summary>
    [Fact]
    public void A_stored_user_preference_marks_the_context_as_per_request()
    {
        string model = Path.Combine(
            Path.GetTempPath(), "parux-ctx-pref-" + Guid.NewGuid().ToString("N") + ".gguf");
        ContextPreferenceStore.Set(
            model, new ContextPreferenceStore.ContextPreference(32_768, "q8_0", false, 1.0));

        try
        {
            var profile = new LlamaPerformanceProfileDto { ContextSize = 8_192, ParallelSlots = 4 };
            ContextPreferenceLaunch.ApplyTo(profile, model);

            Assert.True(profile.ContextIsPerRequest,
                "A stored context preference is the user's per-inference window and must be marked as such.");
            Assert.Equal(32_768, profile.ContextSize);

            List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: true);
            int total = int.Parse(args[args.IndexOf("--ctx-size") + 1]);
            int slots = int.Parse(args[args.IndexOf("--parallel") + 1]);

            Assert.Equal(32_768, total / slots);
        }
        finally { ContextPreferenceStore.Clear(model); }
    }

    /// <summary>
    /// Coding drives one long-horizon request at a time. Extra slots give it no concurrency it can
    /// use and cut the window it depends on, so the recommendation must pin a single slot - the
    /// same reasoning long-context has always used.
    /// </summary>
    [Theory]
    [InlineData("coding")]
    [InlineData("long-context")]
    public void Sequential_workloads_never_recommend_more_than_one_slot(string workload)
    {
        const long modelSize = 13_600L * 1024 * 1024;
        const long plentyOfVram = 80L * 1024 * 1024 * 1024;

        Assert.Equal(1, RuntimeLaunchOptimizer.RecommendParallelSlots(modelSize, plentyOfVram, workload));
    }
}
