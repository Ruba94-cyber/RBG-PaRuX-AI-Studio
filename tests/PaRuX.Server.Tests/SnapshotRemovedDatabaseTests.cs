using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.AutoLab;
using PaRuX.Server.Automation;
using PaRuX.Server.Autopilot;
using PaRuX.Server.CapabilityProfiler;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.EvalLab;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A store that already initialized in this process must not snapshot a database file that was removed underneath
/// it. The normal path recreates the file and its tables (see SqliteSchemaRecreationTests), but a backup copied from
/// that recreated empty file would look like a complete store, and a restore would then pass silently. Each case
/// writes once through one instance, removes its database files, and expects the snapshot to be refused without
/// recreating anything.
/// </summary>
public sealed class SnapshotRemovedDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-snapshot-removed-" + Guid.NewGuid().ToString("N"));

    public SnapshotRemovedDatabaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    public static TheoryData<string> Stores => new()
    {
        nameof(ConversationStore),
        nameof(MemoryStore),
        nameof(AutomationStore),
        nameof(WorkflowStore),
        nameof(TaskStore),
        nameof(AdapterStore),
        nameof(EvalStore),
        nameof(ModelBenchmarkStore),
        nameof(AutopilotStore),
        nameof(ArtifactStore),
        nameof(ContextGraphStore),
        nameof(EngineRaceEvidenceStore),
        nameof(AutoLabStore),
        nameof(CapabilityProfilerStore),
        nameof(VisualContextV2Store),
        nameof(SkillRegistryService),
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void Snapshot_refuses_a_store_whose_database_file_was_removed_and_does_not_recreate_it(string storeName)
    {
        string dataDirectory = Path.Combine(_root, storeName);
        Directory.CreateDirectory(dataDirectory);
        (Action touch, Action<string> backup) = CreateStore(storeName, dataDirectory);
        touch(); // one write through the same instance sets its schema flag, as a running server does

        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(dataDirectory, "*.db*"))
            File.Delete(file);

        string snapshot = Path.Combine(_root, storeName + "-snapshot.db");
        Assert.Throws<FileNotFoundException>(() => backup(snapshot));
        Assert.Empty(Directory.GetFiles(dataDirectory, "*.db*"));
        Assert.False(File.Exists(snapshot));
    }

    // Every case keeps ONE store instance for both the write and the backup, because the schema flag lives on the instance.
    private static (Action Touch, Action<string> Backup) CreateStore(string storeName, string dataDirectory) => storeName switch
    {
        nameof(ConversationStore) => Pair(() => new ConversationStore(dataDirectory),
            store => store.Save(Guid.NewGuid().ToString("N"), new ConversationUpsertRequest
            {
                Title = "snapshot probe", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, DocumentJson = "{}",
            }),
            store => store.BackupDatabaseTo),
        nameof(MemoryStore) => Pair(() => new MemoryStore(new EmbeddingService(Path.Combine(dataDirectory, "embeddings")), dataDirectory),
            store => store.Add(new MemoryAddRequest { Content = "snapshot probe" }),
            store => store.BackupDatabaseTo),
        nameof(AutomationStore) => Pair(() => new AutomationStore(dataDirectory),
            store => store.Create(new CreateAutomationRequest
            {
                Name = "snapshot probe",
                TriggerType = AutomationTriggerType.Schedule,
                TriggerConfigJson = "{}",
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"snapshot probe\"}",
            }),
            store => store.BackupDatabaseTo),
        nameof(WorkflowStore) => Pair(() => new WorkflowStore(dataDirectory),
            store => store.Create(new CreateWorkflowRequest
            {
                Name = "snapshot probe",
                Steps = new List<WorkflowStepTemplate> { new() { Name = "Step 1", Goal = "Do something" } },
            }),
            store => store.BackupDatabaseTo),
        nameof(TaskStore) => Pair(() => new TaskStore(dataDirectory),
            store => store.Create(new CreateAgentTaskRequest { Goal = "snapshot probe" }),
            store => store.BackupDatabaseTo),
        nameof(AdapterStore) => Pair(() => new AdapterStore(dataDirectory),
            store => store.Register(new RegisterAdapterRequest
            {
                Name = "snapshot probe",
                BaseModel = "snapshot-probe-base",
                AdapterPath = Path.Combine(dataDirectory, "snapshot-probe.adapter"),
            }),
            store => store.BackupDatabaseTo),
        nameof(EvalStore) => Pair(() => new EvalStore(dataDirectory),
            store => store.CreateDataset(new CreateEvalDatasetRequest { Name = "snapshot probe" }),
            store => store.BackupDatabaseTo),
        nameof(ModelBenchmarkStore) => Pair(() => new ModelBenchmarkStore(dataDirectory),
            store => store.Create(Path.Combine(dataDirectory, "snapshot-probe.gguf"), "snapshot probe", automatic: false),
            store => store.BackupDatabaseTo),
        nameof(AutopilotStore) => Pair(() => new AutopilotStore(dataDirectory),
            store => store.Create("snapshot probe", "review", "snapshot-probe", new List<string>(), "queued"),
            store => store.BackupDatabaseTo),
        nameof(ArtifactStore) => Pair(() => new ArtifactStore(dataDirectory),
            store => store.Create(new CreateArtifactRequest
            {
                ArtifactType = "note",
                Title = "snapshot probe",
                ContentRef = "snapshot-probe",
            }),
            store => store.BackupDatabaseTo),
        nameof(ContextGraphStore) => Pair(() => new ContextGraphStore(dataDirectory),
            store => store.CreateEntity(new CreateContextEntityRequest
            {
                EntityType = "snapshot-probe",
                Name = "snapshot probe " + Guid.NewGuid().ToString("N"),
            }),
            store => store.BackupDatabaseTo),
        nameof(EngineRaceEvidenceStore) => Pair(() => new EngineRaceEvidenceStore(dataDirectory),
            store => store.List(),
            store => store.BackupDatabaseTo),
        nameof(AutoLabStore) => Pair(() => new AutoLabStore(dataDirectory),
            store => store.Create(new ExperimentDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "snapshot probe",
                DatasetId = "snapshot-probe-dataset",
                EvalRunId = "snapshot-probe-run",
                ModelProfile = "STANDARD",
                ModelPath = Path.Combine(dataDirectory, "snapshot-probe.gguf"),
                CreatedUtc = DateTime.UtcNow,
            }),
            store => store.BackupDatabaseTo),
        nameof(CapabilityProfilerStore) => Pair(() => new CapabilityProfilerStore(dataDirectory),
            store => store.CreateRun(new CapabilityProfileRunDto
            {
                Id = Guid.NewGuid().ToString("N"),
                ModelPath = Path.Combine(dataDirectory, "snapshot-probe.gguf"),
                Role = "fast",
                Status = CapabilityProfileRunStatus.Completed,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
            }),
            store => store.BackupDatabaseTo),
        nameof(VisualContextV2Store) => Pair(() => new VisualContextV2Store(dataDirectory),
            store => store.CreateSnapshot(new CreateVisualSnapshotRequest
            {
                Scope = "task",
                ScopeId = "snapshot-probe",
                Label = "snapshot probe",
                Source = "ui-verification",
                FileName = "snapshot-probe.png",
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            }),
            store => store.BackupDatabaseTo),
        nameof(SkillRegistryService) => Pair(() => new SkillRegistryService(dataDirectory),
            store => store.CreateOrUpdateUserSkill(new SkillManifest
            {
                DisplayName = "Snapshot probe",
                Category = "General",
            }, isImport: false),
            store => store.BackupDatabaseTo),
        _ => throw new ArgumentOutOfRangeException(nameof(storeName), storeName, "No snapshot case is defined for this store."),
    };

    private static (Action Touch, Action<string> Backup) Pair<T>(
        Func<T> create, Action<T> touch, Func<T, Action<string>> backup)
    {
        T store = create();
        return (() => touch(store), backup(store));
    }
}
