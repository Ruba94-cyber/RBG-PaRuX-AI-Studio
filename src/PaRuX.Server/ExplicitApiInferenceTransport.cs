using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Explicit, task-scoped OpenAI-compatible API transport. Unlike legacy CloudFallbackService,
/// this is a separate opt-in path. It is NOT wired into chat or agent routing until UI consent,
/// provider selection and audit integration are complete.
/// </summary>
public sealed class ExplicitApiInferenceTransport
{
    /// <summary>Largest completion budget one remote request may ask for.</summary>
    public const int MaxOutputTokenLimit = 131_072;

    /// <summary>Largest accepted per-token price. Keeps worst-case arithmetic far from decimal overflow.</summary>
    public const decimal MaxPricePerToken = 1_000m;

    private const int MaxPayloadBytes = 1_000_000;

    private readonly HttpClient _http;

    /// <summary>
    /// The production transport. Every connection resolves the host, requires public answers only and
    /// connects to a validated address. Prefer this over supplying an HttpClient, which could bypass that check.
    /// </summary>
    public static ExplicitApiInferenceTransport CreateProduction() =>
        new(new HttpClient(ValidatedRemoteConnector.CreateHandler(static (_, address) => IsPublicAddress(address)))
        {
            Timeout = TimeSpan.FromMinutes(5)
        });

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None)) return false;

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            int a = bytes[0], b = bytes[1];
            return a != 0 && a != 10 && a != 127 && a < 224 &&
                   !(a == 100 && b >= 64 && b <= 127) &&
                   !(a == 169 && b == 254) &&
                   !(a == 172 && b >= 16 && b <= 31) &&
                   !(a == 192 && b == 168) &&
                   !(a == 192 && b == 0) &&
                   !(a == 192 && b == 0 && bytes[2] == 2) &&
                   !(a == 198 && (b == 18 || b == 19)) &&
                   !(a == 198 && b == 51 && bytes[2] == 100) &&
                   !(a == 203 && b == 0 && bytes[2] == 113);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // 2001:db8::/32 is documentation and 2002::/16 is 6to4, which embeds an IPv4 address the IPv4 rules
            // never see. Neither is a public provider destination.
            bool documentation = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8;
            bool sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal &&
                   !address.IsIPv6Multicast &&
                   (bytes[0] & 0xfe) != 0xfc && // unique local fc00::/7
                   (bytes[0] & 0xe0) == 0x20 && // global unicast 2000::/3
                   !documentation && !sixToFour;
        }
        return false;
    }

    /// <summary>
    /// Internal so that only the validated production handler (see <see cref="CreateProduction"/>) or a test
    /// double can carry remote traffic.
    /// </summary>
    internal ExplicitApiInferenceTransport(HttpClient http) =>
        _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Validates every caller-supplied input and returns the worst-case spend of the request: the prompt
    /// (bounded by its UTF-8 size, since every token covers at least one byte) plus the completion limit,
    /// priced per token. Throws ArgumentException for malformed input. Nothing is dispatched here.
    /// </summary>
    internal static decimal Preflight(
        Uri apiBaseUri, string apiKey, string model, JsonElement messages,
        int? maxOutputTokens, decimal? pricePerToken, out string normalizedBase)
    {
        normalizedBase = string.Empty;
        if (apiBaseUri is null || !apiBaseUri.IsAbsoluteUri ||
            !CloudFallbackPolicy.TryNormalizeBaseUrl(apiBaseUri.AbsoluteUri, out normalizedBase))
            throw new ArgumentException("An absolute HTTPS API base URL is required.", nameof(apiBaseUri));
        // Avoid sending billable credentials to loopback, literal IPs or link-local hosts.
        // A hostname resolving to a private address is rejected at the connection layer.
        if (apiBaseUri.IsLoopback || apiBaseUri.HostNameType != UriHostNameType.Dns ||
            apiBaseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            apiBaseUri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A public HTTPS provider hostname is required.", nameof(apiBaseUri));
        if (!string.IsNullOrEmpty(apiBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(apiBaseUri.Query) ||
            !string.IsNullOrEmpty(apiBaseUri.Fragment))
            throw new ArgumentException("Provider base URL must not contain credentials, query or fragment.", nameof(apiBaseUri));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A remote model is required.", nameof(model));
        int payloadBytes = ValidateMessages(messages);
        if (maxOutputTokens is not int completionLimit || completionLimit < 1 || completionLimit > MaxOutputTokenLimit)
            throw new ArgumentException(
                "Every remote request needs an explicit max_tokens between 1 and " + MaxOutputTokenLimit + ".",
                nameof(maxOutputTokens));
        if (pricePerToken is not decimal price || price < 0m || price > MaxPricePerToken)
            throw new ArgumentException(
                "Every remote request needs an explicit non-negative per-token price.", nameof(pricePerToken));

        return (payloadBytes + completionLimit) * price;
    }

    /// <summary>
    /// Returns a live response for an authorized request. Caller owns the response and must dispose it.
    /// The worst-case spend is computed from the request itself and checked against the approved budget
    /// before serialization or HTTP dispatch. Internal on purpose: the only route to this method is
    /// <see cref="ExplicitApiTaskRunner"/>, which consumes a single-use grant first.
    /// </summary>
    internal async Task<HttpResponseMessage> SendChatAsync(
        ProviderExecutionPolicy.Request policyRequest,
        Uri apiBaseUri,
        string apiKey,
        string model,
        JsonElement messages,
        int? maxOutputTokens,
        decimal? pricePerToken,
        CancellationToken cancellationToken,
        bool stream = false)
    {
        ArgumentNullException.ThrowIfNull(policyRequest);
        decimal worstCaseCost = Preflight(
            apiBaseUri, apiKey, model, messages, maxOutputTokens, pricePerToken, out string normalizedBase);
        if (policyRequest.RemainingApprovedBudget is null)
            throw new InvalidOperationException("Remote inference denied: no approved budget was supplied.");

        ProviderExecutionPolicy.Decision decision = ProviderExecutionPolicy.Evaluate(
            policyRequest with { EstimatedRemoteCost = worstCaseCost });
        if (!decision.Allowed || decision.Destination != ProviderExecutionPolicy.Destination.Remote)
            throw new InvalidOperationException("Remote inference denied: " + decision.Reason);

        // Restrict this first transport to the explicit OpenAI-compatible chat protocol. The target is the normalized
        // base URL plus the fixed chat-completions path, and the runner's grant binds that same base URL, so an
        // approval cannot be spent on another path of the provider.
        using var outgoing = new HttpRequestMessage(
            HttpMethod.Post, normalizedBase + "/chat/completions");
        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        outgoing.Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, stream, max_tokens = maxOutputTokens!.Value }),
            Encoding.UTF8, "application/json");

        // No retries: a retry could double-bill or transmit the prompt again.
        HttpResponseMessage response = await _http.SendAsync(
            outgoing, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Do not expose provider response bodies, which can contain sensitive data.
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Remote API returned HTTP {status}.");
        }
        return response;
    }

    /// <summary>
    /// Restricts the payload to text chat messages with a supported role and string content, and no other
    /// property: the consent flow discloses exactly role and content, so anything else is refused rather than sent.
    /// Returns the payload's UTF-8 size so the caller can bound the prompt without serializing it a second time.
    /// </summary>
    private static int ValidateMessages(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            throw new ArgumentException("Messages must be a non-empty JSON array.", nameof(messages));
        int payloadBytes = Encoding.UTF8.GetByteCount(messages.GetRawText());
        if (payloadBytes > MaxPayloadBytes)
            throw new ArgumentException("Messages exceed the maximum permitted payload.", nameof(messages));

        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Only text chat messages with role and content are supported.", nameof(messages));
            string? roleName = role.GetString();
            if (roleName is not ("system" or "developer" or "user" or "assistant"))
                throw new ArgumentException("Unsupported remote message role.", nameof(messages));

            // The message is serialized exactly as received, so an extra property (an attachment, an image, a tool
            // call) or a duplicate would reach the provider undisclosed. Refuse the payload instead of stripping it.
            int properties = 0;
            foreach (JsonProperty property in message.EnumerateObject())
            {
                if (property.Name is not ("role" or "content"))
                    throw new ArgumentException("Remote messages may contain only role and content.", nameof(messages));
                properties++;
            }
            if (properties != 2)
                throw new ArgumentException("Remote messages may contain only role and content.", nameof(messages));
        }
        return payloadBytes;
    }
}
