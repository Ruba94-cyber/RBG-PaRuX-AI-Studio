using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadProvenanceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-provenance-download-" + Guid.NewGuid().ToString("N"));
    private readonly string _fileName = "model.gguf";

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Successful_download_persists_revision_license_consent_and_matching_provider_hash()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 4, 8, 15, 16, 23, 42 };
        string expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        DateTime metadataObserved = DateTime.UtcNow.AddSeconds(-2);
        DateTime acknowledged = DateTime.UtcNow.AddSeconds(-1);
        var request = Request(new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "custom-research-only",
            MetadataObservedUtc = metadataObserved,
            ReviewRequired = true,
            ReviewAcknowledged = true,
            ReviewAcknowledgedUtc = acknowledged
        });
        var handler = new FakeHandler(expectedHash, payload);
        var service = CreateService(handler);

        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("completed", completed.Status);
        var restartedService = CreateService(new FakeHandler(null, payload));
        ManagedModelInfo installed = Assert.Single(restartedService.ListManagedModels());
        Assert.Equal("org/model", installed.RepoId);
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(installed.Provenance);
        Assert.Equal(request.Revision, provenance.Revision);
        Assert.Equal(_fileName, provenance.RepoFilePath);
        Assert.Equal(request.DownloadUrl, provenance.PinnedSourceUrl);
        Assert.Equal("https://huggingface.co/org/model/tree/" + request.Revision, provenance.ModelCardUrl);
        Assert.Equal(ModelLicenseMetadataStatus.Declared, provenance.LicenseMetadataStatus);
        Assert.Equal("custom-research-only", provenance.PublisherDeclaredLicense);
        Assert.Equal(metadataObserved, provenance.MetadataObservedUtc);
        Assert.True(provenance.ReviewRequired);
        Assert.True(provenance.ReviewAcknowledged);
        Assert.Equal(acknowledged, provenance.ReviewAcknowledgedUtc);
        Assert.Equal(expectedHash, provenance.ExpectedSha256);
        Assert.Equal(expectedHash, provenance.ComputedSha256);
        Assert.Equal("verified", provenance.IntegrityStatus);
        Assert.Equal(payload.Length, provenance.SizeBytes);
    }

    [Fact]
    public async Task Nested_sources_with_the_same_flat_name_keep_distinct_revision_pinned_identities()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 1 };
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var first = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        first.DownloadUrl = $"https://huggingface.co/org/model/resolve/{first.Revision}/weights/first/{_fileName}?token=secret#fragment";
        var firstService = CreateService(new FakeHandler(payloadHash, payload));
        ModelDownloadStatus firstJob = await firstService.CreateDownloadAsync(first, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(firstService, firstJob.Id)).Status);
        ModelDownloadProvenance firstEntry = Assert.IsType<ModelDownloadProvenance>(Assert.Single(firstService.ListManagedModels()).Provenance);
        Assert.Equal("weights/first/model.gguf", firstEntry.RepoFilePath);
        Assert.Equal($"https://huggingface.co/org/model/resolve/{first.Revision}/weights/first/model.gguf", firstEntry.PinnedSourceUrl);
        string firstManifest = await File.ReadAllTextAsync(Path.Combine(_directory, ".model-provenance.json"));
        Assert.DoesNotContain("secret", firstManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment", firstManifest, StringComparison.Ordinal);

        var second = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        second.DownloadUrl = $"https://huggingface.co/org/model/resolve/{second.Revision}/weights/second/{_fileName}";
        var secondService = CreateService(new FakeHandler(payloadHash, payload));
        ModelDownloadStatus refused = await secondService.CreateDownloadAsync(second, CancellationToken.None);
        Assert.Equal("failed", refused.Status);
        Assert.Equal("permanent", refused.FailureClass);
        Assert.Contains("ReplaceExisting=true", refused.Remediation, StringComparison.Ordinal);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_directory, _fileName)));
        ModelDownloadProvenance unchanged = Assert.IsType<ModelDownloadProvenance>(Assert.Single(secondService.ListManagedModels()).Provenance);
        Assert.Equal(firstEntry.PinnedSourceUrl, unchanged.PinnedSourceUrl);

        second.ReplaceExisting = true;
        ModelDownloadStatus secondJob = await secondService.CreateDownloadAsync(second, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(secondService, secondJob.Id)).Status);
        ModelDownloadProvenance secondEntry = Assert.IsType<ModelDownloadProvenance>(Assert.Single(secondService.ListManagedModels()).Provenance);
        Assert.Equal("weights/second/model.gguf", secondEntry.RepoFilePath);
        Assert.NotEqual(firstEntry.PinnedSourceUrl, secondEntry.PinnedSourceUrl);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_directory, _fileName)));
    }

    [Fact]
    public async Task Different_nested_path_and_pinned_url_are_not_answered_in_place()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 5, 4, 3, 2, 1 };
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        int getCount = 0;
        var service = CreateService(new FakeHandler(payloadHash, payload, () => getCount++));
        var first = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        first.DownloadUrl = $"https://huggingface.co/org/model/resolve/{first.Revision}/weights/first/{_fileName}";
        ModelDownloadStatus firstJob = await service.CreateDownloadAsync(first, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, firstJob.Id)).Status);
        Assert.Equal(1, getCount);

        var differentSource = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        differentSource.ReplaceExisting = true;
        differentSource.DownloadUrl = $"https://huggingface.co/org/model/resolve/{differentSource.Revision}/weights/second/{_fileName}";
        ModelDownloadStatus secondJob = await service.CreateDownloadAsync(differentSource, CancellationToken.None);

        Assert.Equal("completed", (await WaitForTerminalAsync(service, secondJob.Id)).Status);
        Assert.Equal(2, getCount);
        ModelDownloadProvenance updated = Assert.IsType<ModelDownloadProvenance>(Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Equal("weights/second/model.gguf", updated.RepoFilePath);
        Assert.Equal($"https://huggingface.co/org/model/resolve/{differentSource.Revision}/weights/second/model.gguf", updated.PinnedSourceUrl);
    }

    [Fact]
    public async Task Identical_pinned_source_identity_is_answered_in_place()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 8, 6, 7, 5, 3, 0, 9 };
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        int getCount = 0;
        var service = CreateService(new FakeHandler(payloadHash, payload, () => getCount++));
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        request.DownloadUrl = $"https://huggingface.co/org/model/resolve/{request.Revision}/weights/exact/{_fileName}";
        ModelDownloadStatus firstJob = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, firstJob.Id)).Status);
        Assert.Equal(1, getCount);

        request.ReplaceExisting = true;
        ModelDownloadStatus repeated = await service.CreateDownloadAsync(request, CancellationToken.None);

        Assert.Equal("completed", repeated.Status);
        Assert.Equal(1, getCount);
    }

    [Fact]
    public async Task Missing_provenance_does_not_answer_in_place()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        Directory.CreateDirectory(_directory);
        byte[] payload = { 2, 7, 1, 8 };
        await File.WriteAllBytesAsync(Path.Combine(_directory, _fileName), payload);
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        int getCount = 0;
        var service = CreateService(new FakeHandler(payloadHash, payload, () => getCount++));
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        request.ReplaceExisting = true;

        ModelDownloadStatus job = await service.CreateDownloadAsync(request, CancellationToken.None);

        Assert.Equal("completed", (await WaitForTerminalAsync(service, job.Id)).Status);
        Assert.Equal(1, getCount);
    }

    [Fact]
    public async Task Unknown_source_identity_is_not_answered_in_place()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 1, 6, 1, 8, 0, 3, 3, 9 };
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        int getCount = 0;
        var service = CreateService(new FakeHandler(payloadHash, payload, () => getCount++));
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        request.DownloadUrl = $"https://huggingface.co/org/model/resolve/{request.Revision}/weights%2F{_fileName}";
        ModelDownloadStatus firstJob = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, firstJob.Id)).Status);
        Assert.Equal(1, getCount);
        ModelDownloadProvenance unknown = Assert.IsType<ModelDownloadProvenance>(Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Null(unknown.RepoFilePath);
        Assert.Null(unknown.PinnedSourceUrl);

        request.ReplaceExisting = true;
        ModelDownloadStatus repeated = await service.CreateDownloadAsync(request, CancellationToken.None);

        Assert.Equal("completed", (await WaitForTerminalAsync(service, repeated.Id)).Status);
        Assert.Equal(2, getCount);
    }

    [Fact]
    public async Task Hugging_Face_candidate_download_refuses_to_replace_a_same_name_file_from_another_repository()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] oldPayload = { 1 };
        var oldRequest = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        oldRequest.RepoId = "org/older-model";
        oldRequest.DownloadUrl = $"https://huggingface.co/{oldRequest.RepoId}/resolve/{oldRequest.Revision}/{_fileName}";
        var oldService = CreateService(new FakeHandler(null, oldPayload));
        ModelDownloadStatus oldJob = await oldService.CreateDownloadAsync(oldRequest, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(oldService, oldJob.Id)).Status);

        byte[] newPayload = { 2, 3 };
        DateTime observedUtc = DateTime.UtcNow.AddSeconds(-1);
        string repoId = "org/selected-model";
        string revision = new('c', 40);
        string repoFilePath = "weights/" + _fileName;
        var candidate = new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = repoFilePath,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            DeclaredLicense = "MIT",
            DeclaredSizeBytes = newPayload.Length,
            FirstSeenUtc = observedUtc,
            LastSeenUtc = observedUtc
        };
        var licenseEvidence = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseReviewRules.GetMetadataStatus(candidate.DeclaredLicense, metadataRetrieved: true),
            PublisherDeclaredLicense = candidate.DeclaredLicense,
            MetadataObservedUtc = observedUtc,
            ReviewRequired = ModelLicenseReviewRules.RequiresReview(candidate.DeclaredLicense, metadataRetrieved: true)
        };
        var candidateService = CreateService(new FakeHandler(null, newPayload));

        ModelDownloadStatus refused = await candidateService.CreateCandidateDownloadAsync(
            candidate, licenseEvidence, observedUtc, CancellationToken.None);

        Assert.Equal("failed", refused.Status);
        Assert.Equal("permanent", refused.FailureClass);
        Assert.Contains("different source", refused.Error, StringComparison.Ordinal);
        Assert.Equal(oldPayload, await File.ReadAllBytesAsync(Path.Combine(_directory, _fileName)));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.partial*"));
        ModelDownloadProvenance unchanged = Assert.IsType<ModelDownloadProvenance>(
            Assert.Single(candidateService.ListManagedModels()).Provenance);
        Assert.Equal("org/older-model", unchanged.RepoId);
    }

    [Fact]
    public async Task Hugging_Face_candidate_update_of_the_same_repository_file_replaces_the_managed_copy()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        const string repoId = "org/selected-model";
        const string repoFilePath = "weights/model.gguf";
        byte[] oldPayload = { 1 };
        var oldRequest = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        oldRequest.RepoId = repoId;
        oldRequest.DownloadUrl = $"https://huggingface.co/{repoId}/resolve/{oldRequest.Revision}/{repoFilePath}";
        var oldService = CreateService(new FakeHandler(null, oldPayload));
        ModelDownloadStatus oldJob = await oldService.CreateDownloadAsync(oldRequest, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(oldService, oldJob.Id)).Status);

        byte[] newPayload = { 2, 3 };
        DateTime observedUtc = DateTime.UtcNow.AddSeconds(-1);
        string revision = new('c', 40);
        var candidate = new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, repoFilePath),
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = repoFilePath,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath),
            DeclaredLicense = "MIT",
            DeclaredSizeBytes = newPayload.Length,
            FirstSeenUtc = observedUtc,
            LastSeenUtc = observedUtc
        };
        var licenseEvidence = new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseReviewRules.GetMetadataStatus(candidate.DeclaredLicense, metadataRetrieved: true),
            PublisherDeclaredLicense = candidate.DeclaredLicense,
            MetadataObservedUtc = observedUtc,
            ReviewRequired = ModelLicenseReviewRules.RequiresReview(candidate.DeclaredLicense, metadataRetrieved: true)
        };
        var updateService = CreateService(new FakeHandler(null, newPayload));

        ModelDownloadStatus selected = await updateService.CreateCandidateDownloadAsync(
            candidate, licenseEvidence, observedUtc, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(updateService, selected.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(newPayload, await File.ReadAllBytesAsync(Path.Combine(_directory, _fileName)));
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(
            Assert.Single(updateService.ListManagedModels()).Provenance);
        Assert.Equal(repoId, provenance.RepoId);
        Assert.Equal(revision, provenance.Revision);
        Assert.Equal(repoFilePath, provenance.RepoFilePath);
    }

    [Fact]
    public async Task Legacy_main_download_and_old_schema_one_json_keep_source_identity_unknown()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        request.Revision = null;
        request.DownloadUrl = $"https://huggingface.co/org/model/resolve/main/{_fileName}";
        var service = CreateService(new FakeHandler(null, new byte[] { 3 }));
        ModelDownloadStatus job = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, job.Id)).Status);
        ModelDownloadProvenance entry = Assert.IsType<ModelDownloadProvenance>(Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Null(entry.RepoFilePath);
        Assert.Null(entry.PinnedSourceUrl);

        string manifestPath = Path.Combine(_directory, ".model-provenance.json");
        string oldJson = JsonSerializer.Serialize(ModelDownloadProvenanceManifestStore.Export(_directory),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("repoFilePath", oldJson, StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, oldJson);
        entry = Assert.IsType<ModelDownloadProvenance>(Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Null(entry.RepoFilePath);
        Assert.Null(entry.PinnedSourceUrl);
    }

    [Fact]
    public async Task Missing_provider_hash_keeps_integrity_unavailable_but_records_local_content_fingerprint()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 1, 3, 3, 7 };
        var request = Request(new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = DateTime.UtcNow.AddSeconds(-1),
            ReviewRequired = false
        });
        var handler = new FakeHandler(null, payload);
        var service = CreateService(handler);

        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("completed", completed.Status);
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Null(provenance.ExpectedSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), provenance.ComputedSha256);
        Assert.Equal("unavailable", provenance.IntegrityStatus);
        Assert.False(provenance.ReviewRequired);
        Assert.False(provenance.ReviewAcknowledged);
        Assert.Null(provenance.ReviewAcknowledgedUtc);
    }

    [Fact]
    public async Task Human_readable_unicode_filename_is_accepted_by_transfer_and_manifest_store()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        const string fileName = "Research (Q4) — 模型.gguf";
        byte[] payload = { 9, 2, 6, 5 };
        var request = Request(new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = DateTime.UtcNow.AddSeconds(-1),
            ReviewRequired = false
        });
        request.FileName = fileName;
        request.DownloadUrl = $"https://huggingface.co/org/model/resolve/{request.Revision}/{Uri.EscapeDataString(fileName)}";
        var handler = new FakeHandler(null, payload);
        var service = CreateService(handler);

        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("completed", completed.Status);
        Assert.True(File.Exists(Path.Combine(_directory, fileName)));
        ManagedModelInfo installed = Assert.Single(service.ListManagedModels());
        Assert.Equal(fileName, installed.FileName);
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(installed.Provenance);
        Assert.Equal("unavailable", provenance.IntegrityStatus);
        Assert.Equal(fileName, provenance.RepoFilePath);
        Assert.Equal(request.DownloadUrl, provenance.PinnedSourceUrl);
    }

    [Fact]
    public async Task Manifest_rejects_partial_or_mismatched_source_identity()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        var service = CreateService(new FakeHandler(null, new byte[] { 3 }));
        ModelDownloadStatus job = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, job.Id)).Status);

        ModelProvenanceManifestDocument document = ModelDownloadProvenanceManifestStore.Export(_directory);
        ModelDownloadProvenance entry = document.Entries[_fileName];
        entry.PinnedSourceUrl = null;
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.ValidateDocument(document));
        entry.PinnedSourceUrl = $"https://huggingface.co/org/other/resolve/{request.Revision}/{_fileName}";
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.ValidateDocument(document));
        entry.PinnedSourceUrl = $"https://huggingface.co/org/model/resolve/{request.Revision}/{_fileName}?token=secret";
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.ValidateDocument(document));
        entry.PinnedSourceUrl = request.DownloadUrl;
        entry.RepoFilePath = "other.gguf";
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.ValidateDocument(document));
    }

    [Fact]
    public void Ambiguous_or_invalid_source_urls_never_produce_transfer_identity()
    {
        string revision = new('a', 40);
        Assert.Equal((null, null), ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            "org/model", revision, $"https://evil.example/org/model/resolve/{revision}/model.gguf"));
        Assert.Equal((null, null), ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            "org/model", revision, $"https://huggingface.co/org/model/resolve/{revision}/weights%2Fmodel.gguf"));
        Assert.Equal((null, null), ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            "org/model", null, "https://huggingface.co/org/model/resolve/main/model.gguf"));
    }

    [Fact]
    public async Task Replaced_managed_bytes_are_reported_with_unknown_provenance()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 4, 2, 4, 2 };
        var request = Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided });
        var handler = new FakeHandler(null, payload);
        var service = CreateService(handler);
        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal("completed", (await WaitForTerminalAsync(service, created.Id)).Status);

        string modelPath = Path.Combine(_directory, _fileName);
        File.WriteAllBytes(modelPath, new byte[] { 8, 8, 8, 8 });
        File.SetLastWriteTimeUtc(modelPath, DateTime.UtcNow.AddMinutes(5));

        Assert.Null(Assert.Single(service.ListManagedModels()).Provenance);
    }

    [Fact]
    public async Task Corrupt_manifest_fails_before_network_or_model_promotion()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        Directory.CreateDirectory(_directory);
        string manifestPath = Path.Combine(_directory, ".model-provenance.json");
        const string corrupt = "{not-json";
        File.WriteAllText(manifestPath, corrupt);
        var handler = new FakeHandler(null, new byte[] { 1, 2, 3 });
        var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateDownloadAsync(
            Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided }),
            CancellationToken.None));

        Assert.Equal(corrupt, File.ReadAllText(manifestPath));
        Assert.False(File.Exists(Path.Combine(_directory, _fileName)));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.partial*"));
    }

    [Fact]
    public async Task Manifest_corruption_during_transfer_never_replaces_an_existing_model()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        Directory.CreateDirectory(_directory);
        string modelPath = Path.Combine(_directory, _fileName);
        byte[] existingModel = { 7, 7, 7, 7 };
        File.WriteAllBytes(modelPath, existingModel);
        string manifestPath = Path.Combine(_directory, ".model-provenance.json");
        const string corrupt = "{corrupted-mid-transfer";
        var handler = new FakeHandler(null, new byte[] { 1, 2, 3, 4 }, () => File.WriteAllText(manifestPath, corrupt));
        var service = CreateService(handler);

        ModelDownloadStatus created = await service.CreateDownloadAsync(
            Request(new ModelDownloadLicenseEvidence { MetadataStatus = ModelLicenseMetadataStatus.NotProvided }),
            CancellationToken.None);
        ModelDownloadStatus failed = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("failed", failed.Status);
        Assert.Contains("manifest is corrupt", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingModel, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task Hash_mismatch_never_publishes_a_completed_provenance_entry()
    {
        using var scope = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        byte[] payload = { 5, 5, 5 };
        string wrongHash = new string('f', 64);
        var request = Request(new ModelDownloadLicenseEvidence
        {
            MetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT",
            MetadataObservedUtc = DateTime.UtcNow,
            ReviewRequired = false
        });
        var handler = new FakeHandler(wrongHash, payload);
        var service = CreateService(handler);

        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        ModelDownloadStatus failed = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("failed", failed.Status);
        Assert.False(File.Exists(Path.Combine(_directory, _fileName)));
        Assert.Empty(ModelDownloadProvenanceManifestStore.Load(_directory).Entries);
    }

    private ModelDownloadRequest Request(ModelDownloadLicenseEvidence evidence)
    {
        string revision = new('a', 40);
        return new ModelDownloadRequest
        {
            RepoId = "org/model",
            Revision = revision,
            FileName = _fileName,
            DownloadUrl = $"https://huggingface.co/org/model/resolve/{revision}/{_fileName}",
            SizeBytes = 1024,
            LicenseEvidence = evidence
        };
    }

    private static ModelDownloadService CreateService(HttpMessageHandler handler)
    {
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        service.BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
        {
            WithinBudget = true,
            LimitBytes = long.MaxValue,
            QuotaHeadroomBytes = long.MaxValue,
            DestinationFreeBytes = long.MaxValue
        });
        return service;
    }

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status?.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Model download did not finish before the test timeout.");
    }

    private sealed class FakeHandler(string? expectedSha256, byte[] payload, Action? beforeGet = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Head) beforeGet?.Invoke();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Head
                    ? new ByteArrayContent(Array.Empty<byte>())
                    : new ByteArrayContent(payload)
            };
            if (request.Method == HttpMethod.Head)
                response.Content.Headers.ContentLength = payload.Length;
            if (request.Method == HttpMethod.Head && expectedSha256 != null)
                response.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + expectedSha256 + "\"");
            return Task.FromResult(response);
        }
    }
}
