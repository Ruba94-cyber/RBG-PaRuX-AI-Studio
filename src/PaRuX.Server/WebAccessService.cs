using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Owns PaRuX's opt-in browser boundary: the passphrase, and the sessions that let a person reach
/// PaRuX from another PC through a reverse proxy (Nginx Proxy Manager, Caddy, Traefik) instead of
/// through a paired Android device.
///
/// Why this is a separate boundary from <see cref="RemoteAccessService"/>. That one is built for the
/// Android client, which pins PaRuX's certificate and carries a device token issued during pairing.
/// A browser can do neither, which is exactly why the LAN path refuses any request carrying an
/// Origin header. Rather than weaken that rule for convenience, browsers get their own door with
/// their own key.
///
/// Why a passphrase, and not "trust the proxy". With the proxy on the same machine, a request that
/// travelled from the public Internet arrives at PaRuX from 127.0.0.1 - indistinguishable from a
/// process running on this desktop. Loopback stops being evidence of anything once a proxy is in
/// front of it, so the passphrase becomes the only real frontier. It is stored only as a
/// PBKDF2-SHA256 hash and never leaves this machine.
///
/// The session cookie is HttpOnly (script cannot read it, so an injected script cannot steal it),
/// Secure (sent only over HTTPS, which the proxy terminates; http://localhost also counts as a
/// secure context) and SameSite=Strict (another site cannot make the browser send it at all). Every
/// unsafe request must additionally echo the session's CSRF token in a header, which a cross-site
/// form cannot set. Failed attempts are counted and locked out, because a passphrase reachable from
/// the Internet will be guessed at.
/// </summary>
public sealed class WebAccessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // OWASP's 2023 floor for PBKDF2-HMAC-SHA256. Sized for the attacker who has stolen the file,
    // not the one guessing over the network - lockout handles that one.
    private const int PbkdfIterations = 210_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;

    private const int MaximumFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromHours(12);
    private static readonly TimeSpan SessionAbsoluteLifetime = TimeSpan.FromDays(7);

    public const string SessionCookieName = "parux_session";
    public const string CsrfHeaderName = "X-PaRuX-Csrf";
    public const int MinimumPassphraseLength = 12;

    // A resume that loses the race to another request's slide retries this many times, then fails closed.
    private const int MaximumResumeAttempts = 8;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly List<DateTime> _failedAttempts = new();
    private readonly string _configurationPath;
    private WebAccessDocument _document;

    // Set when web-access.json exists but cannot be read or parsed. Browser access then stays off, and the store refuses to
    // write over the file until it is repaired, rather than behaving as though it had never been configured.
    private string? _unreadableReason;

    // Counts the PBKDF2 verifications that actually start, so a test can prove that a lockout stops them.
    private int _passphraseVerifications;

    public WebAccessService() : this(ConfigurationPath) { }

    /// <summary>Test-only entry point, mirroring <see cref="RemoteAccessService"/>: production always
    /// uses the parameterless constructor registered as the DI singleton.</summary>
    public WebAccessService(string configurationPath)
    {
        _configurationPath = configurationPath;
        _document = LoadDocument(configurationPath, out _unreadableReason);
    }

    public static string ConfigurationPath => Path.Combine(ServerState.StateDirectory, "web-access.json");

    public bool Enabled { get { lock (_gate) return _document.Enabled && _document.PassphraseHash.Length > 0; } }

    public bool PassphraseSet { get { lock (_gate) return _document.PassphraseHash.Length > 0; } }

    /// <summary>Test seam: runs between a resume's read of the session and its write back, the window that a concurrent
    /// revocation must not slip into. Always null in production.</summary>
    internal Action? BeforeResumeWriteForTests { get; set; }

    internal int PassphraseVerificationCount => Volatile.Read(ref _passphraseVerifications);

    public sealed record Session(string Id, string CsrfToken, DateTime IssuedUtc, DateTime LastSeenUtc);

    private sealed class WebAccessDocument
    {
        public bool Enabled { get; set; }
        public string PassphraseHash { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public int Iterations { get; set; } = PbkdfIterations;
    }

    /// <summary>
    /// Sets the passphrase. Every existing session is dropped, so changing it actually evicts
    /// whoever was using the old one rather than leaving them signed in.
    /// </summary>
    public void SetPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < MinimumPassphraseLength)
            throw new InvalidDataException(
                "Choose a passphrase of at least " + MinimumPassphraseLength + " characters. Once a "
                + "reverse proxy is in front of PaRuX, this is the only thing between the Internet "
                + "and this PC.");

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, PbkdfIterations, HashAlgorithmName.SHA256, HashBytes);

        lock (_gate)
        {
            // Persisted before it takes effect, so a failed write leaves the previous passphrase in force rather than one
            // that the next start would forget.
            var next = new WebAccessDocument
            {
                Enabled = _document.Enabled,
                Salt = Convert.ToBase64String(salt),
                PassphraseHash = Convert.ToBase64String(hash),
                Iterations = PbkdfIterations
            };
            Save(next);
            _document = next;
            _failedAttempts.Clear();
        }

        _sessions.Clear();
    }

    /// <summary>Turning this on without a passphrase is refused rather than silently opening a door.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            // An unreadable file is named first: "set a passphrase" would send the person to fix the wrong thing.
            RequireReadable();
            if (enabled && _document.PassphraseHash.Length == 0)
                throw new InvalidDataException("Set a passphrase before turning browser access on.");
            var next = new WebAccessDocument
            {
                Enabled = enabled,
                Salt = _document.Salt,
                PassphraseHash = _document.PassphraseHash,
                Iterations = _document.Iterations
            };
            Save(next);
            _document = next;
        }

        if (!enabled) _sessions.Clear();
    }

    /// <summary>Forgets the passphrase and turns browser access off - the way back to how PaRuX ships.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            var next = new WebAccessDocument();
            Save(next);
            _document = next;
            _failedAttempts.Clear();
        }
        _sessions.Clear();
    }

    public bool IsLockedOut(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            PruneFailedAttempts();
            if (_failedAttempts.Count < MaximumFailedAttempts) { retryAfter = TimeSpan.Zero; return false; }
            retryAfter = _failedAttempts[0] + LockoutWindow - DateTime.UtcNow;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Verifies a passphrase and, on success, issues a session. The derivation runs and the compare
    /// is fixed-time even when no passphrase has been set, so neither the answer nor how long it
    /// took reveals whether this PaRuX has browser access configured at all.
    /// </summary>
    public Session? TryAuthenticate(string? passphrase)
    {
        // The attempt is counted before the expensive verification, not after it. Counting only once PBKDF2 had returned
        // let every concurrent request pass the lockout check before any failure was recorded, so N parallel requests got
        // N guesses per window. Reserving the slot first holds the verifications to the limit, and a locked-out caller
        // never reaches PBKDF2 at all.
        lock (_gate)
        {
            PruneFailedAttempts();
            if (_failedAttempts.Count >= MaximumFailedAttempts) return null;
            _failedAttempts.Add(DateTime.UtcNow);
        }

        string saltText;
        string expected;
        int iterations;
        lock (_gate)
        {
            saltText = _document.Salt;
            expected = _document.PassphraseHash;
            iterations = _document.Iterations > 0 ? _document.Iterations : PbkdfIterations;
        }

        byte[] salt = saltText.Length > 0 ? Convert.FromBase64String(saltText) : new byte[SaltBytes];
        Interlocked.Increment(ref _passphraseVerifications);
        byte[] candidate = Rfc2898DeriveBytes.Pbkdf2(
            passphrase ?? string.Empty, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        byte[] stored = expected.Length > 0 ? Convert.FromBase64String(expected) : new byte[HashBytes];

        bool ok = expected.Length > 0 && CryptographicOperations.FixedTimeEquals(candidate, stored);
        if (!ok) return null; // the reserved attempt stays counted

        lock (_gate) _failedAttempts.Clear();

        var session = new Session(NewSecret(), NewSecret(), DateTime.UtcNow, DateTime.UtcNow);
        _sessions[session.Id] = session;
        PruneSessions();
        return session;
    }

    /// <summary>Returns the live session for a cookie value, sliding its idle timeout forward.</summary>
    public Session? TryResume(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !Enabled) return null;

        // Sliding is a compare-and-swap against the copy that was read. An unconditional write could put back a session
        // that Revoke, RevokeAll or SetPassphrase removed between the read and the write.
        for (int attempt = 0; attempt < MaximumResumeAttempts; attempt++)
        {
            if (!_sessions.TryGetValue(sessionId, out Session? session)) return null;

            DateTime now = DateTime.UtcNow;
            if (now - session.IssuedUtc > SessionAbsoluteLifetime || now - session.LastSeenUtc > SessionIdleTimeout)
            {
                _sessions.TryRemove(sessionId, out _);
                return null;
            }

            Session slid = session with { LastSeenUtc = now };
            BeforeResumeWriteForTests?.Invoke();
            if (_sessions.TryUpdate(sessionId, slid, session)) return slid;
            // Another request slid this session first, or it was removed. Reading again tells the two apart.
        }
        return null;
    }

    public void Revoke(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) _sessions.TryRemove(sessionId, out _);
    }

    public void RevokeAll() => _sessions.Clear();

    public int ActiveSessionCount
    {
        get { PruneSessions(); return _sessions.Count; }
    }

    private void PruneSessions()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, Session> entry in _sessions)
        {
            if (now - entry.Value.IssuedUtc > SessionAbsoluteLifetime
                || now - entry.Value.LastSeenUtc > SessionIdleTimeout)
                _sessions.TryRemove(entry.Key, out _);
        }
    }

    private void PruneFailedAttempts()
    {
        DateTime cutoff = DateTime.UtcNow - LockoutWindow;
        _failedAttempts.RemoveAll(attempt => attempt < cutoff);
    }

    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Refuses every change while the existing file is unreadable, because it holds the passphrase hash.</summary>
    private void RequireReadable()
    {
        if (_unreadableReason is null) return;
        throw new InvalidDataException(
            "The browser-access settings (web-access.json) exist but could not be read, so PaRuX will not change them: "
            + _unreadableReason + ". Repair or remove the file, then restart PaRuX.");
    }

    /// <summary>
    /// Writes through a temporary file that is then moved over the store, so a crash or a locked file leaves either the
    /// previous document or the new one. Refuses while the existing file is unreadable: it must not be overwritten.
    /// </summary>
    private void Save(WebAccessDocument document)
    {
        RequireReadable();

        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        string temporary = _configurationPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, _configurationPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A missing file is an unconfigured browser door. A file that exists but cannot be read or parsed is not: it holds the
    /// passphrase hash, so it is reported and browser access stays off, instead of being replaced by a blank document.
    /// </summary>
    private static WebAccessDocument LoadDocument(string path, out string? unreadableReason)
    {
        unreadableReason = null;
        if (!File.Exists(path)) return new WebAccessDocument();
        try
        {
            WebAccessDocument? document = JsonSerializer.Deserialize<WebAccessDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null) unreadableReason = "the file holds no settings";
            else if (!IsWellFormed(document)) unreadableReason = "a required setting is missing or is not valid";
            else return document;
        }
        catch (Exception exception)
        {
            unreadableReason = exception.Message;
        }
        return new WebAccessDocument();
    }

    /// <summary>
    /// A null or malformed hash or salt is not a blank store. Every check that reads them would fault, and a passphrase that
    /// cannot be verified must not be taken for an unconfigured one. Empty strings are the legitimate "never set" state.
    /// </summary>
    private static bool IsWellFormed(WebAccessDocument document) =>
        document.PassphraseHash is not null && document.Salt is not null
        && (document.PassphraseHash.Length == 0 || DecodesToLength(document.PassphraseHash, HashBytes))
        && (document.Salt.Length == 0 || DecodesToLength(document.Salt, SaltBytes));

    private static bool DecodesToLength(string base64, int length) =>
        Convert.TryFromBase64String(base64, new byte[length], out int written) && written == length;
}
