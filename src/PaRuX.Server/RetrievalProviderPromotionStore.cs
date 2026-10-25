using System.Security.Cryptography;
using System.Text.Json;

namespace PaRuX.Server;

public sealed class RetrievalProviderPromotionRecord
{
    public string Kind { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string? InstructionProfile { get; set; }
    public string RepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string LocalSha256 { get; set; } = string.Empty;
    public float RelevanceThreshold { get; set; } = -1f;
    public RetrievalEvalMetrics? BaselineMetrics { get; set; }
    public RetrievalEvalMetrics? CandidateMetrics { get; set; }
    public double ImprovementPercent { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime PromotedUtc { get; set; }
}

public sealed class RetrievalProviderPromotionDocument
{
    public RetrievalProviderPromotionRecord? Embedding { get; set; }
    public RetrievalProviderPromotionRecord? Reranker { get; set; }
}

/// <summary>
/// Durable fail-closed selection for RAG providers. An installed GGUF is only a candidate. It is
/// written here only after RetrievalEvaluationService accepts comparable external ground-truth
/// evidence. The selected artifact is bound to its local SHA-256 so an upstream/model update
/// automatically invalidates the previous promotion until the new bytes are evaluated again.
/// </summary>
public sealed class RetrievalProviderPromotionStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public RetrievalProviderPromotionStore() : this(Path.Combine(
        ServerState.StateDirectory, "retrieval-provider-promotion.json")) { }

    internal RetrievalProviderPromotionStore(string path) => _path = path;

    public RetrievalProviderPromotionDocument Get()
    {
        lock (_gate) return Clone(LoadUnsafe());
    }

    public RetrievalProviderPromotionRecord PromoteEmbedding(
        RetrievalEmbeddingIdentity identity,
        string repoId,
        string fileName,
        string localSha256,
        RetrievalEvalMetrics baseline,
        RetrievalEvalMetrics candidate,
        float relevanceThreshold = -1f,
        RetrievalPromotionOptions? options = null)
    {
        ValidateArtifact(repoId, fileName, localSha256);
        if (!float.IsFinite(relevanceThreshold) || relevanceThreshold is < -1 or > 1)
            throw new InvalidDataException("Embedding relevance threshold must be a finite cosine score in [-1, 1].");
        RetrievalPromotionDecision decision = new RetrievalEvaluationService().Compare(baseline, candidate, options);
        if (!decision.Promote)
            throw new InvalidOperationException("Retrieval provider did not clear promotion guardrails: " + decision.Reason);

        var record = new RetrievalProviderPromotionRecord
        {
            Kind = "embedding",
            ProviderKey = identity.StableKey,
            ProviderId = identity.ProviderId,
            ModelId = identity.ModelId,
            Revision = identity.Revision,
            Dimensions = identity.Dimensions,
            InstructionProfile = identity.InstructionProfile,
            RepoId = repoId,
            FileName = Path.GetFileName(fileName),
            LocalSha256 = NormalizeSha(localSha256),
            RelevanceThreshold = relevanceThreshold,
            BaselineMetrics = baseline,
            CandidateMetrics = candidate,
            // decision.Promote was already asserted true above, and Promote=true is only ever
            // reached through Compare's finite composite-improvement branch, never Reject - so
            // ImprovementPercent is guaranteed non-null here; .Value documents that invariant
            // instead of silently coalescing a real logic error to 0.
            ImprovementPercent = decision.ImprovementPercent!.Value,
            Reason = decision.Reason,
            PromotedUtc = DateTime.UtcNow
        };
        lock (_gate)
        {
            RetrievalProviderPromotionDocument document = LoadUnsafe();
            document.Embedding = record;
            SaveUnsafe(document);
        }
        return Clone(record);
    }

    public RetrievalProviderPromotionRecord PromoteReranker(
        RetrievalRerankerIdentity identity,
        string repoId,
        string fileName,
        string localSha256,
        RetrievalEvalMetrics baseline,
        RetrievalEvalMetrics candidate,
        RetrievalPromotionOptions? options = null)
    {
        ValidateArtifact(repoId, fileName, localSha256);
        RetrievalPromotionDecision decision = new RetrievalEvaluationService().Compare(baseline, candidate, options);
        if (!decision.Promote)
            throw new InvalidOperationException("Retrieval reranker did not clear promotion guardrails: " + decision.Reason);

        var record = new RetrievalProviderPromotionRecord
        {
            Kind = "reranker",
            ProviderKey = identity.StableKey,
            ProviderId = identity.ProviderId,
            ModelId = identity.ModelId,
            Revision = identity.Revision,
            Dimensions = 0,
            InstructionProfile = identity.InstructionProfile,
            RepoId = repoId,
            FileName = Path.GetFileName(fileName),
            LocalSha256 = NormalizeSha(localSha256),
            BaselineMetrics = baseline,
            CandidateMetrics = candidate,
            // decision.Promote was already asserted true above, and Promote=true is only ever
            // reached through Compare's finite composite-improvement branch, never Reject - so
            // ImprovementPercent is guaranteed non-null here; .Value documents that invariant
            // instead of silently coalescing a real logic error to 0.
            ImprovementPercent = decision.ImprovementPercent!.Value,
            Reason = decision.Reason,
            PromotedUtc = DateTime.UtcNow
        };
        lock (_gate)
        {
            RetrievalProviderPromotionDocument document = LoadUnsafe();
            document.Reranker = record;
            SaveUnsafe(document);
        }
        return Clone(record);
    }

    public void ResetEmbedding()
    {
        lock (_gate)
        {
            RetrievalProviderPromotionDocument document = LoadUnsafe();
            document.Embedding = null;
            SaveUnsafe(document);
        }
    }

    public void ResetReranker()
    {
        lock (_gate)
        {
            RetrievalProviderPromotionDocument document = LoadUnsafe();
            document.Reranker = null;
            SaveUnsafe(document);
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static void ValidateArtifact(string repoId, string fileName, string sha)
    {
        HuggingFaceManagedModelUpdateSource.ValidateRepoId(repoId);
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("Retrieval promotion requires a simple managed model file name.");
        _ = NormalizeSha(sha);
    }

    private static string NormalizeSha(string value)
    {
        string sha = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (sha.Length != 64 || sha.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("Retrieval provider artifact SHA-256 is invalid.");
        return sha;
    }

    private RetrievalProviderPromotionDocument LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<RetrievalProviderPromotionDocument>(
                       File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void SaveUnsafe(RetrievalProviderPromotionDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static RetrievalProviderPromotionDocument Clone(RetrievalProviderPromotionDocument value) => new()
    {
        Embedding = value.Embedding == null ? null : Clone(value.Embedding),
        Reranker = value.Reranker == null ? null : Clone(value.Reranker)
    };

    private static RetrievalProviderPromotionRecord Clone(RetrievalProviderPromotionRecord value) => new()
    {
        Kind = value.Kind,
        ProviderKey = value.ProviderKey,
        ProviderId = value.ProviderId,
        ModelId = value.ModelId,
        Revision = value.Revision,
        Dimensions = value.Dimensions,
        InstructionProfile = value.InstructionProfile,
        RepoId = value.RepoId,
        FileName = value.FileName,
        LocalSha256 = value.LocalSha256,
        RelevanceThreshold = value.RelevanceThreshold,
        BaselineMetrics = value.BaselineMetrics,
        CandidateMetrics = value.CandidateMetrics,
        ImprovementPercent = value.ImprovementPercent,
        Reason = value.Reason,
        PromotedUtc = value.PromotedUtc
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
