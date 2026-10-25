using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the two things that must never regress for this feature: the pure vector math
/// (cosine similarity, the SQLite BLOB round-trip), and - the hard requirement from the spec -
/// that Memory/RAG retrieval degrades to their original plain keyword-overlap ranking, not an
/// exception, whenever the embedding engine isn't installed/loadable. That path is exercised
/// with a real EmbeddingService pointed at an empty directory rather than the machine's actual
/// %LOCALAPPDATA%\PaRuX\ai-runtimes\embeddings\ (which may or may not have the ~90 MB model
/// downloaded), so the test is deterministic regardless of what's installed on the runner.
/// </summary>
public sealed class EmbeddingServiceTests
{
    private static EmbeddingService NoEngine() =>
        new(Path.Combine(Path.GetTempPath(), "parux-embed-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Status_reports_not_installed_when_no_model_files_are_present()
    {
        EmbeddingEngineStatus status = NoEngine().GetStatus();

        Assert.False(status.Installed);
        Assert.False(status.Loaded);
        Assert.Null(status.Dimensions);
    }

    [Fact]
    public void TryEmbed_returns_null_instead_of_throwing_when_the_engine_is_not_installed()
    {
        float[]? embedding = NoEngine().TryEmbed("this should not throw");

        Assert.Null(embedding);
    }

    [Fact]
    public void TryEmbed_returns_null_for_empty_or_whitespace_text_even_with_no_engine()
    {
        EmbeddingService service = NoEngine();

        Assert.Null(service.TryEmbed(""));
        Assert.Null(service.TryEmbed("   "));
        Assert.Null(service.TryEmbed(null));
    }

    [Fact]
    public void CosineSimilarity_is_one_for_identical_vectors_and_zero_for_orthogonal_vectors()
    {
        float[] a = { 1f, 0f, 0f, 0f };
        float[] b = { 1f, 0f, 0f, 0f };
        float[] c = { 0f, 1f, 0f, 0f };

        Assert.Equal(1f, EmbeddingService.CosineSimilarity(a, b), 5);
        Assert.Equal(0f, EmbeddingService.CosineSimilarity(a, c), 5);
    }

    [Fact]
    public void CosineSimilarity_of_a_normalized_opposite_vector_is_negative_one()
    {
        float[] a = { 1f, 0f, 0f };
        float[] negA = { -1f, 0f, 0f };

        Assert.Equal(-1f, EmbeddingService.CosineSimilarity(a, negA), 5);
    }

    [Fact]
    public void ToBytes_and_FromBytes_round_trip_a_vector_exactly()
    {
        float[] original = { 0.125f, -0.5f, 3.25f, 0f, 1e-6f, -1e-6f };

        byte[] bytes = EmbeddingService.ToBytes(original);
        float[]? restored = EmbeddingService.FromBytes(bytes);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void FromBytes_returns_null_for_empty_or_malformed_input()
    {
        Assert.Null(EmbeddingService.FromBytes(null));
        Assert.Null(EmbeddingService.FromBytes(Array.Empty<byte>()));
        Assert.Null(EmbeddingService.FromBytes(new byte[] { 1, 2, 3 })); // not a multiple of sizeof(float)
    }

    [Fact]
    public void Memory_retrieve_falls_back_to_keyword_overlap_when_the_embedding_engine_is_absent()
    {
        var memory = new MemoryStore(NoEngine());
        MemoryEntryDto entry = memory.Add(new MemoryAddRequest
        {
            Scope = "User",
            Content = "The deploy script lives at scripts/deploy and takes the environment name as its only argument."
        });
        try
        {
            List<MemoryEntryDto> hit = memory.Retrieve("Where is the deploy script?", 5);
            Assert.Contains(hit, m => m.Id == entry.Id);

            List<MemoryEntryDto> miss = memory.Retrieve("What's the weather like today?", 5);
            Assert.DoesNotContain(miss, m => m.Id == entry.Id);
        }
        finally
        {
            memory.Delete(entry.Id);
        }
    }

    [Fact]
    public void Memory_add_and_retrieve_do_not_throw_when_the_embedding_engine_is_absent()
    {
        // The graceful-degradation contract end to end: Add() computes (and silently skips) an
        // embedding, Retrieve() ranks by keyword overlap - neither should ever throw just
        // because the model isn't on disk.
        var memory = new MemoryStore(NoEngine());
        var exception = Record.Exception(() =>
        {
            MemoryEntryDto entry = memory.Add(new MemoryAddRequest { Scope = "User", Content = "no engine installed test entry" });
            memory.Retrieve("no engine installed test entry", 5);
            memory.Delete(entry.Id);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task Rag_retrieve_falls_back_to_keyword_overlap_when_the_embedding_engine_is_absent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-rag-embed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"),
            "The deploy script lives at scripts/deploy and takes the environment name as its only argument.");

        var rag = new RagStore(NoEngine());
        RagSourceDto source = rag.AddSource(folder);
        try
        {
            await rag.IndexSourceAsync(source.Id, folder, null, CancellationToken.None);

            List<RagChunkMatch> hit = rag.Retrieve("Where is the deploy script?", 4);
            Assert.Contains(hit, m => m.FilePath.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));

            List<RagChunkMatch> miss = rag.Retrieve("What's the weather like today?", 4);
            Assert.DoesNotContain(miss, m => m.FilePath.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            rag.RemoveSource(source.Id);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
