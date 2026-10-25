using System.Text.Json;

namespace PaRuX.Server;

public enum ManagedAiAssetKind
{
    Application = 0,
    InferenceEngine = 1,
    RuntimeBuild = 2,
    Model = 3,
    EmbeddingModel = 4,
    RerankerModel = 5,
    MediaEngine = 6,
    SpeechEngine = 7,
    TrainingPack = 8,
    ThreeDPack = 9,
    AvatarPack = 10,
    OfflineAssetSet = 11
}

public enum ManagedAiUpdateState
{
    Available = 0,
    Downloading = 1,
    Staged = 2,
    Verifying = 3,
    Benchmarking = 4,
    ReadyToPromote = 5,
    Installed = 6,
    RolledBack = 7,
    Failed = 8,
    Dismissed = 9,
    Cancelled = 10
}

public sealed record ManagedAiAssetIdentity(
    ManagedAiAssetKind Kind,
    string AssetId,
    string DisplayName,
    string? VariantId = null,
    string? EngineId = null)
{
    public string StableKey => string.Join('|',
        Kind.ToString(), AssetId.Trim(), (VariantId ?? string.Empty).Trim(), (EngineId ?? string.Empty).Trim());
}

public sealed class ManagedAiUpdateRecord
{
    public ManagedAiAssetIdentity Asset { get; set; } = new(ManagedAiAssetKind.Application, "parux", "PaRuX");
    public string? InstalledVersion { get; set; }
    public string AvailableVersion { get; set; } = string.Empty;
    public string? SourceRevision { get; set; }
    public string? SourceUri { get; set; }
    public string? ExpectedSha256 { get; set; }
    public ManagedAiUpdateState State { get; set; } = ManagedAiUpdateState.Available;
    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? Message { get; set; }
    public string? RollbackVersion { get; set; }
    public bool UserManaged { get; set; }
    public bool NotificationAcknowledged { get; set; }
}

/// <summary>
/// Durable, engine-neutral update/notification state. This store deliberately does not download or
/// execute anything: discovery, transport, verification and promotion are separate boundaries.
/// Multiple service instances can point at the same state file, so all read-modify-write operations
/// share one process-wide gate rather than racing independent instance locks.
/// </summary>
public sealed class ManagedAiUpdateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly object Gate = new();

    private readonly string _path;

    public ManagedAiUpdateStore() : this(Path.Combine(ServerState.StateDirectory, "managed-ai-updates.json")) { }

    internal ManagedAiUpdateStore(string path) => _path = path;

    public IReadOnlyList<ManagedAiUpdateRecord> List(bool includeDismissed = false)
    {
        lock (Gate)
        {
            List<ManagedAiUpdateRecord> items = LoadUnsafe();
            return items
                .Where(item => includeDismissed || item.State != ManagedAiUpdateState.Dismissed)
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(Clone)
                .ToList();
        }
    }

    public ManagedAiUpdateRecord? Find(ManagedAiAssetIdentity asset)
    {
        lock (Gate)
        {
            ManagedAiUpdateRecord? item = LoadUnsafe().FirstOrDefault(candidate => SameAsset(candidate.Asset, asset));
            return item == null ? null : Clone(item);
        }
    }

    public ManagedAiUpdateRecord UpsertAvailable(
        ManagedAiAssetIdentity asset,
        string? installedVersion,
        string availableVersion,
        string? sourceRevision,
        string? sourceUri,
        string? expectedSha256,
        bool userManaged = false,
        string? message = null)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetId)) throw new ArgumentException("Asset id is required.", nameof(asset));
        if (string.IsNullOrWhiteSpace(availableVersion)) throw new ArgumentException("Available version is required.", nameof(availableVersion));

        lock (Gate)
        {
            List<ManagedAiUpdateRecord> items = LoadUnsafe();
            ManagedAiUpdateRecord? existing = items.FirstOrDefault(candidate => SameAsset(candidate.Asset, asset));
            DateTime now = DateTime.UtcNow;
            if (existing == null)
            {
                existing = new ManagedAiUpdateRecord
                {
                    Asset = asset,
                    InstalledVersion = installedVersion,
                    AvailableVersion = availableVersion,
                    SourceRevision = sourceRevision,
                    SourceUri = sourceUri,
                    ExpectedSha256 = NormalizeSha(expectedSha256),
                    State = ManagedAiUpdateState.Available,
                    DiscoveredUtc = now,
                    UpdatedUtc = now,
                    Message = message,
                    UserManaged = userManaged,
                    NotificationAcknowledged = false
                };
                items.Add(existing);
            }
            else
            {
                bool changedVersion = !string.Equals(existing.AvailableVersion, availableVersion, StringComparison.Ordinal);
                existing.Asset = asset;
                existing.InstalledVersion = installedVersion;
                existing.AvailableVersion = availableVersion;
                existing.SourceRevision = sourceRevision;
                existing.SourceUri = sourceUri;
                existing.ExpectedSha256 = NormalizeSha(expectedSha256);
                existing.UserManaged = userManaged;
                existing.UpdatedUtc = now;

                // Dismissed/failed/cancelled work stays truthful for that exact version. A later
                // periodic check may refresh metadata, but only a genuinely newer version becomes
                // a fresh "available" notification automatically.
                if (changedVersion)
                {
                    existing.State = ManagedAiUpdateState.Available;
                    existing.NotificationAcknowledged = false;
                    existing.DiscoveredUtc = now;
                    existing.Message = message;
                    existing.RollbackVersion = null;
                }
                else if (existing.State == ManagedAiUpdateState.Available)
                {
                    existing.Message = message;
                }
            }
            SaveUnsafe(items);
            return Clone(existing);
        }
    }

    /// <summary>
    /// Records a version that has already been installed by an external trusted lifecycle such as
    /// Windows Update. Returns null when the exact observed version was already known, preventing
    /// repeated notifications on every periodic check. A changed fingerprint produces an Installed
    /// milestone with the previous fingerprint retained as rollback/history evidence; it does not
    /// pretend PaRuX still has to download an update that the operating system already applied.
    /// </summary>
    public ManagedAiUpdateRecord? RecordObservedInstalledVersion(
        ManagedAiAssetIdentity asset,
        string installedVersion,
        string? sourceRevision,
        string? sourceUri,
        string? expectedSha256,
        bool userManaged = false,
        string? message = null)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetId)) throw new ArgumentException("Asset id is required.", nameof(asset));
        if (string.IsNullOrWhiteSpace(installedVersion)) throw new ArgumentException("Installed version is required.", nameof(installedVersion));

        lock (Gate)
        {
            List<ManagedAiUpdateRecord> items = LoadUnsafe();
            ManagedAiUpdateRecord? existing = items.FirstOrDefault(candidate => SameAsset(candidate.Asset, asset));
            string normalizedVersion = installedVersion.Trim();
            string? normalizedSha = NormalizeSha(expectedSha256);
            if (existing != null
                && existing.State == ManagedAiUpdateState.Installed
                && string.Equals(existing.InstalledVersion, normalizedVersion, StringComparison.Ordinal)
                && string.Equals(existing.ExpectedSha256, normalizedSha, StringComparison.OrdinalIgnoreCase))
                return null;

            DateTime now = DateTime.UtcNow;
            string? previous = existing?.InstalledVersion;
            if (existing == null)
            {
                existing = new ManagedAiUpdateRecord();
                items.Add(existing);
            }

            existing.Asset = asset;
            existing.RollbackVersion = previous;
            existing.InstalledVersion = normalizedVersion;
            existing.AvailableVersion = normalizedVersion;
            existing.SourceRevision = sourceRevision;
            existing.SourceUri = sourceUri;
            existing.ExpectedSha256 = normalizedSha;
            existing.State = ManagedAiUpdateState.Installed;
            existing.DiscoveredUtc = now;
            existing.UpdatedUtc = now;
            existing.Message = message;
            existing.UserManaged = userManaged;
            existing.NotificationAcknowledged = false;
            SaveUnsafe(items);
            return Clone(existing);
        }
    }

    public ManagedAiUpdateRecord Transition(
        ManagedAiAssetIdentity asset,
        ManagedAiUpdateState state,
        string? message = null,
        string? installedVersion = null,
        string? rollbackVersion = null)
    {
        lock (Gate)
        {
            List<ManagedAiUpdateRecord> items = LoadUnsafe();
            ManagedAiUpdateRecord item = items.FirstOrDefault(candidate => SameAsset(candidate.Asset, asset))
                ?? throw new KeyNotFoundException("Managed AI update state does not exist for " + asset.StableKey + ".");
            ValidateTransition(item.State, state);
            item.State = state;
            item.UpdatedUtc = DateTime.UtcNow;
            if (message != null) item.Message = message;
            if (installedVersion != null) item.InstalledVersion = installedVersion;
            if (rollbackVersion != null) item.RollbackVersion = rollbackVersion;

            if (state is ManagedAiUpdateState.Available
                or ManagedAiUpdateState.Staged
                or ManagedAiUpdateState.ReadyToPromote
                or ManagedAiUpdateState.Installed
                or ManagedAiUpdateState.Failed
                or ManagedAiUpdateState.RolledBack)
                item.NotificationAcknowledged = false;
            else if (state == ManagedAiUpdateState.Cancelled)
                item.NotificationAcknowledged = true;

            SaveUnsafe(items);
            return Clone(item);
        }
    }

    public void Acknowledge(ManagedAiAssetIdentity asset)
    {
        lock (Gate)
        {
            List<ManagedAiUpdateRecord> items = LoadUnsafe();
            ManagedAiUpdateRecord item = items.FirstOrDefault(candidate => SameAsset(candidate.Asset, asset))
                ?? throw new KeyNotFoundException("Managed AI update state does not exist for " + asset.StableKey + ".");
            item.NotificationAcknowledged = true;
            item.UpdatedUtc = DateTime.UtcNow;
            SaveUnsafe(items);
        }
    }

    private static void ValidateTransition(ManagedAiUpdateState from, ManagedAiUpdateState to)
    {
        if (from == to) return;
        if (to is ManagedAiUpdateState.Failed or ManagedAiUpdateState.RolledBack or ManagedAiUpdateState.Dismissed or ManagedAiUpdateState.Cancelled)
            return;

        bool valid = from switch
        {
            ManagedAiUpdateState.Available => to == ManagedAiUpdateState.Downloading,
            ManagedAiUpdateState.Downloading => to == ManagedAiUpdateState.Staged,
            ManagedAiUpdateState.Staged => to == ManagedAiUpdateState.Verifying,
            ManagedAiUpdateState.Verifying => to is ManagedAiUpdateState.Benchmarking or ManagedAiUpdateState.ReadyToPromote,
            ManagedAiUpdateState.Benchmarking => to == ManagedAiUpdateState.ReadyToPromote,
            ManagedAiUpdateState.ReadyToPromote => to == ManagedAiUpdateState.Installed,
            ManagedAiUpdateState.Installed => to == ManagedAiUpdateState.Available,
            ManagedAiUpdateState.Dismissed or ManagedAiUpdateState.Failed or ManagedAiUpdateState.Cancelled =>
                to == ManagedAiUpdateState.Downloading,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Invalid managed AI update transition: {from} -> {to}.");
    }

    private List<ManagedAiUpdateRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new List<ManagedAiUpdateRecord>();
            return JsonSerializer.Deserialize<List<ManagedAiUpdateRecord>>(File.ReadAllText(_path), JsonOptions)
                ?? new List<ManagedAiUpdateRecord>();
        }
        catch
        {
            try
            {
                string corrupt = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_path, corrupt, overwrite: true);
            }
            catch { }
            return new List<ManagedAiUpdateRecord>();
        }
    }

    private void SaveUnsafe(List<ManagedAiUpdateRecord> items)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static bool SameAsset(ManagedAiAssetIdentity left, ManagedAiAssetIdentity right) =>
        string.Equals(left.StableKey, right.StableKey, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        string normalized = sha.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("Expected SHA-256 must be 64 hexadecimal characters.");
        return normalized;
    }

    private static ManagedAiUpdateRecord Clone(ManagedAiUpdateRecord value) => new()
    {
        Asset = value.Asset,
        InstalledVersion = value.InstalledVersion,
        AvailableVersion = value.AvailableVersion,
        SourceRevision = value.SourceRevision,
        SourceUri = value.SourceUri,
        ExpectedSha256 = value.ExpectedSha256,
        State = value.State,
        DiscoveredUtc = value.DiscoveredUtc,
        UpdatedUtc = value.UpdatedUtc,
        Message = value.Message,
        RollbackVersion = value.RollbackVersion,
        UserManaged = value.UserManaged,
        NotificationAcknowledged = value.NotificationAcknowledged
    };
}
