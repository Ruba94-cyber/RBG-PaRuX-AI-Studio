using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Legacy-named registry/probe for competitor OpenAI-compatible endpoints.
///
/// IMPORTANT: this service is no longer a PaRuX inference provider. Ollama, LM Studio, koboldcpp,
/// text-generation-webui and similar launchers exist here only so PaRuX can discover, capability-
/// probe and benchmark them under the same workload. <see cref="GetConfiguredEndpoint"/> is
/// deliberately fail-closed and always returns null so an old caller cannot accidentally turn a
/// benchmark target into the chat/coding runtime. PaRuX-managed inference is authoritative.
///
/// The old endpoint/config shape remains readable for migration and benchmark UI compatibility.
/// Cloud fallback is a separate explicit feature and never flows through this class.
/// </summary>
public sealed class ExternalTextProviderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, StructuredOutputProbeEvidence> StructuredOutputProbeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PositiveStructuredOutputProbeTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NegativeStructuredOutputProbeTtl = TimeSpan.FromMinutes(10);

    private static string ConfigPath => Path.Combine(
        PaRuXDataRoot.Path, "local-providers", "text.local.json");

    internal static RemoteProviderPolicy Policy { get; set; } = new();

    private static bool RemoteAllowed()
    {
        try { return Policy.IsRemoteAllowed(); }
        catch { return false; }
    }

    private static readonly (string Name, int Port)[] KnownBackends =
    {
        ("Ollama", 11434),
        ("LM Studio", 1234),
        ("koboldcpp", 5001),
        ("text-generation-webui", 5000),
    };

    internal sealed record StructuredOutputProbeEvidence(
        string Endpoint,
        bool Supported,
        DateTime CheckedUtc,
        string Detail);

    public async Task<List<DetectedTextBackend>> DetectAsync(CancellationToken ct)
    {
        var results = new List<DetectedTextBackend>();
        foreach ((string name, int port) in KnownBackends)
        {
            string endpoint = "http://127.0.0.1:" + port;
            if (await ProbeAsync(endpoint, ct).ConfigureAwait(false))
                results.Add(new DetectedTextBackend { Name = name, Endpoint = endpoint });
        }
        return results;
    }

    /// <summary>Status of the currently registered COMPETITOR BENCHMARK TARGET. The contract keeps
    /// its historical name so existing desktop builds can migrate without a wire break.</summary>
    public async Task<ExternalTextProviderStatus> GetStatusAsync(CancellationToken ct)
    {
        Config? config = TryReadConfig();
        if (config == null || string.IsNullOrWhiteSpace(config.Endpoint))
            return new ExternalTextProviderStatus { Configured = false };

        bool reachable = await ProbeAsync(config.Endpoint, ct).ConfigureAwait(false);
        return new ExternalTextProviderStatus
        {
            Configured = true,
            ProviderName = string.IsNullOrWhiteSpace(config.ProviderName) ? "competitor" : config.ProviderName,
            Endpoint = config.Endpoint,
            Reachable = reachable,
            IsRemote = IsRemoteEndpoint(config.Endpoint)
        };
    }

    /// <summary>Backward-compatible setter for the benchmark target registry. It does NOT alter the
    /// PaRuX runtime.</summary>
    public void SetProvider(string providerName, string endpoint)
    {
        bool remoteAllowed = RemoteAllowed();
        if (!TryNormalizeEndpoint(endpoint, remoteAllowed, out string normalized))
        {
            throw new InvalidDataException(
                !remoteAllowed && TryNormalizeEndpoint(endpoint, allowRemote: true, out _)
                    ? "That benchmark endpoint is not on this machine or its private LAN. Enable the explicit remote-provider opt-in only if you intentionally want to benchmark a remote endpoint."
                    : "Use localhost or a literal loopback/private-LAN IP address over http(s), with no credentials/query/fragment.");
        }

        // The stored remote credential belongs to the endpoint it was entered for. Repointing the benchmark target at a
        // different endpoint drops it first, so it cannot follow the target to a host the user never gave it to.
        if (!SameTarget(TryReadConfig()?.Endpoint, normalized) && Policy.HasApiKey())
            Policy.SetApiKey(null);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config
        {
            ProviderName = string.IsNullOrWhiteSpace(providerName) ? "competitor" : providerName.Trim(),
            Endpoint = normalized
        }, JsonOptions));
    }

    public void ClearProvider()
    {
        try { File.Delete(ConfigPath); } catch { }
    }

    /// <summary>
    /// Runtime escape hatch RETIRED. Kept only so old source that still compiles against this method
    /// fails safe instead of routing inference to a competitor launcher.
    /// </summary>
    [Obsolete("Competitor endpoints are benchmark targets only. PaRuX runtime is always managed by PaRuX.")]
    public string? GetConfiguredEndpoint() => null;

    /// <summary>Returns the registered endpoint for explicit benchmark/probe code only.</summary>
    public string? GetBenchmarkEndpoint() => TryReadConfig()?.Endpoint;

    internal static Task<bool> ProbeAsync(string endpoint, CancellationToken ct) =>
        ProbeAsync(endpoint, RemoteAllowed(), ct, resolver: null);

    /// <summary>
    /// Reachability probe. <paramref name="allowRemote"/> and <paramref name="resolver"/> are explicit so the
    /// connect-time rules can be exercised without touching the stored policy or the real resolver.
    /// </summary>
    internal static async Task<bool> ProbeAsync(
        string endpoint, bool allowRemote, CancellationToken ct, ValidatedRemoteConnector.HostResolver? resolver,
        InFlightRequestRegistry? inFlight = null)
    {
        if (!TryNormalizeEndpoint(endpoint, allowRemote, out string safeEndpoint)) return false;
        using InFlightRequestLease? remoteCall = BeginRemoteCall(safeEndpoint, ct, inFlight);
        try
        {
            using HttpClient http = CreateProbeClient(TimeSpan.FromSeconds(4), resolver);
            using HttpResponseMessage response = await http.GetAsync(
                safeEndpoint + "/v1/models", remoteCall?.Token ?? ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Admits an outbound call to a non-local target under the remote-provider scope, so turning the opt-in off cancels
    /// it. Local targets are not remote egress and are not admitted.
    /// </summary>
    private static InFlightRequestLease? BeginRemoteCall(
        string safeEndpoint, CancellationToken ct, InFlightRequestRegistry? inFlight) =>
        IsRemoteEndpoint(safeEndpoint)
            ? (inFlight ?? InFlightRequestRegistry.Shared).Begin(InFlightRequestRegistry.RemoteProviderScope, ct)
            : null;

    /// <summary>
    /// Client for benchmark probes. It never follows a redirect (a Location header could move the request to
    /// another host), and every connection is checked when it is opened: a remote target may only resolve to
    /// public addresses, while a local target may reach loopback or LAN addresses only.
    /// </summary>
    internal static HttpClient CreateProbeClient(
        TimeSpan timeout, ValidatedRemoteConnector.HostResolver? resolver = null) =>
        new(CreateProbeHandler(resolver)) { Timeout = timeout };

    internal static SocketsHttpHandler CreateProbeHandler(ValidatedRemoteConnector.HostResolver? resolver = null) =>
        ValidatedRemoteConnector.CreateHandler(IsAllowedProbeAddress, resolver);

    /// <summary>
    /// Connect-time rule for one resolved address of a probe target. Local targets (localhost, or a literal
    /// loopback/LAN address) may reach loopback and LAN addresses only; every other target may reach public
    /// addresses only.
    /// </summary>
    internal static bool IsAllowedProbeAddress(string host, IPAddress address) =>
        IsLocalTarget(host)
            ? IsLocalAddress(address)
            : ExplicitApiInferenceTransport.IsPublicAddress(address);

    private static bool IsLocalTarget(string host)
    {
        string literal = host.Trim('[', ']');
        return string.Equals(literal, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(literal, out IPAddress? address) && IsLocalAddress(address));
    }

    /// <summary>
    /// Benchmark capability probe. A competitor never inherits PaRuX capabilities by assumption;
    /// this records whether the target itself accepts OpenAI response_format JSON Schema.
    /// </summary>
    internal static async Task<StructuredOutputProbeEvidence> ProbeStructuredOutputAsync(
        string endpoint,
        CancellationToken ct)
    {
        if (!TryNormalizeEndpoint(endpoint, RemoteAllowed(), out string safeEndpoint))
            return new StructuredOutputProbeEvidence(endpoint ?? string.Empty, false, DateTime.UtcNow, "Endpoint is not an allowed benchmark target.");

        if (StructuredOutputProbeCache.TryGetValue(safeEndpoint, out StructuredOutputProbeEvidence? cached))
        {
            TimeSpan ttl = cached.Supported ? PositiveStructuredOutputProbeTtl : NegativeStructuredOutputProbeTtl;
            if (DateTime.UtcNow - cached.CheckedUtc < ttl) return cached;
        }

        using InFlightRequestLease? remoteCall = BeginRemoteCall(safeEndpoint, ct, inFlight: null);
        CancellationToken probeToken = remoteCall?.Token ?? ct;
        using HttpClient capabilityHttp = CreateProbeClient(TimeSpan.FromSeconds(20));
        StructuredOutputProbeEvidence evidence;
        try
        {
            string? modelId = await ResolveProbeModelIdAsync(capabilityHttp, safeEndpoint, probeToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(modelId))
            {
                evidence = new StructuredOutputProbeEvidence(
                    safeEndpoint, false, DateTime.UtcNow, "The benchmark target exposed no model id for a structured-output probe.");
            }
            else
            {
                var schema = new LocalStructuredOutputRequest(
                    "parux_capability_probe",
                    "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}");
                var payload = new JsonObject
                {
                    ["model"] = modelId,
                    ["stream"] = false,
                    ["temperature"] = 0,
                    ["max_tokens"] = 16,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = "Return only the requested JSON object." },
                        new JsonObject { ["role"] = "user", ["content"] = "Set ok to true." }
                    },
                    ["response_format"] = StructuredOutputJson.BuildOpenAiResponseFormat(schema)
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, safeEndpoint + "/v1/chat/completions")
                {
                    Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
                };
                using HttpResponseMessage response = await capabilityHttp.SendAsync(request, probeToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(probeToken).ConfigureAwait(false);

                bool supported = response.IsSuccessStatusCode && ProbeResponseMatchesSchema(body);
                string detail = supported
                    ? "Benchmark target accepted response_format JSON Schema and returned schema-valid JSON."
                    : $"Structured-output benchmark probe was not accepted or did not return schema-valid JSON (HTTP {(int)response.StatusCode}).";
                evidence = new StructuredOutputProbeEvidence(safeEndpoint, supported, DateTime.UtcNow, detail);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Remote access was switched off while the probe ran. Say so, and cache nothing about the target.
            return new StructuredOutputProbeEvidence(safeEndpoint, false, DateTime.UtcNow,
                "The structured-output benchmark probe was cancelled because remote access was turned off.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            evidence = new StructuredOutputProbeEvidence(
                safeEndpoint, false, DateTime.UtcNow, "Structured-output benchmark probe failed: " + Trim(ex.Message, 300));
        }

        StructuredOutputProbeCache[safeEndpoint] = evidence;
        return evidence;
    }

    internal static void ClearStructuredOutputProbeCache() => StructuredOutputProbeCache.Clear();

    private static async Task<string?> ResolveProbeModelIdAsync(HttpClient http, string safeEndpoint, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(safeEndpoint + "/v1/models", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        JsonNode? body;
        try { body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
        catch (JsonException) { return null; }
        return body?["data"]?[0]?["id"]?.GetValue<string>();
    }

    private static bool ProbeResponseMatchesSchema(string body)
    {
        try
        {
            JsonNode? response = JsonNode.Parse(body);
            string? content = response?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content)) return false;
            JsonNode? parsed = JsonNode.Parse(content);
            return parsed is JsonObject obj
                && obj["ok"] is JsonValue value
                && value.TryGetValue(out bool ok)
                && ok;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal static bool TryNormalizeLocalEndpoint(string? endpoint, out string normalized) =>
        TryNormalizeEndpoint(endpoint, allowRemote: false, out normalized);

    internal static bool TryNormalizeEndpoint(string? endpoint, bool allowRemote, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
            return false;

        bool localHost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        bool localIp = IPAddress.TryParse(uri.Host, out IPAddress? address) && IsLocalAddress(address);
        if (!localHost && !localIp)
        {
            if (!allowRemote) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    /// <summary>Legacy benchmark auth helper. PaRuX runtime never calls this. A credential is only
    /// attached for an explicitly configured remote benchmark target, never to loopback/LAN.</summary>
    internal static void ApplyRemoteAuthorization(HttpRequestMessage request, string? endpoint)
    {
        if (request.Headers.Authorization != null || !IsRemoteEndpoint(endpoint)) return;
        // The stored credential is for the configured benchmark target only. Any other remote endpoint would receive a
        // bearer token it was never given, so it receives none.
        if (!SameTarget(TryReadConfig()?.Endpoint, endpoint)) return;
        string? key;
        try { key = Policy.TryGetApiKey(); }
        catch { return; }
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>True when both endpoints normalize to the same scheme, host and port. A path is not part of an endpoint.</summary>
    private static bool SameTarget(string? configured, string? candidate) =>
        TryNormalizeEndpoint(configured, allowRemote: true, out string left)
        && TryNormalizeEndpoint(candidate, allowRemote: true, out string right)
        && string.Equals(left, right, StringComparison.Ordinal);

    internal static bool IsRemoteEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        return TryNormalizeEndpoint(endpoint, allowRemote: true, out _)
            && !TryNormalizeLocalEndpoint(endpoint, out _);
    }

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xFE) == 0xFC
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }

    private static Config? TryReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            Config? config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions);
            if (config == null || !TryNormalizeEndpoint(config.Endpoint, RemoteAllowed(), out string normalized)) return null;
            config.Endpoint = normalized;
            return config;
        }
        catch { return null; }
    }

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed class Config
    {
        public string ProviderName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
    }
}
