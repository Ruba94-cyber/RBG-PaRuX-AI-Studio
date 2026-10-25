using System.IO.Compression;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Additive v2 backup envelope over the existing BackupService. The legacy archive remains format
/// version 1 and fully readable; new exports add one optional execution-profiles.json entry plus the
/// manifest flag. Import validates this sidecar before BackupService mutates any of its stores, then
/// applies the already-validated execution profile atomically as the final swap. This mirrors the
/// existing BackupService guarantee: all inputs are validated first, while individual store swaps
/// are atomic but there is intentionally no impossible cross-store filesystem transaction.
///
/// Visual/App Context is deliberately NOT added to the portable archive: it may contain full desktop
/// screenshots and has independent bounded local retention. The manifest records that policy so the
/// exclusion is visible rather than an undocumented omission; restore leaves destination Visual
/// Context untouched.
/// </summary>
public sealed class ExecutionProfileBackupService
{
    internal const string EntryName = "execution-profiles.json";
    internal const string VisualContextBackupPolicy = "excluded-sensitive-local-screen-evidence";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly BackupService _backups;
    private readonly ExecutionProfileStore _profiles;

    public ExecutionProfileBackupService(BackupService backups, ExecutionProfileStore profiles)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async Task<byte[]> ExportAsync(CancellationToken ct)
    {
        byte[] archive = await _backups.ExportAsync(ct).ConfigureAwait(false);
        return AddProfiles(archive, _profiles.ExportForBackup());
    }

    public async Task<BackupImportResult> ImportAsync(
        Stream zipContent, CancellationToken ct, bool allowIncompleteRollbackSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(zipContent);
        using var buffered = new MemoryStream();
        await zipContent.CopyToAsync(buffered, ct).ConfigureAwait(false);
        if (buffered.Length == 0) throw new InvalidDataException("A backup .zip file is required.");

        byte[] bytes = buffered.ToArray();
        ExecutionProfileStatus? profiles = ReadAndValidateProfiles(bytes);

        using var legacyStream = new MemoryStream(bytes, writable: false);
        BackupImportResult result = await _backups.ImportAsync(legacyStream, ct, allowIncompleteRollbackSnapshot).ConfigureAwait(false);
        if (result.Success && profiles != null)
            _profiles.ImportForBackup(profiles);
        return result;
    }

    internal static byte[] AddProfiles(byte[] legacyArchive, ExecutionProfileStatus profiles)
    {
        ArgumentNullException.ThrowIfNull(legacyArchive);
        ArgumentNullException.ThrowIfNull(profiles);
        ExecutionProfileStore.ValidateBackupForImport(profiles);

        using var stream = new MemoryStream();
        stream.Write(legacyArchive, 0, legacyArchive.Length);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            BackupManifest manifest = ReadManifest(archive);
            manifest.Contents.ExecutionProfilesIncluded = true;
            manifest.Contents.VisualContextIncluded = false;
            manifest.Contents.VisualContextBackupPolicy = VisualContextBackupPolicy;

            archive.GetEntry(EntryName)?.Delete();
            ZipArchiveEntry profilesEntry = archive.CreateEntry(EntryName, CompressionLevel.Optimal);
            using (Stream output = profilesEntry.Open())
                JsonSerializer.Serialize(output, profiles, JsonOptions);

            archive.GetEntry("manifest.json")?.Delete();
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using Stream manifestOutput = manifestEntry.Open();
            JsonSerializer.Serialize(manifestOutput, manifest, JsonOptions);
        }
        return stream.ToArray();
    }

    internal static ExecutionProfileStatus? ReadAndValidateProfiles(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        BackupManifest manifest = ReadManifest(archive);
        if (!manifest.Contents.ExecutionProfilesIncluded) return null;

        ZipArchiveEntry entry = archive.GetEntry(EntryName)
            ?? throw new InvalidDataException("The backup manifest lists execution profiles but execution-profiles.json is missing from the archive.");
        try
        {
            using Stream input = entry.Open();
            ExecutionProfileStatus? profiles = JsonSerializer.Deserialize<ExecutionProfileStatus>(input, JsonOptions);
            if (profiles == null) throw new InvalidDataException("execution-profiles.json in this backup is empty or corrupt.");
            ExecutionProfileStore.ValidateBackupForImport(profiles);
            return profiles;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("execution-profiles.json in this backup is corrupt: " + ex.Message, ex);
        }
    }

    private static BackupManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("This file is not a PaRuX backup (manifest.json is missing).");
        try
        {
            using Stream input = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(input, JsonOptions)
                ?? throw new InvalidDataException("The backup manifest is empty or corrupt.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup manifest is corrupt: " + ex.Message, ex);
        }
    }
}
