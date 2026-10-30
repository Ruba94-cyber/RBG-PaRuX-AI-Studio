using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Answers the question a first-time user actually has - "which model do I download?" - from the
/// hardware in front of them, in words rather than in numbers they have no way to judge.
///
/// PaRuX already had both halves and never joined them: a curated model list with no idea what
/// machine it was being shown on, and a hardware fit estimator that lived in the Windows client so
/// nothing else could use it. The result was a first run that finished with no model and a search
/// box, which is only useful to someone who already knows what to type.
///
/// The memory calculation is a rough pre-download fit estimate, not a quality or latency verdict.
/// Exact file, runtime, context and workload evidence supersede it. Capability groups are candidate
/// shortlists only until comparable local task-quality and performance evidence exists.
/// </summary>
public static class FirstModelAdvisor
{
    /// <summary>Weights are not the whole cost. A quarter on top covers the KV cache at a normal
    /// context plus the compute buffers, which is close to what llama.cpp actually reserves.</summary>
    private const double RuntimeOverheadFactor = 1.25;

    /// <summary>System RAM is shared with Windows, the browser, and whatever else is open. Offering
    /// a model that needs every byte of it is offering a machine that stops responding.</summary>
    private const double UsableSystemRamFraction = 0.60;

    /// <summary>
    /// The one adapter a single model would be loaded onto: the largest real GPU, never the sum and
    /// never "whichever came back first". Both the memory figure and the name must come from here,
    /// together - reading them from different adapters is what told an RTX 5080 machine that its
    /// 15.6 GB of video memory belonged to an AMD 780M iGPU.
    /// </summary>
    public static GpuAdapterStatus? SelectPrimaryAdapter(IEnumerable<GpuAdapterStatus>? adapters) =>
        adapters?
            .Where(adapter => !adapter.IsSoftware && adapter.DedicatedVideoMemoryBytes > 0)
            .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
            .FirstOrDefault();

    public static ModelSuitability Classify(long modelBytes, long vramBytes, long ramBytes)
        => Classify(modelBytes, vramBytes, ramBytes, sharedMemoryPrimary: false);

    private static ModelSuitability Classify(
        long modelBytes, long acceleratorMemoryBytes, long ramBytes, bool sharedMemoryPrimary,
        long? usableSystemRamOverride = null)
    {
        if (modelBytes <= 0) return ModelSuitability.Unknown;

        long needed = (long)(modelBytes * RuntimeOverheadFactor);
        long usableRam = usableSystemRamOverride.HasValue
            ? Math.Max(0, usableSystemRamOverride.Value)
            : (long)(ramBytes * UsableSystemRamFraction);

        if (acceleratorMemoryBytes > 0 && needed <= acceleratorMemoryBytes) return ModelSuitability.FitsOnGpu;
        // Part on the GPU, the rest in RAM. This runs, and it runs several times slower than a model
        // that fits - which is a thing to say out loud rather than to discover.
        if (sharedMemoryPrimary)
        {
            // UMA's accelerator aperture is backed by this same physical RAM. Compare with the
            // conservative host pool, but never add the aperture to RAM a second time.
            if (acceleratorMemoryBytes > 0 && needed <= usableRam) return ModelSuitability.SplitWithRam;
            return ModelSuitability.TooLarge;
        }
        if (acceleratorMemoryBytes > 0 && needed <= acceleratorMemoryBytes + usableRam) return ModelSuitability.SplitWithRam;
        if (needed <= usableRam) return ModelSuitability.CpuOnly;
        return ModelSuitability.TooLarge;
    }

    /// <summary>
    /// The seeded list, ordered by estimated memory fit. It does not rank task quality.
    /// </summary>
    public static FirstModelAdvice Advise(RuntimeManagerHardwareStatus? hardware)
    {
        RuntimeAcceleratorProfile? accelerator = hardware?.AcceleratorProfile;
        if (accelerator != null)
        {
            long ramBytes = Math.Max(0, hardware?.RamTotalBytes ?? accelerator.SystemRamBytes);
            bool sharedMemoryPrimary = accelerator.AcceleratorEligible && accelerator.SharedMemoryPrimary;
            bool sharedMemoryProfile = accelerator.SharedMemoryPrimary;
            long usableRamBytes = Math.Max(0, accelerator.UsableSystemRamBytes
                ?? RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(ramBytes));
            long acceleratorMemoryBytes = !accelerator.AcceleratorEligible
                ? 0
                : sharedMemoryPrimary
                    ? Math.Min(Math.Max(0, accelerator.SharedSystemMemoryLimitBytes), usableRamBytes)
                    : Math.Max(0, accelerator.DedicatedVideoMemoryBytes);
            return Advise(
                acceleratorMemoryBytes,
                ramBytes,
                accelerator.AdapterName ?? string.Empty,
                sharedMemoryPrimary,
                usableRamBytes,
                accelerator.GpuPresent,
                accelerator.AcceleratorEligible);
        }

        // Largest single adapter, not the sum: a model is loaded onto one GPU unless the user
        // explicitly splits it, so adding two cards together would promise memory that is not
        // reachable for this download.
        //
        // The name has to come from the same adapter as the number. Taking the memory from the
        // largest adapter and the name from whichever one the platform happened to enumerate
        // first told a machine with an RTX 5080 and an AMD iGPU that its 15.6 GB of video memory
        // belonged to the iGPU - the first sentence a new user reads, and wrong.
        GpuAdapterStatus? largest = SelectPrimaryAdapter(hardware?.GpuAdapters);

        long vram = largest?.DedicatedVideoMemoryBytes ?? 0;
        string gpuName = largest?.Name ?? string.Empty;

        if (vram <= 0)
        {
            // No per-adapter inventory. The aggregate figures stay usable, but they cannot be
            // attributed to a named card, so the advice says "your GPU" rather than guessing.
            vram = hardware?.GpuLargestDedicatedVideoMemoryBytes ?? 0;
            if (vram <= 0 && hardware is { VramTotalMib: > 0 })
                vram = (long)hardware.VramTotalMib * 1024 * 1024;
            gpuName = string.Empty;
        }

        long hostRam = Math.Max(0, hardware?.RamTotalBytes ?? 0);
        return Advise(vram, hostRam, gpuName,
            sharedMemoryPrimary: false,
            usableSystemRamOverride: RuntimeHardwareProfileResolver.ConservativeUsableSystemRamBytes(hostRam),
            physicalGpuPresent: vram > 0,
            acceleratorEligible: true);
    }

    /// <summary>Production onboarding path: use the same inventory/backend resolver as runtime
    /// status, routing and context budgeting rather than rebuilding a second GPU heuristic.</summary>
    public static FirstModelAdvice Advise(RuntimeManagerStatus? status, GpuInventoryStatus? inventory, IEnumerable<ManagedModelInfo>? availableLocalModels = null, InstalledRuntime? runtime = null)
    {
        if (status == null)
            return Advise(0, 0, string.Empty, availableLocalModels, runtime);

        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(status, inventory);
        long acceleratorMemoryBytes = !profile.AcceleratorEligible
            ? 0
            : profile.SharedMemoryPrimary
                ? Math.Min(profile.SharedSystemMemoryLimitBytes, profile.UsableSystemRamBytes)
                : profile.DedicatedVideoMemoryBytes;

        return Advise(
            acceleratorMemoryBytes,
            Math.Max(0, status.RamTotalBytes),
            profile.AdapterName ?? string.Empty,
            profile.AcceleratorEligible && profile.SharedMemoryPrimary,
            profile.UsableSystemRamBytes,
            profile.GpuPresent,
            profile.AcceleratorEligible,
            availableLocalModels,
            runtime);
    }

    /// <summary>The decision itself, over plain numbers - no hardware plumbing, so it is directly
    /// testable against the machines this has to be right for.</summary>
    public static FirstModelAdvice Advise(long vram, long ram, string gpuName, IEnumerable<ManagedModelInfo>? availableLocalModels = null, InstalledRuntime? runtime = null,
        IReadOnlyList<RecommendedModel>? catalog = null)
        => Advise(vram, ram, gpuName, sharedMemoryPrimary: false, usableSystemRamOverride: null, physicalGpuPresent: vram > 0, acceleratorEligible: true, availableLocalModels: availableLocalModels, runtime: runtime, catalog: catalog);

    private static FirstModelAdvice Advise(
        long vram,
        long ram,
        string gpuName,
        bool sharedMemoryPrimary,
        long? usableSystemRamOverride = null,
        bool physicalGpuPresent = false,
        bool acceleratorEligible = true,
        IEnumerable<ManagedModelInfo>? availableLocalModels = null,
        InstalledRuntime? runtime = null,
        IReadOnlyList<RecommendedModel>? catalog = null)
    {
        var candidates = new List<FirstModelCandidate>();

        if (availableLocalModels != null)
        {
            foreach (ManagedModelInfo local in availableLocalModels)
            {
                if (local.SizeBytes <= 0 || string.IsNullOrWhiteSpace(local.FilePath)) continue;
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(local.FileName);
                string id = "local:" + fileNameWithoutExt;
                string title = (local.Alias ?? fileNameWithoutExt) + " (Installed)";
                List<string> caps = InferCapabilities(local.FileName, local.Alias, local.RepoId);
                string contentProfile = InferContentProfile(local.FileName, local.Alias);
                RecommendedModel rec = new()
                {
                    Id = id,
                    Title = title,
                    Category = "Installed local model",
                    Description = "Available on disk: " + local.FilePath,
                    HuggingFaceRepoId = local.RepoId ?? string.Empty,
                    ApproxParams = ExtractParamHint(local.FileName),
                    QuantHint = local.Quantization,
                    CapabilityIds = caps,
                    ContentProfileId = contentProfile,
                    EstimatedSizeBytes = local.SizeBytes,
                    EstimatedRuntimeMemoryFactor = 1.15
                };
                double runtimeMemoryFactor = 1.15;
                ModelSuitability suitability = Classify(
                    rec.EstimatedSizeBytes, vram, ram, runtimeMemoryFactor, sharedMemoryPrimary, usableSystemRamOverride);
                int baseRank = RankOf(suitability, rec);
                int rank = baseRank > 0 ? baseRank + 1000 : 0;
                candidates.Add(ApplyRuntimeRequirement(new FirstModelCandidate
                {
                    Model = rec,
                    Suitability = suitability.ToString(),
                    Reason = "Locally available on disk. " + Explain(suitability, rec, vram, ram, gpuName, runtimeMemoryFactor, sharedMemoryPrimary),
                    Rank = rank
                }, runtime));
            }
        }
        foreach (RecommendedModel model in catalog ?? RecommendedModelsService.List)
        {
            double runtimeMemoryFactor = RuntimeMemoryFactorFor(model);
            ModelSuitability suitability = Classify(
                model.EstimatedSizeBytes, vram, ram, runtimeMemoryFactor, sharedMemoryPrimary, usableSystemRamOverride);
            candidates.Add(ApplyRuntimeRequirement(new FirstModelCandidate
            {
                Model = model,
                Suitability = suitability.ToString(),
                Reason = Explain(suitability, model, vram, ram, gpuName, runtimeMemoryFactor, sharedMemoryPrimary),
                Rank = RankOf(suitability, model)
            }, runtime));
        }

        List<FirstModelCandidate> ordered = candidates
            .OrderByDescending(c => c.Rank)
            .ThenByDescending(c => c.Model.EstimatedSizeBytes)
            .ToList();

        FirstModelCandidate? best = ordered.FirstOrDefault(c => c.Rank > 0);

        return new FirstModelAdvice
        {
            HardwareSummary = DescribeHardware(
                vram, ram, gpuName, sharedMemoryPrimary, physicalGpuPresent || vram > 0, acceleratorEligible),
            GpuDetected = physicalGpuPresent || vram > 0,
            VramBytes = vram,
            RamBytes = ram,
            RecommendedId = best?.Model.Id,
            Headline = best == null
                ? "PaRuX could not estimate a comfortable general-chat starter from this shortlist. "
                  + "You can still compare task-specific candidates in Model Hub."
                : best.Model.Id.StartsWith("local:", StringComparison.Ordinal)
                    ? "Locally available model ready to use: " + best.Model.Title + ". " + best.Reason
                      + " This model is already present on this PC and ready to run immediately without downloading."
                    : "General-chat starter candidate: " + best.Model.Title + ". " + best.Reason
                      + " This is a hardware-fit estimate, not a task-quality winner; other tasks may need different models.",
            Candidates = ordered,
            CapabilityGroups = BuildCapabilityGroups(ordered)
        };
    }

    /// <summary>
    /// A model whose architecture the installed llama.cpp cannot load is reported as such before download or launch,
    /// and is never the recommended starter. With no runtime information the requirement is treated as unconfirmed.
    /// </summary>
    private static FirstModelCandidate ApplyRuntimeRequirement(FirstModelCandidate candidate, InstalledRuntime? runtime)
    {
        ModelRuntimeAssessment a = ModelRuntimeRequirements.Evaluate(
            candidate.Model.HuggingFaceRepoId, null, runtime?.BuildTag,
            runtime?.Ownership ?? ComponentOwnership.SYSTEM_DETECTED);
        if (a.Verdict == RuntimeRequirementVerdict.NotApplicable) return candidate;

        candidate.RuntimeRequirementState = a.Verdict.ToString();
        if (a.Verdict == RuntimeRequirementVerdict.Satisfied) return candidate;

        candidate.RuntimeRequirementMessage = a.Message;
        candidate.Reason = a.Message + " " + candidate.Reason;
        candidate.Rank = 0;
        return candidate;
    }

    private static List<FirstModelCapabilityGroup> BuildCapabilityGroups(List<FirstModelCandidate> candidates)
    {
        var groups = new List<FirstModelCapabilityGroup>
        {
            BuildTextGroup(ModelCapabilityIds.GeneralChat, "General chat", ModelContentProfileIds.Standard,
                "Hardware-fit shortlist only. Quality and sustained speed for this exact file/runtime/task have not been validated."),
            BuildTextGroup(ModelCapabilityIds.Reasoning, "Multi-step reasoning", ModelContentProfileIds.Standard,
                "Reasoning-focused candidates are not winners until fixed reasoning tasks and reliability are measured."),
            BuildTextGroup(ModelCapabilityIds.Coding, "Coding", ModelContentProfileIds.Standard,
                "Code-specialist candidates require passing the same tests and patch tasks as the incumbent."),
            BuildTextGroup(ModelCapabilityIds.Vision, "Image understanding (vision)", ModelContentProfileIds.Standard,
                "Vision-language candidates are separate from image generation and editing; visual quality is not measured here."),
            BuildTextGroup(ModelCapabilityIds.GeneralChat, "General chat — uncensored preference", ModelContentProfileIds.Uncensored,
                "This is an explicit content preference, not a capability score or default. PaRuX safety controls still apply."),
            BuildMediaGroup(ModelCapabilityIds.ImageGenerate, "Image generation",
                "Workflow-compatible checkpoint candidates; same-prompt image quality and generation time are not yet compared."),
            BuildMediaGroup(ModelCapabilityIds.ImageEdit, "Image editing",
                "Workflow-compatible editing candidates; edit fidelity and generation time are not yet compared."),
            BuildMediaGroup(ModelCapabilityIds.VideoGenerate, "Video generation",
                "No supported video candidate with matching local quality/performance evidence is in this starter catalog.")
        };

        return groups;

        FirstModelCapabilityGroup BuildTextGroup(
            string capabilityId, string title, string contentProfileId, string evidenceNote) => new()
        {
            CapabilityId = capabilityId,
            Title = title,
            ContentProfileId = contentProfileId,
            EvidenceState = ModelAdviceEvidenceStates.Provisional,
            EvidenceNote = evidenceNote,
            TextCandidates = candidates
                .Where(candidate => candidate.Model.ContentProfileId == contentProfileId
                    && candidate.Model.CapabilityIds.Contains(capabilityId, StringComparer.Ordinal))
                .OrderByDescending(candidate => candidate.Rank)
                .ThenByDescending(candidate => candidate.Model.EstimatedSizeBytes)
                .ToList()
        };

        static FirstModelCapabilityGroup BuildMediaGroup(string capabilityId, string title, string evidenceNote) => new()
        {
            CapabilityId = capabilityId,
            Title = title,
            EvidenceState = ModelAdviceEvidenceStates.Provisional,
            EvidenceNote = evidenceNote,
            MediaCandidates = RecommendedCheckpointsService.List
                .Where(candidate => string.Equals(candidate.CapabilityId, capabilityId, StringComparison.Ordinal))
                .ToList()
        };
    }

    /// <summary>
    /// Higher is better for this initial general-chat fit shortlist only; capability tags are explicit
    /// and uncensored content preference is never selected as the standard starter.
    /// </summary>
    private static int RankOf(ModelSuitability suitability, RecommendedModel model)
    {
        int baseRank = suitability switch
        {
            ModelSuitability.FitsOnGpu => 400,
            ModelSuitability.SplitWithRam => 200,
            ModelSuitability.CpuOnly => 100,
            _ => 0
        };
        if (baseRank == 0) return 0;

        bool generalPurpose = model.CapabilityIds.Contains(ModelCapabilityIds.GeneralChat, StringComparer.Ordinal);
        bool uncensored = model.ContentProfileId == ModelContentProfileIds.Uncensored;

        // An uncensored content profile is an explicit user choice, never the implicit standard starter.
        if (uncensored) return baseRank - 60;
        return generalPurpose ? baseRank + 20 : baseRank;
    }

    private static double RuntimeMemoryFactorFor(RecommendedModel model) =>
        model.EstimatedRuntimeMemoryFactor is > 1.0 and <= 2.0
            ? model.EstimatedRuntimeMemoryFactor.Value
            : RuntimeOverheadFactor;

    private static ModelSuitability Classify(long modelBytes, long vramBytes, long ramBytes, double factor)
        => Classify(modelBytes, vramBytes, ramBytes, factor, sharedMemoryPrimary: false);

    private static ModelSuitability Classify(
        long modelBytes,
        long acceleratorMemoryBytes,
        long ramBytes,
        double factor,
        bool sharedMemoryPrimary,
        long? usableSystemRamOverride = null)
    {
        if (modelBytes <= 0) return ModelSuitability.Unknown;

        long needed = (long)(modelBytes * factor);
        long usableRam = usableSystemRamOverride.HasValue
            ? Math.Max(0, usableSystemRamOverride.Value)
            : (long)(ramBytes * UsableSystemRamFraction);

        if (acceleratorMemoryBytes > 0 && needed <= acceleratorMemoryBytes) return ModelSuitability.FitsOnGpu;
        if (sharedMemoryPrimary)
        {
            if (acceleratorMemoryBytes > 0 && needed <= usableRam) return ModelSuitability.SplitWithRam;
            return ModelSuitability.TooLarge;
        }
        if (acceleratorMemoryBytes > 0 && needed <= acceleratorMemoryBytes + usableRam) return ModelSuitability.SplitWithRam;
        if (needed <= usableRam) return ModelSuitability.CpuOnly;
        return ModelSuitability.TooLarge;
    }

    private static string Explain(
        ModelSuitability suitability, RecommendedModel model, long vram, long ram, string gpuName,
        double runtimeMemoryFactor, bool sharedMemoryPrimary)
    {
        string size = Gb(model.EstimatedSizeBytes);
        string gpu = string.IsNullOrWhiteSpace(gpuName) ? "your GPU" : gpuName;

        // What decides the fit is the weights plus the runtime's own overhead, never the weights
        // alone. Quoting the raw weight size against VRAM produced sentences that contradicted
        // themselves - "About 13.4 GB, more than ... 15.6 GB of video memory" - and left the
        // reader unable to tell whether the verdict or the number was wrong. Both are right; the
        // missing quarter was simply never named.
        string needed = Gb((long)(model.EstimatedSizeBytes * runtimeMemoryFactor));

        return suitability switch
        {
            ModelSuitability.FitsOnGpu =>
                $"About {size} of weights, {needed} once the KV cache and compute buffers are counted, "
                + (sharedMemoryPrimary
                    ? $"which fits within {gpu}'s {Gb(vram)} shared system-memory limit for the accelerator. This is not dedicated VRAM, and real speed still depends on memory bandwidth."
                    : $"which still fits entirely in {gpu}'s {Gb(vram)} of video memory - this is the fast case."),
            ModelSuitability.SplitWithRam =>
                $"About {size} of weights, {needed} once the KV cache and compute buffers are counted, "
                + (sharedMemoryPrimary
                    ? $"more than {gpu}'s {Gb(vram)} shared-memory aperture but within conservative usable system RAM; that accelerator aperture is part of RAM and is not added twice. "
                    : $"more than {gpu}'s {Gb(vram)} of video memory, so part of it runs in system RAM. ")
                + "It may work, but expected speed depends on the exact runtime and workload; PaRuX has no matching local throughput measurement for this candidate.",
            ModelSuitability.CpuOnly =>
                $"About {size} of weights, {needed} with runtime overhead, which fits in system RAM "
                + $"({Gb(ram)} total) but runs on the processor. "
                + "Expect a few words per second rather than a stream.",
            ModelSuitability.TooLarge =>
                $"About {size} of weights, {needed} with runtime overhead - more than this machine can "
                + (sharedMemoryPrimary
                    ? "hold within the shared-memory limit and conservative usable system RAM together."
                    : "hold in video memory and RAM together. It would swap to disk and be unusable."),
            _ => "PaRuX could not size this model for your hardware."
        };
    }

    private static string DescribeHardware(
        long vram,
        long ram,
        string gpuName,
        bool sharedMemoryPrimary,
        bool physicalGpuPresent,
        bool acceleratorEligible)
    {
        if (physicalGpuPresent && !acceleratorEligible)
        {
            string detectedGpu = string.IsNullOrWhiteSpace(gpuName) ? "A physical GPU" : gpuName;
            return $"{detectedGpu} detected, but no compatible PaRuX text backend is installed; {Gb(ram)} of system RAM. Current fit advice is CPU/RAM-only.";
        }
        if (vram <= 0)
        {
            return ram > 0
                ? $"No dedicated GPU detected, {Gb(ram)} of system RAM. Models will run on the processor."
                : "PaRuX could not read this machine's memory, so these suggestions are generic.";
        }
        string gpu = string.IsNullOrWhiteSpace(gpuName) ? "GPU" : gpuName;
        if (sharedMemoryPrimary)
            return $"{gpu} with up to {Gb(vram)} of shared system memory for acceleration; {Gb(ram)} of RAM total (one shared pool, not extra VRAM).";
        return $"{gpu} with {Gb(vram)} of video memory, {Gb(ram)} of system RAM.";
    }

    private static string Gb(long bytes) =>
        bytes <= 0 ? "an unknown amount" : (bytes / 1024d / 1024d / 1024d).ToString("0.#",
            System.Globalization.CultureInfo.InvariantCulture) + " GB";

    public static List<string> InferCapabilities(string? fileName, string? alias = null, string? repoId = null)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One taxonomy with ModelCategoryClassifier: the same boundary-aware keywords decide coding, reasoning and
        // vision, so a name never gets one capability on the role screen and another on the search screen.
        foreach (string category in ModelCategoryClassifier.CapabilitiesOf(fileName, alias, repoId))
            caps.Add(CapabilityId(category));

        // Every model can do general chat
        caps.Add(ModelCapabilityIds.GeneralChat);

        return caps.ToList();
    }

    /// <summary>The API capability id for a taxonomy category. The category names are the search screen's words, and
    /// the capability ids are the chat contract's words: reasoning is "chat.reasoning" there, not "reasoning".</summary>
    private static string CapabilityId(string category) => category switch
    {
        ModelCategoryClassifier.Coding => ModelCapabilityIds.Coding,
        ModelCategoryClassifier.Vision => ModelCapabilityIds.Vision,
        ModelCategoryClassifier.Reasoning => ModelCapabilityIds.Reasoning,
        _ => category
    };

    public static string InferContentProfile(string? fileName, string? alias = null)
    {
        string combined = $"{fileName} {alias}".ToLowerInvariant();
        if (combined.Contains("uncensored") || combined.Contains("abliterated") || combined.Contains("unfiltered") || combined.Contains("dare"))
        {
            return ModelContentProfileIds.Uncensored;
        }
        return ModelContentProfileIds.Standard;
    }

    public static string ExtractParamHint(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(?i)(\d+(?:\.\d+)?)\s*[bm]");
        if (match.Success)
        {
            return match.Value.ToUpperInvariant();
        }
        return string.Empty;
    }
}

/// <summary>How well one model matches the machine it would run on.</summary>
public enum ModelSuitability
{
    Unknown,
    /// <summary>Weights and runtime overhead fit in video memory. The fast case.</summary>
    FitsOnGpu,
    /// <summary>Estimated to need some system RAM; local runtime speed is not implied.</summary>
    SplitWithRam,
    /// <summary>Fits in system RAM but runs on the processor.</summary>
    CpuOnly,
    /// <summary>Would not fit even with RAM offload.</summary>
    TooLarge
}
