using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 24 - ArtifactStore's version history. The property that matters most
/// (directive section 49: "Do not silently overwrite final outputs") is that AddVersion always
/// appends and RestoreVersion never deletes or rewrites - both are tested explicitly below by
/// asserting old versions are still readable, byte-for-byte, after later operations.</summary>
public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-artifact-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Create_rejects_empty_type_title_or_content()
    {
        var store = new ArtifactStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateArtifactRequest { ArtifactType = "", Title = "t", ContentRef = "c" }));
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateArtifactRequest { ArtifactType = "document", Title = " ", ContentRef = "c" }));
        Assert.Throws<InvalidDataException>(() => store.Create(new CreateArtifactRequest { ArtifactType = "document", Title = "t", ContentRef = "" }));
    }

    [Fact]
    public void Create_produces_version_1_as_draft()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "Q3 summary", ContentRef = "path/to/v1.md" });

        Assert.Equal(1, artifact.CurrentVersionNumber);
        Assert.Equal(ArtifactStore.StatusDraft, artifact.Status);
        List<ArtifactVersionDto> versions = store.ListVersions(artifact.Id);
        Assert.Single(versions);
        Assert.Equal("path/to/v1.md", versions[0].ContentRef);
    }

    [Fact]
    public void AddVersion_appends_without_touching_earlier_versions()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "Q3 summary", ContentRef = "v1 content" });

        ArtifactVersionDto? v2 = store.AddVersion(artifact.Id, new AddArtifactVersionRequest { ContentRef = "v2 content", Comment = "Fixed a typo" });
        Assert.NotNull(v2);
        Assert.Equal(2, v2!.VersionNumber);

        ArtifactDto? refreshed = store.Get(artifact.Id);
        Assert.Equal(2, refreshed!.CurrentVersionNumber);

        List<ArtifactVersionDto> versions = store.ListVersions(artifact.Id);
        Assert.Equal(2, versions.Count);
        Assert.Equal("v1 content", versions[0].ContentRef); // v1 untouched
        Assert.Equal("v2 content", versions[1].ContentRef);
    }

    [Fact]
    public void AddVersion_returns_null_for_an_unknown_artifact()
    {
        var store = new ArtifactStore(_tempDir);
        Assert.Null(store.AddVersion("parux-test-unknown-" + Guid.NewGuid().ToString("N"), new AddArtifactVersionRequest { ContentRef = "x" }));
    }

    /// <summary>The core "never silently overwrite" guarantee: restoring an old version creates a
    /// brand new version at the head of history, and the version that was restored FROM is still
    /// there afterward, unchanged.</summary>
    [Fact]
    public void RestoreVersion_creates_a_new_version_and_never_deletes_history()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest { ArtifactType = "document", Title = "Draft", ContentRef = "original text" });
        store.AddVersion(artifact.Id, new AddArtifactVersionRequest { ContentRef = "edited text" });
        store.AddVersion(artifact.Id, new AddArtifactVersionRequest { ContentRef = "over-edited, worse text" });

        ArtifactVersionDto? restored = store.RestoreVersion(artifact.Id, versionNumber: 1);
        Assert.NotNull(restored);
        Assert.Equal(4, restored!.VersionNumber); // appended, not overwritten
        Assert.Equal("original text", restored.ContentRef);

        List<ArtifactVersionDto> versions = store.ListVersions(artifact.Id);
        Assert.Equal(4, versions.Count);
        Assert.Equal("original text", versions[0].ContentRef);           // v1 still there
        Assert.Equal("edited text", versions[1].ContentRef);             // v2 still there
        Assert.Equal("over-edited, worse text", versions[2].ContentRef); // v3 still there, not "reverted away"
        Assert.Equal("original text", versions[3].ContentRef);           // v4 is the restore

        ArtifactDto? refreshed = store.Get(artifact.Id);
        Assert.Equal(4, refreshed!.CurrentVersionNumber);
    }

    [Fact]
    public void RestoreVersion_returns_null_for_an_unknown_version_number()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest { ArtifactType = "document", Title = "Draft", ContentRef = "v1" });
        Assert.Null(store.RestoreVersion(artifact.Id, versionNumber: 99));
    }

    [Fact]
    public void SetStatus_updates_and_returns_false_for_an_unknown_id()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest { ArtifactType = "document", Title = "Draft", ContentRef = "v1" });
        Assert.True(store.SetStatus(artifact.Id, ArtifactStore.StatusFinal));
        Assert.Equal(ArtifactStore.StatusFinal, store.Get(artifact.Id)!.Status);
        Assert.False(store.SetStatus("parux-test-unknown-" + Guid.NewGuid().ToString("N"), ArtifactStore.StatusFinal));
    }

    [Fact]
    public void List_filters_by_type()
    {
        var store = new ArtifactStore(_tempDir);
        store.Create(new CreateArtifactRequest { ArtifactType = "presentation", Title = "Deck", ContentRef = "x" });
        store.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "R1", ContentRef = "x" });
        store.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "R2", ContentRef = "x" });

        Assert.Single(store.List("presentation"));
        Assert.Equal(2, store.List("report").Count);
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void FindRelevant_matches_only_final_artifacts_by_keyword_overlap()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto draft = store.Create(new CreateArtifactRequest { ArtifactType = "agent-task-result", Title = "Unfinished quasar report", ContentRef = "quasar telemetry pending review" });
        ArtifactDto final = store.Create(new CreateArtifactRequest { ArtifactType = "agent-task-result", Title = "Quasar telemetry summary", ContentRef = "The quasar telemetry pipeline is verified and stable." });
        store.SetStatus(final.Id, ArtifactStore.StatusFinal);

        List<RelevantArtifact> results = store.FindRelevant("quasar telemetry", 5);

        RelevantArtifact match = Assert.Single(results);
        Assert.Equal(final.Id, match.Artifact.Id);
        Assert.Equal(1, match.Version.VersionNumber);
        Assert.DoesNotContain(results, r => r.Artifact.Id == draft.Id);
    }

    [Fact]
    public void FindRelevant_returns_empty_for_a_blank_query_or_no_overlap()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto final = store.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "Nightly build report", ContentRef = "All checks passed." });
        store.SetStatus(final.Id, ArtifactStore.StatusFinal);

        Assert.Empty(store.FindRelevant("   ", 5));
        Assert.Empty(store.FindRelevant("completely unrelated topic zzzqqq", 5));
    }

    [Fact]
    public void FindRelevant_ignores_common_words_and_substrings_inside_unrelated_terms()
    {
        var store = new ArtifactStore(_tempDir);
        ArtifactDto artifact = store.Create(new CreateArtifactRequest
        {
            ArtifactType = "note", Title = "Rivedere il onboarding", ContentRef = "Promemoria per il team"
        });
        store.SetStatus(artifact.Id, ArtifactStore.StatusFinal);

        Assert.Empty(store.FindRelevant("Quanto fa 17 per 23?", 5));
        Assert.Empty(store.FindRelevant("board", 5));
        Assert.Single(store.FindRelevant("onboarding", 5));
    }

    /// <summary>Simulated process restart, same reasoning as TaskStoreTests/ContextGraphStoreTests
    /// - a new store instance over the same directory is the faithful unit-level equivalent of a
    /// real process kill/restart, and version history must survive it intact.</summary>
    [Fact]
    public void Version_history_survives_a_simulated_process_restart()
    {
        string artifactId;
        {
            var storeBeforeRestart = new ArtifactStore(_tempDir);
            ArtifactDto artifact = storeBeforeRestart.Create(new CreateArtifactRequest { ArtifactType = "report", Title = "R", ContentRef = "v1" });
            storeBeforeRestart.AddVersion(artifact.Id, new AddArtifactVersionRequest { ContentRef = "v2" });
            artifactId = artifact.Id;
        }

        var storeAfterRestart = new ArtifactStore(_tempDir);
        ArtifactDto? recovered = storeAfterRestart.Get(artifactId);
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered!.CurrentVersionNumber);
        Assert.Equal(2, storeAfterRestart.ListVersions(artifactId).Count);
    }
}
