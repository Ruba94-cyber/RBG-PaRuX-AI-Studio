using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorkflowVersioningTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-workflow-version-tests-" + Guid.NewGuid().ToString("N"));

    public WorkflowVersioningTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private WorkflowService NewService() =>
        new(new WorkflowStore(_directory), new TaskStore(_directory));

    private static CreateWorkflowRequest Request(string name, string? previous = null) => new()
    {
        Name = name,
        PreviousVersionId = previous,
        Steps = new List<WorkflowStepTemplate> { new() { Name = "step", Goal = "Do {{input}}" } }
    };

    [Fact]
    public void Revision_is_append_only_and_persists_lineage()
    {
        WorkflowService service = NewService();
        WorkflowDto v1 = service.Create(Request("Flow"));
        WorkflowDto v2 = service.Create(Request("Flow", v1.Id));

        Assert.Equal(1, v1.VersionNumber);
        Assert.Null(v1.PreviousVersionId);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal(v1.Id, v2.PreviousVersionId);

        WorkflowService reopened = NewService();
        WorkflowDto persistedV1 = reopened.Get(v1.Id)!;
        WorkflowDto persistedV2 = reopened.Get(v2.Id)!;
        Assert.Equal(1, persistedV1.VersionNumber);
        Assert.Equal(2, persistedV2.VersionNumber);
        Assert.Equal(v1.Id, persistedV2.PreviousVersionId);
        Assert.Equal("Do {{input}}", persistedV1.Steps[0].Goal);
    }

    [Fact]
    public void Revision_rejects_branching_from_a_non_latest_version()
    {
        WorkflowService service = NewService();
        WorkflowDto v1 = service.Create(Request("Flow"));
        _ = service.Create(Request("Flow", v1.Id));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            service.Create(Request("Flow alternate", v1.Id)));

        Assert.Contains("latest version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_cannot_punch_a_hole_in_version_history()
    {
        WorkflowService service = NewService();
        WorkflowDto v1 = service.Create(Request("Flow"));
        WorkflowDto v2 = service.Create(Request("Flow", v1.Id));

        Assert.False(service.Delete(v1.Id));
        Assert.NotNull(service.Get(v1.Id));
        Assert.True(service.Delete(v2.Id));
        Assert.True(service.Delete(v1.Id));
    }

    [Fact]
    public void Existing_pre_versioning_database_is_migrated_as_version_one()
    {
        string databasePath = Path.Combine(_directory, "workflows.db");
        using (var connection = new SqliteConnection("Data Source=" + databasePath))
        {
            connection.Open();
            using (SqliteCommand create = connection.CreateCommand())
            {
                create.CommandText = @"
CREATE TABLE workflows (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    steps_json TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);";
                create.ExecuteNonQuery();
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = @"INSERT INTO workflows
(id, name, description, steps_json, created_utc, updated_utc)
VALUES ($id, $name, NULL, $steps, $created, $updated);";
            insert.Parameters.AddWithValue("$id", "legacy");
            insert.Parameters.AddWithValue("$name", "Legacy");
            insert.Parameters.AddWithValue("$steps", "[{\"name\":\"one\",\"goal\":\"work\",\"role\":null,\"dependsOnStepIndexes\":[]}]");
            insert.Parameters.AddWithValue("$created", "2026-08-27T00:00:00.0000000Z");
            insert.Parameters.AddWithValue("$updated", "2026-08-27T00:00:00.0000000Z");
            insert.ExecuteNonQuery();
        }

        WorkflowDto migrated = NewService().Get("legacy")!;
        Assert.Equal(1, migrated.VersionNumber);
        Assert.Null(migrated.PreviousVersionId);
        Assert.Single(migrated.Steps);
    }
}
