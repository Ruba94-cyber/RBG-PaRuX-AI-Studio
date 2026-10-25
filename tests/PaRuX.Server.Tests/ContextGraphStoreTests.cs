using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>ContextGraphStore CRUD, one-hop retrieval, bounded multi-hop traversal, inspectable
/// export, and the original section-44 durability scenario.</summary>
public sealed class ContextGraphStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-contextgraph-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateEntity_rejects_an_empty_type_or_name()
    {
        var store = new ContextGraphStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.CreateEntity(new CreateContextEntityRequest { EntityType = "", Name = "x" }));
        Assert.Throws<InvalidDataException>(() => store.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = " " }));
    }

    [Fact]
    public void CreateRelation_rejects_unknown_entity_ids()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto entity = store.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = "PaRuX" });
        Assert.Throws<InvalidDataException>(() => store.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = entity.Id, RelationType = "CONTAINS", ToEntityId = "parux-test-unknown-" + Guid.NewGuid().ToString("N")
        }));
        Assert.Throws<InvalidDataException>(() => store.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = "parux-test-unknown-" + Guid.NewGuid().ToString("N"), RelationType = "CONTAINS", ToEntityId = entity.Id
        }));
    }

    [Fact]
    public void ListEntities_filters_by_type()
    {
        var store = new ContextGraphStore(_tempDir);
        store.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = "PaRuX" });
        store.CreateEntity(new CreateContextEntityRequest { EntityType = "task", Name = "Ship it" });
        store.CreateEntity(new CreateContextEntityRequest { EntityType = "task", Name = "Test it" });

        Assert.Single(store.ListEntities("project"));
        Assert.Equal(2, store.ListEntities("task").Count);
        Assert.Equal(3, store.ListEntities().Count);
    }

    /// <summary>The primitive chat retrieval relies on: turning free text into candidate entity ids
    /// by scanning for known entity names, case-insensitively, without pulling in entities never
    /// actually mentioned.</summary>
    [Fact]
    public void FindEntitiesMentionedIn_matches_case_insensitively_and_ignores_unmentioned_entities()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto project = store.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = "PaRuX vNext" });
        store.CreateEntity(new CreateContextEntityRequest { EntityType = "model", Name = "Qwen3.8-27B" });

        List<ContextEntityDto> found = store.FindEntitiesMentionedIn("What is the release status of parux vnext right now?", 5);

        Assert.Single(found);
        Assert.Equal(project.Id, found[0].Id);

        Assert.Empty(store.FindEntitiesMentionedIn("What's the capital of France?", 5));
        Assert.Empty(store.FindEntitiesMentionedIn("", 5));
    }

    [Fact]
    public void FindEntitiesMentionedIn_does_not_match_short_names_inside_unrelated_words()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto shortName = store.CreateEntity(new CreateContextEntityRequest { EntityType = "tag", Name = "at" });

        Assert.Empty(store.FindEntitiesMentionedIn("Write a creative story about a cat.", 5));
        Assert.Equal(shortName.Id, Assert.Single(store.FindEntitiesMentionedIn("Review at noon.", 5)).Id);
    }

    [Fact]
    public void Traverse_returns_shortest_hop_depths_and_respects_the_depth_boundary()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto a = Entity(store, "project", "A");
        ContextEntityDto b = Entity(store, "task", "B");
        ContextEntityDto c = Entity(store, "file", "C");
        ContextEntityDto d = Entity(store, "artifact", "D");
        ContextEntityDto e = Entity(store, "model", "E");
        Relation(store, a, b, "HAS");
        Relation(store, b, c, "USES");
        Relation(store, c, d, "CREATES");
        Relation(store, e, a, "SUPPORTS"); // incoming edges are traversable too

        ContextTraversalResponse traversal = store.Traverse(a.Id, maxDepth: 2, maxEntities: 20)!;

        Assert.False(traversal.Truncated);
        Assert.Equal(4, traversal.Entities.Count);
        Assert.Contains(traversal.Entities, entity => entity.Id == a.Id);
        Assert.Contains(traversal.Entities, entity => entity.Id == b.Id);
        Assert.Contains(traversal.Entities, entity => entity.Id == c.Id);
        Assert.Contains(traversal.Entities, entity => entity.Id == e.Id);
        Assert.DoesNotContain(traversal.Entities, entity => entity.Id == d.Id);
        Assert.Equal(0, traversal.DepthByEntityId[a.Id]);
        Assert.Equal(1, traversal.DepthByEntityId[b.Id]);
        Assert.Equal(1, traversal.DepthByEntityId[e.Id]);
        Assert.Equal(2, traversal.DepthByEntityId[c.Id]);
        Assert.Equal(3, traversal.Relations.Count);
    }

    [Fact]
    public void Traverse_marks_entity_cap_truncation_and_never_exceeds_the_cap()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto center = Entity(store, "project", "center");
        for (int i = 0; i < 8; i++)
        {
            ContextEntityDto leaf = Entity(store, "task", "leaf-" + i);
            Relation(store, center, leaf, "HAS");
        }

        ContextTraversalResponse traversal = store.Traverse(center.Id, maxDepth: 4, maxEntities: 3)!;

        Assert.True(traversal.Truncated);
        Assert.Equal(3, traversal.Entities.Count);
        Assert.Equal(3, traversal.DepthByEntityId.Count);
        Assert.All(traversal.Entities.Where(entity => entity.Id != center.Id),
            entity => Assert.Equal(1, traversal.DepthByEntityId[entity.Id]));
        Assert.True(traversal.Relations.Count <= 2);
    }

    [Fact]
    public void Traverse_validates_bounds_and_unknown_centers_without_unbounded_fallback()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto center = Entity(store, "project", "center");

        Assert.Throws<InvalidDataException>(() => store.Traverse(center.Id, 0, 10));
        Assert.Throws<InvalidDataException>(() => store.Traverse(center.Id, 5, 10));
        Assert.Throws<InvalidDataException>(() => store.Traverse(center.Id, 1, 1));
        Assert.Throws<InvalidDataException>(() => store.Traverse(center.Id, 1, 201));
        Assert.Throws<InvalidDataException>(() => store.Traverse(" ", 1, 10));
        Assert.Null(store.Traverse("parux-test-unknown-" + Guid.NewGuid().ToString("N"), 1, 10));
    }

    [Fact]
    public void ExportSnapshot_is_versioned_lossless_and_inspectable()
    {
        var store = new ContextGraphStore(_tempDir);
        ContextEntityDto project = store.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project",
            Name = "PaRuX",
            MetadataJson = "{\"branch\":\"main\"}",
            Source = "unit-test"
        });
        ContextEntityDto task = store.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "task",
            Name = "Close graph gaps",
            MetadataJson = "{\"priority\":1}",
            Source = "unit-test"
        });
        ContextRelationDto relation = store.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = project.Id,
            RelationType = "CONTAINS",
            ToEntityId = task.Id,
            MetadataJson = "{\"why\":\"acceptance\"}",
            Source = "unit-test"
        });

        DateTime before = DateTime.UtcNow;
        ContextGraphExportDto export = store.ExportSnapshot();
        DateTime after = DateTime.UtcNow;

        Assert.Equal(1, export.SchemaVersion);
        Assert.InRange(export.ExportedUtc, before, after);
        Assert.Equal(2, export.Entities.Count);
        Assert.Single(export.Relations);
        ContextEntityDto exportedProject = Assert.Single(export.Entities.Where(entity => entity.Id == project.Id));
        Assert.Equal(project.MetadataJson, exportedProject.MetadataJson);
        Assert.Equal(project.Source, exportedProject.Source);
        ContextRelationDto exportedRelation = Assert.Single(export.Relations);
        Assert.Equal(relation.Id, exportedRelation.Id);
        Assert.Equal(relation.MetadataJson, exportedRelation.MetadataJson);
        Assert.Equal(relation.Source, exportedRelation.Source);
    }

    /// <summary>The original acceptance scenario, run for real: project, task, file, artifact
    /// entities with relations; a simulated restart; cross-relationship retrieval; deletion and
    /// verification that the deleted entity no longer affects retrieval.</summary>
    [Fact]
    public void The_directive_section_44_scenario_works_end_to_end_across_a_simulated_restart()
    {
        string projectId, taskId, fileId, artifactId;
        {
            var storeBeforeRestart = new ContextGraphStore(_tempDir);
            ContextEntityDto project = storeBeforeRestart.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = "PaRuX" });
            ContextEntityDto task = storeBeforeRestart.CreateEntity(new CreateContextEntityRequest { EntityType = "task", Name = "Implement Context Graph" });
            ContextEntityDto file = storeBeforeRestart.CreateEntity(new CreateContextEntityRequest { EntityType = "file", Name = "ContextGraphStore.cs" });
            ContextEntityDto artifact = storeBeforeRestart.CreateEntity(new CreateContextEntityRequest { EntityType = "artifact", Name = "vNext Phase 21 report" });

            storeBeforeRestart.CreateRelation(new CreateContextRelationRequest { FromEntityId = project.Id, RelationType = "CONTAINS", ToEntityId = file.Id });
            storeBeforeRestart.CreateRelation(new CreateContextRelationRequest { FromEntityId = task.Id, RelationType = "MODIFIES", ToEntityId = file.Id });
            storeBeforeRestart.CreateRelation(new CreateContextRelationRequest { FromEntityId = artifact.Id, RelationType = "CREATED_BY", ToEntityId = task.Id });

            projectId = project.Id; taskId = task.Id; fileId = file.Id; artifactId = artifact.Id;
        }

        var storeAfterRestart = new ContextGraphStore(_tempDir);

        ContextNeighborhoodResponse? fileNeighborhood = storeAfterRestart.GetNeighborhood(fileId);
        Assert.NotNull(fileNeighborhood);
        Assert.Equal(2, fileNeighborhood!.IncomingRelations.Count);
        Assert.Contains(fileNeighborhood.NeighborEntities, e => e.Id == projectId);
        Assert.Contains(fileNeighborhood.NeighborEntities, e => e.Id == taskId);

        ContextNeighborhoodResponse artifactNeighborhood = storeAfterRestart.GetNeighborhood(artifactId)!;
        Assert.Contains(artifactNeighborhood.OutgoingRelations, r => r.RelationType == "CREATED_BY" && r.ToEntityId == taskId);

        Assert.True(storeAfterRestart.DeleteEntity(taskId));
        Assert.Null(storeAfterRestart.GetEntity(taskId));

        ContextNeighborhoodResponse fileNeighborhoodAfterDelete = storeAfterRestart.GetNeighborhood(fileId)!;
        Assert.Single(fileNeighborhoodAfterDelete.IncomingRelations);
        Assert.DoesNotContain(fileNeighborhoodAfterDelete.NeighborEntities, e => e.Id == taskId);

        ContextNeighborhoodResponse artifactNeighborhoodAfterDelete = storeAfterRestart.GetNeighborhood(artifactId)!;
        Assert.Empty(artifactNeighborhoodAfterDelete.OutgoingRelations);
    }

    [Fact]
    public void GetNeighborhood_returns_null_for_an_unknown_entity()
    {
        var store = new ContextGraphStore(_tempDir);
        Assert.Null(store.GetNeighborhood("parux-test-unknown-" + Guid.NewGuid().ToString("N")));
    }

    private static ContextEntityDto Entity(ContextGraphStore store, string type, string name) =>
        store.CreateEntity(new CreateContextEntityRequest { EntityType = type, Name = name });

    private static ContextRelationDto Relation(ContextGraphStore store, ContextEntityDto from, ContextEntityDto to, string type) =>
        store.CreateRelation(new CreateContextRelationRequest
        {
            FromEntityId = from.Id,
            RelationType = type,
            ToEntityId = to.Id
        });
}
