using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadSecurityTests
{
    private static ModelDownloadRequest Valid(string fileName = "model.Q4_K_M.gguf") => new()
    {
        RepoId = "owner/repository",
        FileName = fileName,
        DownloadUrl = "https://huggingface.co/owner/repository/resolve/main/models/" + fileName,
        SizeBytes = 1024
    };

    [Fact]
    public void Valid_huggingface_gguf_is_accepted()
    {
        ModelDownloadRequest validated = ModelDownloadService.ValidateDownloadRequest(Valid());
        Assert.Equal("owner/repository", validated.RepoId);
        Assert.Equal("model.Q4_K_M.gguf", validated.FileName);
        Assert.StartsWith("https://huggingface.co/owner/repository/resolve/", validated.DownloadUrl);
    }

    [Fact]
    public void Expected_sha256_is_validated_and_normalized()
    {
        ModelDownloadRequest request = Valid();
        request.ExpectedSha256 = new string('A', 64);

        ModelDownloadRequest validated = ModelDownloadService.ValidateDownloadRequest(request);

        Assert.Equal(new string('a', 64), validated.ExpectedSha256);
        request.ExpectedSha256 = "not-a-digest";
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Fact]
    public void Valid_huggingface_safetensors_checkpoint_is_accepted()
    {
        // Safetensors downloads must name their media workflow; this file is a Z-Image checkpoint.
        ModelDownloadRequest request = Valid("z_image_turbo_bf16.safetensors");
        request.MediaCheckpointType = "image";
        ModelDownloadRequest validated = ModelDownloadService.ValidateDownloadRequest(request);
        Assert.EndsWith(".safetensors", validated.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("image", validated.MediaCheckpointType);
    }

    [Fact]
    public void Immutable_revision_must_match_and_is_normalized_on_a_valid_resolve_url()
    {
        string revision = new string('A', 40);
        ModelDownloadRequest request = Valid();
        request.Revision = revision;
        request.DownloadUrl = "https://huggingface.co/owner/repository/resolve/" + revision + "/models/" + request.FileName;

        ModelDownloadRequest validated = ModelDownloadService.ValidateDownloadRequest(request);

        Assert.Equal(revision.ToLowerInvariant(), validated.Revision);
        Assert.Contains("/resolve/" + revision + "/", validated.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declared_immutable_revision_mismatch_with_resolve_url_is_rejected()
    {
        ModelDownloadRequest request = Valid();
        request.Revision = new string('a', 40);
        request.DownloadUrl = "https://huggingface.co/owner/repository/resolve/" + new string('b', 40)
            + "/models/" + request.FileName;

        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Fact]
    public void License_evidence_is_normalized_and_requires_a_recorded_affirmative_review()
    {
        DateTime observed = DateTime.UtcNow.AddSeconds(-2);
        DateTime acknowledged = DateTime.UtcNow.AddSeconds(-1);
        ModelDownloadRequest request = Valid();
        request.LicenseEvidence = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "custom-research-only",
            MetadataObservedUtc = observed,
            ReviewRequired = true,
            ReviewAcknowledged = true,
            ReviewAcknowledgedUtc = acknowledged
        };

        ModelDownloadRequest validated = ModelDownloadService.ValidateDownloadRequest(request);

        Assert.NotSame(request.LicenseEvidence, validated.LicenseEvidence);
        Assert.Equal(observed, validated.LicenseEvidence!.MetadataObservedUtc);
        Assert.Equal(acknowledged, validated.LicenseEvidence.ReviewAcknowledgedUtc);
    }

    [Fact]
    public void License_evidence_rejects_missing_acknowledgement_and_policy_mismatch()
    {
        ModelDownloadRequest missingAcknowledgement = Valid();
        missingAcknowledgement.LicenseEvidence = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "custom-research-only",
            MetadataObservedUtc = DateTime.UtcNow,
            ReviewRequired = true
        };
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(missingAcknowledgement));

        ModelDownloadRequest falsePolicy = Valid();
        falsePolicy.LicenseEvidence = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = DateTime.UtcNow,
            ReviewRequired = true,
            ReviewAcknowledged = true,
            ReviewAcknowledgedUtc = DateTime.UtcNow
        };
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(falsePolicy));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("abc123")]
    [InlineData(" ")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Invalid_declared_revision_is_rejected(string revision)
    {
        ModelDownloadRequest request = Valid();
        request.Revision = revision;
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Theory]
    [InlineData("../escape.gguf")]
    [InlineData("..\\escape.gguf")]
    [InlineData("folder/model.gguf")]
    [InlineData("folder\\model.gguf")]
    [InlineData("model.exe")]
    [InlineData("model.gguf:stream")]
    public void Unsafe_destination_file_names_are_rejected(string fileName)
    {
        ModelDownloadRequest request = Valid();
        request.FileName = fileName;
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Theory]
    [InlineData("http://huggingface.co/owner/repository/resolve/main/model.Q4_K_M.gguf")]
    [InlineData("https://127.0.0.1/owner/repository/resolve/main/model.Q4_K_M.gguf")]
    [InlineData("https://localhost/owner/repository/resolve/main/model.Q4_K_M.gguf")]
    [InlineData("https://example.com/owner/repository/resolve/main/model.Q4_K_M.gguf")]
    [InlineData("https://user@huggingface.co/owner/repository/resolve/main/model.Q4_K_M.gguf")]
    public void Non_huggingface_or_non_https_sources_are_rejected(string url)
    {
        ModelDownloadRequest request = Valid();
        request.DownloadUrl = url;
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Fact]
    public void Declared_repo_must_match_the_resolve_path()
    {
        ModelDownloadRequest request = Valid();
        request.DownloadUrl = "https://huggingface.co/other/repository/resolve/main/model.Q4_K_M.gguf";
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Fact]
    public void Declared_file_name_must_match_the_resolved_source_file()
    {
        ModelDownloadRequest request = Valid();
        request.DownloadUrl = "https://huggingface.co/owner/repository/resolve/main/other.gguf";
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Theory]
    [InlineData("owner/../repository")]
    [InlineData("owner/repository/extra")]
    [InlineData("owner repo/repository")]
    [InlineData("/repository")]
    public void Unsafe_repo_ids_are_rejected(string repoId)
    {
        ModelDownloadRequest request = Valid();
        request.RepoId = repoId;
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Fact]
    public void Encoded_traversal_in_source_url_is_rejected()
    {
        ModelDownloadRequest request = Valid();
        request.DownloadUrl = "https://huggingface.co/owner/repository/resolve/main/%2e%2e/model.Q4_K_M.gguf";
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(751619276801L)] // 700 GiB + 1 byte
    public void Impossible_size_metadata_is_rejected(long size)
    {
        ModelDownloadRequest request = Valid();
        request.SizeBytes = size;
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(request));
    }
}
