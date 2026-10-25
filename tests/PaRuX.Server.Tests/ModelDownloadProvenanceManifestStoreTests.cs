using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadProvenanceManifestStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-provenance-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertExportImportRoundTripsInAnIsolatedDirectory()
    {
        byte[] payload = { 3, 1, 4, 1, 5, 9 };
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var expected = CreateEntry("org/model-a", hash, payload.Length);
        ModelDownloadProvenanceManifestStore.Upsert(_directory, "model.gguf", expected);

        var exported = ModelDownloadProvenanceManifestStore.Export(_directory);
        Assert.Equal(1, exported.SchemaVersion);
        Assert.Equal(expected.RepoId, exported.Entries["MODEL.GGUF"].RepoId);

        var restoredDirectory = Path.Combine(_directory, "restored");
        Directory.CreateDirectory(restoredDirectory);
        File.WriteAllBytes(Path.Combine(restoredDirectory, "model.gguf"), payload);
        Assert.Equal(1, await ModelDownloadProvenanceManifestStore.ImportAsync(restoredDirectory, exported, CancellationToken.None));
        Assert.Equal(expected.Revision, ModelDownloadProvenanceManifestStore.Get(restoredDirectory, "model.gguf")?.Revision);
    }

    [Fact]
    public void Safe_name_policy_accepts_spaces_parentheses_and_unicode_but_rejects_windows_invalid_characters()
    {
        Assert.True(ModelDownloadProvenanceManifestStore.IsSafeFileName("Research (Q4) — 模型.gguf"));
        Assert.True(ModelDownloadProvenanceManifestStore.IsSafeFileName("model name (v2).safetensors"));
        Assert.False(ModelDownloadProvenanceManifestStore.IsSafeFileName("model?.gguf"));
        Assert.False(ModelDownloadProvenanceManifestStore.IsSafeFileName("CON.gguf"));
    }

    [Fact]
    public void EntryKeysAreCaseInsensitiveForReplaceAndRemove()
    {
        ModelDownloadProvenanceManifestStore.Upsert(_directory, "Model.GGUF", CreateEntry("org/first"));
        ModelDownloadProvenanceManifestStore.Upsert(_directory, "model.gguf", CreateEntry("org/second"));

        var document = ModelDownloadProvenanceManifestStore.Load(_directory);
        Assert.Single(document.Entries);
        Assert.Equal("org/second", document.Entries["MODEL.GGUF"].RepoId);
        Assert.True(ModelDownloadProvenanceManifestStore.Remove(_directory, "MODEL.GGUF"));
        Assert.Empty(ModelDownloadProvenanceManifestStore.Load(_directory).Entries);
    }

    [Fact]
    public void CorruptOrMalformedManifestIsSafelyTreatedAsUnknown()
    {
        Directory.CreateDirectory(_directory);
        var manifestPath = Path.Combine(_directory, ".model-provenance.json");
        File.WriteAllText(manifestPath, "{not-json");
        Assert.Empty(ModelDownloadProvenanceManifestStore.Load(_directory).Entries);
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_directory, "old-model.gguf"));

        File.WriteAllText(manifestPath, "{\"schemaVersion\":99,\"entries\":{}}");
        Assert.Empty(ModelDownloadProvenanceManifestStore.Load(_directory).Entries);
        var corruptContent = File.ReadAllText(manifestPath);
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.Upsert(_directory, "would-clobber.gguf", CreateEntry("org/model")));
        Assert.Equal(corruptContent, File.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task ImportRejectsUnsafeKeysAndInvalidEntryShapesAtomically()
    {
        var valid = CreateEntry("org/model");
        var unsafeDocument = new ModelProvenanceManifestDocument();
        unsafeDocument.Entries.Add("..\\outside.gguf", valid);

        Assert.Null(await ModelDownloadProvenanceManifestStore.ImportAsync(_directory, unsafeDocument, CancellationToken.None));
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_directory, "outside.gguf"));

        var invalidEntry = new ModelProvenanceManifestDocument();
        invalidEntry.Entries.Add("model.gguf", CreateEntry("not-a-repo-id"));
        Assert.Null(await ModelDownloadProvenanceManifestStore.ImportAsync(_directory, invalidEntry, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ModelDownloadProvenanceManifestStore.ValidateDocument(invalidEntry));

        Assert.Throws<ArgumentException>(() => ModelDownloadProvenanceManifestStore.Upsert(_directory, "C:\\outside.gguf", valid));
    }

    [Fact]
    public async Task Import_cancellation_does_not_write_or_replace_the_manifest()
    {
        var document = new ModelProvenanceManifestDocument();
        document.Entries.Add("model.gguf", CreateEntry("org/model"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelDownloadProvenanceManifestStore.ImportAsync(_directory, document, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_directory, ".model-provenance.json")));
    }

    [Fact]
    public void MissingManifestAndMissingEntryRemainUnknown()
    {
        Assert.Empty(ModelDownloadProvenanceManifestStore.Load(_directory).Entries);
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_directory, "preexisting.gguf"));

        ModelDownloadProvenanceManifestStore.Upsert(_directory, "known.gguf", CreateEntry("org/known"));
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_directory, "preexisting.gguf"));
    }

    private static ModelDownloadProvenance CreateEntry(string repoId, string? hash = null, long size = 1234) => new()
    {
        RepoId = repoId,
        Revision = new string('a', 40),
        ModelCardUrl = "https://huggingface.co/" + repoId + "/tree/" + new string('a', 40),
        LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
        PublisherDeclaredLicense = "custom-research-only",
        MetadataObservedUtc = new DateTime(2026, 9, 24, 11, 59, 0, DateTimeKind.Utc),
        ReviewRequired = true,
        ReviewAcknowledged = true,
        ReviewAcknowledgedUtc = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc),
        ExpectedSha256 = hash ?? new string('b', 64),
        ComputedSha256 = hash ?? new string('b', 64),
        IntegrityStatus = "verified",
        SizeBytes = size,
        FileLastWriteUtc = new DateTime(2026, 9, 24, 12, 0, 30, DateTimeKind.Utc),
        CompletedUtc = new DateTime(2026, 9, 24, 12, 1, 0, DateTimeKind.Utc)
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
