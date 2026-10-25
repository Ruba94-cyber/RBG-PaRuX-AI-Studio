using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Automation;
using PaRuX.Server.Autopilot;
using PaRuX.Server.AutoLab;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Workflow;
using PaRuX.Server.Orchestration;
using PaRuX.Server.CapabilityProfiler;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Exercises BackupService entirely against private temp directories - every store here is
/// constructed with an explicit databaseDirectory/clientRoot override (see ConversationStore,
/// MemoryStore, RagStore, and BackupService's test-only constructor overloads), never the real
/// %LOCALAPPDATA%\PaRuX or %LOCALAPPDATA%\PaRuX\server. Restore is destructive by design, so
/// this suite must never be able to touch a real user's live conversations, memory, RAG
/// configuration, settings, or any vNext agent/task database.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _modelsDir;

    /// <summary>An isolated folder for the RAG round-trip to point at.
    ///
    /// It used to index Path.GetTempPath() itself - the machine's shared temp directory, which
    /// every other test in this suite, every other test class, and every other process on the
    /// machine writes to and deletes from concurrently. That made the round-trip's result depend on
    /// what else happened to be running, and it failed once in a full-suite run under load while
    /// passing on its own. A backup test must be about the archive, not about the weather.</summary>
    private readonly string _ragDir;

    private readonly ConversationStore _conversations;
    private readonly MemoryStore _memory;
    private readonly RagStore _rag;
    private readonly TaskStore _tasks;
    private readonly ContextGraphStore _contextGraph;
    private readonly ArtifactStore _artifacts;
    private readonly AutomationStore _automations;
    private readonly AutopilotStore _autopilotRuns;
    private readonly EvalStore _evalLab;
    private readonly AdapterStore _adapters;
    private readonly AutoLabStore _autoLab;
    private readonly WorkflowStore _workflows;
    private readonly CapabilityProfilerStore _capabilityProfiles;
    private readonly ModelBenchmarkStore _modelBenchmarks;
    private readonly PersonaService _personas;
    private readonly McpClientRegistry _mcpClients;
    private readonly InternetSettingsService _internetSettings;
    private readonly RemoteAccessService _remoteAccess;
    private readonly SkillRegistryService _skills;
    private readonly CustomWorkflowService _customWorkflows;
    private readonly AiProfileService _aiProfile;
    private readonly BackupService _backups;

    public BackupServiceTests()
    {
        _serverDir = Path.Combine(Path.GetTempPath(), "parux-backup-test-server-" + Guid.NewGuid().ToString("N"));
        _clientDir = Path.Combine(Path.GetTempPath(), "parux-backup-test-client-" + Guid.NewGuid().ToString("N"));
        _modelsDir = Path.Combine(Path.GetTempPath(), "parux-backup-test-models-" + Guid.NewGuid().ToString("N"));
        _ragDir = Path.Combine(Path.GetTempPath(), "parux-backup-test-rag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_modelsDir);
        Directory.CreateDirectory(_ragDir);

        // No embedding model is installed in CI/dev by default, so TryEmbed(...) returns null and
        // MemoryStore/RagStore fall back to their keyword-only path - that's fine, this suite is
        // about the backup archive's shape and safety, not retrieval quality.
        var embeddings = new EmbeddingService();
        _conversations = new ConversationStore(_serverDir);
        _memory = new MemoryStore(embeddings, _serverDir);
        _rag = new RagStore(embeddings, _serverDir);
        _tasks = new TaskStore(_serverDir);
        _contextGraph = new ContextGraphStore(_serverDir);
        _artifacts = new ArtifactStore(_serverDir);
        _automations = new AutomationStore(_serverDir);
        _autopilotRuns = new AutopilotStore(_serverDir);
        _evalLab = new EvalStore(_serverDir);
        _adapters = new AdapterStore(_serverDir);
        _autoLab = new AutoLabStore(_serverDir);
        _workflows = new WorkflowStore(_serverDir);
        _capabilityProfiles = new CapabilityProfilerStore(_serverDir);
        _modelBenchmarks = new ModelBenchmarkStore(_serverDir);
        _personas = new PersonaService(_serverDir);
        _mcpClients = new McpClientRegistry(_serverDir);
        _internetSettings = new InternetSettingsService(_serverDir);
        _remoteAccess = new RemoteAccessService(Path.Combine(_serverDir, "remote-access.json"));
        _skills = new SkillRegistryService(_serverDir);
        _customWorkflows = new CustomWorkflowService(_serverDir);
        _aiProfile = new AiProfileService(
            new LocalRuntimeService(new FakeHostEnvironment(), new LocalAiProcessManager(), new ExternalTextProviderService()),
            _serverDir);
        _backups = new BackupService(
            _conversations, _memory, _rag,
            _tasks, _contextGraph, _artifacts, _automations, _autopilotRuns, _evalLab, _adapters,
            _autoLab, _workflows, _capabilityProfiles, _modelBenchmarks, _personas, _mcpClients,
            _internetSettings, _remoteAccess, _skills, _customWorkflows, _aiProfile, _clientDir, _modelsDir);
    }

    /// <summary>Minimal IHostEnvironment stand-in so AiProfileService's LocalRuntimeService
    /// dependency can be constructed hermetically - AiProfileService's auto-fallback methods
    /// (the only ones this suite exercises) never touch _environment/_process/_externalProvider
    /// at all, so a real host or real runtime process manager is unnecessary here.</summary>
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose()
    {
        _mcpClients.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_serverDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_clientDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_modelsDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_ragDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Export_then_import_round_trips_conversations_memory_and_rag_sources()
    {
        _conversations.Save("conv-1", new ConversationUpsertRequest
        {
            Title = "Round trip test",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            DocumentJson = "{\"id\":\"conv-1\",\"messages\":[]}"
        });
        MemoryEntryDto memoryEntry = _memory.Add(new MemoryAddRequest { Scope = "User", Content = "Backup round-trip marker QwertyUiop77." });
        RagSourceDto ragSource = _rag.AddSource(_ragDir);
        File.WriteAllText(Path.Combine(_clientDir, "settings.json"), "{\"theme\":\"Dark\"}");

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        Assert.True(zip.Length > 0);

        // Wipe the (temp, synthetic) live state entirely, simulating a fresh machine / lost data.
        _conversations.Delete("conv-1");
        _memory.Delete(memoryEntry.Id);
        _rag.RemoveSource(ragSource.Id);
        File.Delete(Path.Combine(_clientDir, "settings.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Restored);
        Assert.Equal(1, result.Restored!.ConversationCount);
        Assert.Equal(1, result.Restored.MemoryCount);
        Assert.Equal(1, result.Restored.RagSourceCount);
        Assert.True(result.Restored.SettingsIncluded);

        ConversationDocument? restoredConversation = _conversations.Get("conv-1");
        Assert.NotNull(restoredConversation);
        Assert.Equal("Round trip test", restoredConversation!.Title);

        Assert.Contains(_memory.List(), m => m.Content == "Backup round-trip marker QwertyUiop77.");
        Assert.Contains(_rag.ListSources(), s => s.FolderPath == _ragDir);

        string settingsJson = File.ReadAllText(Path.Combine(_clientDir, "settings.json"));
        Assert.Contains("Dark", settingsJson);

        // The client's own authoritative history copy (see class comment on BackupService for why
        // there are two conversation stores) was restored too, not just the server-side replica.
        Assert.True(File.Exists(Path.Combine(_clientDir, "parux.db")));
    }

    /// <summary>2026-08-27 Backup/Restore re-audit: model role/candidate assignments and the Smart
    /// Router's candidate pools were real user configuration silently absent from every backup
    /// archive. Safe to test for real here (unlike ModelRolesTests' own SetRoleOverride) because
    /// BackupService's LocalAiPaths.ExportBackupConfig/ImportBackupConfig calls are rooted at
    /// _clientDir, the same isolated temp directory every other test in this class already uses -
    /// never the real %LOCALAPPDATA%\PaRuX.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_model_role_and_candidate_pool_config()
    {
        string roleOverridesJson = "{\"fast\":\"C:\\\\models\\\\backup-roundtrip-marker.gguf\"}";
        string candidatePoolsJson = "{\"fast\":[\"C:\\\\models\\\\backup-roundtrip-marker.gguf\",\"C:\\\\models\\\\second-candidate.gguf\"]}";
        File.WriteAllText(Path.Combine(_clientDir, "model-role-overrides.json"), roleOverridesJson);
        File.WriteAllText(Path.Combine(_clientDir, "model-role-candidate-pools.json"), candidatePoolsJson);

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Wipe the (temp, synthetic) live state, simulating a fresh machine / lost data.
        File.Delete(Path.Combine(_clientDir, "model-role-overrides.json"));
        File.Delete(Path.Combine(_clientDir, "model-role-candidate-pools.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.LocalAiRoleConfigIncluded);
        string restoredRoleOverrides = File.ReadAllText(Path.Combine(_clientDir, "model-role-overrides.json"));
        Assert.Equal(roleOverridesJson, restoredRoleOverrides);
        string restoredCandidatePools = File.ReadAllText(Path.Combine(_clientDir, "model-role-candidate-pools.json"));
        Assert.Equal(candidatePoolsJson, restoredCandidatePools);
    }

    /// <summary>A backup taken before any role/candidate config file ever existed on this machine
    /// must not fabricate one on restore - LocalAiRoleConfigIncluded correctly stays false, and
    /// ImportAsync must not throw or create empty sidecar files that didn't exist before.</summary>
    [Fact]
    public async Task Backup_with_no_local_ai_role_config_yet_restores_without_creating_any()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Restored!.LocalAiRoleConfigIncluded);
        Assert.False(File.Exists(Path.Combine(_clientDir, "model-role-overrides.json")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "model-role-candidate-pools.json")));
    }

    /// <summary>2026-08-27: component-locations.json (the "Locate existing" USER_MANAGED pointer
    /// for optional components like Python/Git/FFmpeg) is a sibling sidecar of the same
    /// LocalAiPaths.ExportBackupConfig/ImportBackupConfig bundle - added in the same change that
    /// introduced the sidecar, per CLAUDE.md's rule to never leave new persistent state
    /// un-backed-up. Safe to test for real for the same reason as the role-overrides round-trip
    /// above: rooted at the isolated _clientDir, never the real %LOCALAPPDATA%\PaRuX.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_component_locations()
    {
        string componentLocationsJson = "{\"python\":\"C:\\\\Users\\\\dev\\\\python\\\\python.exe\"}";
        File.WriteAllText(Path.Combine(_clientDir, "component-locations.json"), componentLocationsJson);

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        File.Delete(Path.Combine(_clientDir, "component-locations.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.LocalAiRoleConfigIncluded);
        string restored = File.ReadAllText(Path.Combine(_clientDir, "component-locations.json"));
        Assert.Equal(componentLocationsJson, restored);
    }

    /// <summary>Sibling of the component-locations round-trip above, for the "Skip for now"
    /// sidecar added in the same change.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_component_skips()
    {
        string componentSkipsJson = "{\"comfyui\":\"2026-08-27T00:00:00.0000000Z\"}";
        File.WriteAllText(Path.Combine(_clientDir, "component-skips.json"), componentSkipsJson);

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        File.Delete(Path.Combine(_clientDir, "component-skips.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.LocalAiRoleConfigIncluded);
        string restored = File.ReadAllText(Path.Combine(_clientDir, "component-skips.json"));
        Assert.Equal(componentSkipsJson, restored);
    }

    /// <summary>2026-08-27 Backup/Restore re-audit: full enumeration of every persistent store
    /// found five more real gaps - model-benchmarks.db (missing the standard SQLite backup pair
    /// every other store already has) and personas.json/mcp-clients.json/internet.json/paired
    /// devices, each holding a live in-memory cache a plain file overwrite would leave stale until
    /// the next restart. Safe to test for real here because all five now have a directory/path-
    /// injectable constructor pointed at the isolated _serverDir this test class already uses -
    /// never the real ServerState.StateDirectory. Deliberately mutates the LIVE service state
    /// (not just the on-disk file) after export, to prove ImportAsync updates the already-running
    /// cache immediately rather than only writing a file a restart would eventually pick up.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_benchmarks_personas_mcp_internet_and_paired_devices()
    {
        ModelBenchmarkRun benchmarkRun = _modelBenchmarks.Create("C:\\fake\\model.gguf", "Backup round-trip benchmark marker", automatic: false);
        _modelBenchmarks.Complete(benchmarkRun.Id, 123.4, 256, 3, "cuda", "RTX-fake");

        Persona persona = _personas.Create("Backup round-trip persona marker", "Be a marker.");
        _personas.SetActive(persona.Id);

        McpServerConfig mcpServer = _mcpClients.AddServer("Backup round-trip mcp marker", "echo", "hello");

        _internetSettings.SetEnabled(true);

        var originalDevice = new RemoteAccessService.PairedDevice
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Backup round-trip device marker",
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fake-token"))),
            CreatedUtc = DateTime.UtcNow
        };
        _remoteAccess.ImportDevicesForBackup(new List<RemoteAccessService.PairedDevice> { originalDevice });

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Mutate the LIVE state to something else entirely - not just deleting the file - so a
        // successful restore can only mean ImportAsync actually refreshed the in-memory cache.
        _personas.Delete(persona.Id);
        _personas.Create("Decoy persona that should not survive restore", "Decoy.");
        await _mcpClients.RemoveServerAsync(mcpServer.Id, CancellationToken.None);
        _mcpClients.AddServer("Decoy mcp server that should not survive restore", "echo", "decoy");
        _internetSettings.SetEnabled(false);
        var decoyDevice = new RemoteAccessService.PairedDevice
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Decoy device that should not survive restore",
            TokenHash = "decoy-hash", CreatedUtc = DateTime.UtcNow
        };
        _remoteAccess.ImportDevicesForBackup(new List<RemoteAccessService.PairedDevice> { decoyDevice });
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string path = Path.Combine(_serverDir, "model-benchmarks.db" + suffix);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None, allowIncompleteRollbackSnapshot: true);

        Assert.True(result.Success);
        Assert.True(result.Restored!.ModelBenchmarksIncluded);
        Assert.True(result.Restored.PersonasIncluded);
        Assert.Equal(1, result.Restored.CustomPersonaCount);
        Assert.True(result.Restored.McpClientsIncluded);
        Assert.Equal(1, result.Restored.McpClientCount);
        Assert.True(result.Restored.InternetSettingIncluded);
        Assert.True(result.Restored.PairedDevicesIncluded);
        Assert.Equal(1, result.Restored.PairedDeviceCount);

        ModelBenchmarkRun? restoredBenchmark = _modelBenchmarks.Get(benchmarkRun.Id);
        Assert.NotNull(restoredBenchmark);
        Assert.Equal(123.4, restoredBenchmark!.TokensPerSecond);
        Assert.Equal("completed", restoredBenchmark.Status);

        PersonaListResponse personaList = _personas.GetAll();
        Assert.Contains(personaList.Personas, p => p.Id == persona.Id && p.Name == "Backup round-trip persona marker");
        Assert.DoesNotContain(personaList.Personas, p => p.Name.StartsWith("Decoy"));
        Assert.Equal(persona.Id, personaList.ActivePersonaId);

        List<McpServerConfig> mcpConfigs = _mcpClients.GetConfigs();
        Assert.Contains(mcpConfigs, c => c.Id == mcpServer.Id && c.Name == "Backup round-trip mcp marker");
        Assert.DoesNotContain(mcpConfigs, c => c.Name.StartsWith("Decoy"));

        Assert.True(_internetSettings.GetEnabled());

        List<PairedDeviceInfo> devices = _remoteAccess.GetStatus().Devices;
        Assert.Contains(devices, d => d.Id == originalDevice.Id && d.Name == "Backup round-trip device marker");
        Assert.DoesNotContain(devices, d => d.Id == decoyDevice.Id);
    }

    /// <summary>A backup taken before any of these five stores has ever been touched must not
    /// fabricate an entry for any of them on restore.</summary>
    [Fact]
    public async Task Backup_with_none_of_the_five_re_audit_stores_populated_restores_without_fabricating_any()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        // model-benchmarks.db is a real SQLite store like every other one here, so it's always
        // included once schema exists (an empty table, same convention as eval.db/adapters.db).
        Assert.True(result.Restored!.ModelBenchmarksIncluded);
        Assert.False(result.Restored.PersonasIncluded);
        Assert.False(result.Restored.McpClientsIncluded);
        Assert.False(result.Restored.InternetSettingIncluded);
        Assert.False(result.Restored.PairedDevicesIncluded);
        Assert.False(File.Exists(Path.Combine(_serverDir, "personas.json")));
        Assert.False(File.Exists(Path.Combine(_serverDir, "mcp-clients.json")));
        Assert.False(File.Exists(Path.Combine(_serverDir, "internet.json")));
    }

    /// <summary>2026-08-27 Backup/Restore re-audit: ModelDownloadService's .aliases.json sidecar
    /// (cosmetic display names for managed GGUF files) was deliberately excluded from earlier
    /// checkpoints as "harmless if lost" - closed for real per a follow-up user directive. Safe to
    /// test hermetically because ExportAliasesForBackup/ImportAliasesForBackup both take an
    /// explicit modelsDirectory, and BackupService threads its own _modelsDirectory (this test's
    /// isolated _modelsDir) through - never the real %LOCALAPPDATA%\PaRuX\models.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_model_alias_labels()
    {
        var aliases = new Dictionary<string, string> { ["backup-roundtrip-marker.gguf"] = "My Coding Assistant" };
        ModelDownloadService.ImportAliasesForBackup(aliases, _modelsDir);

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Wipe the (temp, synthetic) live state, simulating a fresh machine / lost data.
        File.Delete(Path.Combine(_modelsDir, ".aliases.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.ModelAliasesIncluded);
        Assert.Equal(1, result.Restored.ModelAliasCount);
        Dictionary<string, string>? restored = ModelDownloadService.ExportAliasesForBackup(_modelsDir);
        Assert.NotNull(restored);
        Assert.Equal("My Coding Assistant", restored!["backup-roundtrip-marker.gguf"]);
    }

    /// <summary>A backup taken before any alias has ever been set must not fabricate the entry.</summary>
    [Fact]
    public async Task Backup_with_no_model_aliases_yet_restores_without_creating_any()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Restored!.ModelAliasesIncluded);
        Assert.False(File.Exists(Path.Combine(_modelsDir, ".aliases.json")));
    }

    /// <summary>2026-08-27: ModelDownloadService's .repo-ids.json sidecar (which HF repo a managed
    /// GGUF was downloaded from), closing the License/Trust Manager's disclosed "installed-library
    /// backfill" gap. Same isolated-_modelsDir seam as the alias test above.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_model_repo_ids()
    {
        var repoIds = new Dictionary<string, string> { ["backup-roundtrip-marker.gguf"] = "org/some-model" };
        ModelDownloadService.ImportRepoIdsForBackup(repoIds, _modelsDir);

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Wipe the (temp, synthetic) live state, simulating a fresh machine / lost data.
        File.Delete(Path.Combine(_modelsDir, ".repo-ids.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.ModelRepoIdsIncluded);
        Assert.Equal(1, result.Restored.ModelRepoIdCount);
        Dictionary<string, string>? restored = ModelDownloadService.ExportRepoIdsForBackup(_modelsDir);
        Assert.NotNull(restored);
        Assert.Equal("org/some-model", restored!["backup-roundtrip-marker.gguf"]);
    }

    [Fact]
    public async Task Export_then_import_round_trips_revision_bound_model_provenance()
    {
        string revision = new('a', 40);
        byte[] modelBytes = Enumerable.Range(0, 42).Select(value => (byte)value).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
        string modelPath = Path.Combine(_modelsDir, "backup-provenance.gguf");
        File.WriteAllBytes(modelPath, modelBytes);
        var provenance = new ModelProvenanceManifestDocument();
        provenance.Entries.Add("backup-provenance.gguf", new ModelDownloadProvenance
        {
            RepoId = "org/model",
            Revision = revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + revision,
            RepoFilePath = "weights/backup-provenance.gguf",
            PinnedSourceUrl = "https://huggingface.co/org/model/resolve/" + revision + "/weights/backup-provenance.gguf",
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "custom-research-only",
            MetadataObservedUtc = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc),
            ReviewRequired = true,
            ReviewAcknowledged = true,
            ReviewAcknowledgedUtc = new DateTime(2026, 9, 24, 10, 1, 0, DateTimeKind.Utc),
            ExpectedSha256 = hash,
            ComputedSha256 = hash,
            IntegrityStatus = "verified",
            SizeBytes = modelBytes.Length,
            FileLastWriteUtc = File.GetLastWriteTimeUtc(modelPath),
            CompletedUtc = new DateTime(2026, 9, 24, 10, 2, 0, DateTimeKind.Utc)
        });
        Assert.Equal(1, await ModelDownloadService.ImportModelProvenanceForBackupAsync(provenance, _modelsDir, CancellationToken.None));

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        File.Delete(Path.Combine(_modelsDir, ".model-provenance.json"));

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.ModelProvenanceIncluded);
        Assert.Equal(1, result.Restored.ModelProvenanceEntryCount);
        ModelDownloadProvenance restored = Assert.IsType<ModelDownloadProvenance>(
            ModelDownloadProvenanceManifestStore.Get(_modelsDir, "backup-provenance.gguf"));
        Assert.Equal(revision, restored.Revision);
        Assert.Equal("weights/backup-provenance.gguf", restored.RepoFilePath);
        Assert.Equal("https://huggingface.co/org/model/resolve/" + revision + "/weights/backup-provenance.gguf", restored.PinnedSourceUrl);
        Assert.Equal("custom-research-only", restored.PublisherDeclaredLicense);
        Assert.True(restored.ReviewAcknowledged);
        Assert.Equal(hash, restored.ComputedSha256);
    }

    [Fact]
    public async Task Backup_provenance_is_not_attached_to_same_name_file_with_different_bytes()
    {
        string revision = new('c', 40);
        byte[] originalBytes = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        string modelPath = Path.Combine(_modelsDir, "same-name-provenance.gguf");
        File.WriteAllBytes(modelPath, originalBytes);
        var provenance = new ModelProvenanceManifestDocument();
        provenance.Entries.Add("same-name-provenance.gguf", new ModelDownloadProvenance
        {
            RepoId = "org/model",
            Revision = revision,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + revision,
            RepoFilePath = "weights/same-name-provenance.gguf",
            PinnedSourceUrl = "https://huggingface.co/org/model/resolve/" + revision + "/weights/same-name-provenance.gguf",
            LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided,
            ExpectedSha256 = null,
            ComputedSha256 = hash,
            IntegrityStatus = "unavailable",
            SizeBytes = originalBytes.Length,
            FileLastWriteUtc = File.GetLastWriteTimeUtc(modelPath),
            CompletedUtc = DateTime.UtcNow
        });
        Assert.Equal(1, await ModelDownloadService.ImportModelProvenanceForBackupAsync(provenance, _modelsDir, CancellationToken.None));
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        File.Delete(Path.Combine(_modelsDir, ".model-provenance.json"));
        File.WriteAllBytes(modelPath, Enumerable.Repeat((byte)0xA5, originalBytes.Length).ToArray());

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Restored!.ModelProvenanceIncluded);
        Assert.Equal(0, result.Restored.ModelProvenanceEntryCount);
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_modelsDir, "same-name-provenance.gguf"));
    }

    [Fact]
    public async Task Backup_without_model_provenance_keeps_legacy_unknown_state()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Restored!.ModelProvenanceIncluded);
        Assert.False(File.Exists(Path.Combine(_modelsDir, ".model-provenance.json")));
    }

    /// <summary>A backup taken before any file has ever recorded a repo id must not fabricate the entry.</summary>
    [Fact]
    public async Task Backup_with_no_model_repo_ids_yet_restores_without_creating_any()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Restored!.ModelRepoIdsIncluded);
        Assert.False(File.Exists(Path.Combine(_modelsDir, ".repo-ids.json")));
    }

    /// <summary>2026-08-27 second re-audit pass: an independent full re-enumeration at the end of
    /// the first pass found five more real gaps (skills.db, custom ComfyUI workflow templates,
    /// RemoteAccessService's own Enabled/Port fields, and AiProfileService's auto-fallback setting -
    /// per-role adapter assignments, the fifth, needed no BackupService change at all since
    /// LocalAiPaths.ExportBackupConfig already bundles it into the existing local-ai-role-config.json
    /// entry). Same "mutate the live state after export, not just the file" discipline as the
    /// first pass's combined test, so a pass proves the in-memory caches were actually refreshed.</summary>
    [Fact]
    public async Task Export_then_import_round_trips_skills_custom_workflows_auto_fallback_and_remote_access_settings()
    {
        SkillManifest skill = _skills.CreateOrUpdateUserSkill(new SkillManifest
        {
            Name = "backup-marker-skill", DisplayName = "Backup Marker Skill", Description = "Marker."
        }, isImport: false);

        string workflowJson = """{"1":{"class_type":"SaveImage","inputs":{}}}""";
        CustomWorkflowInfo workflow = _customWorkflows.Import("Backup Marker Workflow", workflowJson);

        _aiProfile.SetAutoFallbackEnabled(false);

        _remoteAccess.ImportSettingsForBackup(new RemoteAccessService.RemoteAccessSettingsBackup { Enabled = true, Port = 55123 });

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Mutate the LIVE state to something else entirely after export.
        _skills.DeleteUserSkill(skill.Id);
        _skills.CreateOrUpdateUserSkill(new SkillManifest { Name = "decoy-skill", DisplayName = "Decoy", Description = "Decoy." }, isImport: false);
        _customWorkflows.Delete(workflow.Id);
        _customWorkflows.Import("Decoy Workflow", workflowJson);
        _aiProfile.SetAutoFallbackEnabled(true);
        _remoteAccess.ImportSettingsForBackup(new RemoteAccessService.RemoteAccessSettingsBackup { Enabled = false, Port = 9999 });

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Restored!.SkillsIncluded);
        Assert.Equal(1, result.Restored.UserSkillCount);
        Assert.True(result.Restored.CustomWorkflowsIncluded);
        Assert.Equal(1, result.Restored.CustomWorkflowCount);
        Assert.True(result.Restored.AutoFallbackSettingIncluded);
        Assert.True(result.Restored.RemoteAccessSettingsIncluded);

        List<SkillManifest> skills = _skills.List();
        Assert.Contains(skills, s => s.Id == skill.Id && s.DisplayName == "Backup Marker Skill");
        Assert.DoesNotContain(skills, s => s.DisplayName == "Decoy");

        List<CustomWorkflowInfo> workflows = _customWorkflows.List();
        Assert.Contains(workflows, w => w.Id == workflow.Id && w.Name == "Backup Marker Workflow");
        Assert.DoesNotContain(workflows, w => w.Name == "Decoy Workflow");

        Assert.False(_aiProfile.GetAutoFallbackEnabled());

        RemoteAccessStatus status = _remoteAccess.GetStatus();
        Assert.True(status.Enabled);
        Assert.Equal(55123, status.Port);
    }

    /// <summary>A backup taken before any of these four stores has ever been touched must not
    /// fabricate an entry for any of them on restore.</summary>
    [Fact]
    public async Task Backup_with_none_of_the_second_pass_stores_populated_restores_without_fabricating_any()
    {
        byte[] zip = await _backups.ExportAsync(CancellationToken.None);
        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        // skills.db is a real SQLite store like every other one here, so it's always included
        // once schema exists (an empty table, same convention as eval.db/adapters.db).
        Assert.True(result.Restored!.SkillsIncluded);
        Assert.Equal(0, result.Restored.UserSkillCount);
        Assert.False(result.Restored.CustomWorkflowsIncluded);
        Assert.False(result.Restored.AutoFallbackSettingIncluded);
        Assert.False(result.Restored.RemoteAccessSettingsIncluded);
    }

    [Fact]
    public async Task Export_then_import_round_trips_every_vNext_agent_task_database()
    {
        AgentTaskDto task = _tasks.Create(new CreateAgentTaskRequest { Goal = "Backup round-trip task marker" });
        ContextEntityDto entity = _contextGraph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project", Name = "Backup round-trip entity marker"
        });
        ArtifactDto artifact = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "text", Title = "Backup round-trip artifact marker", ContentRef = "inline:marker"
        });
        AutomationDto automation = _automations.Create(new CreateAutomationRequest
        {
            Name = "Backup round-trip automation marker",
            TriggerType = AutomationTriggerType.Schedule,
            TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{}"
        });
        AutopilotRunDto autopilotRun = _autopilotRuns.Create(
            "Backup round-trip autopilot marker", AutopilotPermissionMode.Supervised, "project-1",
            new List<string>(), AutopilotRunStatus.Running);
        EvalDatasetDto dataset = _evalLab.CreateDataset(new CreateEvalDatasetRequest { Name = "Backup round-trip dataset marker" });
        AdapterDto adapter = _adapters.Register(new RegisterAdapterRequest
        {
            Name = "Backup round-trip adapter marker", BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", AdapterPath = "C:\\fake\\adapter"
        });
        ExperimentDto experiment = _autoLab.Create(new ExperimentDto
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Backup round-trip experiment marker",
            DatasetId = dataset.Id, EvalRunId = "fake-run", ModelProfile = "STANDARD", ModelPath = "C:\\fake\\model.gguf",
            CreatedUtc = DateTime.UtcNow,
        });
        WorkflowDto workflow = _workflows.Create(new CreateWorkflowRequest
        {
            Name = "Backup round-trip workflow marker",
            Steps = new List<WorkflowStepTemplate> { new() { Name = "Step 1", Goal = "Do something" } },
        });
        CapabilityProfileRunDto profileRun = _capabilityProfiles.CreateRun(new CapabilityProfileRunDto
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = "C:\\fake\\model.gguf", Role = "fast",
            Status = CapabilityProfileRunStatus.Completed, StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
        });

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        // Wipe the (temp, synthetic) live databases entirely, simulating a fresh machine / lost
        // data - these stores have no public bulk-delete API, so this goes straight at the files,
        // same as BackupService.ReplaceDatabaseFile itself does on the way in.
        SqliteConnection.ClearAllPools();
        foreach (string dbName in new[] { "orchestrator.db", "context-graph.db", "artifacts.db", "automations.db", "autopilot.db", "eval.db", "adapters.db", "autolab.db", "workflows.db", "capability-profiles.db" })
        {
            string dbPath = Path.Combine(_serverDir, dbName);
            foreach (string candidate in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); } catch { /* best effort */ }
            }
        }

        using var zipStream = new MemoryStream(zip);
        BackupImportResult result = await _backups.ImportAsync(zipStream, CancellationToken.None, allowIncompleteRollbackSnapshot: true);

        Assert.True(result.Success);
        Assert.NotNull(result.Restored);
        Assert.Equal(1, result.Restored!.TaskCount);
        Assert.Equal(1, result.Restored.ContextGraphEntityCount);
        Assert.Equal(1, result.Restored.ArtifactCount);
        Assert.Equal(1, result.Restored.AutomationCount);
        Assert.Equal(1, result.Restored.AutopilotRunCount);
        Assert.Equal(1, result.Restored.EvalDatasetCount);
        Assert.Equal(1, result.Restored.AdapterCount);
        Assert.Equal(1, result.Restored.ExperimentCount);
        Assert.Equal(1, result.Restored.WorkflowCount);
        Assert.Equal(1, result.Restored.CapabilityProfileRunCount);

        Assert.Equal("Backup round-trip task marker", _tasks.Get(task.Id)?.Goal);
        Assert.Equal("Backup round-trip entity marker", _contextGraph.GetEntity(entity.Id)?.Name);
        Assert.Equal("Backup round-trip artifact marker", _artifacts.Get(artifact.Id)?.Title);
        Assert.Equal("Backup round-trip automation marker", _automations.Get(automation.Id)?.Name);
        Assert.Equal("Backup round-trip autopilot marker", _autopilotRuns.Get(autopilotRun.Id)?.Goal);
        Assert.Equal("Backup round-trip dataset marker", _evalLab.GetDataset(dataset.Id)?.Name);
        Assert.Equal("Backup round-trip adapter marker", _adapters.Get(adapter.Id)?.Name);
        Assert.Equal("Backup round-trip experiment marker", _autoLab.Get(experiment.Id)?.Name);
        Assert.Equal("Backup round-trip workflow marker", _workflows.Get(workflow.Id)?.Name);
        Assert.Equal("C:\\fake\\model.gguf", _capabilityProfiles.GetRun(profileRun.Id)?.ModelPath);
    }

    [Fact]
    public async Task Export_prefers_the_client_authoritative_history_db_over_the_servers_synced_replica()
    {
        // The server-side replica (ConversationStore) has stale content...
        _conversations.Save("conv-x", new ConversationUpsertRequest
        {
            Title = "Stale server replica", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, DocumentJson = "{}"
        });

        // ...but the client's own parux.db (what the desktop app's HistoryStore actually reads,
        // and pushes FROM - see HistoryStore.cs) has different, real content. A correct backup
        // must capture this copy, not the server's, since the server's is only a best-effort sync
        // target that can lag behind it.
        CreateMinimalConversationsDb(Path.Combine(_clientDir, "parux.db"), "conv-y", "Authoritative client copy");

        byte[] zip = await _backups.ExportAsync(CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry("conversations.db");
        Assert.NotNull(entry);

        string extractPath = Path.Combine(Path.GetTempPath(), "parux-backup-extract-" + Guid.NewGuid().ToString("N") + ".db");
        entry!.ExtractToFile(extractPath);
        try
        {
            var titles = new List<string>();
            using var connection = new SqliteConnection("Data Source=" + extractPath + ";Mode=ReadOnly");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT title FROM conversations;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) titles.Add(reader.GetString(0));

            Assert.Contains("Authoritative client copy", titles);
            Assert.DoesNotContain("Stale server replica", titles);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(extractPath)) File.Delete(extractPath);
        }
    }

    [Fact]
    public async Task Import_rejects_an_archive_from_a_newer_unsupported_format_version_without_touching_anything()
    {
        _conversations.Save("keep-me", new ConversationUpsertRequest
        {
            Title = "Untouched", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, DocumentJson = "{}"
        });

        byte[] zip = BuildManifestOnlyZip(new BackupManifest
        {
            FormatVersion = BackupService.CurrentFormatVersion + 998,
            CreatedUtc = DateTime.UtcNow,
            Contents = new BackupContents()
        });

        using var zipStream = new MemoryStream(zip);
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _backups.ImportAsync(zipStream, CancellationToken.None));
        Assert.Contains("newer version", ex.Message);

        // Validation happens before any swap - the conversation seeded above must be untouched.
        Assert.NotNull(_conversations.Get("keep-me"));
    }

    [Fact]
    public async Task Import_rejects_a_zero_or_negative_format_version_as_corrupt()
    {
        byte[] zip = BuildManifestOnlyZip(new BackupManifest { FormatVersion = 0, CreatedUtc = DateTime.UtcNow, Contents = new BackupContents() });
        using var zipStream = new MemoryStream(zip);
        await Assert.ThrowsAsync<InvalidDataException>(() => _backups.ImportAsync(zipStream, CancellationToken.None));
    }

    [Fact]
    public async Task Import_rejects_an_archive_with_no_manifest_entry()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("readme.txt");
            using Stream entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("this zip has no manifest.json, so it isn't a PaRuX backup");
        }
        zipStream.Position = 0;

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _backups.ImportAsync(zipStream, CancellationToken.None));
        Assert.Contains("manifest.json", ex.Message);
    }

    [Fact]
    public async Task Import_rejects_a_file_that_is_not_a_zip_archive_at_all()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("this is plain text, not a zip file, pretending to be a PaRuX backup");
        using var stream = new MemoryStream(garbage);
        await Assert.ThrowsAsync<InvalidDataException>(() => _backups.ImportAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Import_rejects_a_manifest_that_declares_conversations_but_the_db_entry_is_missing()
    {
        var manifest = new BackupManifest
        {
            FormatVersion = BackupService.CurrentFormatVersion,
            CreatedUtc = DateTime.UtcNow,
            Contents = new BackupContents { ConversationsIncluded = true }
        };
        byte[] zip = BuildManifestOnlyZip(manifest); // no conversations.db entry ever added
        using var zipStream = new MemoryStream(zip);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _backups.ImportAsync(zipStream, CancellationToken.None));
        Assert.Contains("conversations.db", ex.Message);
    }

    [Fact]
    public async Task Import_rejects_a_conversations_db_entry_that_is_not_actually_a_sqlite_file()
    {
        var manifest = new BackupManifest
        {
            FormatVersion = BackupService.CurrentFormatVersion,
            CreatedUtc = DateTime.UtcNow,
            Contents = new BackupContents { ConversationsIncluded = true }
        };

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            using (Stream manifestStream = manifestEntry.Open())
                JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            ZipArchiveEntry dbEntry = archive.CreateEntry("conversations.db");
            using Stream dbStream = dbEntry.Open();
            byte[] notReallyASqliteFile = Encoding.UTF8.GetBytes("this is not a sqlite database file");
            dbStream.Write(notReallyASqliteFile, 0, notReallyASqliteFile.Length);
        }
        stream.Position = 0;

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _backups.ImportAsync(stream, CancellationToken.None));
        Assert.Contains("conversations.db", ex.Message);
    }

    private static byte[] BuildManifestOnlyZip(BackupManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("manifest.json");
            using Stream entryStream = entry.Open();
            JsonSerializer.Serialize(entryStream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        return stream.ToArray();
    }

    /// <summary>Hand-builds a tiny SQLite file with the exact schema ConversationStore/HistoryStore
    /// both use, standing in for the client's real parux.db without touching HistoryStore itself
    /// (which is a PaRuX.App type this test project doesn't reference).</summary>
    private static void CreateMinimalConversationsDb(string path, string id, string title)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadWriteCreate");
        connection.Open();
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = @"CREATE TABLE conversations (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL, document_json TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO conversations (id, title, created_utc, updated_utc, document_json) VALUES ($id,$title,$c,$u,$j);";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$j", "{}");
        insert.ExecuteNonQuery();
    }
}
