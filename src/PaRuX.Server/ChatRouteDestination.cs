using System.Net;

namespace PaRuX.Server;

/// <summary>
/// Where a routed chat turn runs, reported on the model_routed event. It names the provider and whether the work left
/// this machine: the managed local runtime is "local", and an endpoint is "local" only when its host is localhost or a
/// loopback/private address. Only a remote host is ever reported - never a path, query or credential.
/// </summary>
internal sealed record ChatRouteDestination(string Provider, string Locality, string? EndpointHost)
{
    /// <summary>PaRuX's managed local runtime.</summary>
    internal const string ManagedProvider = "local";

    /// <summary>The OpenAI-compatible endpoint the user configured for the runtime.</summary>
    internal const string ExternalProvider = "external-openai-compatible";

    internal static ChatRouteDestination Describe(bool usesExternalProvider, string? endpoint)
    {
        if (!usesExternalProvider) return new ChatRouteDestination(ManagedProvider, "local", null);

        // Classified by host, not by whether the endpoint would be accepted as configuration: a remote host must never
        // be reported as local because its URL is malformed or over-specified.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
            return new ChatRouteDestination(ExternalProvider, "remote", null);

        return IsLocalHost(uri.Host)
            ? new ChatRouteDestination(ExternalProvider, "local", null)
            : new ChatRouteDestination(ExternalProvider, "remote", uri.Host);
    }

    private static bool IsLocalHost(string host)
    {
        string literal = host.Trim('[', ']');
        return string.Equals(literal, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(literal, out IPAddress? address) && ExternalTextProviderService.IsLocalAddress(address));
    }
}
