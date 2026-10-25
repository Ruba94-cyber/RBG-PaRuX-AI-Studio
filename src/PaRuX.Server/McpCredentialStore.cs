using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Encrypted per-server credentials for HTTP MCP servers: the bearer token a user pastes, and the
/// OAuth tokens the authorization flow returns.
///
/// Kept in its own file rather than inside <c>mcp-clients.json</c> for one concrete reason: that
/// config is exported by Backup, and a backup archive that carries live API tokens is a much worse
/// object than one that does not. Restoring a backup therefore restores which servers you had
/// configured, and asks you to re-authorise them - which is the correct trade.
/// </summary>
internal sealed class McpCredentialStore
{
    private const string Purpose = "PaRuX.McpServer.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    internal McpCredentialStore(string stateDirectory)
        : this(stateDirectory, SecretProtection.For(stateDirectory)) { }

    internal McpCredentialStore(string stateDirectory, ISecretProtector protector)
    {
        _stateDirectory = stateDirectory;
        _protector = protector;
    }

    private string StorePath => Path.Combine(_stateDirectory, "mcp-credentials.json");

    /// <summary>Stores (or, with null/blank, clears) a credential for one server and kind.</summary>
    internal void Set(string serverId, string kind, string? secret)
    {
        lock (_gate)
        {
            Dictionary<string, string> store = Load();
            string key = Key(serverId, kind);
            if (string.IsNullOrWhiteSpace(secret)) store.Remove(key);
            else store[key] = _protector.Protect(Purpose, secret);
            Save(store);
        }
    }

    /// <summary>Null when absent or unreadable - a credential that cannot be decrypted is, for
    /// every purpose the caller has, not there.</summary>
    internal string? Get(string serverId, string kind)
    {
        lock (_gate)
        {
            return Load().TryGetValue(Key(serverId, kind), out string? blob)
                ? _protector.Unprotect(Purpose, blob)
                : null;
        }
    }

    internal bool Has(string serverId, string kind)
    {
        lock (_gate) return Load().ContainsKey(Key(serverId, kind));
    }

    /// <summary>Drops every credential for a server. Called when the server is removed, so deleting
    /// a server does not leave its token behind on disk.</summary>
    internal void Forget(string serverId)
    {
        lock (_gate)
        {
            Dictionary<string, string> store = Load();
            string prefix = serverId + ":";
            foreach (string key in store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                store.Remove(key);
            Save(store);
        }
    }

    internal const string BearerKind = "bearer";
    internal const string OAuthClientSecretKind = "oauth-client-secret";
    internal const string OAuthTokensKind = "oauth-tokens";

    private static string Key(string serverId, string kind) => serverId + ":" + kind;

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath), JsonOptions)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> store)
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(store, JsonOptions));
    }
}
