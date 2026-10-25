using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CapabilityAcquisitionPlannerTests
{
    [Fact]
    public void RecommendedInstalledCapabilityReturnsAlreadySatisfied()
    {
        var installed = new[]
        {
            new InstalledCapabilityModel("installed-model", new[] { ModelCapabilityIds.ImageEdit }, IsRecommended: true)
        };

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, Array.Empty<ModelDiscoveryCandidate>(), installedModels: installed);

        var satisfied = Assert.IsType<AlreadySatisfied>(outcome);
        Assert.Equal("installed-model", satisfied.InstalledModelId);
        Assert.Equal(ModelCapabilityIds.ImageEdit, satisfied.Capability);
    }

    [Fact]
    public void NonRecommendedInstalledCapabilityDoesNotSuppressProposal()
    {
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        var installed = new[]
        {
            new InstalledCapabilityModel("manual-model", new[] { ModelCapabilityIds.ImageEdit }, IsRecommended: false)
        };

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate }, installedModels: installed);

        Assert.IsType<ProposeWithConsent>(outcome);
    }

    [Fact]
    public void ProposalIncludesReasonSizeEstimateAndLicenseBoundary()
    {
        const long size = 4_000_000_000;
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image", size);

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate });

        var proposal = Assert.IsType<ProposeWithConsent>(outcome);
        Assert.Equal("edit-model", proposal.CandidateKey);
        Assert.Contains("image editing", proposal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(size, proposal.ApproximateDownloadBytes);
        Assert.Equal(size, proposal.ApproximateStorageBytes);
        Assert.False(string.IsNullOrWhiteSpace(proposal.LicenceBoundary));
        Assert.Equal(ModelSuitability.FitsOnGpu, proposal.HardwareFit);
    }

    [Fact]
    public void GenericVisionNameDoesNotClaimImageEditingWithoutAnEditSignal()
    {
        ModelDiscoveryCandidate candidate = Candidate("vision-model", "example/vision-model", ModelCapabilityIds.Vision, pipelineTag: null);

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate });

        Assert.IsType<NoCompatibleCandidate>(outcome);
    }

    [Fact]
    public void CandidateCatalogOrderDoesNotChangeTheSelectedProposal()
    {
        ModelDiscoveryCandidate cpuCandidate = Candidate("z-cpu", "example/code-coder-cpu", ModelCapabilityIds.Coding, "text-generation", size: 10);
        ModelDiscoveryCandidate gpuCandidate = Candidate("a-gpu", "example/code-coder-gpu", ModelCapabilityIds.Coding, "text-generation", size: 500);
        var fits = new Dictionary<string, ModelSuitability>(StringComparer.Ordinal)
        {
            [cpuCandidate.Key] = ModelSuitability.CpuOnly,
            [gpuCandidate.Key] = ModelSuitability.FitsOnGpu
        };

        var forward = Assert.IsType<ProposeWithConsent>(Plan(ModelCapabilityIds.Coding, new[] { cpuCandidate, gpuCandidate }, fits: fits));
        var reversed = Assert.IsType<ProposeWithConsent>(Plan(ModelCapabilityIds.Coding, new[] { gpuCandidate, cpuCandidate }, fits: fits));

        Assert.Equal("a-gpu", forward.CandidateKey);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void CloudInferenceCandidateIsNeverNamedOrProposed()
    {
        ModelDiscoveryCandidate remoteCandidate = Candidate("remote-edit", "cloud-inference/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        remoteCandidate.Provider = "cloud-inference";

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { remoteCandidate });

        Assert.IsType<NoCompatibleCandidate>(outcome);
        Assert.False(outcome.ToString()!.Contains("cloud-inference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StorageOutcomeReportsExactMissingBytes()
    {
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image", size: 500);

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate }, freeBytes: 350);

        var exceeded = Assert.IsType<StorageBudgetExceeded>(outcome);
        Assert.Equal(500, exceeded.RequiredBytes);
        Assert.Equal(350, exceeded.AvailableBytes);
        Assert.Equal(150, exceeded.BytesMissing);
    }

    [Fact]
    public void RestrictedLicenseReturnsDecisionOutcome()
    {
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        var licenses = new Dictionary<string, CandidateLicenseClass>(StringComparer.Ordinal)
        {
            [candidate.Key] = CandidateLicenseClass.NonCommercial
        };

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate }, licenses: licenses);

        var decision = Assert.IsType<LicenceRequiresDecision>(outcome);
        Assert.Equal(candidate.Key, decision.CandidateKey);
        Assert.Equal(CandidateLicenseClass.NonCommercial, decision.LicenseClass);
        Assert.Contains("commercial", decision.LicenceBoundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingRevisionIsMarkedDoNotOffer()
    {
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        candidate.Revision = string.Empty;

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate });

        var rejected = Assert.IsType<DoNotOffer>(outcome);
        Assert.Contains("pinned revision", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteCustomCodeIsMarkedDoNotOffer()
    {
        ModelDiscoveryCandidate candidate = Candidate("edit-model", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        candidate.DeclaredTags.Add("trust_remote_code");

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { candidate });

        var rejected = Assert.IsType<DoNotOffer>(outcome);
        Assert.Contains("custom code", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateUnsupportedEntriesHaveStableRejectionReason()
    {
        ModelDiscoveryCandidate missingRevision = Candidate("duplicate", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        missingRevision.Revision = string.Empty;
        ModelDiscoveryCandidate missingSize = Candidate("duplicate", "example/image-edit", ModelCapabilityIds.ImageEdit, "image-to-image");
        missingSize.DeclaredSizeBytes = null;

        var forward = Assert.IsType<DoNotOffer>(Plan(ModelCapabilityIds.ImageEdit, new[] { missingRevision, missingSize }));
        var reversed = Assert.IsType<DoNotOffer>(Plan(ModelCapabilityIds.ImageEdit, new[] { missingSize, missingRevision }));

        Assert.Equal(forward, reversed);
        Assert.Contains("pinned revision", forward.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsafeCandidateDoesNotBlockACompatibleLocalCandidate()
    {
        ModelDiscoveryCandidate unsupported = Candidate("a-unsafe", "example/image-edit-unsafe", ModelCapabilityIds.ImageEdit, "image-to-image");
        unsupported.Revision = string.Empty;
        ModelDiscoveryCandidate supported = Candidate("b-safe", "example/image-edit-safe", ModelCapabilityIds.ImageEdit, "image-to-image");

        CapabilityAcquisitionOutcome outcome = Plan(ModelCapabilityIds.ImageEdit, new[] { unsupported, supported });

        Assert.Equal("b-safe", Assert.IsType<ProposeWithConsent>(outcome).CandidateKey);
    }

    private static CapabilityAcquisitionOutcome Plan(
        string capability,
        IEnumerable<ModelDiscoveryCandidate> candidates,
        IReadOnlyCollection<InstalledCapabilityModel>? installedModels = null,
        long freeBytes = long.MaxValue,
        IReadOnlyDictionary<string, ModelSuitability>? fits = null,
        IReadOnlyDictionary<string, CandidateLicenseClass>? licenses = null)
    {
        ModelDiscoveryCandidate[] candidateArray = candidates.ToArray();
        var fitMap = fits ?? candidateArray
            .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, _ => ModelSuitability.FitsOnGpu, StringComparer.Ordinal);
        var licenseMap = licenses ?? candidateArray
            .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, _ => CandidateLicenseClass.Permissive, StringComparer.Ordinal);

        return CapabilityAcquisitionPlanner.Plan(
            capability,
            new ModelDiscoveryCatalog { Candidates = candidateArray.ToList() },
            installedModels ?? Array.Empty<InstalledCapabilityModel>(),
            freeBytes,
            fitMap,
            licenseMap);
    }

    private static ModelDiscoveryCandidate Candidate(
        string key,
        string repoId,
        string capability,
        string? pipelineTag,
        long size = 250)
    {
        string fileName = capability == ModelCapabilityIds.Coding ? "code-model.gguf" : "image-model.safetensors";
        return new ModelDiscoveryCandidate
        {
            Key = key,
            Provider = "huggingface",
            RepoId = repoId,
            Revision = "0123456789abcdef0123456789abcdef01234567",
            RepoFilePath = fileName,
            PinnedUrl = "https://example.com/model",
            Lifecycle = "Candidate",
            Disposition = "active",
            DeclaredPipelineTag = pipelineTag,
            DeclaredSizeBytes = size
        };
    }
}
