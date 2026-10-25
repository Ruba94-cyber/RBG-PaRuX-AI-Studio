using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Chooses the production discovery provider. Default is Hugging Face. Only when an explicit adapter endpoint is configured
/// (<c>PARUX_MODEL_DISCOVERY_ADAPTER_URL</c>) is the versioned adapter used instead. An explicit but unsafe endpoint never silently
/// falls back to Hugging Face: discovery fails with the typed <c>unsafe_endpoint</c> code.
/// </summary>
internal static class ModelDiscoveryProviderSelector
{
    internal const string EndpointVariable = "PARUX_MODEL_DISCOVERY_ADAPTER_URL";

    internal static IModelDiscoveryProvider Create(string? configuredEndpoint, Func<IModelDiscoveryProvider> defaultProvider,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(configuredEndpoint)) return defaultProvider();
        try
        {
            if (!Uri.TryCreate(configuredEndpoint.Trim(), UriKind.Absolute, out Uri? feed))
                throw new ModelDiscoveryFetchException("unsafe_endpoint");
            return new VersionedModelDiscoveryAdapter(feed, http);
        }
        catch (ModelDiscoveryFetchException ex)
        {
            return new RefusedProvider(ex.Code);
        }
    }

    private sealed class RefusedProvider(string code) : IModelDiscoveryProvider
    {
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new ModelDiscoveryFetchException(code);
    }
}
