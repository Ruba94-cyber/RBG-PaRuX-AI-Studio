using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RepoIndexLanguageCoverageTests : IDisposable
{
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "parux-repo-language-db-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "parux-repo-language-src-" + Guid.NewGuid().ToString("N"));

    public RepoIndexLanguageCoverageTests() => Directory.CreateDirectory(_repoDir);

    public void Dispose()
    {
        DeleteBestEffort(_dbDir);
        DeleteBestEffort(_repoDir);
    }

    [Fact]
    public void Java_extractor_finds_modified_types_and_methods()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "Worker.java",
            new[]
            {
                "public final class Worker {",
                "  public static Result execute(Request request) {",
                "    return null;",
                "  }",
                "}"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "Worker" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "execute" && symbol.LineNumber == 2);
    }

    [Fact]
    public void Kotlin_extractor_finds_data_classes_and_suspend_functions()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "Repository.kt",
            new[]
            {
                "data class UserRecord(val id: String)",
                "suspend fun loadUser(",
                "    id: String",
                "): UserRecord = TODO()"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "UserRecord" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "loadUser" && symbol.LineNumber == 2);
    }

    [Fact]
    public void Go_extractor_finds_receiver_functions_and_struct_types()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "store.go",
            new[]
            {
                "type Store struct { value string }",
                "func (s *Store) Load(key string) string {",
                "  return s.value",
                "}"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "Store" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "Load" && symbol.LineNumber == 2);
    }

    [Fact]
    public void Ruby_extractor_finds_namespaced_types_and_bang_methods()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "worker.rb",
            new[]
            {
                "class Jobs::Worker",
                "  def self.perform!",
                "  end",
                "end"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "Jobs::Worker" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "perform!" && symbol.LineNumber == 2);
    }

    [Fact]
    public void Cpp_extractor_finds_types_and_functions_in_sources_and_headers()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "engine.hpp",
            new[]
            {
                "struct RuntimeState { int value; };",
                "static Result buildRuntime(const Config& config);",
                "class EngineController {",
                "};"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "RuntimeState" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "buildRuntime" && symbol.LineNumber == 2);
        Assert.Contains(symbols, symbol => symbol.Name == "EngineController" && symbol.LineNumber == 3);
    }

    [Fact]
    public void Rust_extractor_finds_public_types_traits_and_functions()
    {
        List<(string Name, string Signature, int LineNumber)> symbols = RepoIndexService.ExtractSymbols(
            "runtime.rs",
            new[]
            {
                "pub struct RuntimeState { value: i32 }",
                "pub trait EngineAdapter {",
                "pub async fn execute_job(id: String) -> Result<(), Error> {",
                "}"
            });

        Assert.Contains(symbols, symbol => symbol.Name == "RuntimeState" && symbol.LineNumber == 1);
        Assert.Contains(symbols, symbol => symbol.Name == "EngineAdapter" && symbol.LineNumber == 2);
        Assert.Contains(symbols, symbol => symbol.Name == "execute_job" && symbol.LineNumber == 3);
    }

    [Fact]
    public void Full_index_makes_symbols_from_all_declared_non_dotnet_languages_searchable()
    {
        Write("java/BackupWorker.java", "public final class BackupWorker {\n  public void exportArchive() {}\n}\n");
        Write("kotlin/BackupRepository.kt", "class BackupRepository\nsuspend fun verifyArchive() = Unit\n");
        Write("go/backup.go", "type BackupStore struct {}\nfunc (s *BackupStore) RestoreArchive() {}\n");
        Write("ruby/backup.rb", "class BackupCoordinator\n  def restore_archive!\n  end\nend\n");
        Write("native/ArchiveEngine.cpp", "struct ArchiveEngine {};\nResult restoreNativeArchive(const Config& config) { return {}; }\n");
        Write("rust/archive.rs", "pub struct ArchiveLedger {}\npub fn verify_native_archive() -> bool { true }\n");

        ContextGraphStore graph = new(_dbDir);
        RepoIndexService index = new(graph);
        PaRuX.Contracts.RepoIndexResult result = index.IndexRepository(_repoDir);

        Assert.Equal(6, result.FilesIndexed);
        Assert.True(result.SymbolsIndexed >= 12);
        Assert.Contains(index.SearchContext(_repoDir, "export archive").RelevantSymbols,
            value => value.Contains("exportArchive", StringComparison.Ordinal));
        Assert.Contains(index.SearchContext(_repoDir, "verify archive").RelevantSymbols,
            value => value.Contains("verifyArchive", StringComparison.Ordinal));
        Assert.Contains(index.SearchContext(_repoDir, "restore archive").RelevantSymbols,
            value => value.Contains("RestoreArchive", StringComparison.Ordinal)
                || value.Contains("restore_archive", StringComparison.Ordinal));
        Assert.Contains(index.SearchContext(_repoDir, "native archive").RelevantSymbols,
            value => value.Contains("restoreNativeArchive", StringComparison.Ordinal)
                || value.Contains("verify_native_archive", StringComparison.Ordinal));
    }

    private void Write(string relativePath, string content)
    {
        string fullPath = Path.Combine(_repoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
