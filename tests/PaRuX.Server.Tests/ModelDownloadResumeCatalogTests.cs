using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadResumeCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-model-resume-catalog-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadResumeCatalogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public void Preserved_partial_reappears_after_catalog_reopen_and_resolves_to_stable_source_request()
    {
        const string fileName = "model-Q4_K_M.gguf";
        var request = new ModelDownloadRequest
        {
            RepoId = "owner/model",
            FileName = fileName,
            DownloadUrl = "https://huggingface.co/owner/model/resolve/main/model-Q4_K_M.gguf",
            SizeBytes = 10_000
        };

        var firstProcess = new ModelDownloadResumeCatalog(_directory);
        firstProcess.Remember(request);
        File.WriteAllBytes(Path.Combine(_directory, fileName + ".partial"), new byte[4096]);

        // New instance models the desktop/Core process coming back with no in-memory server jobs.
        var afterRestart = new ModelDownloadResumeCatalog(_directory);
        ModelDownloadStatus resumable = Assert.Single(afterRestart.ListResumable(Array.Empty<ModelDownloadStatus>()));
        Assert.StartsWith(ModelDownloadResumeCatalog.SyntheticIdPrefix, resumable.Id, StringComparison.Ordinal);
        Assert.Equal("cancelled", resumable.Status);
        Assert.Equal(fileName, resumable.FileName);
        Assert.Equal(4096, resumable.BytesDownloaded);
        Assert.Equal(10_000, resumable.TotalBytes);
        Assert.Contains("previous PaRuX Core session", resumable.Error, StringComparison.OrdinalIgnoreCase);

        Assert.True(afterRestart.TryResolve(resumable.Id, out ModelDownloadRequest restored));
        Assert.Equal(request.RepoId, restored.RepoId);
        Assert.Equal(request.FileName, restored.FileName);
        Assert.Equal(request.DownloadUrl, restored.DownloadUrl);
        Assert.Equal(request.SizeBytes, restored.SizeBytes);
        Assert.Null(restored.Revision); // Legacy catalog rows remain explicitly revision-unknown.
        Assert.Null(restored.ResumeFromDownloadId);
    }

    [Fact]
    public void Immutable_revision_survives_catalog_reopen_and_legacy_json_without_revision_stays_resumable()
    {
        const string fileName = "pinned-model.gguf";
        string revision = new string('a', 40);
        var pinned = new ModelDownloadRequest
        {
            RepoId = "owner/model",
            Revision = revision,
            FileName = fileName,
            DownloadUrl = $"https://huggingface.co/owner/model/resolve/{revision}/{fileName}",
            SizeBytes = 2048,
            LicenseEvidence = new ModelDownloadLicenseEvidence
            {
                MetadataStatus = ModelLicenseMetadataStatus.Declared,
                PublisherDeclaredLicense = "custom-research-only",
                MetadataObservedUtc = DateTime.UtcNow.AddMinutes(-1),
                ReviewRequired = true,
                ReviewAcknowledged = true,
                ReviewAcknowledgedUtc = DateTime.UtcNow
            }
        };
        var firstProcess = new ModelDownloadResumeCatalog(_directory);
        firstProcess.Remember(pinned);
        File.WriteAllBytes(Path.Combine(_directory, fileName + ".partial"), new byte[512]);

        var afterRestart = new ModelDownloadResumeCatalog(_directory);
        ModelDownloadStatus status = Assert.Single(afterRestart.ListResumable(Array.Empty<ModelDownloadStatus>()));
        Assert.Equal(revision, status.Revision);
        Assert.True(afterRestart.TryResolve(status.Id, out ModelDownloadRequest restored));
        Assert.Equal(revision, restored.Revision);
        Assert.Equal(pinned.DownloadUrl, restored.DownloadUrl);
        Assert.NotNull(restored.LicenseEvidence);
        Assert.Equal("custom-research-only", restored.LicenseEvidence!.PublisherDeclaredLicense);
        Assert.True(restored.LicenseEvidence.ReviewRequired);
        Assert.True(restored.LicenseEvidence.ReviewAcknowledged);
        Assert.Equal(pinned.LicenseEvidence.ReviewAcknowledgedUtc, restored.LicenseEvidence.ReviewAcknowledgedUtc);

        Directory.Delete(_directory, recursive: true);
        Directory.CreateDirectory(_directory);
        string legacyId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_directory, ".download-resume.json"),
            "[{\"id\":\"" + legacyId + "\",\"repoId\":\"owner/legacy\",\"fileName\":\"legacy.gguf\","
            + "\"downloadUrl\":\"https://huggingface.co/owner/legacy/resolve/main/legacy.gguf\",\"sizeBytes\":100}]");
        File.WriteAllBytes(Path.Combine(_directory, "legacy.gguf.partial"), new byte[25]);

        var legacyCatalog = new ModelDownloadResumeCatalog(_directory);
        ModelDownloadStatus legacyStatus = Assert.Single(legacyCatalog.ListResumable(Array.Empty<ModelDownloadStatus>()));
        Assert.Null(legacyStatus.Revision);
        Assert.True(legacyCatalog.TryResolve(legacyStatus.Id, out ModelDownloadRequest legacyRequest));
        Assert.Null(legacyRequest.Revision);
        Assert.Null(legacyRequest.LicenseEvidence);
        Assert.Contains("/resolve/main/", legacyRequest.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_server_job_suppresses_duplicate_synthetic_row_for_the_same_destination()
    {
        const string fileName = "same.gguf";
        var catalog = new ModelDownloadResumeCatalog(_directory);
        catalog.Remember(new ModelDownloadRequest
        {
            RepoId = "owner/same",
            FileName = fileName,
            DownloadUrl = "https://huggingface.co/owner/same/resolve/main/same.gguf",
            SizeBytes = 100
        });
        File.WriteAllBytes(Path.Combine(_directory, fileName + ".partial"), new byte[25]);

        var live = new[]
        {
            new ModelDownloadStatus
            {
                Id = "server-job",
                FileName = fileName,
                Status = "downloading",
                BytesDownloaded = 25,
                TotalBytes = 100,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            }
        };

        Assert.Empty(catalog.ListResumable(live));
    }

    [Fact]
    public void Completed_or_deleted_partial_is_pruned_and_cannot_be_resolved_later()
    {
        const string fileName = "done.gguf";
        var catalog = new ModelDownloadResumeCatalog(_directory);
        catalog.Remember(new ModelDownloadRequest
        {
            RepoId = "owner/done",
            FileName = fileName,
            DownloadUrl = "https://huggingface.co/owner/done/resolve/main/done.gguf",
            SizeBytes = 100
        });
        string partial = Path.Combine(_directory, fileName + ".partial");
        File.WriteAllBytes(partial, new byte[50]);

        ModelDownloadStatus synthetic = Assert.Single(catalog.ListResumable(Array.Empty<ModelDownloadStatus>()));
        File.Delete(partial);
        File.WriteAllBytes(Path.Combine(_directory, fileName), new byte[100]);

        Assert.Empty(catalog.ListResumable(Array.Empty<ModelDownloadStatus>()));
        Assert.False(catalog.TryResolve(synthetic.Id, out _));
    }

    [Theory]
    [InlineData("../escape.gguf")]
    [InlineData("subdir/model.gguf")]
    [InlineData("not-a-model.bin")]
    public void Unsafe_or_non_gguf_sources_are_not_journaled(string fileName)
    {
        var catalog = new ModelDownloadResumeCatalog(_directory);
        catalog.Remember(new ModelDownloadRequest
        {
            RepoId = "owner/model",
            FileName = fileName,
            DownloadUrl = "https://huggingface.co/owner/model/resolve/main/model.gguf",
            SizeBytes = 100
        });

        Assert.False(File.Exists(Path.Combine(_directory, ".download-resume.json")));
    }
}
