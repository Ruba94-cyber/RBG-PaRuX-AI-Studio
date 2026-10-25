namespace PaRuX.Server;

/// <summary>
/// Qwen3-style retrieval embeddings are instruction-aware on the query side. Documents/chunks are
/// embedded as their original text, while a query can be prefixed with the promoted provider's
/// externally-evaluated instruction profile. MiniLM and providers with no real instruction keep
/// their input byte-for-byte unchanged.
/// </summary>
internal static class RetrievalEmbeddingQueryFormatter
{
    public static string PrepareQuery(RetrievalEmbeddingIdentity identity, string query)
    {
        ArgumentNullException.ThrowIfNull(identity);
        query ??= string.Empty;
        if (!string.Equals(identity.ProviderId, "llama-cpp-embedding", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.InstructionProfile)
            || string.Equals(identity.InstructionProfile, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity.InstructionProfile, "retrieval", StringComparison.OrdinalIgnoreCase))
            return query;

        return "Instruct: " + identity.InstructionProfile.Trim() + "\nQuery: " + query.Trim();
    }

    public static async Task<float[]?> EmbedQueryAsync(
        ResolvedRetrievalEmbeddingProvider provider,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.IsAvailable || string.IsNullOrWhiteSpace(query)) return null;
        IReadOnlyList<float[]?> result = await provider.EmbedBatchAsync(
            new[] { PrepareQuery(provider.Identity, query) },
            cancellationToken).ConfigureAwait(false);
        return result.Count == 1 ? result[0] : null;
    }
}
