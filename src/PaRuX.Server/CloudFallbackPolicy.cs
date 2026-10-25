using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Stores the opt-in configuration for the cloud <em>fallback</em> provider: the one PaRuX is
/// allowed to call when its own local runtime could not serve a turn at all.
///
/// This is deliberately a different thing from <see cref="RemoteProviderPolicy"/>, which widens
/// where a USER_MANAGED provider may live and then routes <em>every</em> turn there. Here the local
/// runtime stays the only configured provider: nothing is re-routed, nothing is load-balanced, and
/// with the local runtime healthy the settings on this object are never read on the chat path. It
/// exists so that "my model will not load right now" degrades to a working answer with a visible
/// notice instead of to a dead product, and for nothing else.
///
/// Off by default. Every core capability continues to work with it off, which is what keeps the
/// product's local-only rule intact: PaRuX may use a cloud API, it must never depend on one.
///
/// The API key is never written beside the config in clear text, because a provider key bills the
/// user's account. It goes through <see cref="SecretProtection"/>: DPAPI under the current user on
/// Windows, AES-256-GCM under an owner-only key file elsewhere.
/// </summary>
public sealed class CloudFallbackPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One gate for every load and write of the configuration file in this process. Each read-modify-write runs under it,
    /// so a Save that read the file before a Disable cannot write its key back after the Disable has cleared it.
    /// </summary>
    private static readonly object FileGate = new();

    private const string Purpose = "PaRuX.CloudFallback.v1";

    private readonly string _stateDirectory;
    private readonly ISecretProtector _protector;

    public CloudFallbackPolicy() : this(ServerState.StateDirectory) { }

    internal CloudFallbackPolicy(string stateDirectory)
        : this(stateDirectory, SecretProtection.For(stateDirectory)) { }

    /// <summary>The protector is injectable so the arming rules - the part that decides whether
    /// text may leave the device - can be driven from a test without touching the host's real
    /// credential storage.</summary>
    internal CloudFallbackPolicy(string stateDirectory, ISecretProtector protector)
    {
        _stateDirectory = stateDirectory;
        _protector = protector;
    }

    private string ConfigPath => Path.Combine(_stateDirectory, "cloud-fallback.json");

    /// <summary>
    /// The presets the Settings UI offers. Every one of them speaks the OpenAI chat-completions
    /// wire format, which is the only reason a single client can serve all of them; "custom" exists
    /// so a provider that is not on this list (or a self-hosted gateway) is not a dead end.
    /// </summary>
    public static readonly IReadOnlyList<CloudFallbackProvider> Providers = new List<CloudFallbackProvider>
    {
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-oss-120b"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini"),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-large-latest"),
        new("together", "Together AI", "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
        new("custom", "Custom OpenAI-compatible", "", ""),
    };

    public static CloudFallbackProvider? FindProvider(string? providerId) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Current settings with the credential omitted. Any read failure yields a disabled
    /// configuration: a corrupt file must not leave a network egress path enabled.</summary>
    public CloudFallbackSettings Get()
    {
        Config config = Load();
        return new CloudFallbackSettings(
            config.Enabled,
            config.ProviderId ?? "openrouter",
            config.BaseUrl ?? string.Empty,
            config.Model ?? string.Empty,
            !string.IsNullOrEmpty(config.ProtectedApiKey),
            config.LastUsedUtc,
            config.LastUsedReason,
            config.UseCount);
    }

    /// <summary>
    /// Writes the configuration. <paramref name="apiKey"/> is null when the caller is not changing
    /// the credential (so re-saving a model id does not wipe a working key), and empty to clear it.
    /// Disabling always clears it: leaving a billable credential on disk for a capability the user
    /// just switched off is not something they asked for.
    /// </summary>
    public void Save(bool enabled, string providerId, string? baseUrl, string model, string? apiKey)
    {
        CloudFallbackProvider? preset = FindProvider(providerId);
        if (preset == null)
            throw new InvalidDataException("Unknown fallback provider '" + providerId + "'.");

        string resolvedBase = string.IsNullOrWhiteSpace(baseUrl) ? preset.BaseUrl : baseUrl.Trim();
        if (enabled && !TryNormalizeBaseUrl(resolvedBase, out resolvedBase))
            throw new InvalidDataException(
                "The provider endpoint must be an absolute https:// URL with no credentials, query or fragment.");
        if (enabled && string.IsNullOrWhiteSpace(model))
            throw new InvalidDataException("Choose the model the fallback provider should answer with.");

        lock (FileGate)
        {
            Config config = Load();
            // A credential belongs to the host it was saved for. Moving to another host must not carry the stored
            // key along, or the next /test (or any later caller) would send it to the new host.
            bool endpointChanged = !SameEndpoint(config.BaseUrl, resolvedBase);
            config.Enabled = enabled;
            config.ProviderId = preset.Id;
            config.BaseUrl = resolvedBase;
            config.Model = model?.Trim() ?? string.Empty;

            if (!enabled) config.ProtectedApiKey = null;
            else if (apiKey != null)
                config.ProtectedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(Purpose, apiKey);
            else if (endpointChanged)
                config.ProtectedApiKey = null;

            Write(config);
        }
    }

    /// <summary>Turns the fallback off and drops the stored credential.</summary>
    public void Disable()
    {
        lock (FileGate)
        {
            Config config = Load();
            config.Enabled = false;
            config.ProtectedApiKey = null;
            Write(config);
        }
    }

    /// <summary>Null when absent, or when the blob cannot be decrypted - which is the correct
    /// outcome if the file was copied from another machine or another user account.</summary>
    public string? TryGetApiKey()
    {
        string? blob = Load().ProtectedApiKey;
        return string.IsNullOrEmpty(blob) ? null : _protector.Unprotect(Purpose, blob);
    }

    /// <summary>
    /// Records that a turn actually left the device, so Settings can show it after the fact rather
    /// than only in the moment the chat stream said so. Best-effort: a bookkeeping write must never
    /// be the reason an answer the user is already reading fails.
    /// </summary>
    public void RecordUse(string reason)
    {
        try
        {
            lock (FileGate)
            {
                Config config = Load();
                config.LastUsedUtc = DateTime.UtcNow;
                config.LastUsedReason = reason;
                config.UseCount++;
                Write(config);
            }
        }
        catch { }
    }

    /// <summary>
    /// A fallback endpoint is always off-device, so - unlike the local-provider rules - the only
    /// acceptable scheme is https: a prompt and a bearer token crossing the public Internet in
    /// clear text would be a worse outcome than refusing to send them. The structural checks match
    /// <see cref="ExternalTextProviderService.TryNormalizeEndpoint"/> so a URL carrying credentials,
    /// a query string or a fragment is rejected the same way here as it is there.
    /// </summary>
    internal static bool TryNormalizeBaseUrl(string? baseUrl, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length > 0;
    }

    /// <summary>True when both endpoints name the same host and port. Only https endpoints are stored.</summary>
    private static bool SameEndpoint(string? stored, string candidate) =>
        Authority(stored) is { } left && Authority(candidate) is { } right
        && string.Equals(left, right, StringComparison.Ordinal);

    private static string? Authority(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.IdnHost.ToLowerInvariant() + ":" + uri.Port
            : null;

    private Config Load()
    {
        lock (FileGate)
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions) ?? new Config();
            }
            catch { }
            return new Config();
        }
    }

    /// <summary>
    /// Writes a sibling file and moves it over the configuration. A crash or a concurrent reader then sees the old
    /// configuration or the new one, never a truncated file, which would read as a disabled default and drop the key.
    /// Callers hold <see cref="FileGate"/>.
    /// </summary>
    private void Write(Config config)
    {
        Directory.CreateDirectory(_stateDirectory);
        string temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed class Config
    {
        public bool Enabled { get; set; }
        public string? ProviderId { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
        public DateTime? LastUsedUtc { get; set; }
        public string? LastUsedReason { get; set; }
        public int UseCount { get; set; }
    }
}

/// <summary>One selectable fallback provider preset. <see cref="BaseUrl"/> is empty for "custom",
/// where the user supplies it.</summary>
public sealed record CloudFallbackProvider(string Id, string DisplayName, string BaseUrl, string SuggestedModel);

/// <summary>Stored fallback configuration with the credential replaced by whether one exists.</summary>
public sealed record CloudFallbackSettings(
    bool Enabled,
    string ProviderId,
    string BaseUrl,
    string Model,
    bool HasApiKey,
    DateTime? LastUsedUtc,
    string? LastUsedReason,
    int UseCount);
