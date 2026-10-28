using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Failure classes carried on a model download status. Cancelled and paused are user-driven
/// states; they are reported through the status, not as failures.
/// </summary>
internal static class ModelDownloadFailureClass
{
    /// <summary>The source refused the request or delivered unusable content. Retrying the same request will not help.</summary>
    public const string Permanent = "permanent";
    /// <summary>A temporary condition (network loss, stall, 408/429/5xx). PaRuX retries with backoff and resumes the partial.</summary>
    public const string Transient = "transient";
    /// <summary>Token required or licence not accepted: the repository is gated or private, or the saved token was refused.</summary>
    public const string Auth = "auth";
    /// <summary>The model drive is full, or the managed AI storage budget would be exceeded.</summary>
    public const string Disk = "disk";
    /// <summary>The user cancelled the download.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// A classified transfer failure. <see cref="Exception.Message"/> is the short cause and
/// <see cref="Remediation"/> is the next step the user can take.
/// </summary>
internal sealed class ModelDownloadFailure : Exception
{
    public ModelDownloadFailure(string failureClass, string message, string remediation,
        int? httpStatusCode = null, TimeSpan? retryAfter = null) : base(message)
    {
        FailureClass = failureClass;
        Remediation = remediation;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
    }

    public string FailureClass { get; }
    public string Remediation { get; }
    public int? HttpStatusCode { get; }
    /// <summary>Server-requested minimum delay (Retry-After), when the response carried one.</summary>
    public TimeSpan? RetryAfter { get; }
    public bool Retryable => FailureClass == ModelDownloadFailureClass.Transient;

    /// <summary>Maps an HTTP refusal to a failure class. The numeric status stays on the failure for callers.
    /// <paramref name="civitAi"/> selects the CivitAI wording (LoRA lane): the text names the right source and the right credential
    /// and never includes the token itself.</summary>
    public static ModelDownloadFailure FromHttpStatus(HttpStatusCode status, bool tokenSent, TimeSpan? retryAfter, bool civitAi = false)
    {
        int code = (int)status;
        string source = civitAi ? "CivitAI" : "Hugging Face";
        switch (code)
        {
            case 401:
                if (civitAi)
                    return new ModelDownloadFailure(ModelDownloadFailureClass.Auth,
                        tokenSent
                            ? "CivitAI rejected the saved API key (HTTP 401)."
                            : "A CivitAI API key is needed: CivitAI requires you to be signed in to download this file (HTTP 401).",
                        tokenSent
                            ? "Create a new API key in your CivitAI account settings, replace the saved CivitAI API key in PaRuX, then press Retry."
                            : "Create an API key in your CivitAI account settings, add it as the CivitAI API key in PaRuX, then press Retry.",
                        code);
                return new ModelDownloadFailure(ModelDownloadFailureClass.Auth,
                    tokenSent
                        ? "Hugging Face rejected the saved access token (HTTP 401)."
                        : "Token required: this Hugging Face repository needs an access token (HTTP 401).",
                    tokenSent
                        ? "Replace the access token in Model Hub settings, then press Retry."
                        : "Add a Hugging Face access token in Model Hub settings, then press Retry.",
                    code);
            case 403:
                if (civitAi)
                    return new ModelDownloadFailure(ModelDownloadFailureClass.Auth,
                        tokenSent
                            ? "CivitAI refused this file for the saved API key (HTTP 403)."
                            : "A CivitAI API key is needed, or this file is restricted on CivitAI (HTTP 403).",
                        tokenSent
                            ? "Check that the account behind the CivitAI API key may download this file, or replace the key, then press Retry."
                            : "Add a CivitAI API key (created in your CivitAI account settings) and make sure the account may download this file, then press Retry.",
                        code);
                return new ModelDownloadFailure(ModelDownloadFailureClass.Auth,
                    tokenSent
                        ? "Licence not accepted or token refused: Hugging Face refused this file for the saved token (HTTP 403)."
                        : "Token required or licence not accepted: this Hugging Face repository is gated or private (HTTP 403).",
                    tokenSent
                        ? "Accept the model licence on huggingface.co with the token's account, or replace the token, then press Retry."
                        : "Add a Hugging Face access token and accept the model licence on huggingface.co, then press Retry.",
                    code);
            case 404:
            case 410:
                return new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    civitAi
                        ? "The file was not found on CivitAI (HTTP " + code + ")."
                        : "The file was not found at the pinned repository revision (HTTP " + code + ").",
                    civitAi ? "Check the CivitAI model version, or choose another source."
                            : "Check the repository and revision, or choose another source.", code);
            case 408:
            case 429:
            case 500:
            case 502:
            case 503:
            case 504:
                return new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                    source + " is temporarily unavailable (HTTP " + code + ").",
                    "PaRuX retries automatically with backoff and keeps the partial file.", code, retryAfter);
            default:
                return new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    source + " refused the request (HTTP " + code + ").",
                    civitAi ? "Check the CivitAI model version, or choose another source."
                            : "Check the repository and revision, or choose another source.", code);
        }
    }
}

/// <summary>
/// Timing, retry and space policy for model transfers. Production uses <see cref="Default"/>;
/// tests inject shorter windows through the service's internal transfer-policy seam.
/// </summary>
internal sealed class ModelDownloadTransferPolicy
{
    public static ModelDownloadTransferPolicy Default { get; } = new();

    /// <summary>Upper bound for receiving response headers (the connect phase). It never caps the body.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest silence between received body bytes. A longer gap ends the attempt, keeps the partial and retries.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Total attempts for one job before a transient failure becomes final.</summary>
    public int MaxAttempts { get; init; } = 4;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Largest Retry-After delay honoured from a server response.</summary>
    public TimeSpan RetryAfterCap { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Free space kept on the model drive when no other transfer is promised bytes.</summary>
    public long FreeSpaceReserveBytes { get; init; } = 1L << 30;

    /// <summary>Free space kept on the model drive when other transfers are also promised bytes.</summary>
    public long FreeSpaceReserveWithOtherTransfersBytes { get; init; } = 2L << 30;

    /// <summary>How many received bytes pass between free-space probes while streaming.</summary>
    public long FreeSpaceCheckIntervalBytes { get; init; } = 64L << 20;

    /// <summary>How many received bytes pass between managed-budget re-checks while streaming.</summary>
    public long BudgetCheckIntervalBytes { get; init; } = 256L << 20;

    /// <summary>Managed-budget reservation for a transfer whose size no source has declared.</summary>
    public long UnknownSizeReserveBytes { get; init; } = 64L << 30;
}

/// <summary>Capped exponential backoff with jitter, honouring a server Retry-After as a minimum.</summary>
internal static class ModelDownloadRetrySchedule
{
    public static TimeSpan DelayAfter(int failedAttempt, ModelDownloadTransferPolicy policy, TimeSpan? retryAfter, Random random)
    {
        double exponentialMs = policy.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(failedAttempt - 1, 0, 20));
        double cappedMs = Math.Min(exponentialMs, policy.RetryMaxDelay.TotalMilliseconds);
        // Jitter spreads retries of several jobs so they do not hit the hub in lock-step (+/-20%).
        TimeSpan delay = TimeSpan.FromMilliseconds(cappedMs * (0.8 + 0.4 * random.NextDouble()));
        if (retryAfter is TimeSpan serverDelay)
        {
            TimeSpan honoured = serverDelay > policy.RetryAfterCap ? policy.RetryAfterCap : serverDelay;
            if (honoured > delay) delay = honoured;
        }
        return delay;
    }
}

/// <summary>
/// Moving-average transfer speed from recorded byte counts. The speed is unknown until the samples
/// span at least <see cref="MinimumWindow"/>, and it is unknown again once no bytes arrived for a while.
/// </summary>
internal sealed class ModelDownloadSpeedMeter
{
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);
    private readonly LinkedList<(DateTime AtUtc, long Bytes)> _samples = new();

    public void Record(long totalBytes, DateTime atUtc)
    {
        if (_samples.Count > 0 && _samples.Last!.Value.Bytes == totalBytes) return;
        _samples.AddLast((atUtc, totalBytes));
        while (_samples.Count > 2 && atUtc - _samples.First!.Value.AtUtc > SampleWindow)
            _samples.RemoveFirst();
    }

    /// <summary>Bytes per second over the sliding window, or null while the window is too short or stale.</summary>
    public double? SpeedBytesPerSecond(DateTime nowUtc)
    {
        if (_samples.Count < 2) return null;
        var first = _samples.First!.Value;
        var last = _samples.Last!.Value;
        if (nowUtc - last.AtUtc > StaleAfter) return null;
        TimeSpan span = last.AtUtc - first.AtUtc;
        if (span < MinimumWindow) return null;
        if (last.Bytes <= first.Bytes) return 0d;
        return (last.Bytes - first.Bytes) / span.TotalSeconds;
    }

    /// <summary>Remaining time, or null when the speed is unknown or zero.</summary>
    public static double? EstimateSeconds(long remainingBytes, double? speedBytesPerSecond) =>
        speedBytesPerSecond is > 0 && remainingBytes > 0 ? remainingBytes / speedBytesPerSecond.Value : null;
}

/// <summary>
/// One shared pacing schedule for every model transfer. Each chunk that any transfer receives is charged
/// against the same clock, so the limit is global rather than per job. A chunk may start up to about 100 ms
/// ahead of schedule. Zero means unlimited. A new limit applies to the next chunk at once.
/// </summary>
internal sealed class ModelDownloadRateLimiter
{
    private static readonly double MaximumRunAheadSeconds = TimeSpan.FromMilliseconds(100).TotalSeconds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private double _bytesPerSecond;
    private double _scheduledUntilSeconds;

    public double MegabytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond / (1024d * 1024d); }
    }

    public void SetMegabytesPerSecond(double megabytesPerSecond)
    {
        lock (_gate)
        {
            _bytesPerSecond = megabytesPerSecond > 0 ? megabytesPerSecond * 1024d * 1024d : 0;
            _scheduledUntilSeconds = _clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>Waits until <paramref name="bytes"/> may be accepted under the shared schedule.</summary>
    public async Task WaitForAsync(long bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_bytesPerSecond <= 0 || bytes <= 0) return;
            // The schedule is the moment the already-accepted bytes would finish at the limit (a leaky bucket).
            // A chunk is accepted once that backlog is no more than the allowed run-ahead, and then adds its own
            // duration to the backlog, so sustained transfers settle at the configured rate.
            double now = _clock.Elapsed.TotalSeconds;
            double acceptAt = Math.Max(now, _scheduledUntilSeconds - MaximumRunAheadSeconds);
            _scheduledUntilSeconds = Math.Max(_scheduledUntilSeconds, now) + bytes / _bytesPerSecond;
            wait = TimeSpan.FromSeconds(Math.Max(0, acceptAt - now));
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}

/// <summary>Persisted model-download settings (the shared bandwidth limit). Read once at startup; a missing or
/// unreadable file means the defaults.</summary>
internal static class ModelDownloadSettingsStore
{
    public const double MaximumMegabytesPerSecond = 10000;
    private const string FileName = "model-download-settings.json";

    private sealed class Document
    {
        public double BandwidthLimitMegabytesPerSecond { get; set; }
    }

    public static double LoadBandwidthLimitMegabytesPerSecond(string stateDirectory)
    {
        try
        {
            string path = Path.Combine(stateDirectory, FileName);
            if (!File.Exists(path)) return 0;
            double value = JsonSerializer.Deserialize<Document>(File.ReadAllText(path))?.BandwidthLimitMegabytesPerSecond ?? 0;
            return double.IsFinite(value) && value > 0 ? Math.Min(value, MaximumMegabytesPerSecond) : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    public static void SaveBandwidthLimitMegabytesPerSecond(string stateDirectory, double megabytesPerSecond)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document { BandwidthLimitMegabytesPerSecond = megabytesPerSecond }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>
/// Hosts a model transfer may touch. The hub itself and its file CDN domains (huggingface.co and hf.co with
/// their subdomains) are allowed, over HTTPS on the default port and without credentials in the URL.
/// Anything else a redirect points to is refused before a connection is made.
/// </summary>
internal static class ModelDownloadHostPolicy
{
    /// <summary>The one host that may receive the Hugging Face access token.</summary>
    public static bool IsOfficialHubHost(string host) =>
        string.Equals(host, "huggingface.co", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedTransferHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (IsUnderDomain(uri.Host, "huggingface.co") || IsUnderDomain(uri.Host, "hf.co"));

    // LoRA lane (CivitAI). Exact hosts only, never a domain suffix: the source that may receive the optional
    // CivitAI token, and the CDN hosts a download may be redirected to (no token is ever sent to those).
    internal static readonly IReadOnlyList<string> LoraSourceHosts = new[] { "civitai.com", "www.civitai.com" };
    internal static readonly IReadOnlyList<string> LoraCdnHosts = new[]
    {
        "civitai-delivery-worker-prod.5ac0637cfd0766c97916cefa3764fbdf.r2.cloudflarestorage.com"
    };

    public static bool IsLoraSourceHost(string host) =>
        LoraSourceHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Only the exact CivitAI source host may receive the CivitAI token.</summary>
    public static bool IsLoraTokenHost(string host) => IsLoraSourceHost(host);

    public static bool IsAllowedLoraTransferHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (IsLoraSourceHost(uri.Host) || LoraCdnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));

    private static bool IsUnderDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
