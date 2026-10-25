using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Mirrors EmbeddingServiceTests' own convention exactly: the hard requirement is that
/// RerankerService (and, through it, RagStore's reranking pass) degrades to null/unchanged
/// ordering rather than throwing whenever the engine isn't installed - exercised with a real
/// RerankerService pointed at an empty temp directory rather than the machine's actual
/// %LOCALAPPDATA%\PaRuX\ai-runtimes\reranker\ (which may or may not have the model downloaded),
/// so this is deterministic regardless of what's installed on the runner.
/// </summary>
public sealed class RerankerServiceTests
{
    private static RerankerService NoEngine() =>
        new(Path.Combine(Path.GetTempPath(), "parux-reranker-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Status_reports_not_installed_when_no_model_files_are_present()
    {
        RerankerEngineStatus status = NoEngine().GetStatus();

        Assert.False(status.Installed);
        Assert.False(status.Loaded);
    }

    [Fact]
    public void TryScore_returns_null_instead_of_throwing_when_the_engine_is_not_installed()
    {
        Assert.Null(NoEngine().TryScore("what is the capital of france", "paris is the capital of france"));
    }

    [Theory]
    [InlineData(null, "a real document")]
    [InlineData("", "a real document")]
    [InlineData("   ", "a real document")]
    [InlineData("a real query", null)]
    [InlineData("a real query", "")]
    public void TryScore_returns_null_for_empty_or_whitespace_input_even_with_no_engine(string? query, string? document)
    {
        Assert.Null(NoEngine().TryScore(query, document));
    }

    [Fact]
    public void Rerank_returns_an_empty_list_when_the_engine_is_not_installed()
    {
        List<RerankResult> results = NoEngine().Rerank("query", new[] { "doc a", "doc b", "doc c" });
        Assert.Empty(results);
    }

    [Fact]
    public void Rerank_returns_an_empty_list_for_no_documents()
    {
        Assert.Empty(NoEngine().Rerank("query", Array.Empty<string>()));
    }

    [Fact]
    public void Dispose_does_not_throw_when_the_engine_was_never_loaded()
    {
        var exception = Record.Exception(() => NoEngine().Dispose());
        Assert.Null(exception);
    }
}

/// <summary>
/// RagStore's reranking pass is entirely opt-in (a null RerankerService, the default for every
/// pre-existing constructor overload, makes ApplyRerankIfAvailable a pure passthrough) - these
/// tests confirm both that the opt-out default really is a no-op, and that supplying a real
/// (but not-installed, hence null-scoring) RerankerService doesn't change behavior either, since
/// TryScore's graceful-degradation contract makes "supplied but not installed" indistinguishable
/// in outcome from "not supplied at all". A genuine cross-encoder re-sort would need the real
/// ONNX model this sandbox cannot download (huggingface.co is blocked here - see
/// RerankerService's own doc comment) and is therefore not exercised end-to-end in this suite.
/// </summary>
public sealed class RagStoreRerankerTests
{
    private static RerankerService NoEngine() =>
        new(Path.Combine(Path.GetTempPath(), "parux-rag-reranker-test-" + Guid.NewGuid().ToString("N")));

    private static EmbeddingService NoEmbeddingEngine() =>
        new(Path.Combine(Path.GetTempPath(), "parux-rag-embed-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Retrieve_behaves_identically_whether_or_not_a_not_installed_reranker_is_supplied()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-rag-reranker-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"),
            "The deploy script lives at scripts/deploy and takes the environment name as its only argument.");

        var withoutReranker = new RagStore(NoEmbeddingEngine());
        var withUninstalledReranker = new RagStore(NoEmbeddingEngine(), NoEngine());

        RagSourceDto sourceA = withoutReranker.AddSource(folder);
        RagSourceDto sourceB = withUninstalledReranker.AddSource(folder);
        try
        {
            await withoutReranker.IndexSourceAsync(sourceA.Id, folder, null, CancellationToken.None);
            await withUninstalledReranker.IndexSourceAsync(sourceB.Id, folder, null, CancellationToken.None);

            // Compared over this test's own folder only. Retrieve reads the whole store, and the
            // store is shared with every other suite in the run - so a top-4 taken twice can differ
            // simply because something else indexed content in between. That made this test fail in
            // a full run and pass on its own, which is a property of the assertion, not of the
            // reranker argument it is meant to be about.
            List<string> a = withoutReranker.Retrieve("Where is the deploy script?", 12)
                .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content).ToList();
            List<string> b = withUninstalledReranker.Retrieve("Where is the deploy script?", 12)
                .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content).ToList();

            Assert.NotEmpty(a);
            Assert.Equal(a, b);
        }
        finally
        {
            withoutReranker.RemoveSource(sourceA.Id);
            withUninstalledReranker.RemoveSource(sourceB.Id);
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }
}
