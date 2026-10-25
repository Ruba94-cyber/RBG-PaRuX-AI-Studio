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
/// Stores that cache "schema initialized" on a long-lived instance must keep working once their
/// database file is gone. Each case writes through one store instance, deletes the database file and
/// its WAL/SHM sidecars the way a data-root redirect, manual repair or backup restore does, and then
/// expects the same instance to write and read again instead of failing with "no such table".
/// </summary>
public sealed class SqliteSchemaRecreationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-schema-recreation-" + Guid.NewGuid().ToString("N"));

    public SqliteSchemaRecreationTests() => Directory.CreateDirectory(_root);

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
        nameof(RagStore),
        nameof(AdapterStore),
        nameof(EvalStore),
        nameof(ModelBenchmarkStore),
        nameof(AutopilotStore),
        nameof(ArtifactStore),
        nameof(ContextGraphStore),
        nameof(EngineRaceEvidenceStore),
        nameof(GoalModeV2Store),
        nameof(AutoLabStore),
        nameof(CapabilityProfilerStore),
        nameof(VisualContextV2Store),
        nameof(SkillRegistryService),
        nameof(SemanticIndexV2Service),
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void Store_recreates_its_tables_after_its_database_file_is_deleted(string storeName)
    {
        string dataDirectory = Path.Combine(_root, storeName);
        Directory.CreateDirectory(dataDirectory);
        Func<bool> writeAndRead = CreateRoundTrip(storeName, dataDirectory);

        Assert.True(writeAndRead(), "The store should round-trip a row before its database file is removed.");

        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(dataDirectory, "*.db*"))
            File.Delete(file);

        Assert.True(writeAndRead(), "The store should recreate its tables after its database file was removed.");
    }

    [Fact]
    public void Schema_guard_reports_only_tables_that_exist()
    {
        string path = Path.Combine(_root, "guard.db");
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadWriteCreate");
        connection.Open();
        Assert.False(SqliteSchemaGuard.HasAllTables(connection, "widgets"));

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE widgets (id TEXT PRIMARY KEY);";
            create.ExecuteNonQuery();
        }

        Assert.True(SqliteSchemaGuard.HasAllTables(connection, "widgets"));
        Assert.False(SqliteSchemaGuard.HasAllTables(connection, "widgets", "gadgets"));
        Assert.True(SqliteSchemaGuard.HasAllTables(connection));
    }

    // Each round trip keeps ONE store instance alive across the file deletion. A fresh instance would
    // read the schema flag again and hide the cached-flag bug, so the instance is created once here.
    private static Func<bool> CreateRoundTrip(string storeName, string dataDirectory) => storeName switch
    {
        nameof(ConversationStore) => ConversationRoundTrip(dataDirectory),
        nameof(MemoryStore) => MemoryRoundTrip(dataDirectory),
        nameof(AutomationStore) => AutomationRoundTrip(dataDirectory),
        nameof(WorkflowStore) => WorkflowRoundTrip(dataDirectory),
        nameof(TaskStore) => TaskRoundTrip(dataDirectory),
        nameof(RagStore) => RagRoundTrip(dataDirectory),
        nameof(AdapterStore) => AdapterRoundTrip(dataDirectory),
        nameof(EvalStore) => EvalRoundTrip(dataDirectory),
        nameof(ModelBenchmarkStore) => ModelBenchmarkRoundTrip(dataDirectory),
        nameof(AutopilotStore) => AutopilotRoundTrip(dataDirectory),
        nameof(ArtifactStore) => ArtifactRoundTrip(dataDirectory),
        nameof(ContextGraphStore) => ContextGraphRoundTrip(dataDirectory),
        nameof(EngineRaceEvidenceStore) => EngineRaceRoundTrip(dataDirectory),
        nameof(GoalModeV2Store) => GoalModeRoundTrip(dataDirectory),
        nameof(AutoLabStore) => AutoLabRoundTrip(dataDirectory),
        nameof(CapabilityProfilerStore) => CapabilityProfilerRoundTrip(dataDirectory),
        nameof(VisualContextV2Store) => VisualContextRoundTrip(dataDirectory),
        nameof(SkillRegistryService) => SkillRegistryRoundTrip(dataDirectory),
        nameof(SemanticIndexV2Service) => SemanticIndexRoundTrip(dataDirectory),
        _ => throw new ArgumentOutOfRangeException(nameof(storeName), storeName, "No round trip is defined for this store."),
    };

    private static Func<bool> ConversationRoundTrip(string dataDirectory)
    {
        var store = new ConversationStore(dataDirectory);
        return () =>
        {
            string id = Guid.NewGuid().ToString("N");
            store.Save(id, new ConversationUpsertRequest
            {
                Title = "schema probe",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                DocumentJson = "{}",
            });
            return store.Get(id) != null;
        };
    }

    private static Func<bool> MemoryRoundTrip(string dataDirectory)
    {
        var store = new MemoryStore(new EmbeddingService(Path.Combine(dataDirectory, "embeddings")), dataDirectory);
        return () =>
        {
            string id = store.Add(new MemoryAddRequest { Content = "schema probe " + Guid.NewGuid().ToString("N") }).Id;
            return store.List().Any(entry => entry.Id == id);
        };
    }

    private static Func<bool> AutomationRoundTrip(string dataDirectory)
    {
        var store = new AutomationStore(dataDirectory);
        return () =>
        {
            string id = store.Create(new CreateAutomationRequest
            {
                Name = "schema probe",
                TriggerType = AutomationTriggerType.Schedule,
                TriggerConfigJson = "{}",
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"schema probe\"}",
            }).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> WorkflowRoundTrip(string dataDirectory)
    {
        var store = new WorkflowStore(dataDirectory);
        return () =>
        {
            string id = store.Create(new CreateWorkflowRequest
            {
                Name = "schema probe",
                Steps = new List<WorkflowStepTemplate> { new() { Name = "Step 1", Goal = "Do something" } },
            }).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> TaskRoundTrip(string dataDirectory)
    {
        var store = new TaskStore(dataDirectory);
        return () =>
        {
            string id = store.Create(new CreateAgentTaskRequest { Goal = "schema probe" }).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> RagRoundTrip(string dataDirectory)
    {
        var store = new RagStore(new EmbeddingService(Path.Combine(dataDirectory, "embeddings")), dataDirectory);
        return () =>
        {
            string sourceId = store.AddSource(Path.Combine(dataDirectory, Guid.NewGuid().ToString("N"))).Id;
            // Also touches rag_chunk_embeddings, which is guarded by its own once-per-instance flag.
            _ = store.GetProviderEmbeddingCoverage(store.EmbeddingIdentity);
            return store.ListSources().Any(source => source.Id == sourceId);
        };
    }

    private static Func<bool> AdapterRoundTrip(string dataDirectory)
    {
        var store = new AdapterStore(dataDirectory);
        return () =>
        {
            string id = store.Register(new RegisterAdapterRequest
            {
                Name = "schema probe",
                BaseModel = "schema-probe-base",
                AdapterPath = Path.Combine(dataDirectory, "schema-probe.adapter"),
            }).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> EvalRoundTrip(string dataDirectory)
    {
        var store = new EvalStore(dataDirectory);
        return () =>
        {
            string id = store.CreateDataset(new CreateEvalDatasetRequest { Name = "schema probe" }).Id;
            return store.GetDataset(id) != null;
        };
    }

    private static Func<bool> ModelBenchmarkRoundTrip(string dataDirectory)
    {
        var store = new ModelBenchmarkStore(dataDirectory);
        return () =>
        {
            string id = store.Create(Path.Combine(dataDirectory, "schema-probe.gguf"), "schema probe", automatic: false).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> AutopilotRoundTrip(string dataDirectory)
    {
        var store = new AutopilotStore(dataDirectory);
        return () =>
        {
            string id = store.Create("schema probe", "review", "schema-probe", new List<string>(), "queued").Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> ArtifactRoundTrip(string dataDirectory)
    {
        var store = new ArtifactStore(dataDirectory);
        return () =>
        {
            string id = store.Create(new CreateArtifactRequest
            {
                ArtifactType = "note",
                Title = "schema probe",
                ContentRef = "schema-probe",
            }).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> ContextGraphRoundTrip(string dataDirectory)
    {
        var store = new ContextGraphStore(dataDirectory);
        return () =>
        {
            string id = store.CreateEntity(new CreateContextEntityRequest
            {
                EntityType = "schema-probe",
                Name = "schema probe " + Guid.NewGuid().ToString("N"),
            }).Id;
            return store.GetEntity(id) != null;
        };
    }

    private static Func<bool> EngineRaceRoundTrip(string dataDirectory)
    {
        var store = new EngineRaceEvidenceStore(dataDirectory);
        return () =>
        {
            string id = store.RecordMedia(new MediaEngineRaceResult
            {
                Status = "quality-evaluation-failed",
                Measurements = new List<MediaEngineRaceMeasurement>
                {
                    new()
                    {
                        EngineId = "rendered", EngineVersion = "1", Success = true,
                        QualityEvaluationSucceeded = false, QualityError = "judge offline",
                    },
                    new()
                    {
                        EngineId = "broken", EngineVersion = "1", Success = false,
                        Error = "renderer crashed",
                    },
                },
            }).Id;
            return store.List().Any(evidence => evidence.Id == id);
        };
    }

    private static Func<bool> GoalModeRoundTrip(string dataDirectory)
    {
        var store = new GoalModeV2Store(dataDirectory);
        return () =>
        {
            string goalId = store.Create(new CreateGoalSpecificationRequest
            {
                Goal = "schema probe",
                AcceptanceCriteria = new List<string> { "probe recorded" },
            }).GoalId;
            return store.Get(goalId) != null;
        };
    }

    private static Func<bool> AutoLabRoundTrip(string dataDirectory)
    {
        var store = new AutoLabStore(dataDirectory);
        return () =>
        {
            string id = Guid.NewGuid().ToString("N");
            store.Create(new ExperimentDto
            {
                Id = id,
                Name = "schema probe",
                DatasetId = "schema-probe-dataset",
                EvalRunId = "schema-probe-run",
                ModelProfile = "STANDARD",
                ModelPath = Path.Combine(dataDirectory, "schema-probe.gguf"),
                CreatedUtc = DateTime.UtcNow,
            });
            return store.Get(id) != null;
        };
    }

    private static Func<bool> CapabilityProfilerRoundTrip(string dataDirectory)
    {
        var store = new CapabilityProfilerStore(dataDirectory);
        return () =>
        {
            string id = Guid.NewGuid().ToString("N");
            store.CreateRun(new CapabilityProfileRunDto
            {
                Id = id,
                ModelPath = Path.Combine(dataDirectory, "schema-probe.gguf"),
                Role = "fast",
                Status = CapabilityProfileRunStatus.Completed,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
            });
            return store.GetRun(id) != null;
        };
    }

    private static Func<bool> VisualContextRoundTrip(string dataDirectory)
    {
        var store = new VisualContextV2Store(dataDirectory);
        return () =>
        {
            // The eight-byte PNG signature is what the store's image check accepts in VerifierServiceTests.
            string id = store.CreateSnapshot(new CreateVisualSnapshotRequest
            {
                Scope = "task",
                ScopeId = "schema-probe",
                Label = "schema probe",
                Source = "ui-verification",
                FileName = "schema-probe.png",
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            }).Id;
            return store.GetSnapshot(id) != null;
        };
    }

    private static Func<bool> SkillRegistryRoundTrip(string dataDirectory)
    {
        var store = new SkillRegistryService(dataDirectory);
        return () =>
        {
            string id = store.CreateOrUpdateUserSkill(new SkillManifest
            {
                DisplayName = "Schema probe " + Guid.NewGuid().ToString("N")[..8],
                Category = "General",
            }, isImport: false).Id;
            return store.Get(id) != null;
        };
    }

    private static Func<bool> SemanticIndexRoundTrip(string dataDirectory)
    {
        // The index database must sit directly in the data root so the file deletion above reaches it.
        var store = new SemanticIndexV2Service(
            dataDirectory,
            new RepoIndexService(new ContextGraphStore(Path.Combine(dataDirectory, "graph"))));
        return () =>
        {
            // A unique alias and root per call: repository_root carries a UNIQUE index.
            string alias = "schema-" + Guid.NewGuid().ToString("N")[..12];
            string root = Path.Combine(dataDirectory, "repos", alias);
            Directory.CreateDirectory(root);
            store.Register(new RegisterSemanticRepositoryRequest { Alias = alias, RepositoryRoot = root });
            return store.ListRepositories().Any(repository => repository.Alias == alias);
        };
    }
}
