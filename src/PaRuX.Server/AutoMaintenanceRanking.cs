using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The text runtime actually in use: which backend, its build tag and its real ownership.</summary>
internal sealed record ResolvedTextRuntime(string Backend, InstalledRuntime Runtime);

/// <summary>A newer-build observation for one installed text backend (read-only; nothing is downloaded).</summary>
internal sealed record RuntimeUpdateObservation(string Backend, string? InstalledTag, string LatestTag, ComponentOwnership Ownership);

internal static class InstalledRuntimeResolver
{
    private static readonly (string ComponentId, string Backend)[] Order =
    {
        ("llama-server-cuda", "text-cuda"),
        ("llama-server-vulkan", "text-vulkan")
    };

    /// <summary>First detected llama.cpp component, with the ownership the component inventory reports and the
    /// per-backend active build tag. Null when no text runtime is installed.</summary>
    internal static ResolvedTextRuntime? Resolve(IReadOnlyList<ComponentStatus> components, Func<string, string?> activeTag)
    {
        foreach ((string id, string backend) in Order)
        {
            ComponentStatus? c = components.FirstOrDefault(x => x.Id == id && x.Detected);
            if (c is not null) return new ResolvedTextRuntime(backend, new InstalledRuntime(activeTag(backend), c.Ownership));
        }
        return null;
    }
}

/// <summary>Pure ranking of discovery candidates against the current hardware. It states its criterion
/// (hardware fit versus what is installed) and never presents popularity or recency as quality.</summary>
internal static class MaintenanceCandidateRanker
{
    /// <summary>Per lane (standard/uncensored) and per capability.</summary>
    internal const int MaxPerLaneCapability = 5;
    internal const int MaxProposals = MaxPerLaneCapability * 8;
    internal const string Label = "candidate, not validated";

    internal static List<MaintenanceProposal> Rank(
        IReadOnlyList<ModelDiscoveryCandidate> candidates,
        IReadOnlyList<ManagedModelInfo> installed,
        HardwareFingerprint hardware,
        InstalledRuntime? runtime,
        IReadOnlyDictionary<string, string> alreadyProposed)
    {
        long vram = hardware.Adapters.Count == 0 ? 0 : hardware.Adapters.Max(a => a.DedicatedVramBytes);
        long ram = hardware.SystemRamBytes;

        var installedFits = installed
            .Where(m => !m.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .Select(m => (Cap: FirstModelAdvisor.InferCapabilities(m.FileName, m.Alias, m.RepoId),
                          Fit: FirstModelAdvisor.Classify(m.SizeBytes, vram, ram)))
            .ToList();

        var scored = new List<(ModelDiscoveryCandidate C, ModelSuitability Fit, string Cap, string Reason, int Cohort, ModelLaneAssignment Lane)>();
        foreach (ModelDiscoveryCandidate c in candidates)
        {
            if (c.Disposition != "active" || c.IgnoredUtc is not null) continue;
            if (alreadyProposed.TryGetValue(c.Key, out string? rev) && rev == c.Revision) continue;
            if (c.DeclaredSizeBytes is not > 0) continue;
            string file = Path.GetFileName(c.RepoFilePath);
            if (file.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;

            ModelSuitability fit = FirstModelAdvisor.Classify(c.DeclaredSizeBytes.Value, vram, ram);
            int rank = FitRank(fit);
            if (rank >= 99) continue; // too large / unknown: never proposed

            string cap = PrimaryCapability(FirstModelAdvisor.InferCapabilities(file, null, c.RepoId));
            int bestInstalled = installedFits.Where(i => i.Cap.Contains(cap)).Select(i => FitRank(i.Fit)).DefaultIfEmpty(99).Min();
            if (rank >= bestInstalled) continue; // does not beat what is installed on the stated criterion

            string reason = bestInstalled >= 99
                ? $"Criterion: hardware fit. No installed model covers {cap}; this candidate is estimated to {Describe(fit)}."
                : $"Criterion: hardware fit. Estimated to {Describe(fit)}, better than the best installed {cap} model ({Describe(Fit(installedFits, cap))}).";
            int cohort = c.DiscoveryCohorts.Any(x => x is "new" or "recently_updated") ? 0 : 1;
            scored.Add((c, fit, cap, reason, cohort, ModelLaneClassifier.Assign(c.RepoId, file, c.DeclaredTags)));
        }

        var result = new List<MaintenanceProposal>();
        // Each lane keeps its own ranking per capability: hardware fit first, then declared cohort, then key.
        // Popularity is never used as quality.
        var ranked = scored
            .GroupBy(x => (x.Lane.Lane, x.Cap))
            .SelectMany(g => g.OrderBy(x => FitRank(x.Fit)).ThenBy(x => x.Cohort).ThenBy(x => x.C.Key, StringComparer.Ordinal).Take(MaxPerLaneCapability))
            .OrderBy(x => x.Lane.Lane == ModelLaneClassifier.Standard ? 0 : 1).ThenBy(x => x.Cap, StringComparer.Ordinal)
            .ThenBy(x => FitRank(x.Fit)).ThenBy(x => x.Cohort).ThenBy(x => x.C.Key, StringComparer.Ordinal);
        foreach (var s in ranked)
        {
            ModelRuntimeAssessment rt = ModelRuntimeRequirements.Evaluate(
                s.C.RepoId, null, runtime?.BuildTag, runtime?.Ownership ?? ComponentOwnership.SYSTEM_DETECTED);
            bool needsRuntime = rt.Verdict is RuntimeRequirementVerdict.NeedsRuntimeUpdate or RuntimeRequirementVerdict.NeedsUserAction;
            string reason = needsRuntime ? s.Reason + " Needs runtime update before use: " + rt.Message : s.Reason;
            result.Add(new MaintenanceProposal
            {
                Kind = needsRuntime ? "candidate_needs_runtime_update" : "new_candidate",
                Key = s.C.Key,
                Repo = s.C.RepoId,
                Revision = s.C.Revision,
                Cohorts = s.C.DiscoveryCohorts.Take(4).ToList(),
                SizeBytes = s.C.DeclaredSizeBytes,
                FitClass = s.Fit.ToString(),
                License = s.C.DeclaredLicense,
                Capability = s.Cap,
                Lane = s.Lane.Lane,
                LaneEvidence = s.Lane.Evidence,
                Reason = Clip(reason),
                Detail = Label + ". " + Clip(reason) + (needsRuntime ? "" : " Review in Model Hub before any download.")
            });
        }
        return result;
    }

    private static ModelSuitability Fit(List<(List<string> Cap, ModelSuitability Fit)> installed, string cap) =>
        installed.Where(i => i.Cap.Contains(cap)).OrderBy(i => FitRank(i.Fit)).Select(i => i.Fit).FirstOrDefault();

    internal static int FitRank(ModelSuitability fit) => fit switch
    {
        ModelSuitability.FitsOnGpu => 0,
        ModelSuitability.SplitWithRam => 1,
        ModelSuitability.CpuOnly => 2,
        _ => 99
    };

    private static string Describe(ModelSuitability fit) => fit switch
    {
        ModelSuitability.FitsOnGpu => "fit in GPU memory",
        ModelSuitability.SplitWithRam => "need a GPU/RAM split",
        ModelSuitability.CpuOnly => "run on the CPU only",
        _ => "not fit this machine"
    };

    private static string PrimaryCapability(List<string> caps) =>
        caps.Contains(ModelCapabilityIds.Coding) ? ModelCapabilityIds.Coding
        : caps.Contains(ModelCapabilityIds.Vision) ? ModelCapabilityIds.Vision
        : caps.Contains(ModelCapabilityIds.Reasoning) ? ModelCapabilityIds.Reasoning
        : ModelCapabilityIds.GeneralChat;

    private static string Clip(string v) => v.Length <= 300 ? v : v[..300];
}
