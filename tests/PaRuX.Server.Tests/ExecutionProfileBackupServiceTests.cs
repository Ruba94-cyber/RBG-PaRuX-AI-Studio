using System.IO.Compression;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionProfileBackupServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void V2_export_adds_profiles_without_changing_format_version_and_discloses_visual_privacy_boundary()
    {
        byte[] legacy = CreateLegacyArchive();
        ModelExecutionProfile tuned = ExecutionProfileStore.BuildDefault("model-a", ExecutionQualityProfile.Overnight);
        tuned.EvidenceSource = "benchmark:unit";
        var status = new ExecutionProfileStatus
        {
            ActiveQualityProfile = ExecutionQualityProfile.Overnight,
            Effective = ExecutionProfileStore.BuildDefault("active", ExecutionQualityProfile.Overnight),
            ModelOverrides = new List<ModelExecutionProfile> { tuned }
        };

        byte[] augmented = ExecutionProfileBackupService.AddProfiles(legacy, status);
        BackupImportGuardMiddleware.ValidateArchive(new MemoryStream(augmented, writable: false));
        ExecutionProfileStatus? restored = ExecutionProfileBackupService.ReadAndValidateProfiles(augmented);

        Assert.NotNull(restored);
        Assert.Equal(ExecutionQualityProfile.Overnight, restored!.ActiveQualityProfile);
        Assert.Single(restored.ModelOverrides);
        Assert.Equal("model-a", restored.ModelOverrides[0].ModelKey);
        Assert.Equal("benchmark:unit", restored.ModelOverrides[0].EvidenceSource);

        using var stream = new MemoryStream(augmented, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using Stream manifestStream = zip.GetEntry("manifest.json")!.Open();
        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(manifestStream, JsonOptions)!;
        Assert.Equal(1, manifest.FormatVersion);
        Assert.True(manifest.Contents.ExecutionProfilesIncluded);
        Assert.False(manifest.Contents.VisualContextIncluded);
        Assert.Equal(ExecutionProfileBackupService.VisualContextBackupPolicy, manifest.Contents.VisualContextBackupPolicy);
        Assert.NotNull(zip.GetEntry(ExecutionProfileBackupService.EntryName));
        Assert.Null(zip.GetEntry("visual-context-v2.db"));
    }

    [Fact]
    public void Legacy_v1_archive_without_profile_flag_remains_import_compatible()
    {
        byte[] legacy = CreateLegacyArchive();
        Assert.Null(ExecutionProfileBackupService.ReadAndValidateProfiles(legacy));
    }

    [Fact]
    public void Manifest_flag_without_sidecar_is_rejected_before_restore()
    {
        byte[] archive = CreateLegacyArchive(executionProfilesIncluded: true);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ExecutionProfileBackupService.ReadAndValidateProfiles(archive));
        Assert.Contains("execution-profiles.json", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_execution_profile_is_rejected_before_restore()
    {
        byte[] archive;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteJson(zip, "manifest.json", new BackupManifest
                {
                    FormatVersion = 1,
                    Contents = new BackupContents { ExecutionProfilesIncluded = true }
                });
                WriteJson(zip, ExecutionProfileBackupService.EntryName, new ExecutionProfileStatus
                {
                    ActiveQualityProfile = "turbo",
                    ModelOverrides = new List<ModelExecutionProfile>()
                });
            }
            archive = stream.ToArray();
        }

        Assert.Throws<InvalidDataException>(() => ExecutionProfileBackupService.ReadAndValidateProfiles(archive));
    }

    private static byte[] CreateLegacyArchive(bool executionProfilesIncluded = false)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "manifest.json", new BackupManifest
            {
                FormatVersion = 1,
                CreatedUtc = DateTime.UtcNow,
                AppVersion = "test",
                MachineName = "test",
                Contents = new BackupContents { ExecutionProfilesIncluded = executionProfilesIncluded }
            });
            ZipArchiveEntry marker = zip.CreateEntry("memory.db");
            using Stream output = marker.Open();
            output.WriteByte(1);
        }
        return stream.ToArray();
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream output = entry.Open();
        JsonSerializer.Serialize(output, value, JsonOptions);
    }
}
