using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Produces a local model proposal without changing catalog, storage, or runtime state.</summary>
public static class CapabilityAcquisitionPlanner
{
    private const string LocalCatalogProvider = "huggingface";

    public static CapabilityAcquisitionOutcome Plan(
        string requestedCapability,
        ModelDiscoveryCatalog catalog,
        IReadOnlyCollection<InstalledCapabilityModel> installedModels,
        long freeStorageBudgetBytes,
        IReadOnlyDictionary<string, ModelSuitability> hardwareFitByCandidateKey,
        IReadOnlyDictionary<string, CandidateLicenseClass> licenseClassByCandidateKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installedModels);
        ArgumentNullException.ThrowIfNull(hardwareFitByCandidateKey);
        ArgumentNullException.ThrowIfNull(licenseClassByCandidateKey);

        if (string.IsNullOrWhiteSpace(requestedCapability))
            return new NoCompatibleCandidate("Choose a capability before looking for a local model.");

        string capability = NormalizeCapability(requestedCapability);
        if (!IsSupportedCapability(capability))
            return new NoCompatibleCandidate($"No compatible local model candidate supports '{capability}'.");

        InstalledCapabilityModel? satisfied = installedModels
            .Where(model => model is not null && model.IsRecommended && SupportsInstalledCapability(model, capability))
            .OrderBy(model => model.ModelId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (satisfied is not null)
            return new AlreadySatisfied(satisfied.ModelId, capability);

        var unsupportedCandidates = new List<(ModelDiscoveryCandidate Candidate, string Reason)>();
        var compatibleCandidates = new List<(ModelDiscoveryCandidate Candidate, ModelSuitability Fit, long Size)>();
        bool matchingLocalCandidateFound = false;

        foreach (ModelDiscoveryCandidate? candidate in catalog.Candidates ?? new List<ModelDiscoveryCandidate>())
        {
            // Acquisition is local-only. Other providers are ignored so their names and services never
            // leak into a proposal or become a substitute for a local model.
            if (candidate is null || !string.Equals(candidate.Provider?.Trim(), LocalCatalogProvider, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!SupportsCandidateCapability(candidate, capability))
                continue;

            matchingLocalCandidateFound = true;
            string candidateKey = CandidateKey(candidate);
            string? unsupportedReason = ValidateCandidate(candidate);
            if (unsupportedReason is not null)
            {
                unsupportedCandidates.Add((candidate, unsupportedReason));
                continue;
            }

            if (!hardwareFitByCandidateKey.TryGetValue(candidate.Key, out ModelSuitability fit))
            {
                unsupportedCandidates.Add((candidate, "Hardware fit has not been evaluated for this candidate."));
                continue;
            }

            if (fit == ModelSuitability.Unknown)
            {
                unsupportedCandidates.Add((candidate, "The hardware fit is unknown, so this candidate cannot be recommended yet."));
                continue;
            }

            if (fit == ModelSuitability.TooLarge)
                continue;

            compatibleCandidates.Add((candidate, fit, candidate.DeclaredSizeBytes!.Value));
        }

        if (compatibleCandidates.Count == 0)
        {
            if (unsupportedCandidates.Count > 0)
            {
                // The first unsupported entry is stable even if the catalog order changes.
                var unsupported = unsupportedCandidates
                    .OrderBy(item => CandidateKey(item.Candidate), StringComparer.Ordinal)
                    .ThenBy(item => item.Candidate.RepoId, StringComparer.Ordinal)
                    .ThenBy(item => item.Candidate.RepoFilePath, StringComparer.Ordinal)
                    .ThenBy(item => item.Candidate.Revision, StringComparer.Ordinal)
                    .ThenBy(item => item.Reason, StringComparer.Ordinal)
                    .First();
                return new DoNotOffer(CandidateKey(unsupported.Candidate), unsupported.Reason);
            }

            string reason = matchingLocalCandidateFound
                ? $"No local candidate for '{capability}' fits the reported hardware."
                : $"No compatible local model candidate supports '{capability}'.";
            return new NoCompatibleCandidate(reason);
        }

        // Stable ranking: best FirstModelAdvisor fit tier first (GPU, split with RAM, CPU-only),
        // then smaller declared size, then candidate key, repository, file, and revision in ordinal order.
        var best = compatibleCandidates
            .OrderByDescending(item => FitRank(item.Fit))
            .ThenBy(item => item.Size)
            .ThenBy(item => CandidateKey(item.Candidate), StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.RepoId, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.RepoFilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Revision, StringComparer.Ordinal)
            .First();

        CandidateLicenseClass licenseClass = licenseClassByCandidateKey.TryGetValue(best.Candidate.Key, out CandidateLicenseClass suppliedClass)
            ? suppliedClass
            : CandidateLicenseClass.Unknown;
        string licenseBoundary = LicenseBoundaryFor(licenseClass);
        string reasonText = BuildReason(capability, best.Fit);
        string selectedKey = CandidateKey(best.Candidate);

        if (RequiresLicenseDecision(licenseClass))
        {
            return new LicenceRequiresDecision(
                selectedKey,
                licenseClass,
                "Review the model's license terms before deciding whether to acquire it.",
                licenseBoundary);
        }

        long availableBytes = Math.Max(0, freeStorageBudgetBytes);
        if (best.Size > availableBytes)
        {
            return new StorageBudgetExceeded(
                selectedKey,
                best.Size,
                availableBytes,
                best.Size - availableBytes);
        }

        // The catalog's declared artifact size is used as an estimate for both transfer and disk use.
        // No transfer or reservation is performed here.
        return new ProposeWithConsent(
            selectedKey,
            best.Candidate.RepoId,
            best.Candidate.Revision,
            best.Candidate.RepoFilePath,
            reasonText,
            best.Size,
            best.Size,
            licenseClass,
            licenseBoundary,
            best.Fit);
    }

    private static string? ValidateCandidate(ModelDiscoveryCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Key)
            || string.IsNullOrWhiteSpace(candidate.RepoId)
            || string.IsNullOrWhiteSpace(candidate.RepoFilePath))
            return "The candidate is missing a stable model identity or file name.";

        if (!string.Equals(candidate.Lifecycle?.Trim(), "Candidate", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Disposition?.Trim(), "active", StringComparison.OrdinalIgnoreCase))
            return "This candidate is not active in the catalog.";

        if (string.IsNullOrWhiteSpace(candidate.Revision))
            return "This candidate has no pinned revision, so its files could change after review.";

        if (RequiresRemoteCode(candidate))
            return "This candidate requests custom code from its repository, so it is not eligible for a proposal.";

        if (candidate.DeclaredSizeBytes is not > 0)
            return "The download size is missing, so the required storage cannot be estimated.";

        return null;
    }

    private static bool RequiresRemoteCode(ModelDiscoveryCandidate candidate)
    {
        IEnumerable<string> values = (candidate.DeclaredTags ?? new List<string>())
            .Append(candidate.DeclaredPipelineTag ?? string.Empty);
        foreach (string value in values)
        {
            string tag = value.Trim().ToLowerInvariant().Replace('_', '-');
            if (tag is "custom-code" or "custom-code-required" or "trust-remote-code" or "requires-remote-code" or "remote-code-required")
                return true;
        }

        return candidate.RepoFilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsCandidateCapability(ModelDiscoveryCandidate candidate, string capability)
    {
        IReadOnlyList<string> inferred = ModelCategoryClassifier.CapabilitiesOf(
            candidate.RepoFilePath,
            candidate.RepoId,
            candidate.RepoId);

        return capability switch
        {
            ModelCapabilityIds.Coding => inferred.Contains(ModelCategoryClassifier.Coding, StringComparer.Ordinal),
            ModelCapabilityIds.Vision => inferred.Contains(ModelCategoryClassifier.Vision, StringComparer.Ordinal),
            ModelCapabilityIds.Reasoning => inferred.Contains(ModelCategoryClassifier.Reasoning, StringComparer.Ordinal),
            ModelCapabilityIds.ImageEdit => HasDeclaredSignal(candidate, ModelCapabilityIds.ImageEdit, "image-edit", "image-editing", "image-to-image", "img2img"),
            ModelCapabilityIds.ImageGenerate => HasDeclaredSignal(candidate, ModelCapabilityIds.ImageGenerate, "image-generation", "text-to-image"),
            ModelCapabilityIds.VideoGenerate => HasDeclaredSignal(candidate, ModelCapabilityIds.VideoGenerate, "text-to-video", "video-generation"),
            _ => false
        };
    }

    private static bool SupportsInstalledCapability(InstalledCapabilityModel model, string capability)
    {
        return model.Capabilities is not null
            && model.Capabilities.Any(value => string.Equals(NormalizeCapability(value), capability, StringComparison.Ordinal));
    }

    private static bool HasDeclaredSignal(ModelDiscoveryCandidate candidate, params string[] acceptedSignals)
    {
        HashSet<string> accepted = acceptedSignals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> signals = (candidate.DeclaredTags ?? new List<string>())
            .Append(candidate.DeclaredPipelineTag ?? string.Empty);

        return signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal))
            .Select(signal => signal.Trim().ToLowerInvariant().Replace('_', '-'))
            .Any(signal => accepted.Contains(signal));
    }

    private static bool IsSupportedCapability(string capability) => capability is
        ModelCapabilityIds.Coding or
        ModelCapabilityIds.Vision or
        ModelCapabilityIds.Reasoning or
        ModelCapabilityIds.ImageEdit or
        ModelCapabilityIds.ImageGenerate or
        ModelCapabilityIds.VideoGenerate;

    private static string NormalizeCapability(string capability)
    {
        string value = capability.Trim().ToLowerInvariant();
        return value switch
        {
            "reasoning" => ModelCapabilityIds.Reasoning,
            "image-edit" or "image_edit" => ModelCapabilityIds.ImageEdit,
            "image-generate" or "image_generate" => ModelCapabilityIds.ImageGenerate,
            "video-generate" or "video_generate" => ModelCapabilityIds.VideoGenerate,
            _ => value
        };
    }

    private static bool RequiresLicenseDecision(CandidateLicenseClass licenseClass) => licenseClass is
        CandidateLicenseClass.Copyleft or
        CandidateLicenseClass.NonCommercial or
        CandidateLicenseClass.Unknown;

    private static string LicenseBoundaryFor(CandidateLicenseClass licenseClass) => licenseClass switch
    {
        CandidateLicenseClass.Permissive => "Permissive terms; check the model card and keep any required notices.",
        CandidateLicenseClass.AttributionRequired => "Attribution is required under the model's license terms.",
        CandidateLicenseClass.Copyleft => "Copyleft terms may impose obligations that depend on use or redistribution.",
        CandidateLicenseClass.NonCommercial => "Non-commercial terms may restrict commercial use.",
        _ => "The license terms are unknown and need review before use."
    };

    private static int FitRank(ModelSuitability fit) => fit switch
    {
        ModelSuitability.FitsOnGpu => 3,
        ModelSuitability.SplitWithRam => 2,
        ModelSuitability.CpuOnly => 1,
        _ => 0
    };

    private static string BuildReason(string capability, ModelSuitability fit)
    {
        string capabilityText = capability switch
        {
            ModelCapabilityIds.ImageEdit => "image editing",
            ModelCapabilityIds.ImageGenerate => "image generation",
            ModelCapabilityIds.VideoGenerate => "video generation",
            ModelCapabilityIds.Reasoning => "reasoning",
            ModelCapabilityIds.Vision => "image input",
            _ => "coding"
        };

        string fitText = fit switch
        {
            ModelSuitability.FitsOnGpu => "is estimated to fit in video memory",
            ModelSuitability.SplitWithRam => "is estimated to fit using some system memory",
            ModelSuitability.CpuOnly => "is estimated to fit in system memory and run on the CPU",
            _ => "has no supported hardware fit"
        };

        return $"This local model matches {capabilityText} and {fitText}.";
    }

    private static string CandidateKey(ModelDiscoveryCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Key)
            ? $"{candidate.RepoId}/{candidate.RepoFilePath}"
            : candidate.Key.Trim();
}

public sealed record InstalledCapabilityModel(
    string ModelId,
    IReadOnlyCollection<string> Capabilities,
    bool IsRecommended);

public enum CandidateLicenseClass
{
    Unknown,
    Permissive,
    AttributionRequired,
    Copyleft,
    NonCommercial
}

public abstract record CapabilityAcquisitionOutcome;

public sealed record ProposeWithConsent(
    string CandidateKey,
    string RepoId,
    string Revision,
    string RepoFilePath,
    string Reason,
    long ApproximateDownloadBytes,
    long ApproximateStorageBytes,
    CandidateLicenseClass LicenseClass,
    string LicenceBoundary,
    ModelSuitability HardwareFit) : CapabilityAcquisitionOutcome;

public sealed record AlreadySatisfied(
    string InstalledModelId,
    string Capability) : CapabilityAcquisitionOutcome;

public sealed record NoCompatibleCandidate(string Reason) : CapabilityAcquisitionOutcome;

public sealed record StorageBudgetExceeded(
    string CandidateKey,
    long RequiredBytes,
    long AvailableBytes,
    long BytesMissing) : CapabilityAcquisitionOutcome;

public sealed record LicenceRequiresDecision(
    string CandidateKey,
    CandidateLicenseClass LicenseClass,
    string Reason,
    string LicenceBoundary) : CapabilityAcquisitionOutcome;

public sealed record DoNotOffer(
    string CandidateKey,
    string Reason) : CapabilityAcquisitionOutcome;
