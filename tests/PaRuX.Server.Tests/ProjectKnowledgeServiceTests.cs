using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ProjectKnowledgeServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "parux-project-knowledge-" + Guid.NewGuid().ToString("N"));
    private readonly TaskStore _tasks;
    private readonly ContextGraphStore _graph;
    private readonly ArtifactStore _artifacts;
    private readonly ProjectManagerService _projects;
    private readonly ProjectKnowledgeService _knowledge;

    public ProjectKnowledgeServiceTests()
    {
        Directory.CreateDirectory(_temp);
        _tasks = new TaskStore(_temp);
        _graph = new ContextGraphStore(_temp);
        _artifacts = new ArtifactStore(_temp);
        _projects = new ProjectManagerService(new FakeLocalCompletionClient(), _tasks, _graph);
        _knowledge = new ProjectKnowledgeService(_projects, _graph, _tasks, _artifacts);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch { }
    }

    [Fact]
    public void Build_rejects_unknown_or_non_managed_project_identity()
    {
        Assert.Throws<InvalidDataException>(() => _knowledge.Build("missing"));

        ContextEntityDto note = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project",
            Name = "Fake project",
            Source = "paired-client"
        });
        Assert.Throws<InvalidDataException>(() => _knowledge.Build(note.Id));
    }

    [Fact]
    public void Project_pack_contains_only_authoritative_work_attached_to_that_project()
    {
        ContextEntityDto first = ManagedProject("First project");
        ContextEntityDto second = ManagedProject("Second project");

        AgentTaskDto firstTask = _tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement first project feature",
            Role = "coding"
        });
        _tasks.UpdateStatus(firstTask.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ResultJson = "{\"summary\":\"First project implementation completed\"}",
            VerificationStatus = "pass"
        });
        _tasks.AppendLog(firstTask.Id, "first-project-log");
        _projects.AttachTask(first.Id, firstTask.Id);

        AgentTaskDto secondTask = _tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Second project secret task",
            Role = "coding"
        });
        _projects.AttachTask(second.Id, secondTask.Id);

        ArtifactDto firstArtifact = InlineArtifact("First result", "FIRST_PROJECT_VERIFIED_CONTENT", final: true);
        AttachArtifact(first.Id, firstArtifact);
        ArtifactDto secondArtifact = InlineArtifact("Second result", "SECOND_PROJECT_SHOULD_NOT_LEAK", final: true);
        AttachArtifact(second.Id, secondArtifact);

        ProjectKnowledgePackDto pack = _knowledge.Build(first.Id);

        Assert.Equal(first.Id, pack.ProjectId);
        Assert.Equal("First project", pack.Goal);
        Assert.Contains("Implement first project feature", pack.PromptText);
        Assert.Contains("First project implementation completed", pack.PromptText);
        Assert.Contains("first-project-log", pack.PromptText);
        Assert.Contains("FIRST_PROJECT_VERIFIED_CONTENT", pack.PromptText);
        Assert.DoesNotContain("Second project secret task", pack.PromptText);
        Assert.DoesNotContain("SECOND_PROJECT_SHOULD_NOT_LEAK", pack.PromptText);
        Assert.Contains(pack.Items, item => item.Identity == "task:" + firstTask.Id);
        Assert.Contains(pack.Items, item => item.Identity.StartsWith("artifact:" + firstArtifact.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Draft_artifact_is_visible_as_draft_but_its_unverified_content_is_not_injected()
    {
        ContextEntityDto project = ManagedProject("Draft safety");
        ArtifactDto draft = InlineArtifact("Unverified draft", "DO_NOT_TRUST_THIS_DRAFT_PAYLOAD", final: false);
        AttachArtifact(project.Id, draft);

        ProjectKnowledgePackDto pack = _knowledge.Build(project.Id);
        ProjectKnowledgeItemDto item = Assert.Single(pack.Items);

        Assert.Equal("draft", item.Status);
        Assert.Contains("not treated as authoritative", item.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DO_NOT_TRUST_THIS_DRAFT_PAYLOAD", pack.PromptText);
    }

    [Fact]
    public void File_artifact_exposes_only_the_leaf_name_not_the_windows_or_temp_path()
    {
        ContextEntityDto project = ManagedProject("File safety");
        string folder = Path.Combine(_temp, "private", "nested");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "report.pdf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        ArtifactDto artifact = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "pdf",
            Title = "Report",
            ContentRef = path,
            Source = "test"
        });
        _artifacts.SetStatus(artifact.Id, ArtifactStore.StatusFinal);
        artifact = _artifacts.Get(artifact.Id)!;
        AttachArtifact(project.Id, artifact);

        ProjectKnowledgePackDto pack = _knowledge.Build(project.Id);

        Assert.Contains("report.pdf", pack.PromptText);
        Assert.DoesNotContain(folder, pack.PromptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_budget_is_hard_bounded_and_reports_truncation()
    {
        ContextEntityDto project = ManagedProject(new string('G', 5_000));
        ProjectKnowledgePackDto pack = _knowledge.Build(project.Id, 1_000);

        Assert.Equal(1_000, pack.MaxCharacters);
        Assert.True(pack.UsedCharacters <= 1_000);
        Assert.True(pack.Truncated);
    }

    private ContextEntityDto ManagedProject(string goal) => _graph.CreateEntity(new CreateContextEntityRequest
    {
        EntityType = "project",
        Name = goal,
        Source = "project-manager",
        MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, goal })
    });

    private ArtifactDto InlineArtifact(string title, string content, bool final)
    {
        ArtifactDto artifact = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "text",
            Title = title,
            ContentRef = content,
            Source = "test"
        });
        if (final)
        {
            _artifacts.SetStatus(artifact.Id, ArtifactStore.StatusFinal);
            artifact = _artifacts.Get(artifact.Id)!;
        }
        return artifact;
    }

    private void AttachArtifact(string projectId, ArtifactDto artifact)
    {
        ContextEntityDto entity = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "artifact",
            Name = artifact.Title,
            Source = "artifact-workspace",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { artifactId = artifact.Id })
        });
        _graph.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = projectId,
            RelationType = "CONTAINS",
            ToEntityId = entity.Id,
            Source = "project-artifact-bridge",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { artifactId = artifact.Id })
        });
    }
}
