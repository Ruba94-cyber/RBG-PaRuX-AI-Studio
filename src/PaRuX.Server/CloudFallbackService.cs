using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Legacy cloud-fallback configuration/probe surface.
///
/// PaRuX Core's shipped inference contract is local-only. Historical fallback settings remain
/// readable so upgrades/restores do not destroy user state and the credential can still be checked
/// with a prompt-free GET /models request, but no completion is ever sent through this service.
/// Keeping the final guard here - at the network boundary itself - means a future caller cannot
/// bypass local-only inference merely by skipping ChatProxyService's higher-level IsArmed check.
/// </summary>
public sealed class CloudFallbackService
{
    internal const string StrictLocalInferenceReason =
        "PaRuX Core is local-only: cloud inference egress is disabled even when fallback credentials are configured.";

    /// <summary>
    /// Largest provider answer the credential check reads: the same 2 MiB bound the explicit API path applies to its
    /// responses. A provider's full model catalogue has no guaranteed small size, so the bound must not assume one. A
    /// longer answer is refused rather than buffered: a slow or hostile endpoint must not be able to make the Core
    /// allocate without bound.
    /// </summary>
    internal const int MaxProbeBodyBytes = ExplicitApiResponseReader.MaxResponseBytes;

    // Retained only for the prompt-free credential/model-list probe. User inference must never use
    // this client while the local-only product contract is in force.
    private readonly CloudFallbackPolicy _policy;
    private readonly HttpClient _probeHttp;
    private readonly InFlightRequestRegistry _inFlight;

    /// <summary>
    /// The probe sends the bearer key, so it must never follow a redirect: a Location header naming another
    /// host would otherwise carry the credential there. Cookies are not kept either. Every connection is also
    /// checked as it opens: the provider host may resolve only to public addresses, the same connect-time rule the
    /// explicit API transport applies, so a DNS answer cannot send the credential to a private or loopback address.
    /// </summary>
    internal static SocketsHttpHandler CreateProbeHandler(ValidatedRemoteConnector.HostResolver? resolver = null) =>
        ValidatedRemoteConnector.CreateHandler(
            static (_, address) => ExplicitApiInferenceTransport.IsPublicAddress(address), resolver);

    public CloudFallbackService() : this(new CloudFallbackPolicy()) { }

    /// <summary>
    /// The resolver and registry are injectable so tests can exercise the connect-time rule and revocation without real
    /// DNS or sockets. Production shares the process-wide registry, which the settings routes cancel through.
    /// </summary>
    internal CloudFallbackService(
        CloudFallbackPolicy policy,
        ValidatedRemoteConnector.HostResolver? resolver = null,
        InFlightRequestRegistry? inFlight = null)
        : this(policy, CreateProbeHandler(resolver), inFlight) { }

    /// <summary>
    /// Probe handler injection for tests that answer with a controlled body. Production always reaches this through
    /// <see cref="CreateProbeHandler"/>, so the connect-time rule applies to every real probe.
    /// </summary>
    internal CloudFallbackService(CloudFallbackPolicy policy, HttpMessageHandler probeHandler, InFlightRequestRegistry? inFlight)
    {
        _policy = policy;
        _probeHttp = new HttpClient(probeHandler) { Timeout = TimeSpan.FromSeconds(20) };
        _inFlight = inFlight ?? InFlightRequestRegistry.Shared;
    }

    public CloudFallbackStatus GetStatus()
    {
        CloudFallbackSettings settings = _policy.Get();
        bool armed = TryDescribeArmed(settings, out string? notArmedReason);
        CloudFallbackProvider? provider = CloudFallbackPolicy.FindProvider(settings.ProviderId);

        return new CloudFallbackStatus
        {
            Enabled = settings.Enabled,
            ProviderId = settings.ProviderId,
            ProviderDisplayName = provider?.DisplayName ?? settings.ProviderId,
            BaseUrl = settings.BaseUrl,
            Model = settings.Model,
            HasApiKey = settings.HasApiKey,
            Armed = armed,
            NotArmedReason = notArmedReason,
            LastUsedUtc = settings.LastUsedUtc,
            LastUsedReason = settings.LastUsedReason,
            UseCount = settings.UseCount,
            AvailableProviders = CloudFallbackPolicy.Providers
                .Select(p => new CloudFallbackProviderOption
                {
                    Id = p.Id,
                    DisplayName = p.DisplayName,
                    BaseUrl = p.BaseUrl,
                    SuggestedModel = p.SuggestedModel
                })
                .ToList()
        };
    }

    /// <summary>Saving a changed fallback cancels any credential check still running under the previous settings.</summary>
    public void Configure(SetCloudFallbackRequest request)
    {
        _policy.Save(request.Enabled, request.ProviderId, request.BaseUrl, request.Model, request.ApiKey);
        _inFlight.Cancel(InFlightRequestRegistry.CloudFallbackScope);
    }

    /// <summary>Turning the fallback off cancels a credential check that is still running.</summary>
    public void Disable()
    {
        _policy.Disable();
        _inFlight.Cancel(InFlightRequestRegistry.CloudFallbackScope);
    }

    /// <summary>
    /// A cloud inference fallback is never armed in the shipped local-only core. Configuration
    /// completeness is deliberately separate: TestAsync may validate a credential/model id without
    /// sending any user text, while inference callers always receive the strict-local reason.
    /// </summary>
    public bool IsArmed(out string? reason)
    {
        CloudFallbackSettings settings = _policy.Get();
        if (!TryDescribeConfigured(settings, out reason)) return false;

        // The credential is only readable on the machine/account that stored it. Keep this
        // diagnostic truthful even though inference itself is prohibited.
        if (string.IsNullOrWhiteSpace(_policy.TryGetApiKey()))
        {
            reason = "The stored API key could not be read on this account. Enter it again.";
            return false;
        }

        reason = StrictLocalInferenceReason;
        return false;
    }

    private static bool TryDescribeArmed(CloudFallbackSettings settings, out string? reason)
    {
        if (!TryDescribeConfigured(settings, out reason)) return false;
        reason = StrictLocalInferenceReason;
        return false;
    }

    private static bool TryDescribeConfigured(CloudFallbackSettings settings, out string? reason)
    {
        if (!settings.Enabled) { reason = "Cloud fallback is turned off."; return false; }
        if (!settings.HasApiKey) { reason = "No API key has been saved for the fallback provider."; return false; }
        if (string.IsNullOrWhiteSpace(settings.Model)) { reason = "No fallback model has been chosen."; return false; }
        if (!CloudFallbackPolicy.TryNormalizeBaseUrl(settings.BaseUrl, out _))
        {
            reason = "The fallback provider endpoint is not a valid https:// URL.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// Checks the credential against the provider's model list. This is intentionally the only
    /// network operation retained here: a GET of /models contains no user prompt, conversation,
    /// retrieved context, tool payload or generated answer. Web research/downloads use their own
    /// explicit network paths and are not inference egress either.
    /// </summary>
    public async Task<CloudFallbackTestResult> TestAsync(CancellationToken cancellationToken)
    {
        // Admitted before the settings are read: a change that lands after this point cancels the check, and a change
        // that landed before it is already visible in the settings.
        using InFlightRequestLease check = _inFlight.Begin(InFlightRequestRegistry.CloudFallbackScope, cancellationToken);
        CloudFallbackSettings settings = _policy.Get();
        if (!TryDescribeConfigured(settings, out string? notConfigured))
            return new CloudFallbackTestResult { Success = false, Detail = notConfigured ?? "Cloud fallback is not configured." };

        string? key = _policy.TryGetApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new CloudFallbackTestResult
            {
                Success = false,
                Detail = "The stored API key could not be read on this account. Enter it again."
            };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl + "/models");
            Authorize(request, key);
            // Headers first: the default option would let HttpClient buffer the whole answer before the bounded read below.
            using HttpResponseMessage response = await _probeHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, check.Token).ConfigureAwait(false);
            (string body, bool truncated) = await ReadBoundedAsync(response.Content, check.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new CloudFallbackTestResult
                {
                    Success = false,
                    Detail = $"The provider rejected the credential (HTTP {(int)response.StatusCode}): {Trim(body, 200)}"
                };
            if (truncated)
                return new CloudFallbackTestResult
                {
                    Success = false,
                    Detail = "The provider's model list is larger than a credential check accepts, so it was not read in full."
                };

            bool modelListed = ModelIsListed(body, settings.Model);
            return new CloudFallbackTestResult
            {
                Success = true,
                ModelAvailable = modelListed,
                Detail = modelListed
                    ? $"{settings.Model} is visible to this credential. PaRuX Core remains local-only and will not send inference to it."
                    : $"The credential works, but '{settings.Model}' was not in the provider's model list. "
                      + "PaRuX Core remains local-only and will not send inference to it."
            };
        }
        catch (Exception) when (check.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The fallback was changed or turned off while the check ran: report that, not a network failure.
            return new CloudFallbackTestResult
            {
                Success = false,
                Detail = "The check was cancelled because the cloud fallback was changed or turned off."
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CloudFallbackTestResult { Success = false, Detail = "Could not reach the provider: " + Trim(ex.Message, 200) };
        }
    }

    /// <summary>
    /// Retained as a compatibility boundary for older callers. It must fail before serializing or
    /// opening a network request, even when a restored configuration contains a valid remote URL,
    /// model and credential. This is the decisive strict-local invariant.
    /// </summary>
    public Task<HttpResponseMessage> OpenStreamAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = request ?? throw new ArgumentNullException(nameof(request));
        throw new InvalidOperationException(StrictLocalInferenceReason);
    }

    /// <summary>Historical audit state is preserved for migrations; current local-only code has no
    /// successful inference path that calls this method.</summary>
    public void RecordUse(string reason) => _policy.RecordUse(reason);

    /// <summary>
    /// Reads at most <see cref="MaxProbeBodyBytes"/> of the answer and probes one byte past the limit. Bytes beyond the limit
    /// are never buffered; <c>Truncated</c> reports that the answer continued past it.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (buffer.Length < MaxProbeBodyBytes)
        {
            int wanted = (int)Math.Min(chunk.Length, MaxProbeBodyBytes - buffer.Length);
            int read = await input.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), false);
            buffer.Write(chunk, 0, read);
        }

        bool more = await input.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0;
        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), more);
    }

    private static void Authorize(HttpRequestMessage request, string key)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio");
        request.Headers.TryAddWithoutValidation("X-Title", "RBG PaRuX AI Studio");
    }

    internal static bool ModelIsListed(string body, string model)
    {
        try
        {
            JsonNode? parsed = JsonNode.Parse(body);
            if (parsed?["data"] is not JsonArray data) return false;
            return data.Any(entry => string.Equals(
                entry?["id"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
