using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalProviderPromotionDocumentCharacterizationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static RetrievalEvalMetrics Metrics(string profileId = "profile-a") => new(
        Cases: 10,
        TopK: 5,
        RecallAtK: .8,
        MeanReciprocalRank: .7,
        NdcgAtK: .75,
        MedianLatencyMilliseconds: 12.5,
        P95LatencyMilliseconds: 30,
        EstimatedResidentBytes: 1 << 30,
        ProfileId: profileId);

    private static RetrievalProviderPromotionRecord Record(
        string kind,
        string providerKey = "key",
        float relevanceThreshold = -1f,
        RetrievalEvalMetrics? baseline = null,
        RetrievalEvalMetrics? candidate = null) => new()
        {
            Kind = kind,
            ProviderKey = providerKey,
            ProviderId = "provider-1",
            ModelId = "model-1",
            Revision = "rev-1",
            Dimensions = kind == "embedding" ? 384 : 0,
            InstructionProfile = "instruction-1",
            RepoId = "owner/repo",
            FileName = "model.gguf",
            LocalSha256 = new string('a', 64),
            RelevanceThreshold = relevanceThreshold,
            BaselineMetrics = baseline,
            CandidateMetrics = candidate,
            ImprovementPercent = 12.5,
            Reason = "candidate clears guardrails",
            PromotedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        };

    [Fact]
    public void New_document_defaults_both_slots_to_null()
    {
        var document = new RetrievalProviderPromotionDocument();

        Assert.Null(document.Embedding);
        Assert.Null(document.Reranker);
    }

    [Fact]
    public void Record_defaults_to_empty_strings_and_sentinel_threshold()
    {
        var record = new RetrievalProviderPromotionRecord();

        Assert.Equal(string.Empty, record.Kind);
        Assert.Equal(string.Empty, record.ProviderKey);
        Assert.Equal(string.Empty, record.ProviderId);
        Assert.Equal(string.Empty, record.ModelId);
        Assert.Equal(string.Empty, record.Revision);
        Assert.Equal(0, record.Dimensions);
        Assert.Null(record.InstructionProfile);
        Assert.Equal(string.Empty, record.RepoId);
        Assert.Equal(string.Empty, record.FileName);
        Assert.Equal(string.Empty, record.LocalSha256);
        Assert.Equal(-1f, record.RelevanceThreshold);
        Assert.Null(record.BaselineMetrics);
        Assert.Null(record.CandidateMetrics);
        Assert.Equal(0.0, record.ImprovementPercent);
        Assert.Equal(string.Empty, record.Reason);
        Assert.Equal(default, record.PromotedUtc);
    }

    [Fact]
    public void Record_properties_round_trip_through_setters()
    {
        var record = Record(
            kind: "reranker",
            providerKey: "reranker-key",
            relevanceThreshold: .25f,
            baseline: Metrics("baseline"),
            candidate: Metrics("candidate"));

        Assert.Equal("reranker", record.Kind);
        Assert.Equal("reranker-key", record.ProviderKey);
        Assert.Equal(.25f, record.RelevanceThreshold);
        Assert.Equal("baseline", record.BaselineMetrics!.ProfileId);
        Assert.Equal("candidate", record.CandidateMetrics!.ProfileId);
    }

    [Fact]
    public void Document_slots_accept_embedding_and_reranker_records_independently()
    {
        var embedding = Record("embedding");
        var reranker = Record("reranker", providerKey: "reranker-key");

        var document = new RetrievalProviderPromotionDocument
        {
            Embedding = embedding,
            Reranker = reranker
        };

        Assert.Same(embedding, document.Embedding);
        Assert.Same(reranker, document.Reranker);
        Assert.NotSame(embedding, document.Reranker);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Json_round_trip_preserves_record_slots(bool withEmbedding, bool withReranker)
    {
        var document = new RetrievalProviderPromotionDocument
        {
            Embedding = withEmbedding ? Record("embedding") : null,
            Reranker = withReranker ? Record("reranker", providerKey: "reranker-key") : null
        };

        string json = JsonSerializer.Serialize(document, WebOptions);
        RetrievalProviderPromotionDocument? restored = JsonSerializer.Deserialize<RetrievalProviderPromotionDocument>(json, WebOptions);

        Assert.NotNull(restored);
        if (withEmbedding)
        {
            Assert.NotNull(restored!.Embedding);
            Assert.Equal("embedding", restored.Embedding!.Kind);
            Assert.Equal("key", restored.Embedding.ProviderKey);
        }
        else
        {
            Assert.Null(restored!.Embedding);
        }

        if (withReranker)
        {
            Assert.NotNull(restored.Reranker);
            Assert.Equal("reranker", restored.Reranker!.Kind);
            Assert.Equal("reranker-key", restored.Reranker.ProviderKey);
        }
        else
        {
            Assert.Null(restored.Reranker);
        }
    }

    [Fact]
    public void Json_round_trip_preserves_null_metrics_and_sentinel_threshold()
    {
        var document = new RetrievalProviderPromotionDocument
        {
            Embedding = Record("embedding", baseline: null, candidate: null)
        };

        string json = JsonSerializer.Serialize(document, WebOptions);
        RetrievalProviderPromotionDocument? restored = JsonSerializer.Deserialize<RetrievalProviderPromotionDocument>(json, WebOptions);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Embedding);
        Assert.Null(restored.Embedding!.BaselineMetrics);
        Assert.Null(restored.Embedding.CandidateMetrics);
        Assert.Equal(-1f, restored.Embedding.RelevanceThreshold);
    }

    [Fact]
    public void Json_round_trip_preserves_full_record_payload()
    {
        var document = new RetrievalProviderPromotionDocument
        {
            Embedding = Record(
                kind: "embedding",
                relevanceThreshold: .5f,
                baseline: Metrics("base"),
                candidate: Metrics("cand"))
        };

        string json = JsonSerializer.Serialize(document, WebOptions);
        RetrievalProviderPromotionDocument? restored = JsonSerializer.Deserialize<RetrievalProviderPromotionDocument>(json, WebOptions);

        RetrievalProviderPromotionRecord record = restored!.Embedding!;
        Assert.Equal("embedding", record.Kind);
        Assert.Equal("provider-1", record.ProviderId);
        Assert.Equal("model-1", record.ModelId);
        Assert.Equal("rev-1", record.Revision);
        Assert.Equal(384, record.Dimensions);
        Assert.Equal("instruction-1", record.InstructionProfile);
        Assert.Equal("owner/repo", record.RepoId);
        Assert.Equal("model.gguf", record.FileName);
        Assert.Equal(new string('a', 64), record.LocalSha256);
        Assert.Equal(.5f, record.RelevanceThreshold);
        Assert.Equal(12.5, record.ImprovementPercent);
        Assert.Equal("candidate clears guardrails", record.Reason);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), record.PromotedUtc);
        Assert.Equal("base", record.BaselineMetrics!.ProfileId);
        Assert.Equal("cand", record.CandidateMetrics!.ProfileId);
        Assert.Equal(10, record.BaselineMetrics.Cases);
        Assert.Equal(5, record.BaselineMetrics.TopK);
        Assert.Equal(.8, record.BaselineMetrics.RecallAtK);
    }

    [Fact]
    public void Json_round_trip_of_empty_document_produces_null_slots()
    {
        string json = JsonSerializer.Serialize(new RetrievalProviderPromotionDocument(), WebOptions);

        RetrievalProviderPromotionDocument? restored = JsonSerializer.Deserialize<RetrievalProviderPromotionDocument>(json, WebOptions);

        Assert.NotNull(restored);
        Assert.Null(restored!.Embedding);
        Assert.Null(restored.Reranker);
    }
}
