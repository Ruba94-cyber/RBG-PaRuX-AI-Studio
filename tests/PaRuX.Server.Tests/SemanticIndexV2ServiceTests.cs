using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SemanticIndexV2ServiceTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "parux-semantic-v2-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoOne = Path.Combine(Path.GetTempPath(), "parux-semantic-v2-repo1-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoTwo = Path.Combine(Path.GetTempPath(), "parux-semantic-v2-repo2-" + Guid.NewGuid().ToString("N"));

    public SemanticIndexV2ServiceTests()
    {
        Directory.CreateDirectory(_stateDir);
        Directory.CreateDirectory(_repoOne);
        Directory.CreateDirectory(_repoTwo);
    }

    public void Dispose()
    {
        DeleteBestEffort(_stateDir);
        DeleteBestEffort(_repoOne);
        DeleteBestEffort(_repoTwo);
    }

    [Fact]
    public void First_sync_builds_index_and_persists_registration_across_service_instances()
    {
        Write(_repoOne, "src/AlphaService.cs", "public sealed class AlphaService { public void AlphaOperation() {} }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "alpha", RepositoryRoot = _repoOne });

        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "alpha" }
        }).Repositories.Single();

        Assert.True(sync.FullRebuild);
        Assert.Equal(1, sync.AddedFiles);
        Assert.Equal(1, sync.IndexedFiles);

        SemanticIndexV2Service reopened = new(_stateDir, index);
        SemanticRepositoryRegistrationDto registration = reopened.ListRepositories().Single();
        Assert.Equal("alpha", registration.Alias);
        Assert.Equal(1, registration.IndexedFiles);
        Assert.NotNull(registration.LastSyncedUtc);
    }

    [Fact]
    public void Content_only_change_remains_incremental_when_hash_verification_detects_it()
    {
        Write(_repoOne, "src/Widget.cs", "public sealed class Widget\n{\n    public void OldName() {}\n}\n");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "one", RepositoryRoot = _repoOne });
        service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "one" } });

        Write(_repoOne, "src/Widget.cs", "public sealed class Widget\n{\n    public void NewName() {}\n}\n");
        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "one" },
            VerifyAllHashes = true
        }).Repositories.Single();

        Assert.False(sync.FullRebuild);
        Assert.Equal(0, sync.AddedFiles);
        Assert.Equal(0, sync.RemovedFiles);
        Assert.Equal(1, sync.ChangedFiles);
        Assert.Contains(index.SearchContext(_repoOne, "new name").RelevantSymbols, value => value.Contains("NewName"));
        Assert.DoesNotContain(index.SearchContext(_repoOne, "old name").RelevantSymbols, value => value.Contains("OldName"));
    }

    [Fact]
    public void Adding_a_test_file_is_incremental_and_creates_tests_relation_without_replacing_unchanged_entity()
    {
        Write(_repoOne, "src/TaskStore.cs", "public sealed class TaskStore { public void Create() {} }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "repo", RepositoryRoot = _repoOne });
        service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "repo" } });
        string originalSourceId = graph.ListEntities("file")
            .Single(entity => string.Equals(entity.Name, "src/TaskStore.cs", StringComparison.OrdinalIgnoreCase)).Id;

        Write(_repoOne, "tests/TaskStoreTests.cs", "public sealed class TaskStoreTests { public void Create_works() {} }");
        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "repo" }
        }).Repositories.Single();

        Assert.False(sync.FullRebuild);
        Assert.Equal(1, sync.AddedFiles);
        Assert.Equal(originalSourceId, graph.ListEntities("file")
            .Single(entity => string.Equals(entity.Name, "src/TaskStore.cs", StringComparison.OrdinalIgnoreCase)).Id);

        ContextEntityDto testEntity = graph.ListEntities("file")
            .Single(entity => string.Equals(entity.Name, "tests/TaskStoreTests.cs", StringComparison.OrdinalIgnoreCase));
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(testEntity.Id)!;
        Assert.Contains(neighborhood.OutgoingRelations, relation => relation.RelationType == "TESTS");
        Assert.Contains(neighborhood.NeighborEntities, entity =>
            string.Equals(entity.Name, "src/TaskStore.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Removing_a_file_is_incremental_and_removes_stale_graph_entities_without_replacing_survivors()
    {
        Write(_repoOne, "src/TaskStore.cs", "public sealed class TaskStore { public void Create() {} }");
        Write(_repoOne, "tests/TaskStoreTests.cs", "public sealed class TaskStoreTests { public void Create_works() {} }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "repo", RepositoryRoot = _repoOne });
        service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "repo" } });
        string originalSourceId = graph.ListEntities("file")
            .Single(entity => string.Equals(entity.Name, "src/TaskStore.cs", StringComparison.OrdinalIgnoreCase)).Id;

        File.Delete(Path.Combine(_repoOne, "tests", "TaskStoreTests.cs"));
        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "repo" }
        }).Repositories.Single();

        Assert.False(sync.FullRebuild);
        Assert.Equal(1, sync.RemovedFiles);
        Assert.DoesNotContain(graph.ListEntities("file"), entity =>
            string.Equals(entity.Name, "tests/TaskStoreTests.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(originalSourceId, graph.ListEntities("file")
            .Single(entity => string.Equals(entity.Name, "src/TaskStore.cs", StringComparison.OrdinalIgnoreCase)).Id);
    }

    [Fact]
    public void Rename_is_one_incremental_add_remove_batch_and_survives_service_restart()
    {
        Write(_repoOne, "src/OldName.cs", "public sealed class RenamedService { public void Run() {} }");
        Write(_repoOne, "src/Stable.cs", "public sealed class Stable { }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "repo", RepositoryRoot = _repoOne });
        service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "repo" } });
        string stableId = graph.ListEntities("file").Single(entity => entity.Name == "src/Stable.cs").Id;

        string oldPath = Path.Combine(_repoOne, "src", "OldName.cs");
        string newPath = Path.Combine(_repoOne, "src", "NewName.cs");
        File.Move(oldPath, newPath);
        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "repo" },
            VerifyAllHashes = true
        }).Repositories.Single();

        Assert.False(sync.FullRebuild);
        Assert.Equal(1, sync.AddedFiles);
        Assert.Equal(1, sync.RemovedFiles);
        Assert.DoesNotContain(graph.ListEntities("file"), entity => entity.Name == "src/OldName.cs");
        Assert.Contains(graph.ListEntities("file"), entity => entity.Name == "src/NewName.cs");
        Assert.Equal(stableId, graph.ListEntities("file").Single(entity => entity.Name == "src/Stable.cs").Id);

        SemanticIndexV2Service reopened = new(_stateDir, index);
        SemanticRepositorySyncResult afterRestart = reopened.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "repo" },
            VerifyAllHashes = true
        }).Repositories.Single();
        Assert.False(afterRestart.FullRebuild);
        Assert.Equal(0, afterRestart.AddedFiles);
        Assert.Equal(0, afterRestart.RemovedFiles);
        Assert.Equal(0, afterRestart.ChangedFiles);
        Assert.Equal(2, afterRestart.IndexedFiles);
    }

    [Theory]
    [InlineData("ui/Main.qml")]
    [InlineData("Views/MainWindow.xaml")]
    [InlineData("cmake/toolchain.cmake")]
    [InlineData("cmake/CMakeLists.txt")]
    [InlineData("config/appsettings.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("frontend/build.gradle.kts")]
    public void Project_and_configuration_files_are_in_index_policy(string relativePath)
    {
        Assert.True(SemanticIndexV2Service.IsIndexableFile(relativePath));
    }

    [Fact]
    public void Project_and_configuration_files_are_persisted_as_searchable_file_evidence()
    {
        Write(_repoOne, "ui/Main.qml", "import QtQuick\nItem { objectName: \"dashboardRoot\" }");
        Write(_repoOne, "Views/MainWindow.xaml", "<Window><Grid x:Name=\"RuntimeDashboard\" /></Window>");
        Write(_repoOne, "cmake/CMakeLists.txt", "add_executable(parux_native main.cpp)");
        Write(_repoOne, "config/appsettings.json", "{ \"runtimeMode\": \"local\" }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "project", RepositoryRoot = _repoOne });

        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "project" }
        }).Repositories.Single();

        Assert.Equal(4, sync.IndexedFiles);
        string[] names = graph.ListEntities("file").Select(entity => entity.Name).ToArray();
        Assert.Contains("ui/Main.qml", names);
        Assert.Contains("Views/MainWindow.xaml", names);
        Assert.Contains("cmake/CMakeLists.txt", names);
        Assert.Contains("config/appsettings.json", names);

        SemanticIndexSearchResponse search = service.Search(new SemanticIndexSearchRequest
        {
            Query = "CMake MainWindow appsettings qml",
            Aliases = new List<string> { "project" },
            MaxFiles = 10,
            MaxSymbols = 10
        });
        Assert.Contains(search.RelevantFiles, value => value.Contains("CMakeLists", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(search.RelevantFiles, value => value.Contains("MainWindow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Force_full_remains_explicit_and_rebuilds_graph_from_current_manifest_scope()
    {
        Write(_repoOne, "src/Alpha.cs", "public sealed class Alpha { }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "repo", RepositoryRoot = _repoOne });
        service.Sync(new SemanticIndexSyncRequest { Aliases = new List<string> { "repo" } });
        string before = graph.ListEntities("file").Single().Id;

        SemanticRepositorySyncResult forced = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "repo" },
            ForceFull = true
        }).Repositories.Single();

        Assert.True(forced.FullRebuild);
        Assert.NotEqual(before, graph.ListEntities("file").Single().Id);
    }

    [Fact]
    public void Search_without_alias_filter_merges_registered_repositories_with_alias_provenance()
    {
        Write(_repoOne, "src/AlphaBackup.cs", "public sealed class AlphaBackup { public void ExportArchive() {} }");
        Write(_repoTwo, "src/BetaBackup.cs", "public sealed class BetaBackup { public void VerifyArchive() {} }");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "alpha", RepositoryRoot = _repoOne });
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "beta", RepositoryRoot = _repoTwo });
        service.Sync(new SemanticIndexSyncRequest());

        SemanticIndexSearchResponse result = service.Search(new SemanticIndexSearchRequest
        {
            Query = "backup archive",
            MaxFiles = 10,
            MaxSymbols = 20
        });

        Assert.Equal(new[] { "alpha", "beta" }, result.RepositoriesSearched.OrderBy(x => x).ToArray());
        Assert.Contains(result.RelevantFiles, value => value.StartsWith("alpha:", StringComparison.Ordinal));
        Assert.Contains(result.RelevantFiles, value => value.StartsWith("beta:", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_and_search_include_cpp_headers_sources_and_rust_modules()
    {
        Write(_repoOne, "native/RuntimeEngine.hpp", "struct RuntimeEngine {};\nResult buildNativeRuntime(const Config& config);\n");
        Write(_repoOne, "native/RuntimeEngine.cpp", "Result buildNativeRuntime(const Config& config) { return {}; }\n");
        Write(_repoOne, "rust/runtime.rs", "pub struct RustRuntime {}\npub fn verify_runtime_device() -> bool { true }\n");
        ContextGraphStore graph = new(_stateDir);
        RepoIndexService index = new(graph);
        SemanticIndexV2Service service = new(_stateDir, index);
        service.Register(new RegisterSemanticRepositoryRequest { Alias = "native", RepositoryRoot = _repoOne });

        SemanticRepositorySyncResult sync = service.Sync(new SemanticIndexSyncRequest
        {
            Aliases = new List<string> { "native" }
        }).Repositories.Single();

        Assert.Equal(3, sync.IndexedFiles);
        SemanticIndexSearchResponse result = service.Search(new SemanticIndexSearchRequest
        {
            Query = "native runtime device",
            Aliases = new List<string> { "native" },
            MaxFiles = 10,
            MaxSymbols = 20
        });
        Assert.Contains(result.RelevantFiles, value => value.Contains("RuntimeEngine", StringComparison.Ordinal));
        Assert.Contains(result.RelevantSymbols, value => value.Contains("buildNativeRuntime", StringComparison.Ordinal)
            || value.Contains("verify_runtime_device", StringComparison.Ordinal));
    }

    private static void Write(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
