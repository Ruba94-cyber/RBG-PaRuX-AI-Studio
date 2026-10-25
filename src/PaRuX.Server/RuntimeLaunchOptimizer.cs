using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record RuntimeLaunchOptimization(
    LlamaPerformanceProfileDto Profile,
    string Source,
    string Workload,
    string Backend,
    string RuntimeTag,
    string HardwareKey,
    LaunchPlan? Plan = null,
    LlamaPerformanceProfileDto? BaselineProfile = null);

/// <summary>What the launch planner needs besides the profile. Production builds it from the live machine; tests supply it.</summary>
public sealed record LaunchPlanContext(
    LaunchHardware Hardware,
    LlamaServerCapabilities Capabilities,
    PaRuX.Intelligence.GgufModelInfo Model);

/// <summary>
/// Resolves the launch profile that llama-server should actually receive. A persisted measured
/// winner wins only while its model/runtime/GPU identity remains valid; otherwise PaRuX falls back
/// to a conservative hardware-aware profile. All automatic controls stay inside llama.cpp/runtime
/// knobs; PaRuX never changes driver clocks, voltage, BIOS settings or user-managed runtimes.
///
/// Context policy is finalized here, not at individual load call sites. That makes a measured
/// profile, an adaptive profile, an arbitrary model, a routed candidate and every hot-pool child
/// obey the same explicit user preference / Execution Profile envelope.
/// </summary>
public static class RuntimeLaunchOptimizer
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;

    /// <param name="gpuInventory">
    /// The adapter inventory the launch is planned against. Null reads DXGI; tests pass a fake inventory.
    /// </param>
    public static async Task<RuntimeLaunchOptimization> ResolveAsync(
        string modelPath,
        string workload,
        bool hasCuda,
        int fallbackBatch,
        int fallbackUbatch,
        string fallbackCache,
        CancellationToken ct,
        GpuInventoryStatus? gpuInventory = null,
        bool planLaunch = true,
        LaunchPlanContext? planContext = null)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        string backend = hasCuda ? "text-cuda" : "text-vulkan";
        string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backend)
            ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
        GpuInventoryStatus inventory = gpuInventory ?? new GpuInventoryService().GetStatus();
        string hardwareKey = RuntimePerformanceTuner.BuildGpuFingerprint(inventory);
        var store = new RuntimePerformanceProfileStore();
        RuntimePerformanceProfileSelection? tuned = store.FindValid(
            modelPath, workload, runtimeTag, hardwareKey);
        if (tuned != null)
        {
            return Finalize(
                modelPath,
                RuntimePerformanceProfileCopy.Copy(tuned.Profile),
                "measured", workload, backend, runtimeTag, hardwareKey);
        }

        long modelSize = 0;
        try { modelSize = new FileInfo(modelPath).Length; } catch { }
        long largestVram = inventory.Available ? inventory.LargestDedicatedVideoMemoryBytes : 0;
        int context = RuntimePerformanceTuner.RecommendContext(modelSize, largestVram, workload, modelPath);
        (string reasoningEffort, int reasoningBudget) = RecommendReasoning(workload);
        int slots = RecommendParallelSlots(modelSize, largestVram, workload);
        (int threads, int threadsBatch, bool cpuStrict) = RecommendCpuThreads(hasCuda);

        var profile = new LlamaPerformanceProfileDto
        {
            Id = "adaptive-default",
            DisplayName = "Adaptive safe default",
            BatchSize = fallbackBatch,
            UBatchSize = fallbackUbatch,
            CacheTypeK = fallbackCache,
            CacheTypeV = fallbackCache,
            FlashAttention = "on",
            GpuLayers = -1,
            SplitMode = "none",
            MainGpu = 0,
            ContextSize = context,
            FitTargetMib = 1024,
            ParallelSlots = slots,
            ContinuousBatching = true,
            SlotPromptSimilarity = workload == "coding" ? 0.20 : 0.10,
            ReasoningEffort = reasoningEffort,
            ReasoningBudget = reasoningBudget,
            LoadMode = "auto",
            TensorReadLazy = "auto",
            Threads = threads,
            ThreadsBatch = threadsBatch,
            CpuStrict = cpuStrict,
            ProcessPriority = workload == "coding" ? 1 : 0,
            PollPercent = workload == "coding" ? 65 : 45
        };

        if (!hasCuda)
        {
            profile.Device = "Vulkan0";
            // Auto rather than a pinned single slot: measured on the iGPU reference machine,
            // "--parallel 1" cost ~4.1 s to first token against ~1.0 s when llama.cpp sized the
            // slots itself (which chose 4 slots while keeping the full per-slot context), with
            // identical decode throughput. See RuntimeLaunchOptimizerParallelTests.
            profile.ParallelSlots = 0;
            LaunchPlanContext? vulkanPlan = planLaunch
                ? planContext ?? await BuildPlanContextAsync(modelPath, hasCuda, inventory, await ResolveLiveFreeDedicatedBytesAsync(inventory, ct).ConfigureAwait(false), runtimeTag, ct).ConfigureAwait(false)
                : null;
            return Finalize(modelPath, profile, "adaptive-default", workload, backend, runtimeTag, hardwareKey, vulkanPlan);
        }

        List<GpuPerformanceTelemetryDto> cuda = await new GpuPerformanceTelemetryService()
            .QueryAsync(ct).ConfigureAwait(false);
        GpuPerformanceTelemetryDto? strongest = cuda
            .Where(gpu => gpu.MemoryTotalMib > 0)
            .OrderByDescending(gpu => gpu.MemoryTotalMib)
            .ThenByDescending(gpu => gpu.SmClockMhz)
            .FirstOrDefault();
        long? freeDedicatedBytes = null;
        if (strongest != null)
        {
            profile.MainGpu = strongest.Index;
            // A measured 0 MiB free is a real reading (the card is full), not "unknown".
            if (strongest.MemoryFreeMib is >= 0) freeDedicatedBytes = (long)strongest.MemoryFreeMib.Value * MiB;
            long estimated = EstimateLoadedBytes(modelSize);
            long comfortable = (long)(strongest.MemoryTotalMib!.Value * MiB * 0.85);
            profile.LoadMode = RecommendLoadMode(modelSize, (long)strongest.MemoryTotalMib!.Value * MiB);
            if (estimated > comfortable && cuda.Count > 1)
            {
                long aggregateComfortable = (long)(cuda.Sum(gpu => (long)(gpu.MemoryTotalMib ?? 0) * MiB) * 0.85);
                if (estimated <= aggregateComfortable)
                {
                    profile.SplitMode = "layer";
                    List<GpuPerformanceTelemetryDto> ordered = cuda.OrderBy(gpu => gpu.Index).ToList();
                    profile.Device = string.Join(',', ordered.Select(gpu => "CUDA" + gpu.Index));
                    profile.TensorSplit = string.Join(',', ordered.Select(gpu => Math.Max(1, gpu.MemoryTotalMib ?? 1)));
                    profile.MainGpu = strongest.Index;
                    // Multi-device serving is benchmarked first; do not multiply slots on top of
                    // an unmeasured interconnect/topology decision.
                    profile.ParallelSlots = 1;
                }
            }
        }

        LaunchPlanContext? cudaPlan = planLaunch
            ? planContext ?? await BuildPlanContextAsync(modelPath, hasCuda, inventory, freeDedicatedBytes, runtimeTag, ct).ConfigureAwait(false)
            : null;
        return Finalize(modelPath, profile, "adaptive-default", workload, backend, runtimeTag, hardwareKey, cudaPlan);
    }

    /// <summary>
    /// Live free dedicated memory of a discrete non-NVIDIA adapter (DXGI budget minus system-wide usage), measured
    /// now: a launch right after a game exited must not be planned against the game's memory pressure. Null when
    /// the host has no such adapter or the sample is unavailable, which the planner treats conservatively.
    /// </summary>
    private static async Task<long?> ResolveLiveFreeDedicatedBytesAsync(GpuInventoryStatus inventory, CancellationToken ct)
    {
        try
        {
            RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(
                new RuntimeManagerStatus { Backends = { new RuntimeBackendStatus { Backend = "text-vulkan", Installed = true } } },
                inventory, 0);
            if (!hardware.AcceleratorEligible || hardware.UsesSharedSystemMemoryAsPrimary || hardware.DedicatedVideoMemoryBytes <= 0)
                return null;
            CachedGpuPerformanceTelemetrySnapshotProvider.Shared.Invalidate();
            GpuPerformanceTelemetrySnapshot? snapshot = await CachedGpuPerformanceTelemetrySnapshotProvider.Shared
                .GetSnapshotAsync(ct).ConfigureAwait(false);
            return RuntimeGpuMemoryBudget.TryResolveAvailableDedicatedGpuBytes(hardware, snapshot, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Gathers the live facts the launch planner needs. Returns null (no plan, the launch keeps its default arguments)
    /// whenever the model has no readable GGUF header, so a file the planner cannot reason about is never touched.
    /// </summary>
    private static async Task<LaunchPlanContext?> BuildPlanContextAsync(
        string modelPath, bool hasCuda, GpuInventoryStatus inventory, long? freeDedicatedBytes, string runtimeTag, CancellationToken ct)
    {
        try
        {
            PaRuX.Intelligence.GgufModelInfo model = PaRuX.Intelligence.GgufMetadataReader.Read(modelPath);
            if (!model.ParsedSuccessfully) return null;

            SystemRamSnapshot? ram = ExecutionResourceGuard.TryGetSystemRamSnapshot();
            long totalRam = ram?.TotalBytesAsLong ?? 0;
            long availableRam = ram is { IsValid: true } ? (long)ram.AvailableBytes : 0;
            string runtimeDirectory = LocalAiPaths.ResolveGpuTextRuntimePath(hasCuda);
            string backend = string.Equals(runtimeDirectory, LocalAiPaths.RuntimeCudaPath, StringComparison.Ordinal) ? "text-cuda" : "text-vulkan";
            var status = new RuntimeManagerStatus { RamTotalBytes = totalRam };
            status.Backends.Add(new RuntimeBackendStatus { Backend = backend, Installed = true });
            RuntimeHardwareProfile hardware = RuntimeHardwareProfileResolver.Resolve(status, inventory, totalRam);

            LlamaServerCapabilities capabilities = await LlamaServerCapabilityProbe.GetAsync(
                Path.Combine(runtimeDirectory, LocalAiPaths.LlamaServerExecutable), runtimeTag, ct).ConfigureAwait(false);

            return new LaunchPlanContext(
                LaunchHardware.From(hardware, freeDedicatedBytes, availableRam, CpuTopology.PhysicalCores(), Environment.ProcessorCount),
                capabilities, model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // planning is an optimization; any failure leaves the default launch untouched
    }

    /// <summary>
    /// Measured on RTX 5080 16 GB / 25.8 GB RAM with Qwen3.8-27B IQ4_XS fully offloaded
    /// (.ai/artifacts/llama-load-mode-20260929): the default mmap load kept ~12.4 GB of file pages
    /// resident (free RAM 13.6 GB → 0.8 GB) although every layer ran on the GPU, and under desktop
    /// load coding steps slowed to minutes. "--load-mode none" left 13.2 GB free, loaded in 5.1 s
    /// instead of 11.2 s and decoded at the same 49.7 tok/s. mmap remains the default when the
    /// weights clearly exceed the GPU, where host-mapped layers avoid a second copy.
    /// </summary>
    internal static string RecommendLoadMode(long weightBytes, long gpuTotalBytes) =>
        weightBytes > 0 && gpuTotalBytes > 0 && weightBytes <= gpuTotalBytes - LoadModeVramReserveBytes
            ? "none"
            : "auto";

    /// <summary>Headroom for the CUDA context and a small KV cache next to fully offloaded weights.</summary>
    private const long LoadModeVramReserveBytes = 1536L * MiB;

    private static RuntimeLaunchOptimization Finalize(
        string modelPath,
        LlamaPerformanceProfileDto profile,
        string source,
        string workload,
        string backend,
        string runtimeTag,
        string hardwareKey,
        LaunchPlanContext? planContext = null)
    {
        ContextPreferenceLaunch.ApplyTo(profile, modelPath);
        if (planContext == null)
            return new RuntimeLaunchOptimization(profile, source, workload, backend, runtimeTag, hardwareKey);

        // The planner runs after the user's context preference is applied, so it fits the window the launch will really use.
        // The pre-plan profile is kept as the known-good baseline the launch ladder falls back to.
        LlamaPerformanceProfileDto baseline = RuntimePerformanceProfileCopy.Copy(profile);
        baseline.ContextIsPerRequest = profile.ContextIsPerRequest;
        LaunchPlan plan = LaunchPlanner.Plan(new LaunchPlanInput(planContext.Model, planContext.Hardware, planContext.Capabilities, baseline));
        return plan.Applied
            ? new RuntimeLaunchOptimization(plan.Profile, source + "+launch-plan", workload, backend, runtimeTag, hardwareKey, plan, baseline)
            : new RuntimeLaunchOptimization(profile, source, workload, backend, runtimeTag, hardwareKey, plan);
    }

    public static string InferWorkload(string? label, string? profilePrefix = null)
    {
        string value = ((profilePrefix ?? string.Empty) + " " + (label ?? string.Empty)).ToLowerInvariant();
        if (value.Contains("coding") || value.Contains("coder") || value.Contains("review")
            || value.Contains("test") || value.Contains("documentation") || value.Contains("agent"))
            return "coding";
        if (value.Contains("reasoning") || value.Contains("research") || value.Contains("planner")
            || value.Contains("long") || value.Contains("rag"))
            return "long-context";
        return "chat";
    }

    /// <summary>
    /// Operator override for the coding reasoning budget (tokens), used to measure the
    /// quality/latency trade-off: at ~47 tok/s a fully spent 4096-token budget costs ~87 s per
    /// agent step on the reference 27B.
    /// </summary>
    internal static int? CodingReasoningBudgetOverride(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 0 and <= 16384 ? parsed : null;

    internal static (string Effort, int Budget) RecommendReasoning(string workload) =>
        RuntimePerformanceTuner.NormalizeWorkload(workload) switch
        {
            "coding" when CodingReasoningBudgetOverride(Environment.GetEnvironmentVariable("PARUX_CODING_REASONING_BUDGET")) is int budget
                => (budget <= 1024 ? "medium" : "high", budget),
            "coding" => ("high", 4096),
            "long-context" => ("high", 6144),
            _ => ("low", 768)
        };

    internal static int RecommendParallelSlots(long modelSizeBytes, long largestVramBytes, string workload)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        // Coding is a single long-horizon agent driving one request at a time. Extra slots buy it
        // no concurrency it can use and cost it the thing it needs most: llama.cpp divides
        // --ctx-size across slots, so every additional slot is a proportional cut to the window a
        // coding turn can actually see. Measured on the physical PC (2026-09-21): the coding role
        // ran with 4 slots, leaving 8192 tokens per request, and the agent looped on re-reads until
        // its exploration budget stopped it without producing a single edit. Same reasoning as
        // long-context, which has always pinned 1.
        if (workload is "coding" or "long-context") return 1;
        if (largestVramBytes <= 0 || modelSizeBytes <= 0) return 1;
        long estimated = EstimateLoadedBytes(modelSizeBytes);
        long headroom = Math.Max(0, largestVramBytes - estimated);
        if (headroom >= 6 * GiB) return 2;
        return 1;
    }

    private static (int Threads, int ThreadsBatch, bool Strict) RecommendCpuThreads(bool hasCuda)
    {
        if (hasCuda) return (0, 0, false);
        int logical = Math.Max(1, Environment.ProcessorCount);
        int generation = Math.Max(1, logical - Math.Max(1, logical / 4));
        return (generation, logical, logical >= 8);
    }

    /// <summary>
    /// Smallest PER-SLOT context PaRuX will launch a text runtime with. Not a llama.cpp limit - it
    /// is the smallest window in which PaRuX's own system prompt plus ChatProxyService's reply
    /// reserve still leave room to answer. See the note in BuildRuntimeArguments.
    /// </summary>
    internal const int MinimumUsablePerSlotContext = 8192;

    /// <summary>
    /// How many slots llama.cpp picks for itself when ParallelSlots is left at 0 (auto). Measured
    /// on the iGPU reference machine: it chose 4. The number matters because --ctx-size is the
    /// TOTAL across slots, so the floor has to be multiplied by it to reach a usable per-slot
    /// window; assuming auto means "one slot" is what let the window collapse to a quarter.
    /// </summary>
    internal const int AssumedAutoParallelSlots = 4;

    internal static int EffectiveSlots(int parallelSlots) =>
        Math.Max(1, parallelSlots > 0 ? parallelSlots : AssumedAutoParallelSlots);

    internal static int MinimumTotalContextFor(int parallelSlots) =>
        MinimumUsablePerSlotContext * EffectiveSlots(parallelSlots);

    /// <summary>
    /// Resolves the TOTAL <c>--ctx-size</c> and the slot count to launch with, so that the window
    /// a single request actually sees is never smaller than the one the profile asked for.
    ///
    /// <para><see cref="LlamaPerformanceProfileDto.ContextSize"/> carries the user's own physical
    /// context choice - <c>ContextPreferenceLaunch</c> copies it straight out of
    /// <c>ContextPreferenceStore</c> - and Runtime Manager sells that control as "how many tokens
    /// the selected model can attend to simultaneously in one inference". It is a PER-INFERENCE
    /// quantity. <c>--ctx-size</c> is the TOTAL that llama.cpp then divides across its slots, so
    /// handing the per-inference number to it verbatim silently divides the user's choice by the
    /// slot count.</para>
    ///
    /// <para>Measured on the physical PC (RTX 5080, 2026-09-21): the coding runtime launched
    /// <c>--ctx-size 32768 --parallel 4</c>; <c>/props</c> reported <c>total_slots 4</c> and
    /// <c>default_generation_settings.n_ctx 8192</c>. The coding agent correctly read 8192 and cut
    /// its repository-evidence budget to 9,338 characters against the Execution Profile's declared
    /// 96,000, then re-read the same 154-line file until its own repeat guard and exploration
    /// budget stopped it - twice, without ever producing an edit. Raising the window in the UI
    /// changed nothing: the old expression <c>Math.Max(MinimumTotalContextFor(slots), ContextSize)</c>
    /// treated the per-request choice as a total, so the per-slot window stayed pinned at the 8192
    /// floor no matter what the user picked. The control was inert.</para>
    ///
    /// <para>Concurrency yields, memory does not. The total stays inside the same envelope the old
    /// expression produced, and the per-request window is bought by dropping slots rather than by
    /// allocating more KV cache - so this can never turn a launch that fitted into one that does
    /// not. With the shipped default (8192 per request, 4 slots) it reproduces today's arguments
    /// exactly.</para>
    /// </summary>
    internal static (int TotalContext, int Slots) ResolveContextAndSlots(
        int contextSize, int parallelSlots, bool contextIsPerRequest)
    {
        // Clamped up front, to the same range the --parallel argument is clamped to. Previously the
        // context arithmetic used the raw slot count while the argument used the clamped one, so a
        // stored profile asking for 999 slots launched --parallel 8 against a --ctx-size sized for
        // 999 - the two disagreed by two orders of magnitude.
        int maxSlots = Math.Clamp(EffectiveSlots(parallelSlots), 1, MaximumParallelSlots);
        long envelope = Math.Max((long)MinimumUsablePerSlotContext * maxSlots, Math.Max(0, contextSize));

        // An adaptive context is a resource envelope - a total llama.cpp may divide as it likes.
        // Only an explicit user preference is a per-inference promise that has to survive the split.
        if (!contextIsPerRequest)
            return ((int)Math.Min((long)int.MaxValue, envelope), maxSlots);

        int want = Math.Max(MinimumUsablePerSlotContext, contextSize);
        int slots = (int)Math.Clamp(envelope / want, 1, maxSlots);
        return ((int)Math.Min((long)int.MaxValue, (long)want * slots), slots);
    }

    internal const int MaximumParallelSlots = 8;

    internal static int TotalContextFor(int contextSize, int parallelSlots, bool contextIsPerRequest = false) =>
        ResolveContextAndSlots(contextSize, parallelSlots, contextIsPerRequest).TotalContext;

    public static List<string> BuildRuntimeArguments(LlamaPerformanceProfileDto profile, bool hasCuda)
    {
        // profile.TensorReadLazy is deliberately not turned into a --tensor-read-lazy argument
        // here: the pinned baseline llama.cpp build (b10549 - the exact tag hash-locked in
        // packaging/windows-baseline-runtime.lock.json and staged into every MSI) does not
        // recognize that flag at all ("error: invalid argument: --tensor-read-lazy"), which made
        // llama-server exit immediately and every chat completion fail with 503 on first real use.
        // Re-add once the pinned baseline moves to a llama.cpp build that supports it.
        var args = new List<string>
        {
            // --ctx-size is the TOTAL context, which llama.cpp then splits across its slots, so the
            // floor is a per-slot requirement multiplied by the slot count. Measured on the physical
            // PC: launching 8192 with slots left on auto produced total_slots 4 and n_ctx 2048 per
            // slot (2048 x 4 = 8192 exactly). ChatProxyService reserves 1024 of that for the reply
            // and PaRuX's own system prompt measured 1025 tokens, so the Core reported "The system
            // prompt alone fills this model's context" and every chat turn carrying retrieved
            // context returned zero content tokens - a silent empty answer from a 4B model that
            // advertises 262,144 context. The comment above about auto slots "keeping the full
            // per-slot context" does not hold for the pinned baseline build.
            "--ctx-size", ResolveContextAndSlots(profile.ContextSize, profile.ParallelSlots, profile.ContextIsPerRequest).TotalContext.ToString(),
            "--batch-size", Math.Max(32, profile.BatchSize).ToString(),
            "--ubatch-size", Math.Max(16, Math.Min(profile.UBatchSize, profile.BatchSize)).ToString(),
            "--cache-type-k", NormalizeCache(profile.CacheTypeK),
            "--cache-type-v", NormalizeCache(profile.CacheTypeV),
            "--flash-attn", NormalizeFlash(profile.FlashAttention),
            "--gpu-layers", profile.GpuLayers < 0 ? "all" : profile.GpuLayers.ToString(),
            "--fit", "on",
            "--fit-target", Math.Max(256, profile.FitTargetMib).ToString(),
            // --parallel is appended below only when the profile actually pins a slot count.
            profile.ContinuousBatching ? "--cont-batching" : "--no-cont-batching",
            "--slot-prompt-similarity", Math.Clamp(profile.SlotPromptSimilarity, 0, 1).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            "--reasoning-effort", NormalizeReasoningEffort(profile.ReasoningEffort),
            "--reasoning-budget", Math.Max(-1, profile.ReasoningBudget).ToString(),
            "--load-mode", NormalizeLoadMode(profile.LoadMode),
            "--prio", Math.Clamp(profile.ProcessPriority, -1, 2).ToString(),
            "--poll", Math.Clamp(profile.PollPercent, 0, 100).ToString()
        };

        // These two controls used to be appended only by StartTextAsync after the common builder,
        // which meant arbitrary loads and Smart Router/hot-pool candidates silently ignored the
        // same explicit context preference. They now travel on the concrete launch profile.
        if (profile.DisableKvOffload) args.Add("--no-kv-offload");
        if (double.IsFinite(profile.RopeScale) && profile.RopeScale > 1.0)
        {
            args.AddRange([
                "--rope-scaling", "yarn",
                "--rope-scale", profile.RopeScale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            ]);
        }

        // ParallelSlots <= 0 still means "let llama.cpp size the slots itself". On this project's
        // iGPU reference machine auto chose 4 slots and reached first token in ~1.0 s, while a
        // pinned "--parallel 1" produced one slot at the same per-slot context and ~4.1 s - that
        // pin cost 4x TTFT and bought nothing, so auto is left alone whenever the resolved slot
        // count matches what auto is assumed to pick anyway.
        //
        // The pin is added when the resolution deliberately *reduced* the slot count to honor the
        // profile's per-request window (see ResolveContextAndSlots). In that case auto must not be
        // allowed to re-divide --ctx-size behind PaRuX's back: the context arithmetic and the slot
        // count have to agree, or the window the user selected silently shrinks again.
        int resolvedSlots = ResolveContextAndSlots(profile.ContextSize, profile.ParallelSlots, profile.ContextIsPerRequest).Slots;
        if (profile.ParallelSlots > 0 || resolvedSlots != AssumedAutoParallelSlots)
            args.AddRange(["--parallel", Math.Clamp(resolvedSlots, 1, MaximumParallelSlots).ToString()]);

        if (profile.Threads > 0) args.AddRange(["--threads", profile.Threads.ToString()]);
        if (profile.ThreadsBatch > 0) args.AddRange(["--threads-batch", profile.ThreadsBatch.ToString()]);
        if (profile.CpuStrict)
            args.AddRange(["--cpu-strict", "1", "--cpu-strict-batch", "1"]);
        string numa = NormalizeNuma(profile.NumaMode);
        if (numa != "none") args.AddRange(["--numa", numa]);

        string split = NormalizeSplit(profile.SplitMode);
        args.AddRange(["--split-mode", split]);
        if (split == "none") args.AddRange(["--main-gpu", Math.Max(0, profile.MainGpu).ToString()]);
        if (!string.IsNullOrWhiteSpace(profile.Device)) args.AddRange(["--device", profile.Device.Trim()]);
        if (split is "layer" or "row" && !string.IsNullOrWhiteSpace(profile.TensorSplit))
            args.AddRange(["--tensor-split", profile.TensorSplit.Trim()]);

        if (split == "tensor")
            throw new InvalidDataException("Experimental tensor-split launch requires the dedicated validated topology path.");

        if (!hasCuda && string.IsNullOrWhiteSpace(profile.Device))
            args.AddRange(["--device", "Vulkan0"]);

        // Routed-expert placement is set only by the launch planner, after the runtime build's flag was verified.
        if (profile.CpuMoeLayers > 0) args.AddRange(["--n-cpu-moe", profile.CpuMoeLayers.ToString()]);
        if (!string.IsNullOrWhiteSpace(profile.OverrideTensor)) args.AddRange(["--override-tensor", profile.OverrideTensor.Trim()]);

        AppendSpeculativeArguments(args, profile);
        if (profile.BackendSampling) args.Add("--backend-sampling");
        return args;
    }

    /// <summary>
    /// True when DXGI positively reports that no usable GPU adapter exists: no adapter at all, or only
    /// software adapters such as the Microsoft Basic Render Driver. An unavailable inventory is not proof
    /// of absence, so it returns false and the launch path stays as it was.
    /// </summary>
    internal static bool HasNoUsableGpuAdapter(GpuInventoryStatus inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory.Available && inventory.Adapters.All(adapter => adapter.IsSoftware);
    }

    /// <summary>
    /// The CPU-only launch decision, with the inventory supplied by the caller so both the decision and the call
    /// sites are testable without DXGI. The inventory is read only when no heavy GPU was found: a heavy GPU is never
    /// CPU-only, and a host with an integrated or non-NVIDIA adapter keeps the Vulkan launch.
    /// </summary>
    internal static bool ResolveCpuOnly(bool hasHeavyGpu, Func<GpuInventoryStatus> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return !hasHeavyGpu && HasNoUsableGpuAdapter(inventory());
    }

    /// <summary>
    /// The text-launch arguments for a profile. On a host with no usable adapter the CPU arguments are built
    /// directly, so no Vulkan0 attempt is made and there is no failed GPU launch to recover from. In every
    /// other case the result is exactly what <see cref="BuildRuntimeArguments"/> returns.
    /// </summary>
    internal static List<string> BuildLaunchArguments(LlamaPerformanceProfileDto profile, bool hasCuda, bool cpuOnly)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return cpuOnly && !hasCuda
            ? ToCpuFallbackArguments(BuildRuntimeArguments(profile, hasCuda: false))
            : BuildRuntimeArguments(profile, hasCuda);
    }

    public static void ApplyEnvironment(ProcessStartInfo psi, LlamaPerformanceProfileDto profile)
    {
        foreach ((string name, string value) in LaunchEnvironment(profile)) psi.Environment[name] = value;
    }

    /// <summary>The single source of launch environment overrides, shared by the process start
    /// and the launch descriptor so the two cannot disagree.</summary>
    internal static IReadOnlyDictionary<string, string> LaunchEnvironment(LlamaPerformanceProfileDto? profile)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (profile?.EnableCudaP2P == true) environment["GGML_CUDA_P2P"] = "1";
        return environment;
    }

    /// <summary>
    /// Rewrites a GPU-oriented argument list into a CPU-only one for the last-resort fallback used
    /// when a machine turns out to have no usable accelerator at all.
    ///
    /// PaRuX ships only CUDA and Vulkan runtime builds, and the non-CUDA path pins
    /// <c>--device Vulkan0</c>. On a machine with no Vulkan-capable device (a VM, a headless
    /// server, an old iGPU without a Vulkan driver) that pin makes llama-server fail to start and
    /// text inference is simply unavailable - whereas the shipped Vulkan build already contains a
    /// CPU backend and can serve the same model without it. This keeps PaRuX usable there instead
    /// of failing outright, without adding another runtime download.
    ///
    /// Pure and order-preserving so it can be unit-tested directly: the "no accelerator present"
    /// machine cannot be reproduced on a development box that has one.
    /// </summary>
    public static List<string> ToCpuFallbackArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Flags whose values only describe accelerator placement/VRAM fitting; on CPU they are
        // either meaningless or actively rejected by llama-server.
        var dropWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "--device", "--tensor-split", "--main-gpu", "--gpu-layers", "--split-mode",
            "--fit", "--fit-target", "--spec-draft-device", "--n-cpu-moe", "--override-tensor"
        };

        var result = new List<string>(arguments.Count + 4);
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (dropWithValue.Contains(argument))
            {
                i++; // also skip its value
                continue;
            }
            result.Add(argument);
        }

        // Explicitly keep every layer on the CPU rather than relying on a default.
        result.AddRange(["--gpu-layers", "0"]);

        int logical = Math.Max(1, Environment.ProcessorCount);
        if (!result.Contains("--threads"))
            result.AddRange(["--threads", Math.Max(1, logical - Math.Max(1, logical / 4)).ToString()]);
        if (!result.Contains("--threads-batch"))
            result.AddRange(["--threads-batch", logical.ToString()]);
        return result;
    }

    private static void AppendSpeculativeArguments(List<string> args, LlamaPerformanceProfileDto profile)
    {
        string type = NormalizeSpeculative(profile.SpeculativeType);
        if (type == "none") return;
        args.AddRange(["--spec-type", type]);
        if (profile.SpeculativeDraftMax > 0)
            args.AddRange(["--spec-draft-n-max", profile.SpeculativeDraftMax.ToString()]);
        if (!string.IsNullOrWhiteSpace(profile.SpeculativeDraftModelPath))
            args.AddRange(["--spec-draft-model", profile.SpeculativeDraftModelPath!]);
        if (!string.IsNullOrWhiteSpace(profile.SpeculativeDraftDevice))
            args.AddRange(["--spec-draft-device", profile.SpeculativeDraftDevice!]);
    }

    internal static long EstimateLoadedBytes(long modelSizeBytes)
    {
        if (modelSizeBytes <= 0) return 0;
        double estimate = modelSizeBytes * 1.16 + 768d * MiB;
        return estimate >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(estimate);
    }

    private static string NormalizeCache(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "f16" => "f16",
        "bf16" => "bf16",
        "q8_0" => "q8_0",
        "q5_0" => "q5_0",
        "q5_1" => "q5_1",
        "q4_0" => "q4_0",
        "q4_1" => "q4_1",
        "iq4_nl" => "iq4_nl",
        _ => "q8_0"
    };

    private static string NormalizeFlash(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "off" => "off",
        "auto" => "auto",
        _ => "on"
    };

    private static string NormalizeSplit(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "layer" => "layer",
        "row" => "row",
        "tensor" => "tensor",
        _ => "none"
    };

    private static string NormalizeSpeculative(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ngram-simple" => "ngram-simple",
        "ngram-mod" => "ngram-mod",
        "draft-simple" => "draft-simple",
        "draft-eagle3" => "draft-eagle3",
        "draft-dflash" => "draft-dflash",
        "draft-dspark" => "draft-dspark",
        "draft-mtp" => "draft-mtp",
        "draft-mtp,ngram-mod" => "draft-mtp,ngram-mod",
        _ => "none"
    };

    private static string NormalizeReasoningEffort(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        "max" => "max",
        _ => "default"
    };

    private static string NormalizeLoadMode(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "none" => "none",
        "mmap" => "mmap",
        "mlock" => "mlock",
        "mmap+mlock" => "mmap+mlock",
        "dio" => "dio",
        _ => "auto"
    };

    private static string NormalizeNuma(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "distribute" => "distribute",
        "isolate" => "isolate",
        "numactl" => "numactl",
        _ => "none"
    };
}
