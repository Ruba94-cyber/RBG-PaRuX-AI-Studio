using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Opt-in policy for using a non-local inference provider.
///
/// PaRuX's first architecture rule is that runtime AI must not *depend* on a proprietary cloud
/// API, and that is unchanged: this is disabled by default, nothing in the product requires it,
/// and every core capability continues to work with it off. What it changes is rule 2's blanket
/// restriction of USER_MANAGED providers to local/private-LAN endpoints - a remote endpoint is now
/// permitted, but only after the user has explicitly turned this on as a separate, deliberate act.
/// Configuring an endpoint can never silently enable it.
///
/// The API key is never written in clear text beside the config, because a provider key is a
/// credential that can bill the user's account. It goes through <see cref="SecretProtection"/>:
/// DPAPI under the current user on Windows, AES-256-GCM under an owner-only key file elsewhere.
/// </summary>
public sealed class RemoteProviderPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Purpose = "PaRuX.RemoteProvider.v1";

    private readonly string _stateDirectory;
    private readonly ISecretProtector _protector;
    private readonly InFlightRequestRegistry _inFlight;

    public RemoteProviderPolicy() : this(ServerState.StateDirectory) { }

    internal RemoteProviderPolicy(string stateDirectory)
        : this(stateDirectory, SecretProtection.For(stateDirectory)) { }

    internal RemoteProviderPolicy(string stateDirectory, ISecretProtector protector)
        : this(stateDirectory, protector, InFlightRequestRegistry.Shared) { }

    internal RemoteProviderPolicy(string stateDirectory, ISecretProtector protector, InFlightRequestRegistry inFlight)
    {
        _stateDirectory = stateDirectory;
        _protector = protector;
        _inFlight = inFlight;
    }

    private string PolicyPath => Path.Combine(_stateDirectory, "remote-provider-policy.json");

    /// <summary>True only when the user has explicitly allowed non-local inference. Any read
    /// failure is treated as "not allowed": defaulting a privacy boundary open on a corrupt file
    /// would be the wrong direction to fail.</summary>
    public bool IsRemoteAllowed()
    {
        try
        {
            if (!File.Exists(PolicyPath)) return false;
            Policy? policy = JsonSerializer.Deserialize<Policy>(File.ReadAllText(PolicyPath), JsonOptions);
            return policy?.AllowRemoteProviders == true;
        }
        catch { return false; }
    }

    public void SetRemoteAllowed(bool allowed)
    {
        Directory.CreateDirectory(_stateDirectory);
        Policy policy = Load();
        policy.AllowRemoteProviders = allowed;
        // Turning the policy off also drops the stored key: leaving a usable credential behind for
        // a capability the user just disabled is not something they asked for.
        if (!allowed) policy.ProtectedApiKey = null;
        File.WriteAllText(PolicyPath, JsonSerializer.Serialize(policy, JsonOptions));
        // Once the opt-in is off on disk, remote calls it admitted are cancelled rather than left to finish.
        if (!allowed) _inFlight.Cancel(InFlightRequestRegistry.RemoteProviderScope);
    }

    public bool HasApiKey() => !string.IsNullOrEmpty(Load().ProtectedApiKey);

    /// <summary>Stores the provider credential encrypted for the current user.</summary>
    public void SetApiKey(string? apiKey)
    {
        Directory.CreateDirectory(_stateDirectory);
        Policy policy = Load();
        policy.ProtectedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(Purpose, apiKey);
        File.WriteAllText(PolicyPath, JsonSerializer.Serialize(policy, JsonOptions));
    }

    /// <summary>Null when absent, or when the blob cannot be decrypted - which is the correct
    /// outcome if it was copied from another machine or user account.</summary>
    public string? TryGetApiKey()
    {
        string? blob = Load().ProtectedApiKey;
        return string.IsNullOrEmpty(blob) ? null : _protector.Unprotect(Purpose, blob);
    }

    private Policy Load()
    {
        try
        {
            if (File.Exists(PolicyPath))
                return JsonSerializer.Deserialize<Policy>(File.ReadAllText(PolicyPath), JsonOptions) ?? new Policy();
        }
        catch { }
        return new Policy();
    }

    private sealed class Policy
    {
        public bool AllowRemoteProviders { get; set; }
        public string? ProtectedApiKey { get; set; }
    }
}
