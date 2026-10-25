using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable keys that let another application - Open WebUI, a script, an IDE plugin, anything that
/// speaks OpenAI or Ollama - call this PaRuX as a model provider.
///
/// Why the existing credentials do not serve this. The desktop token in bootstrap.json is generated
/// fresh every time PaRuX Core starts, so anything holding it breaks on the next restart; it is a
/// handle for the app that launched the process, not a credential. A paired-device token belongs to
/// one Android install and rides on certificate pinning a third-party HTTP client will not do. A
/// browser session is a cookie and a passphrase typed by a person. None of those is what you paste
/// into another program's "API key" box.
///
/// Why a fast hash here and a slow one for the passphrase. A passphrase is chosen by a human and
/// therefore guessable, so <see cref="WebAccessService"/> spends 210,000 PBKDF2 iterations making
/// each guess expensive. A key is 32 bytes from the system CSPRNG: there is nothing to guess, and
/// the only job of the hash is to keep a stolen key file from being replayed. SHA-256 does that,
/// and does it fast enough to run on every API request.
///
/// The full key is returned exactly once, when it is created. PaRuX cannot show it again because it
/// does not keep it - only the hash, the name the user gave it, and when it was last used.
/// </summary>
public sealed class ApiKeyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Prefixed so a leaked key is recognisable in a log or a paste, and so a client that
    /// sends the wrong secret entirely can be told what it should have sent.</summary>
    public const string KeyPrefix = "parux-sk-";

    private const int KeyBytes = 32;
    private const int MaximumKeys = 32;

    private readonly object _gate = new();
    private readonly string _configurationPath;
    private readonly InFlightRequestRegistry _inFlight;
    private ApiKeyDocument _document;

    // Set when api-keys.json exists but cannot be read or parsed. That file may hold every issued key, so the store
    // reports the failure instead of behaving as empty, and it refuses to write over the file until someone repairs it.
    private string? _unreadableReason;

    public ApiKeyService() : this(ConfigurationPath) { }

    /// <summary>Test-only entry point, mirroring <see cref="RemoteAccessService"/>.</summary>
    public ApiKeyService(string configurationPath) : this(configurationPath, InFlightRequestRegistry.Shared) { }

    /// <summary>Revoking a key cancels the requests it authenticated through <paramref name="inFlight"/>.</summary>
    public ApiKeyService(string configurationPath, InFlightRequestRegistry inFlight)
    {
        _configurationPath = configurationPath;
        _inFlight = inFlight ?? throw new ArgumentNullException(nameof(inFlight));
        _document = LoadDocument(configurationPath, out _unreadableReason);
    }

    public static string ConfigurationPath => Path.Combine(ServerState.StateDirectory, "api-keys.json");

    private sealed class ApiKeyDocument
    {
        public List<StoredKey> Keys { get; set; } = new();
    }

    private sealed class StoredKey
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
    }

    public IReadOnlyList<ApiKeyInfo> List()
    {
        lock (_gate)
        {
            EnsureReadable();
            return _document.Keys
                .Select(key => new ApiKeyInfo
                {
                    Id = key.Id,
                    Name = key.Name,
                    CreatedUtc = key.CreatedUtc,
                    LastUsedUtc = key.LastUsedUtc
                })
                .OrderBy(key => key.CreatedUtc)
                .ToList();
        }
    }

    /// <summary>
    /// Mints a key and returns it in full. This is the only moment the plaintext exists outside the
    /// caller's own program, so the caller must show it to the user now or lose it.
    /// </summary>
    public ApiKeyCreated Create(string? name)
    {
        string label = string.IsNullOrWhiteSpace(name) ? "Unnamed key" : name.Trim();
        if (label.Length > 64) label = label[..64];

        string secret = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var stored = new StoredKey
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = label,
            Hash = Hash(secret),
            CreatedUtc = DateTime.UtcNow
        };

        lock (_gate)
        {
            EnsureReadable();
            if (_document.Keys.Count >= MaximumKeys)
                throw new InvalidDataException(
                    "This PaRuX already has " + MaximumKeys + " API keys. Revoke one before creating another.");
            // Built as a new document and swapped in only once it is on disk, so a failed write changes nothing.
            var next = new ApiKeyDocument { Keys = new List<StoredKey>(_document.Keys) { stored } };
            Save(next);
            _document = next;
        }

        return new ApiKeyCreated
        {
            Id = stored.Id,
            Name = stored.Name,
            CreatedUtc = stored.CreatedUtc,
            Key = secret
        };
    }

    /// <summary>
    /// Removes the key, then cancels the requests it authenticated. The removal is written to disk before it takes
    /// effect: if the write fails the key stays issued and the exception reaches the caller, so a retry still finds it
    /// and a revocation is never reported as done while the key would come back after a restart. Once removed, a request
    /// admitted afterwards fails validation, and the requests already running under the key are cancelled.
    /// </summary>
    public bool Revoke(string id)
    {
        lock (_gate)
        {
            EnsureReadable();
            List<StoredKey> remaining = _document.Keys
                .Where(key => !string.Equals(key.Id, id, StringComparison.Ordinal))
                .ToList();
            if (remaining.Count == _document.Keys.Count) return false;

            var next = new ApiKeyDocument { Keys = remaining };
            Save(next);
            _document = next;
        }
        _inFlight.Cancel(InFlightRequestRegistry.ApiKeyScope(id));
        return true;
    }

    /// <summary>
    /// Whether the key is still issued. The middleware asks this after admitting a request, so a revocation that races
    /// the admission is still refused.
    /// </summary>
    public bool IsActive(string id)
    {
        lock (_gate)
            return _document.Keys.Exists(key => string.Equals(key.Id, id, StringComparison.Ordinal));
    }

    public bool Any { get { lock (_gate) return _document.Keys.Count > 0; } }

    /// <summary>
    /// Recognises a presented key. The compare is fixed-time over the hash, and the loop always runs
    /// to the end rather than returning on the first match, so how long a rejection took says
    /// nothing about how many keys exist or where in the list a near-miss landed.
    /// </summary>
    public bool TryValidate(string? presented, out string keyId)
    {
        keyId = string.Empty;
        if (string.IsNullOrEmpty(presented) || !presented.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return false;

        byte[] candidate = Convert.FromHexString(Hash(presented));
        string matched = string.Empty;

        lock (_gate)
        {
            foreach (StoredKey key in _document.Keys)
            {
                byte[] stored;
                try { stored = Convert.FromHexString(key.Hash); }
                catch { continue; }
                if (stored.Length == candidate.Length
                    && CryptographicOperations.FixedTimeEquals(stored, candidate))
                    matched = key.Id;
            }

            if (matched.Length == 0) return false;

            StoredKey? used = _document.Keys.Find(key => key.Id == matched);
            if (used != null)
            {
                // Recorded so a user revoking keys can tell which one is still in use. Written at
                // most once a minute: an API key is used on every request, and rewriting the file
                // that often would put a disk write on the inference path.
                DateTime now = DateTime.UtcNow;
                if (used.LastUsedUtc == null || now - used.LastUsedUtc.Value > TimeSpan.FromMinutes(1))
                {
                    used.LastUsedUtc = now;
                    TryPersistLastUsed();
                }
            }
        }

        keyId = matched;
        return true;
    }

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Writes through a temporary file that is then moved over the store. A crash or a locked file leaves either the
    /// previous document or the new one; a torn file would load as no keys at all.
    /// </summary>
    private void Save(ApiKeyDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        string temporary = _configurationPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, _configurationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Persists LastUsedUtc without letting a failed write fail the request that presented a valid key. The
    /// authentication stands and the in-memory timestamp is kept, so the next write, after the minute, retries it.
    /// </summary>
    private void TryPersistLastUsed()
    {
        try { Save(_document); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void EnsureReadable()
    {
        if (_unreadableReason is null) return;
        throw new InvalidDataException(
            "The API key store (api-keys.json) exists but could not be read, so PaRuX will not change or list it: "
            + _unreadableReason + ". Repair or remove the file, then restart PaRuX.");
    }

    /// <summary>
    /// A missing file is an empty store. A file that exists but cannot be read or parsed is not: it may hold every issued
    /// key. It is reported on use rather than replaced by an empty document that the next write would then overwrite.
    /// </summary>
    private static ApiKeyDocument LoadDocument(string path, out string? unreadableReason)
    {
        unreadableReason = null;
        if (!File.Exists(path)) return new ApiKeyDocument();
        try
        {
            ApiKeyDocument? document = JsonSerializer.Deserialize<ApiKeyDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null) unreadableReason = "the file holds no key list";
            else if (document.Keys is null || !document.Keys.TrueForAll(IsCompleteKey))
                unreadableReason = "the key list is missing or incomplete";
            else return document;
        }
        catch (Exception exception)
        {
            unreadableReason = exception.Message;
        }
        return new ApiKeyDocument();
    }

    // A key with a missing identifier, name or hash cannot be listed or matched, and would fault on the first request that reaches it.
    private static bool IsCompleteKey(StoredKey? key) =>
        key is not null && key.Id is not null && key.Name is not null && key.Hash is not null;
}
