using PaRuX.Services;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelLicenseConsentPolicyTests
{
    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("0BSD")]
    [InlineData("ISC")]
    [InlineData("BSL-1.0")]
    [InlineData("Unlicense")]
    [InlineData("Zlib")]
    [InlineData(" mit ")]
    public void Exact_allow_list_license_does_not_require_extra_review(string license)
    {
        var decision = ModelLicenseConsentPolicy.Evaluate(license, metadataRetrieved: true);

        Assert.False(decision.RequiresReview);
        Assert.True(decision.MetadataRetrieved);
        Assert.Equal(ModelLicenseMetadataStatus.Declared, decision.LicenseMetadataStatus);
        Assert.Equal(license, decision.PublisherDeclaredLicense);
        Assert.Equal(DateTimeKind.Utc, decision.MetadataObservedUtc.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("llama3")]
    [InlineData("MIT OR Apache-2.0")]
    [InlineData("GPL-3.0-only")]
    public void Missing_unknown_and_non_allow_list_licenses_require_review(string? license)
    {
        var decision = ModelLicenseConsentPolicy.Evaluate(license, metadataRetrieved: true);

        Assert.True(decision.RequiresReview);
        Assert.True(decision.MetadataRetrieved);
        Assert.Equal(license is null or "" ? ModelLicenseMetadataStatus.NotDeclared : ModelLicenseMetadataStatus.Declared,
            decision.LicenseMetadataStatus);
        Assert.Contains("review", decision.MetadataStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Details_lookup_failure_requires_review_even_if_no_license_value_is_available()
    {
        var decision = ModelLicenseConsentPolicy.Evaluate("MIT", metadataRetrieved: false);

        Assert.True(decision.RequiresReview);
        Assert.False(decision.MetadataRetrieved);
        Assert.Equal(ModelLicenseMetadataStatus.Unavailable, decision.LicenseMetadataStatus);
        Assert.Null(decision.PublisherDeclaredLicense);
        Assert.Contains("unavailable", decision.MetadataStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Candidate_consent_preserves_the_catalog_metadata_observation_time()
    {
        DateTime observedUtc = new(2026, 9, 24, 12, 30, 0, DateTimeKind.Utc);

        var decision = ModelLicenseConsentPolicy.Evaluate(
            "custom-research-only", metadataRetrieved: true, metadataObservedUtc: observedUtc);

        Assert.True(decision.RequiresReview);
        Assert.Equal(observedUtc, decision.MetadataObservedUtc);
    }

    [Fact]
    public void License_metadata_observation_time_must_be_utc()
    {
        DateTime unspecified = new(2026, 9, 24, 12, 30, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() => ModelLicenseConsentPolicy.Evaluate(
            "MIT", metadataRetrieved: true, metadataObservedUtc: unspecified));
    }

    [Fact]
    public void Model_card_link_uses_fixed_hugging_face_host_and_escapes_segments()
    {
        Uri? uri = ModelLicenseConsentPolicy.GetModelCardUri("owner/model name");

        Assert.NotNull(uri);
        Assert.Equal("https", uri!.Scheme);
        Assert.Equal("huggingface.co", uri.Host);
        Assert.Equal("/owner/model%20name", uri.AbsolutePath);
    }

    [Fact]
    public void Model_card_link_is_pinned_to_the_validated_immutable_revision()
    {
        string revision = new string('a', 40);

        Uri? uri = ModelLicenseConsentPolicy.GetModelCardUri("owner/model", revision);

        Assert.NotNull(uri);
        Assert.Equal("https://huggingface.co/owner/model/tree/" + revision, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://evil.example/owner/model")]
    [InlineData("owner/model/extra")]
    [InlineData("../model")]
    public void Invalid_repository_ids_do_not_create_links(string repoId)
    {
        Assert.Null(ModelLicenseConsentPolicy.GetModelCardUri(repoId));
    }
}
