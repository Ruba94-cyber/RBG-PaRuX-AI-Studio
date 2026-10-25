using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MobileArtifactServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "parux-mobile-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly string _storeDir;
    private readonly string _allowedDir;
    private readonly ArtifactStore _artifacts;
    private readonly ContextGraphStore _graph;
    private readonly MobileArtifactService _service;

    public MobileArtifactServiceTests()
    {
        _storeDir = Path.Combine(_temp, "store");
        _allowedDir = Path.Combine(_temp, "managed", "generated");
        Directory.CreateDirectory(_allowedDir);
        _artifacts = new ArtifactStore(_storeDir);
        _graph = new ContextGraphStore(_storeDir);
        _service = new MobileArtifactService(_artifacts, _graph, new[] { _allowedDir });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch { }
    }

    [Fact]
    public void Registered_file_inside_managed_root_is_downloadable_by_identity()
    {
        string path = Path.Combine(_allowedDir, "image.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        ArtifactDto artifact = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "image",
            Title = "Generated image",
            ContentRef = path,
            Source = "create-workspace"
        });

        MobileArtifactDto mobile = Assert.Single(_service.List().Artifacts);
        Assert.Equal(artifact.Id, mobile.Id);
        Assert.True(mobile.Downloadable);
        Assert.Equal("image.png", mobile.FileName);
        Assert.Equal("image/png", mobile.MediaType);
        Assert.Equal(5, mobile.SizeBytes);

        MobileArtifactService.DownloadTarget target = _service.ResolveDownload(artifact.Id)!;
        Assert.Equal(Path.GetFullPath(path), target.Path);
        Assert.Equal("image/png", target.MediaType);
        Assert.Equal(5, target.SizeBytes);
    }

    [Fact]
    public void External_path_and_inline_content_are_metadata_only_not_mobile_file_server_entries()
    {
        string outside = Path.Combine(_temp, "private-outside.txt");
        File.WriteAllText(outside, "must not be remotely served");
        ArtifactDto external = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "file",
            Title = "External file",
            ContentRef = outside,
            Source = "manual"
        });
        ArtifactDto inline = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "text",
            Title = "Inline note",
            ContentRef = "This is inline artifact text, not a Windows path.",
            Source = "manual"
        });

        Dictionary<string, MobileArtifactDto> items = _service.List().Artifacts.ToDictionary(item => item.Id);
        Assert.False(items[external.Id].Downloadable);
        Assert.False(items[inline.Id].Downloadable);
        Assert.Null(items[external.Id].FileName);
        Assert.Null(items[inline.Id].FileName);
        Assert.Null(_service.ResolveDownload(external.Id));
        Assert.Null(_service.ResolveDownload(inline.Id));
    }

    [Fact]
    public void Project_filter_returns_only_artifacts_connected_by_the_durable_contains_edge()
    {
        string firstPath = Path.Combine(_allowedDir, "first.wav");
        string secondPath = Path.Combine(_allowedDir, "second.wav");
        File.WriteAllBytes(firstPath, new byte[] { 1 });
        File.WriteAllBytes(secondPath, new byte[] { 2 });
        ArtifactDto first = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "audio", Title = "First", ContentRef = firstPath, Source = "tts"
        });
        ArtifactDto second = _artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "audio", Title = "Second", ContentRef = secondPath, Source = "tts"
        });

        ContextEntityDto project = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project",
            Name = "Phone project",
            Source = "project-manager",
            MetadataJson = "{\"goal\":\"Phone project\"}"
        });
        ContextEntityDto artifactEntity = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "artifact",
            Name = first.Title,
            Source = "artifact-workspace",
            MetadataJson = "{\"artifactId\":\"" + first.Id + "\"}"
        });
        _graph.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = project.Id,
            RelationType = "CONTAINS",
            ToEntityId = artifactEntity.Id,
            Source = "project-artifact-bridge"
        });

        MobileArtifactDto scoped = Assert.Single(_service.List(project.Id).Artifacts);
        Assert.Equal(first.Id, scoped.Id);
        Assert.DoesNotContain(_service.List(project.Id).Artifacts, item => item.Id == second.Id);
    }

    [Fact]
    public void Project_filter_rejects_non_project_graph_entities()
    {
        ContextEntityDto note = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "note",
            Name = "Not a project",
            Source = "project-manager"
        });

        Assert.Throws<InvalidDataException>(() => _service.List(note.Id));
        Assert.Throws<InvalidDataException>(() => _service.List("missing-project"));
    }
}
