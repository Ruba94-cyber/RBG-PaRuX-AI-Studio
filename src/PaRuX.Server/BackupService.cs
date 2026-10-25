using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.Automation;
using PaRuX.Server.Autopilot;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.AutoLab;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using PaRuX.Server.CapabilityProfiler;

namespace PaRuX.Server;

/// <summary>
/// Builds and restores the single "Backup &amp; Restore" .zip archive covering everything PaRuX
/// considers the user's own data: conversation history, Memory entries, RAG folder configuration
/// (not the indexed folder contents - those are the user's own files elsewhere on disk, and
/// re-indexing after restore is fine), the desktop client's app settings, and every vNext-era
/// agent/task database - the Multi-Agent Orchestrator's task graph (orchestrator.db), the Local
/// Context Graph (context-graph.db), Artifact Workspace metadata/version history (artifacts.db),
/// configured Automations (automations.db), Autopilot run history (autopilot.db), the Eval/
/// Regression Lab (eval.db), and the Adapter Manager registry (adapters.db). Every one of those
/// stores follows the exact same SQLite
/// BackupDatabaseTo/ReplaceDatabaseFile convention as MemoryStore/ConversationStore, so wiring them
/// in required no new persistence mechanism, just using the one that already existed - before this,
/// a restore silently orphaned all agent/task/artifact state even though it correctly brought back
/// chat/memory. Deliberately still excludes downloaded AI models, the ComfyUI/llama.cpp runtime,
/// RAG-indexed file contents, and the actual binary payload an Artifact version's ContentRef points
/// at: large, external, and either re-downloadable or already sitting untouched on the user's own
/// disk.
///
/// Ownership split this had to account for (see ConversationStore.cs and
/// PaRuX.App/Services/HistoryStore.cs for the full story): conversation history actually has TWO
/// stores. PaRuX.App's own HistoryStore (%LOCALAPPDATA%\PaRuX\parux.db) is the reliable,
/// always-available, authoritative copy the desktop app itself reads; PaRuX.Server's
/// ConversationStore (conversations.db, this process's own %LOCALAPPDATA%\PaRuX\server) is a
/// best-effort synced replica that exists purely so a paired Android device can see the same
/// history, and can lag behind if the server wasn't running when a save happened. A correct backup
/// has to use the authoritative client copy, not the possibly-stale server copy - so this service
/// reads/writes parux.db directly by its well-known path (server and client run as the same OS
/// user on the same machine) rather than treating conversation history as purely server-owned. On
/// restore, the same bytes are written into BOTH stores (they share an identical schema) so the
/// desktop app and its synced Android replica agree again immediately, without waiting for every
/// conversation to be individually re-pushed.
///
/// Memory and RAG source configuration have no such split - MemoryStore and RagStore are the only
/// copies, so those come from the server-owned singletons directly.
/// </summary>
public sealed class BackupService
{
    /// <summary>
    /// Bumped only when the archive's on-disk shape changes in a way an older PaRuX version
    /// could not read (a renamed/removed entry, an incompatible DB schema baked into the file).
    /// ImportAsync refuses any manifest whose FormatVersion isn't between 1 and this value.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
    private readonly string _clientRoot;
    private readonly string _modelsDirectory;

    /// <summary>Serializes restores (and therefore their snapshots and rollbacks): two concurrent restores would interleave
    /// store replacement and corrupt each other's rollback archives.</summary>
    private readonly SemaphoreSlim _restoreGate = new(1, 1);

    public BackupService(
        ConversationStore conversations, MemoryStore memory, RagStore rag,
        TaskStore tasks, ContextGraphStore contextGraph, ArtifactStore artifacts,
        AutomationStore automations, AutopilotStore autopilotRuns, EvalStore evalLab, AdapterStore adapters,
        AutoLabStore autoLab, WorkflowStore workflows, CapabilityProfilerStore capabilityProfiles,
        ModelBenchmarkStore modelBenchmarks, PersonaService personas, McpClientRegistry mcpClients,
        InternetSettingsService internetSettings, RemoteAccessService remoteAccess,
        SkillRegistryService skills, CustomWorkflowService customWorkflows, AiProfileService aiProfile)
        : this(conversations, memory, rag, tasks, contextGraph, artifacts, automations, autopilotRuns, evalLab, adapters,
              autoLab, workflows, capabilityProfiles, modelBenchmarks, personas, mcpClients, internetSettings,
              remoteAccess, skills, customWorkflows, aiProfile, DefaultClientRoot, ModelDownloadService.ModelsDirectory) { }

    /// <summary>
    /// Test-only entry point (see BackupServiceTests) so restore tests can point the client-owned
    /// portion (parux.db, settings.json) and the managed-model alias sidecar at isolated temp
    /// directories instead of the real %LOCALAPPDATA%\PaRuX / ModelsDirectory - restore is
    /// destructive, so a test must never be able to touch the user's live history, settings, or
    /// managed model library. Production code always goes through the shorter constructor above
    /// (the DI singleton registered in Program.cs).
    /// </summary>
    public BackupService(
        ConversationStore conversations, MemoryStore memory, RagStore rag,
        TaskStore tasks, ContextGraphStore contextGraph, ArtifactStore artifacts,
        AutomationStore automations, AutopilotStore autopilotRuns, EvalStore evalLab, AdapterStore adapters,
        AutoLabStore autoLab, WorkflowStore workflows, CapabilityProfilerStore capabilityProfiles,
        ModelBenchmarkStore modelBenchmarks, PersonaService personas, McpClientRegistry mcpClients,
        InternetSettingsService internetSettings, RemoteAccessService remoteAccess,
        SkillRegistryService skills, CustomWorkflowService customWorkflows, AiProfileService aiProfile,
        string clientRoot, string modelsDirectory)
    {
        _conversations = conversations;
        _memory = memory;
        _rag = rag;
        _tasks = tasks;
        _contextGraph = contextGraph;
        _artifacts = artifacts;
        _automations = automations;
        _autopilotRuns = autopilotRuns;
        _evalLab = evalLab;
        _adapters = adapters;
        _autoLab = autoLab;
        _workflows = workflows;
        _capabilityProfiles = capabilityProfiles;
        _modelBenchmarks = modelBenchmarks;
        _personas = personas;
        _mcpClients = mcpClients;
        _internetSettings = internetSettings;
        _remoteAccess = remoteAccess;
        _skills = skills;
        _customWorkflows = customWorkflows;
        _aiProfile = aiProfile;
        _clientRoot = clientRoot;
        _modelsDirectory = modelsDirectory;
    }

    /// <summary>%LOCALAPPDATA%\PaRuX - mirrors PaRuX.App/Services/AppPaths.PaRuXRoot. PaRuX.Server
    /// can't reference PaRuX.App directly (wrong dependency direction), so this is a deliberate,
    /// documented duplication of that one path convention rather than a new cross-project dependency.</summary>
    private static string DefaultClientRoot => PaRuX.Contracts.PaRuXDataRoot.Path;

    /// <summary>The client's history database. PaRuX.App renames the pre-rename file onto the new
    /// name on first use, so both spellings can legitimately be the live file depending on whether
    /// the App has started since the rename; back up whichever one is actually there.</summary>
    private string ClientHistoryDbPath
    {
        get
        {
            return Path.Combine(_clientRoot, "parux.db");
        }
    }
    private string ClientSettingsPath => Path.Combine(_clientRoot, "settings.json");

    public Task<byte[]> ExportAsync(CancellationToken ct) => ExportCoreAsync(ct, null);

    /// <summary>
    /// Exports every store. With <paramref name="unsnapshottable"/> == null any failing store aborts the export
    /// (the user-facing backup must never silently omit data). With a list, a store that cannot be exported
    /// (missing/corrupt) is recorded by name and omitted from the archive and manifest instead; this is used only
    /// for the pre-restore safety snapshot, which must work even when the live data is what is being repaired.
    /// </summary>
    private async Task<byte[]> ExportCoreAsync(CancellationToken ct, List<string>? unsnapshottable)
    {
        bool TrySnapshot(string label, Action snapshot)
        {
            if (unsnapshottable == null) { snapshot(); return true; }
            try { snapshot(); return true; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { unsnapshottable.Add(label); return false; }
        }

        string workDir = Path.Combine(Path.GetTempPath(), "parux-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var manifest = new BackupManifest
            {
                FormatVersion = CurrentFormatVersion,
                CreatedUtc = DateTime.UtcNow,
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                MachineName = Environment.MachineName
            };

            ct.ThrowIfCancellationRequested();
            string conversationsTemp = Path.Combine(workDir, "conversations.db");
            bool conversationsOk = TrySnapshot("conversation history (conversations.db)", () =>
            {
                if (File.Exists(ClientHistoryDbPath))
                    BackupSqliteFileTo(ClientHistoryDbPath, conversationsTemp);
                else
                    _conversations.BackupDatabaseTo(conversationsTemp);
                manifest.Contents.ConversationCount = CountRows(conversationsTemp, "conversations");
                manifest.Contents.ConversationsIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string memoryTemp = Path.Combine(workDir, "memory.db");
            bool memoryOk = TrySnapshot("Memory (memory.db)", () =>
            {
                _memory.BackupDatabaseTo(memoryTemp);
                manifest.Contents.MemoryCount = _memory.List().Count;
                manifest.Contents.MemoryIncluded = true;
            });

            List<RagSourceDto>? ragSources = null;
            TrySnapshot("RAG folder configuration", () =>
            {
                List<RagSourceDto> listed = _rag.ListSources();
                manifest.Contents.RagSourceCount = listed.Count;
                manifest.Contents.RagSourcesIncluded = true;
                ragSources = listed;
            });

            bool hasSettings = File.Exists(ClientSettingsPath);
            manifest.Contents.SettingsIncluded = hasSettings;

            ct.ThrowIfCancellationRequested();
            string tasksTemp = Path.Combine(workDir, "orchestrator.db");
            bool tasksOk = TrySnapshot("orchestrator.db", () =>
            {
                _tasks.BackupDatabaseTo(tasksTemp);
                manifest.Contents.TaskCount = CountRows(tasksTemp, "agent_tasks");
                manifest.Contents.TasksIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string contextGraphTemp = Path.Combine(workDir, "context-graph.db");
            bool contextGraphOk = TrySnapshot("context-graph.db", () =>
            {
                _contextGraph.BackupDatabaseTo(contextGraphTemp);
                manifest.Contents.ContextGraphEntityCount = CountRows(contextGraphTemp, "context_entities");
                manifest.Contents.ContextGraphIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string artifactsTemp = Path.Combine(workDir, "artifacts.db");
            bool artifactsOk = TrySnapshot("artifacts.db", () =>
            {
                _artifacts.BackupDatabaseTo(artifactsTemp);
                manifest.Contents.ArtifactCount = CountRows(artifactsTemp, "artifacts");
                manifest.Contents.ArtifactsIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string automationsTemp = Path.Combine(workDir, "automations.db");
            bool automationsOk = TrySnapshot("automations.db", () =>
            {
                _automations.BackupDatabaseTo(automationsTemp);
                manifest.Contents.AutomationCount = CountRows(automationsTemp, "automations");
                manifest.Contents.AutomationsIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string autopilotTemp = Path.Combine(workDir, "autopilot.db");
            bool autopilotOk = TrySnapshot("autopilot.db", () =>
            {
                _autopilotRuns.BackupDatabaseTo(autopilotTemp);
                manifest.Contents.AutopilotRunCount = CountRows(autopilotTemp, "autopilot_runs");
                manifest.Contents.AutopilotRunsIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string evalTemp = Path.Combine(workDir, "eval.db");
            bool evalOk = TrySnapshot("eval.db", () =>
            {
                _evalLab.BackupDatabaseTo(evalTemp);
                manifest.Contents.EvalDatasetCount = CountRows(evalTemp, "eval_datasets");
                manifest.Contents.EvalIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string adaptersTemp = Path.Combine(workDir, "adapters.db");
            bool adaptersOk = TrySnapshot("adapters.db", () =>
            {
                _adapters.BackupDatabaseTo(adaptersTemp);
                manifest.Contents.AdapterCount = CountRows(adaptersTemp, "adapters");
                manifest.Contents.AdaptersIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string autoLabTemp = Path.Combine(workDir, "autolab.db");
            bool autoLabOk = TrySnapshot("autolab.db", () =>
            {
                _autoLab.BackupDatabaseTo(autoLabTemp);
                manifest.Contents.ExperimentCount = CountRows(autoLabTemp, "autolab_experiments");
                manifest.Contents.AutoLabIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string workflowsTemp = Path.Combine(workDir, "workflows.db");
            bool workflowsOk = TrySnapshot("workflows.db", () =>
            {
                _workflows.BackupDatabaseTo(workflowsTemp);
                manifest.Contents.WorkflowCount = CountRows(workflowsTemp, "workflows");
                manifest.Contents.WorkflowsIncluded = true;
            });

            ct.ThrowIfCancellationRequested();
            string capabilityProfilesTemp = Path.Combine(workDir, "capability-profiles.db");
            bool capabilityProfilesOk = TrySnapshot("capability-profiles.db", () =>
            {
                _capabilityProfiles.BackupDatabaseTo(capabilityProfilesTemp);
                manifest.Contents.CapabilityProfileRunCount = CountRows(capabilityProfilesTemp, "profile_runs");
                manifest.Contents.CapabilityProfilesIncluded = true;
            });

            Dictionary<string, string> localAiRoleConfig = LocalAiPaths.ExportBackupConfig(_clientRoot);
            manifest.Contents.LocalAiRoleConfigIncluded = localAiRoleConfig.Count > 0;

            ct.ThrowIfCancellationRequested();
            string modelBenchmarksTemp = Path.Combine(workDir, "model-benchmarks.db");
            bool modelBenchmarksOk = TrySnapshot("model-benchmarks.db", () =>
            {
                _modelBenchmarks.BackupDatabaseTo(modelBenchmarksTemp);
                manifest.Contents.ModelBenchmarkRunCount = CountRows(modelBenchmarksTemp, "benchmark_runs");
                manifest.Contents.ModelBenchmarksIncluded = true;
            });

            PersonaService.PersonaStoreDocument? personas = _personas.ExportForBackup();
            manifest.Contents.PersonasIncluded = personas != null;
            manifest.Contents.CustomPersonaCount = personas?.Custom?.Count ?? 0;

            List<McpServerConfig>? mcpClients = _mcpClients.ExportForBackup();
            manifest.Contents.McpClientsIncluded = mcpClients != null;
            manifest.Contents.McpClientCount = mcpClients?.Count ?? 0;

            bool? internetSetting = _internetSettings.ExportForBackup();
            manifest.Contents.InternetSettingIncluded = internetSetting.HasValue;

            Dictionary<string, string>? modelAliases = ModelDownloadService.ExportAliasesForBackup(_modelsDirectory);
            manifest.Contents.ModelAliasesIncluded = modelAliases != null;
            manifest.Contents.ModelAliasCount = modelAliases?.Count ?? 0;

            Dictionary<string, string>? modelRepoIds = ModelDownloadService.ExportRepoIdsForBackup(_modelsDirectory);
            manifest.Contents.ModelRepoIdsIncluded = modelRepoIds != null;
            manifest.Contents.ModelRepoIdCount = modelRepoIds?.Count ?? 0;

            ModelProvenanceManifestDocument? modelProvenance = ModelDownloadService.ExportModelProvenanceForBackup(_modelsDirectory);
            manifest.Contents.ModelProvenanceIncluded = modelProvenance != null;
            manifest.Contents.ModelProvenanceEntryCount = modelProvenance?.Entries.Count ?? 0;

            List<RemoteAccessService.PairedDevice>? pairedDevices = _remoteAccess.ExportDevicesForBackup();
            manifest.Contents.PairedDevicesIncluded = pairedDevices != null;
            manifest.Contents.PairedDeviceCount = pairedDevices?.Count ?? 0;

            ct.ThrowIfCancellationRequested();
            string skillsTemp = Path.Combine(workDir, "skills.db");
            bool skillsOk = TrySnapshot("skills.db", () =>
            {
                _skills.BackupDatabaseTo(skillsTemp);
                manifest.Contents.UserSkillCount = CountRows(skillsTemp, "user_skills");
                manifest.Contents.SkillsIncluded = true;
            });

            CustomWorkflowService.CustomWorkflowBackupBundle? customWorkflows = _customWorkflows.ExportForBackup();
            manifest.Contents.CustomWorkflowsIncluded = customWorkflows != null;
            manifest.Contents.CustomWorkflowCount = customWorkflows?.Index.Count ?? 0;

            bool? autoFallbackSetting = _aiProfile.ExportAutoFallbackForBackup();
            manifest.Contents.AutoFallbackSettingIncluded = autoFallbackSetting.HasValue;

            RemoteAccessService.RemoteAccessSettingsBackup? remoteAccessSettings = _remoteAccess.ExportSettingsForBackup();
            manifest.Contents.RemoteAccessSettingsIncluded = remoteAccessSettings != null;

            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteJsonEntryAsync(archive, "manifest.json", manifest, ct).ConfigureAwait(false);
                if (conversationsOk) await WriteFileEntryAsync(archive, "conversations.db", conversationsTemp, ct).ConfigureAwait(false);
                if (memoryOk) await WriteFileEntryAsync(archive, "memory.db", memoryTemp, ct).ConfigureAwait(false);
                if (ragSources != null) await WriteJsonEntryAsync(archive, "rag-sources.json", ragSources, ct).ConfigureAwait(false);
                if (hasSettings) await WriteFileEntryAsync(archive, "settings.json", ClientSettingsPath, ct).ConfigureAwait(false);
                if (tasksOk) await WriteFileEntryAsync(archive, "orchestrator.db", tasksTemp, ct).ConfigureAwait(false);
                if (contextGraphOk) await WriteFileEntryAsync(archive, "context-graph.db", contextGraphTemp, ct).ConfigureAwait(false);
                if (artifactsOk) await WriteFileEntryAsync(archive, "artifacts.db", artifactsTemp, ct).ConfigureAwait(false);
                if (automationsOk) await WriteFileEntryAsync(archive, "automations.db", automationsTemp, ct).ConfigureAwait(false);
                if (autopilotOk) await WriteFileEntryAsync(archive, "autopilot.db", autopilotTemp, ct).ConfigureAwait(false);
                if (evalOk) await WriteFileEntryAsync(archive, "eval.db", evalTemp, ct).ConfigureAwait(false);
                if (adaptersOk) await WriteFileEntryAsync(archive, "adapters.db", adaptersTemp, ct).ConfigureAwait(false);
                if (autoLabOk) await WriteFileEntryAsync(archive, "autolab.db", autoLabTemp, ct).ConfigureAwait(false);
                if (workflowsOk) await WriteFileEntryAsync(archive, "workflows.db", workflowsTemp, ct).ConfigureAwait(false);
                if (capabilityProfilesOk) await WriteFileEntryAsync(archive, "capability-profiles.db", capabilityProfilesTemp, ct).ConfigureAwait(false);
                if (manifest.Contents.LocalAiRoleConfigIncluded)
                    await WriteJsonEntryAsync(archive, "local-ai-role-config.json", localAiRoleConfig, ct).ConfigureAwait(false);
                if (modelBenchmarksOk) await WriteFileEntryAsync(archive, "model-benchmarks.db", modelBenchmarksTemp, ct).ConfigureAwait(false);
                if (personas != null) await WriteJsonEntryAsync(archive, "personas.json", personas, ct).ConfigureAwait(false);
                if (mcpClients != null) await WriteJsonEntryAsync(archive, "mcp-clients.json", mcpClients, ct).ConfigureAwait(false);
                if (internetSetting.HasValue) await WriteJsonEntryAsync(archive, "internet.json", internetSetting.Value, ct).ConfigureAwait(false);
                if (modelAliases != null) await WriteJsonEntryAsync(archive, "model-aliases.json", modelAliases, ct).ConfigureAwait(false);
                if (modelRepoIds != null) await WriteJsonEntryAsync(archive, "model-repo-ids.json", modelRepoIds, ct).ConfigureAwait(false);
                if (modelProvenance != null) await WriteJsonEntryAsync(archive, "model-provenance.json", modelProvenance, ct).ConfigureAwait(false);
                if (pairedDevices != null) await WriteJsonEntryAsync(archive, "paired-devices.json", pairedDevices, ct).ConfigureAwait(false);
                if (skillsOk) await WriteFileEntryAsync(archive, "skills.db", skillsTemp, ct).ConfigureAwait(false);
                if (customWorkflows != null) await WriteJsonEntryAsync(archive, "custom-workflows.json", customWorkflows, ct).ConfigureAwait(false);
                if (autoFallbackSetting.HasValue) await WriteJsonEntryAsync(archive, "auto-fallback.json", autoFallbackSetting.Value, ct).ConfigureAwait(false);
                if (remoteAccessSettings != null) await WriteJsonEntryAsync(archive, "remote-access-settings.json", remoteAccessSettings, ct).ConfigureAwait(false);
            }
            return zipStream.ToArray();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>Pre-restore snapshots kept under <c>&lt;client root&gt;\backups</c> (newest first).</summary>
    internal const int PreRestoreSnapshotsToKeep = 3;

    /// <summary>Directory for automatic pre-restore snapshots. It lives beside the client data, not under
    /// the model/dataset/runtime directories that AiStorageBudget measures, so it never counts against the AI
    /// storage quota.</summary>
    private string PreRestoreDirectory => Path.Combine(_clientRoot, "backups");

    /// <summary>
    /// Validate-then-swap restore with automatic rollback. After validation and the lock preflight the
    /// CURRENT state is exported to a pre-restore archive (abort with nothing changed if that fails). If any
    /// replace step then fails, the stores are restored from that archive (a rollback is never itself rolled back)
    /// and the error reports the rollback outcome and the archive path.
    /// </summary>
    public async Task<BackupImportResult> ImportAsync(
        Stream zipContent, CancellationToken ct, bool allowIncompleteRollbackSnapshot = false)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ImportGatedAsync(zipContent, ct, allowIncompleteRollbackSnapshot).ConfigureAwait(false);
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private async Task<BackupImportResult> ImportGatedAsync(
        Stream zipContent, CancellationToken ct, bool allowIncompleteRollbackSnapshot)
    {
        string? snapshotPath = null;
        IReadOnlyList<string> missing = Array.Empty<string>();
        try
        {
            BackupImportResult done = await ImportCoreAsync(
                zipContent, ct, takeSnapshot: true, allowIncompleteRollbackSnapshot,
                (path, unsnapshottable) => { snapshotPath = path; missing = unsnapshottable; }).ConfigureAwait(false);
            done.RollbackSnapshotPath = snapshotPath;
            done.UnsnapshottableStores = missing.ToList();
            return done;
        }
        catch (RestoreCommitException failure) when (snapshotPath != null)
        {
            string outcome;
            try
            {
                await using FileStream rollback = File.OpenRead(snapshotPath);
                await ImportCoreAsync(rollback, CancellationToken.None, takeSnapshot: false, false, null).ConfigureAwait(false);
                outcome = missing.Count == 0
                    ? "Rolled back automatically: every restored part was returned to its pre-restore content."
                    : "Rolled back automatically from a PARTIAL snapshot. These stores could not be snapshotted and CANNOT be rolled back: "
                      + string.Join(", ", missing) + ".";
            }
            catch (Exception rollbackError)
            {
                outcome = "AUTOMATIC ROLLBACK ALSO FAILED (" + rollbackError.Message + "). Some parts may hold restored " +
                    "content; restore the pre-restore archive manually.";
            }
            throw new IOException(
                failure.Message + " " + outcome + " Pre-restore archive: " + snapshotPath, failure);
        }
    }

    /// <summary>Restore refused because some live stores cannot be exported for the rollback snapshot and the
    /// caller did not opt in. Nothing was changed.</summary>
    public sealed class IncompleteRollbackSnapshotException : IOException
    {
        public IReadOnlyList<string> UnsnapshottableStores { get; }

        public IncompleteRollbackSnapshotException(IReadOnlyList<string> stores)
            : base("The restore was not started and nothing was changed: these live stores cannot be exported for the " +
                   "automatic rollback snapshot (missing or corrupt): " + string.Join(", ", stores) +
                   ". Restoring anyway means they cannot be rolled back if a later step fails. Confirm explicitly " +
                   "(allowIncompleteRollbackSnapshot=true) to proceed.")
        {
            UnsnapshottableStores = stores;
        }
    }

    private sealed class RestoreCommitException : IOException
    {
        public RestoreCommitException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Creates the snapshot directory readable only by the current user and SYSTEM (inheritance off). Snapshots hold
    /// the user's full data, so a permissive inherited ACL would be a privacy regression. Fails closed: if the
    /// restriction cannot be applied, no snapshot is written and the restore does not start.
    /// </summary>
    private void EnsurePrivateSnapshotDirectory()
    {
        Directory.CreateDirectory(PreRestoreDirectory);
        if (!OperatingSystem.IsWindows()) return;
        var info = new DirectoryInfo(PreRestoreDirectory);
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        System.Security.Principal.SecurityIdentifier me = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new IOException("Cannot determine the current user to restrict the safety snapshot folder.");
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        foreach (System.Security.Principal.SecurityIdentifier sid in new[] { me, system })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                sid, System.Security.AccessControl.FileSystemRights.FullControl, inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Deletes every automatic pre-restore safety snapshot. Waits for any restore in progress (its rollback
    /// source must not vanish). Returns how many files were removed.</summary>
    public async Task<int> ClearPreRestoreSnapshotsAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(PreRestoreDirectory)) return 0;
            int removed = 0;
            foreach (string file in Directory.GetFiles(PreRestoreDirectory, "pre-restore-*"))
            {
                File.Delete(file);
                removed++;
            }
            return removed;
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private async Task<(string Path, IReadOnlyList<string> Missing)> CreatePreRestoreSnapshotAsync(
        CancellationToken ct, bool allowIncomplete)
    {
        var missing = new List<string>();
        string finalPath = Path.Combine(PreRestoreDirectory, "pre-restore-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "Z.zip");
        string temp = finalPath + ".tmp";
        try
        {
            byte[] snapshot = await ExportCoreAsync(ct, missing).ConfigureAwait(false);
            if (missing.Count > 0 && !allowIncomplete) throw new IncompleteRollbackSnapshotException(missing.ToList());
            EnsurePrivateSnapshotDirectory();
            await File.WriteAllBytesAsync(temp, snapshot, ct).ConfigureAwait(false);
            File.Move(temp, finalPath, overwrite: false);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (ex is OperationCanceledException or IncompleteRollbackSnapshotException) throw;
            throw new IOException(
                "Could not create the pre-restore safety snapshot, so the restore was not started and nothing was changed: " + ex.Message, ex);
        }

        try
        {
            // Newest by write time, and the snapshot just created is never a candidate (it is the rollback source).
            foreach (string old in Directory.GetFiles(PreRestoreDirectory, "pre-restore-*.zip")
                         .Where(f => !string.Equals(f, finalPath, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                         .Skip(PreRestoreSnapshotsToKeep - 1))
                File.Delete(old);
        }
        catch { /* retention is best effort; the new snapshot is already safe */ }
        return (finalPath, missing);
    }

    private async Task<BackupImportResult> ImportCoreAsync(
        Stream zipContent, CancellationToken ct, bool takeSnapshot, bool allowIncompleteRollbackSnapshot,
        Action<string, IReadOnlyList<string>>? snapshotCreated)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "parux-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            ct.ThrowIfCancellationRequested();
            string zipTemp = Path.Combine(workDir, "upload.zip");
            using (FileStream fileStream = File.Create(zipTemp))
                await zipContent.CopyToAsync(fileStream, ct).ConfigureAwait(false);

            using ZipArchive archive = OpenArchiveOrThrow(zipTemp);

            BackupManifest manifest = await ReadManifestAsync(archive, ct).ConfigureAwait(false);
            ValidateFormatVersion(manifest);

            string? conversationsPath = null;
            string? memoryPath = null;
            List<RagSourceDto>? ragSources = null;
            string? settingsJson = null;
            string? tasksPath = null;
            string? contextGraphPath = null;
            string? artifactsPath = null;
            string? automationsPath = null;
            string? autopilotPath = null;
            string? evalPath = null;
            string? adaptersPath = null;
            string? autoLabPath = null;
            string? workflowsPath = null;
            string? capabilityProfilesPath = null;
            Dictionary<string, string>? localAiRoleConfig = null;
            string? modelBenchmarksPath = null;
            PersonaService.PersonaStoreDocument? personas = null;
            List<McpServerConfig>? mcpClients = null;
            bool? internetSetting = null;
            Dictionary<string, string>? modelAliases = null;
            Dictionary<string, string>? modelRepoIds = null;
            ModelProvenanceManifestDocument? modelProvenance = null;
            List<RemoteAccessService.PairedDevice>? pairedDevices = null;
            string? skillsPath = null;
            CustomWorkflowService.CustomWorkflowBackupBundle? customWorkflows = null;
            bool? autoFallbackSetting = null;
            RemoteAccessService.RemoteAccessSettingsBackup? remoteAccessSettings = null;

            if (manifest.Contents.ConversationsIncluded)
            {
                conversationsPath = Path.Combine(workDir, "conversations.db");
                ExtractEntryOrThrow(archive, "conversations.db", conversationsPath);
                ValidateSqliteFileOrThrow(conversationsPath, "conversations.db");
            }
            if (manifest.Contents.MemoryIncluded)
            {
                memoryPath = Path.Combine(workDir, "memory.db");
                ExtractEntryOrThrow(archive, "memory.db", memoryPath);
                ValidateSqliteFileOrThrow(memoryPath, "memory.db");
            }
            if (manifest.Contents.RagSourcesIncluded)
            {
                ZipArchiveEntry ragEntry = archive.GetEntry("rag-sources.json")
                    ?? throw new InvalidDataException("The backup manifest lists RAG sources but rag-sources.json is missing from the archive.");
                EnsureEntryWithinCap(ragEntry, MaximumJsonEntryBytes);
                await using Stream ragStream = ragEntry.Open();
                ragSources = await JsonSerializer.DeserializeAsync<List<RagSourceDto>>(ragStream, JsonOptions, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("rag-sources.json in this backup is corrupt.");
            }
            if (manifest.Contents.SettingsIncluded)
            {
                ZipArchiveEntry settingsEntry = archive.GetEntry("settings.json")
                    ?? throw new InvalidDataException("The backup manifest lists settings but settings.json is missing from the archive.");
                EnsureEntryWithinCap(settingsEntry, MaximumJsonEntryBytes);
                using var reader = new StreamReader(settingsEntry.Open());
                settingsJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                try { using JsonDocument _ = JsonDocument.Parse(settingsJson); }
                catch (JsonException ex) { throw new InvalidDataException("settings.json in this backup is corrupt: " + ex.Message); }
            }
            if (manifest.Contents.TasksIncluded)
            {
                tasksPath = Path.Combine(workDir, "orchestrator.db");
                ExtractEntryOrThrow(archive, "orchestrator.db", tasksPath);
                ValidateSqliteFileOrThrow(tasksPath, "orchestrator.db");
            }
            if (manifest.Contents.ContextGraphIncluded)
            {
                contextGraphPath = Path.Combine(workDir, "context-graph.db");
                ExtractEntryOrThrow(archive, "context-graph.db", contextGraphPath);
                ValidateSqliteFileOrThrow(contextGraphPath, "context-graph.db");
            }
            if (manifest.Contents.ArtifactsIncluded)
            {
                artifactsPath = Path.Combine(workDir, "artifacts.db");
                ExtractEntryOrThrow(archive, "artifacts.db", artifactsPath);
                ValidateSqliteFileOrThrow(artifactsPath, "artifacts.db");
            }
            if (manifest.Contents.AutomationsIncluded)
            {
                automationsPath = Path.Combine(workDir, "automations.db");
                ExtractEntryOrThrow(archive, "automations.db", automationsPath);
                ValidateSqliteFileOrThrow(automationsPath, "automations.db");
            }
            if (manifest.Contents.AutopilotRunsIncluded)
            {
                autopilotPath = Path.Combine(workDir, "autopilot.db");
                ExtractEntryOrThrow(archive, "autopilot.db", autopilotPath);
                ValidateSqliteFileOrThrow(autopilotPath, "autopilot.db");
            }
            if (manifest.Contents.EvalIncluded)
            {
                evalPath = Path.Combine(workDir, "eval.db");
                ExtractEntryOrThrow(archive, "eval.db", evalPath);
                ValidateSqliteFileOrThrow(evalPath, "eval.db");
            }
            if (manifest.Contents.AdaptersIncluded)
            {
                adaptersPath = Path.Combine(workDir, "adapters.db");
                ExtractEntryOrThrow(archive, "adapters.db", adaptersPath);
                ValidateSqliteFileOrThrow(adaptersPath, "adapters.db");
            }
            if (manifest.Contents.AutoLabIncluded)
            {
                autoLabPath = Path.Combine(workDir, "autolab.db");
                ExtractEntryOrThrow(archive, "autolab.db", autoLabPath);
                ValidateSqliteFileOrThrow(autoLabPath, "autolab.db");
            }
            if (manifest.Contents.WorkflowsIncluded)
            {
                workflowsPath = Path.Combine(workDir, "workflows.db");
                ExtractEntryOrThrow(archive, "workflows.db", workflowsPath);
                ValidateSqliteFileOrThrow(workflowsPath, "workflows.db");
            }
            if (manifest.Contents.CapabilityProfilesIncluded)
            {
                capabilityProfilesPath = Path.Combine(workDir, "capability-profiles.db");
                ExtractEntryOrThrow(archive, "capability-profiles.db", capabilityProfilesPath);
                ValidateSqliteFileOrThrow(capabilityProfilesPath, "capability-profiles.db");
            }
            if (manifest.Contents.LocalAiRoleConfigIncluded)
            {
                ZipArchiveEntry configEntry = archive.GetEntry("local-ai-role-config.json")
                    ?? throw new InvalidDataException("The backup manifest lists local AI role config but local-ai-role-config.json is missing from the archive.");
                EnsureEntryWithinCap(configEntry, MaximumJsonEntryBytes);
                await using Stream configStream = configEntry.Open();
                localAiRoleConfig = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(configStream, JsonOptions, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("local-ai-role-config.json in this backup is corrupt.");
                try { LocalAiPaths.ValidateBackupConfig(localAiRoleConfig); }
                catch (JsonException ex) { throw new InvalidDataException("local-ai-role-config.json in this backup is corrupt: " + ex.Message); }
            }
            if (manifest.Contents.ModelBenchmarksIncluded)
            {
                modelBenchmarksPath = Path.Combine(workDir, "model-benchmarks.db");
                ExtractEntryOrThrow(archive, "model-benchmarks.db", modelBenchmarksPath);
                ValidateSqliteFileOrThrow(modelBenchmarksPath, "model-benchmarks.db");
            }
            if (manifest.Contents.PersonasIncluded)
                personas = await ReadJsonEntryOrThrow<PersonaService.PersonaStoreDocument>(archive, "personas.json", ct).ConfigureAwait(false);
            if (manifest.Contents.McpClientsIncluded)
                mcpClients = await ReadJsonEntryOrThrow<List<McpServerConfig>>(archive, "mcp-clients.json", ct).ConfigureAwait(false);
            if (manifest.Contents.InternetSettingIncluded)
                internetSetting = await ReadJsonEntryOrThrow<bool>(archive, "internet.json", ct).ConfigureAwait(false);
            if (manifest.Contents.ModelAliasesIncluded)
                modelAliases = await ReadJsonEntryOrThrow<Dictionary<string, string>>(archive, "model-aliases.json", ct).ConfigureAwait(false);
            if (manifest.Contents.ModelRepoIdsIncluded)
                modelRepoIds = await ReadJsonEntryOrThrow<Dictionary<string, string>>(archive, "model-repo-ids.json", ct).ConfigureAwait(false);
            if (manifest.Contents.ModelProvenanceIncluded)
            {
                modelProvenance = await ReadJsonEntryOrThrow<ModelProvenanceManifestDocument>(archive, "model-provenance.json", ct).ConfigureAwait(false);
                ModelDownloadService.ValidateModelProvenanceBackup(modelProvenance);
            }
            if (manifest.Contents.PairedDevicesIncluded)
                pairedDevices = await ReadJsonEntryOrThrow<List<RemoteAccessService.PairedDevice>>(archive, "paired-devices.json", ct).ConfigureAwait(false);
            if (manifest.Contents.SkillsIncluded)
            {
                skillsPath = Path.Combine(workDir, "skills.db");
                ExtractEntryOrThrow(archive, "skills.db", skillsPath);
                ValidateSqliteFileOrThrow(skillsPath, "skills.db");
            }
            if (manifest.Contents.CustomWorkflowsIncluded)
                customWorkflows = await ReadJsonEntryOrThrow<CustomWorkflowService.CustomWorkflowBackupBundle>(archive, "custom-workflows.json", ct).ConfigureAwait(false);
            if (manifest.Contents.AutoFallbackSettingIncluded)
                autoFallbackSetting = await ReadJsonEntryOrThrow<bool>(archive, "auto-fallback.json", ct).ConfigureAwait(false);
            if (manifest.Contents.RemoteAccessSettingsIncluded)
                remoteAccessSettings = await ReadJsonEntryOrThrow<RemoteAccessService.RemoteAccessSettingsBackup>(archive, "remote-access-settings.json", ct).ConfigureAwait(false);

            // Commit boundary. Everything above only read and validated the archive; nothing live
            // has been touched. Cancellation (e.g. the HTTP client disconnecting) is honoured up to
            // here and refused after it: the replace steps below span many independent stores, so
            // abandoning them midway would leave a mixture of restored and old data. A file the
            // restore cannot write (locked by another process) must be discovered now, before the
            // first store is replaced, so the restore fails closed with nothing changed.
            ct.ThrowIfCancellationRequested();
            ProbeClientHistoryWritableOrThrow();
            if (takeSnapshot)
            {
                (string snapshot, IReadOnlyList<string> unsnapshottable) =
                    await CreatePreRestoreSnapshotAsync(ct, allowIncompleteRollbackSnapshot).ConfigureAwait(false);
                snapshotCreated?.Invoke(snapshot, unsnapshottable);
            }
            CancellationToken commitCt = CancellationToken.None;
            var completedSteps = new List<string>();

            void RunStep(string label, Action step)
            {
                try
                {
                    step();
                    completedSteps.Add(label);
                    if (takeSnapshot) CommitStepObserver?.Invoke(label); // not during a rollback run
                }
                catch (Exception ex)
                {
                    throw new RestoreCommitException(
                        $"Restoring {label} failed: {ex.Message}" + DescribeCompletedSteps(completedSteps), ex);
                }
            }

            async Task RunStepAsync(string label, Func<Task> step)
            {
                try
                {
                    await step().ConfigureAwait(false);
                    completedSteps.Add(label);
                    if (takeSnapshot) CommitStepObserver?.Invoke(label);
                }
                catch (Exception ex)
                {
                    throw new RestoreCommitException(
                        $"Restoring {label} failed: {ex.Message}" + DescribeCompletedSteps(completedSteps), ex);
                }
            }

            if (conversationsPath != null)
            {
                RunStep("the server's conversation history", () => _conversations.ReplaceDatabaseFile(conversationsPath));
                RunStep("the desktop app's conversation history", () => ReplaceClientHistoryFile(conversationsPath));
            }
            if (memoryPath != null) RunStep("Memory", () => _memory.ReplaceDatabaseFile(memoryPath));
            if (ragSources != null) RunStep("RAG folder configuration", () => _rag.ReplaceSources(ragSources));
            if (settingsJson != null) RunStep("app settings", () => ReplaceClientSettingsFile(settingsJson));
            if (tasksPath != null) RunStep("the Multi-Agent task graph", () => _tasks.ReplaceDatabaseFile(tasksPath));
            if (contextGraphPath != null) RunStep("the Local Context Graph", () => _contextGraph.ReplaceDatabaseFile(contextGraphPath));
            if (artifactsPath != null) RunStep("the Artifact Workspace", () => _artifacts.ReplaceDatabaseFile(artifactsPath));
            if (automationsPath != null) RunStep("Automations", () => _automations.ReplaceDatabaseFile(automationsPath));
            if (autopilotPath != null) RunStep("Autopilot run history", () => _autopilotRuns.ReplaceDatabaseFile(autopilotPath));
            if (evalPath != null) RunStep("the Eval/Regression Lab", () => _evalLab.ReplaceDatabaseFile(evalPath));
            if (adaptersPath != null) RunStep("the Adapter Manager registry", () => _adapters.ReplaceDatabaseFile(adaptersPath));
            if (autoLabPath != null) RunStep("PaRuX AutoLab's experiments", () => _autoLab.ReplaceDatabaseFile(autoLabPath));
            if (workflowsPath != null) RunStep("Workflow Builder's templates", () => _workflows.ReplaceDatabaseFile(workflowsPath));
            if (capabilityProfilesPath != null) RunStep("the Capability Profiler's run history", () => _capabilityProfiles.ReplaceDatabaseFile(capabilityProfilesPath));
            if (localAiRoleConfig != null) RunStep("model role/candidate assignments", () => LocalAiPaths.ImportBackupConfig(localAiRoleConfig, _clientRoot));
            if (modelBenchmarksPath != null) RunStep("model benchmark history", () => _modelBenchmarks.ReplaceDatabaseFile(modelBenchmarksPath));
            if (personas != null) RunStep("Personas", () => _personas.ImportForBackup(personas));
            if (mcpClients != null) await RunStepAsync("configured MCP servers", () => _mcpClients.ImportForBackupAsync(mcpClients, commitCt)).ConfigureAwait(false);
            if (internetSetting.HasValue) RunStep("the internet-access setting", () => _internetSettings.ImportForBackup(internetSetting.Value));
            if (modelAliases != null) RunStep("model alias labels", () => ModelDownloadService.ImportAliasesForBackup(modelAliases, _modelsDirectory));
            if (modelRepoIds != null) RunStep("model repo-id labels", () => ModelDownloadService.ImportRepoIdsForBackup(modelRepoIds, _modelsDirectory));
            int? importedModelProvenanceCount = null;
            if (modelProvenance != null)
                await RunStepAsync("model provenance evidence", async () =>
                    importedModelProvenanceCount = await ModelDownloadService.ImportModelProvenanceForBackupAsync(
                        modelProvenance, _modelsDirectory, commitCt).ConfigureAwait(false))
                    .ConfigureAwait(false);
            if (pairedDevices != null) RunStep("paired LAN devices", () => _remoteAccess.ImportDevicesForBackup(pairedDevices));
            if (skillsPath != null) RunStep("user-created Skills", () => _skills.ReplaceDatabaseFile(skillsPath));
            if (customWorkflows != null) RunStep("custom ComfyUI workflows", () => _customWorkflows.ImportForBackup(customWorkflows));
            if (autoFallbackSetting.HasValue) RunStep("the auto-fallback setting", () => _aiProfile.ImportAutoFallbackForBackup(autoFallbackSetting.Value));
            if (remoteAccessSettings != null) RunStep("Remote Access settings", () => _remoteAccess.ImportSettingsForBackup(remoteAccessSettings));

            // Rollback only: a settings file that did not exist before the failed restore must not survive it.
            if (!takeSnapshot && !manifest.Contents.SettingsIncluded)
                RunStep("removal of app settings created by the failed restore", () =>
                {
                    if (File.Exists(ClientSettingsPath)) File.Delete(ClientSettingsPath);
                });

            if (importedModelProvenanceCount is int importedCount)
            {
                manifest.Contents.ModelProvenanceIncluded = importedCount > 0;
                manifest.Contents.ModelProvenanceEntryCount = importedCount;
            }

            return new BackupImportResult
            {
                Success = true,
                Message = "Backup restored successfully.",
                Restored = manifest.Contents
            };
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>Test seam: invoked after each restore step completes (see BackupResilienceTests).</summary>
    internal Action<string>? CommitStepObserver { get; set; }

    private static string DescribeCompletedSteps(IReadOnlyList<string> completed) =>
        completed.Count == 0
            ? " Nothing had been restored yet."
            : " Already restored before the failure: " + string.Join(", ", completed) +
              ". The remaining parts were not changed; restore the same backup again after fixing the cause.";

    /// <summary>
    /// Fails closed before any live store is replaced when the desktop client's history database is
    /// write-locked by another process or connection. Uses a short busy timeout and always rolls back.
    /// BEST EFFORT and limited to the client history DB: it is a point-in-time probe (the lock can appear
    /// right after it), and other stores/files are not probed. The pre-restore snapshot and automatic
    /// rollback are the safety net for anything the probe cannot foresee.
    /// </summary>
    private void ProbeClientHistoryWritableOrThrow()
    {
        string path = ClientHistoryDbPath;
        if (!File.Exists(path)) return;
        try
        {
            using var probe = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    DefaultTimeout = 2
                }.ToString());
            probe.Open();
            using SqliteCommand begin = probe.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            using SqliteCommand rollback = probe.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            throw new IOException(
                "The desktop app's conversation history is locked by another process, so the restore was " +
                "not started and nothing was changed. Close the other process or retry. (" + ex.Message + ")", ex);
        }
    }

    private static ZipArchive OpenArchiveOrThrow(string zipPath)
    {
        try { return ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("This file is not a valid .zip archive: " + ex.Message); }
    }

    private static async Task<T> ReadJsonEntryOrThrow<T>(ZipArchive archive, string entryName, CancellationToken ct)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"The backup manifest lists {entryName} but it is missing from the archive.");
        EnsureEntryWithinCap(entry, MaximumJsonEntryBytes);
        try
        {
            await using Stream stream = entry.Open();
            T? value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
            return value ?? throw new InvalidDataException($"{entryName} in this backup is corrupt.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{entryName} in this backup is corrupt: {ex.Message}");
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(ZipArchive archive, CancellationToken ct)
    {
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("This file is not a PaRuX backup (manifest.json is missing).");
        try
        {
            await using Stream manifestStream = manifestEntry.Open();
            return await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The backup manifest is empty or corrupt.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup manifest is corrupt: " + ex.Message);
        }
    }

    private static void ValidateFormatVersion(BackupManifest manifest)
    {
        if (manifest.FormatVersion < 1 || manifest.FormatVersion > CurrentFormatVersion)
        {
            string reason = manifest.FormatVersion > CurrentFormatVersion
                ? "a newer version of PaRuX"
                : "an unrecognized or corrupt version marker";
            throw new InvalidDataException(
                $"This backup was created by {reason} (format {manifest.FormatVersion}) and can't be " +
                $"restored by this version of PaRuX (supports up to format {CurrentFormatVersion}).");
        }
    }

    /// <summary>Cap for JSON entries, which are deserialized in memory.</summary>
    internal const long MaximumJsonEntryBytes = 256L * 1024 * 1024;

    internal static void EnsureEntryWithinCap(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length < 0 || entry.Length > maxBytes)
            throw new InvalidDataException(
                $"{entry.FullName} in this backup declares {entry.Length} uncompressed bytes, over the {maxBytes}-byte limit; refusing to extract it.");
    }

    internal static void ExtractEntryOrThrow(ZipArchive archive, string entryName, string destinationPath,
        long maxBytes = BackupImportGuardMiddleware.MaximumSingleEntryBytes)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"The backup manifest lists {entryName} but it is missing from the archive.");
        EnsureEntryWithinCap(entry, maxBytes);
        try { entry.ExtractToFile(destinationPath, overwrite: true); }
        catch (Exception ex) { throw new InvalidDataException($"Could not extract {entryName} from this backup: {ex.Message}"); }
    }

    private static void ValidateSqliteFileOrThrow(string path, string label)
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{label} in this backup is not a valid, readable database: {ex.Message}");
        }
    }

    private static int CountRows(string sqliteFilePath, string table)
    {
        using var connection = new SqliteConnection("Data Source=" + sqliteFilePath + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void BackupSqliteFileTo(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection("Data Source=" + sourcePath + ";Mode=ReadOnly;Pooling=False");
        source.Open();
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate");
        destination.Open();
        source.BackupDatabase(destination);
        SqlitePoolIsolation.ReleaseBackupDestination(destination);
    }

    private void ReplaceClientHistoryFile(string sourcePath)
    {
        Directory.CreateDirectory(_clientRoot);
        using var source = new SqliteConnection("Data Source=" + sourcePath + ";Mode=ReadOnly;Pooling=False");
        source.Open();
        using var destination = new SqliteConnection("Data Source=" + ClientHistoryDbPath + ";Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        destination.Open();
        source.BackupDatabase(destination);
    }

    private void ReplaceClientSettingsFile(string json)
    {
        Directory.CreateDirectory(_clientRoot);
        string temp = ClientSettingsPath + ".import-tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, ClientSettingsPath, overwrite: true);
    }

    private static async Task WriteFileEntryAsync(ZipArchive archive, string entryName, string sourcePath, CancellationToken ct)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await using FileStream source = File.OpenRead(sourcePath);
        await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string entryName, T value, CancellationToken ct)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, value, JsonOptions, ct).ConfigureAwait(false);
    }
}
