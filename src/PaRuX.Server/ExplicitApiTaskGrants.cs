using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// In-memory, single-use authorization for one explicitly selected remote inference task.
/// A grant is bound to exactly one <see cref="ExplicitApiTaskBinding"/>: provider, normalized endpoint
/// (scheme, host, port and base path), a SHA-256 fingerprint of the API key, model, task id, the approved
/// per-token price and completion limit and, when recorded, the SHA-256 of the exact message payload.
/// The token itself is kept only as a digest.
/// The UI must issue grants only after displaying provider, model and disclosure details.
/// No grants survive process restarts, and no grant is persisted with credentials.
/// </summary>
public sealed class ExplicitApiTaskGrants
{
    private const int MaxPendingGrants = 128;
    private const int TokenHexLength = 64;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private sealed record Grant(
        byte[] TokenDigest, ExplicitApiTaskBinding Binding, decimal ApprovedBudget, DateTimeOffset ExpiresAt);

    private readonly List<Grant> _grants = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public ExplicitApiTaskGrants() : this(TimeProvider.System) { }
    public ExplicitApiTaskGrants(TimeProvider clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Issues a single-use token for exactly <paramref name="binding"/>. The token is returned once to the
    /// trusted caller; only its SHA-256 digest is retained.
    /// </summary>
    public string Issue(ExplicitApiTaskBinding binding, TimeSpan lifetime, decimal approvedBudget = 0m)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (approvedBudget < 0m) throw new ArgumentOutOfRangeException(nameof(approvedBudget));

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenHexLength / 2));
        byte[] digest = TokenDigest(token);
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            // Bound memory use even if consent dialogs are repeatedly opened.
            _grants.RemoveAll(grant => grant.ExpiresAt <= now);
            if (_grants.Count >= MaxPendingGrants)
                throw new InvalidOperationException("Too many pending API authorizations.");
            _grants.Add(new Grant(digest, binding, approvedBudget, now + lifetime));
        }
        return token;
    }

    /// <summary>
    /// Atomically consumes the grant that <paramref name="token"/> names, provided it is unexpired and
    /// matches <paramref name="expected"/> exactly, and reports the approved budget.
    /// Malformed input (a token that cannot have been issued) is refused before any state is touched, so a
    /// caller bug cannot burn a valid grant. Once a well-formed token names a grant, that grant is removed
    /// whether or not the binding, the expiry or the caller matches: a mismatch is never a second attempt.
    /// </summary>
    public bool TryConsume(string? token, ExplicitApiTaskBinding expected, out decimal approvedBudget)
    {
        approvedBudget = 0m;
        if (token is not { Length: TokenHexLength } || expected is null) return false;

        byte[] digest = TokenDigest(token);
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            int index = IndexOf(digest);
            if (index < 0) return false;

            Grant grant = _grants[index];
            _grants.RemoveAt(index);
            if (grant.ExpiresAt <= now || !grant.Binding.Matches(expected)) return false;
            approvedBudget = grant.ApprovedBudget;
            return true;
        }
    }

    /// <summary>
    /// Withdraws a grant that was issued but not yet consumed, so its token can no longer authorize a call. Returns false
    /// when the token names no pending grant. Revocation only removes authority; it never grants any.
    /// </summary>
    public bool Revoke(string? token)
    {
        if (token is not { Length: TokenHexLength }) return false;

        byte[] digest = TokenDigest(token);
        lock (_gate)
        {
            int index = IndexOf(digest);
            if (index < 0) return false;
            _grants.RemoveAt(index);
            return true;
        }
    }

    /// <summary>Scans every entry without an early exit, so the work done does not reveal which grant matched.</summary>
    private int IndexOf(byte[] digest)
    {
        int index = -1;
        for (int i = 0; i < _grants.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(_grants[i].TokenDigest, digest)) index = i;
        }
        return index;
    }

    private static byte[] TokenDigest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>
/// The exact parameters one remote task is authorized for. Credentials and payloads are never retained:
/// the API key is reduced to its SHA-256 fingerprint and the payload to its SHA-256 digest.
/// </summary>
public sealed class ExplicitApiTaskBinding
{
    private const int MaxIdentifierLength = 256;
    private const int MaxTaskIdLength = 128;

    private ExplicitApiTaskBinding(
        string providerId, string endpointBaseUrl, string endpointAuthority, string apiKeyFingerprint,
        string model, string taskId, string? payloadSha256, decimal? pricePerToken, int? maxOutputTokens)
    {
        ProviderId = providerId;
        EndpointBaseUrl = endpointBaseUrl;
        EndpointAuthority = endpointAuthority;
        ApiKeyFingerprint = apiKeyFingerprint;
        Model = model;
        TaskId = taskId;
        PayloadSha256 = payloadSha256;
        PricePerToken = pricePerToken;
        MaxOutputTokens = maxOutputTokens;
    }

    public string ProviderId { get; }

    /// <summary>
    /// The normalized base URL the grant was approved for: https, lowercase host, an explicit port only when it is
    /// not the default, and the base path without a trailing slash, for example "https://api.example.org/v1".
    /// Dispatch targets exactly this base, so a grant cannot be spent on another path of the same host.
    /// </summary>
    public string EndpointBaseUrl { get; }

    /// <summary>Lowercase host and port, for example "api.example.org:443".</summary>
    public string EndpointAuthority { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the API key.</summary>
    public string ApiKeyFingerprint { get; }

    public string Model { get; }

    public string TaskId { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the exact message payload, or null when the grant is not payload-bound.</summary>
    public string? PayloadSha256 { get; }

    /// <summary>The per-token price the user approved, or null when the binding carries no price term.</summary>
    public decimal? PricePerToken { get; }

    /// <summary>The completion limit the user approved, or null when the binding carries no completion-limit term.</summary>
    public int? MaxOutputTokens { get; }

    /// <summary>
    /// Builds the binding for one task. The endpoint is normalized exactly as dispatch normalizes it, so the
    /// approved scheme, host, port and path are the ones later used. The key and payload are digested here;
    /// the spend terms are kept as given.
    /// </summary>
    public static ExplicitApiTaskBinding Create(
        string providerId, Uri endpoint, string apiKey, string model, string taskId,
        string? payloadSha256 = null, decimal? pricePerToken = null, int? maxOutputTokens = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("An absolute endpoint URI is required.", nameof(endpoint));
        if (!CloudFallbackPolicy.TryNormalizeBaseUrl(endpoint.AbsoluteUri, out string baseUrl))
            throw new ArgumentException(
                "An absolute HTTPS endpoint without credentials, query or fragment is required.", nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey, nameof(apiKey));
        if (pricePerToken is < 0m) throw new ArgumentOutOfRangeException(nameof(pricePerToken));
        if (maxOutputTokens is < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));

        return new ExplicitApiTaskBinding(
            RequireText(providerId, nameof(providerId), MaxIdentifierLength),
            baseUrl,
            $"{endpoint.IdnHost.ToLowerInvariant()}:{endpoint.Port}",
            Sha256Hex(apiKey),
            RequireText(model, nameof(model), MaxIdentifierLength),
            RequireText(taskId, nameof(taskId), MaxTaskIdLength),
            payloadSha256 is null ? null : RequireDigest(payloadSha256),
            pricePerToken,
            maxOutputTokens);
    }

    /// <summary>The payload digest a grant is bound to, computed from the messages exactly as they are sent.</summary>
    public static string PayloadSha256Of(JsonElement messages) => Sha256Hex(messages.GetRawText());

    internal bool Matches(ExplicitApiTaskBinding other) =>
        string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal) &&
        string.Equals(EndpointBaseUrl, other.EndpointBaseUrl, StringComparison.Ordinal) &&
        FixedTimeEquals(ApiKeyFingerprint, other.ApiKeyFingerprint) &&
        string.Equals(Model, other.Model, StringComparison.Ordinal) &&
        string.Equals(TaskId, other.TaskId, StringComparison.Ordinal) &&
        PayloadMatches(PayloadSha256, other.PayloadSha256) &&
        OptionalTermMatches(PricePerToken, other.PricePerToken) &&
        OptionalTermMatches(MaxOutputTokens, other.MaxOutputTokens);

    private static bool PayloadMatches(string? expected, string? presented) =>
        expected is null ? presented is null : presented is not null && FixedTimeEquals(expected, presented);

    /// <summary>
    /// A spend term must be presented exactly as it was approved. A term that was not approved cannot be
    /// supplied later, so a grant issued without a term is never usable for a call that carries one.
    /// </summary>
    private static bool OptionalTermMatches<T>(T? expected, T? presented) where T : struct =>
        expected.HasValue
            ? presented.HasValue && EqualityComparer<T>.Default.Equals(expected.Value, presented.Value)
            : !presented.HasValue;

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RequireText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"{name} must be non-blank and at most {maxLength} characters.", name);
        return value;
    }

    private static string RequireDigest(string value)
    {
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A payload digest must be a 64-character hexadecimal SHA-256.", nameof(value));
        return value.ToLowerInvariant();
    }
}
