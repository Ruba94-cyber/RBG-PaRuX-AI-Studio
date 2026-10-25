using PaRuX.Contracts;
using PaRuX.Services;
using UiImportRoute = PaRuX.Services.ExternalModelImportRoute;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExternalModelImportPolicyTests
{
    [Fact]
    public void Already_imported_model_never_routes_to_link_or_copy()
    {
        ExternalModelCandidate candidate = Candidate(alreadyImported: true, sameVolume: true);

        Assert.Equal(UiImportRoute.AlreadyImported,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: true, hardLinkFailed: true));
    }

    [Fact]
    public void Same_volume_model_uses_a_hard_link_without_copy_consent()
    {
        ExternalModelCandidate candidate = Candidate(alreadyImported: false, sameVolume: true);

        Assert.Equal(UiImportRoute.HardLink,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: false));
    }

    [Fact]
    public void Different_volume_model_requires_consent_before_copy()
    {
        ExternalModelCandidate candidate = Candidate(alreadyImported: false, sameVolume: false);

        Assert.Equal(UiImportRoute.CopyConsentRequired,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: false));
        Assert.Equal(UiImportRoute.Copy,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: true));
    }

    [Fact]
    public void Failed_hard_link_requires_new_consent_before_copy_fallback()
    {
        ExternalModelCandidate candidate = Candidate(alreadyImported: false, sameVolume: true);

        Assert.Equal(UiImportRoute.CopyConsentRequired,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: false, hardLinkFailed: true));
        Assert.Equal(UiImportRoute.Copy,
            ExternalModelImportPolicy.Decide(candidate, copyConsentAccepted: true, hardLinkFailed: true));
    }

    private static ExternalModelCandidate Candidate(bool alreadyImported, bool sameVolume) => new()
    {
        AlreadyImported = alreadyImported,
        SameVolumeAsLibrary = sameVolume
    };
}
