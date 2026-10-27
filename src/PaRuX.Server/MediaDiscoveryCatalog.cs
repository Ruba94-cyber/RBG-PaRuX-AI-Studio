using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// One media checkpoint found on Hugging Face whose family PaRuX can run, read from the repository listing (size, LFS sha256, pinned
/// commit) and the safetensors header. Publisher-declared metadata, not curated and never a quality claim. Hardware fit is NOT stored:
/// it is computed against the current PC every time the candidate is shown.
/// </summary>
internal sealed class MediaDiscoveredCandidate
{
    public string Key { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>LFS sha256 exactly as listed, or null when the listing did not carry one.</summary>
    public string? Sha256 { get; set; }
    public string Capability { get; set; } = string.Empty;
    /// <summary>standard or uncensored.</summary>
    public string Lane { get; set; } = "standard";
    /// <summary>The phrase or tag from the repository that put it in the uncensored lane (required for uncensored).</summary>
    public string? LaneEvidence { get; set; }
    public string RegistryFamilyId { get; set; } = string.Empty;
    public string Precision { get; set; } = "unknown";
    public double ParametersBillion { get; set; }
    public string License { get; set; } = string.Empty;
    public long Downloads { get; set; }
    public long Likes { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    internal static string MakeKey(string repo, string revision, string path) => repo + "@" + revision + ":" + path;
}

/// <summary>What was concluded about one repository at one commit, so an unchanged repository is not re-inspected.</summary>
internal sealed class MediaInspectedRepo
{
    public string RepoId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public DateTime InspectedUtc { get; set; }
    /// <summary>accepted or rejected:&lt;reason&gt;.</summary>
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>Cached search answer with its HTTP validator, so an unchanged result costs one conditional request.</summary>
internal sealed class MediaSearchCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public DateTime FetchedUtc { get; set; }
    public List<MediaRepoSummary> Repos { get; set; } = new();
}

internal sealed class MediaDiscoveryCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<MediaDiscoveredCandidate> Candidates { get; set; } = new();
    public List<MediaInspectedRepo> Inspected { get; set; } = new();
    public List<MediaSearchCacheEntry> SearchCache { get; set; } = new();
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? NextRefreshUtc { get; set; }
    public string? LastErrorCode { get; set; }
}

/// <summary>Versioned discovery catalogue under the PaRuX state directory (like the chat discovery store). A corrupt or future-schema
/// file is never silently reset: Load throws InvalidDataException and the file is left for repair.</summary>
internal sealed class MediaDiscoveryStore
{
    internal const string FileName = "media-discovery-candidates.json";
    internal const int MaximumCandidates = 300;
    internal const int MaximumInspected = 600;
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 32 };
    private static readonly object WriteLock = new();
    private readonly string _path;

    internal MediaDiscoveryStore() : this(ServerState.StateDirectory) { }

    internal MediaDiscoveryStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = System.IO.Path.Combine(stateDirectory, FileName);
    }

    internal string FilePath => _path;

    /// <summary>File length and write time; the effective catalogue is rebuilt only when this changes.</summary>
    internal string Stamp()
    {
        try { var f = new FileInfo(_path); return f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "none"; }
        catch (IOException) { return "none"; }
    }

    internal MediaDiscoveryCatalog Load()
    {
        if (!File.Exists(_path)) return new MediaDiscoveryCatalog();
        try
        {
            if (new FileInfo(_path).Length > MaximumDocumentBytes) throw new InvalidDataException("The media discovery catalogue exceeds its size limit.");
            MediaDiscoveryCatalog? catalog = JsonSerializer.Deserialize<MediaDiscoveryCatalog>(File.ReadAllText(_path), JsonOptions);
            Validate(catalog);
            return catalog!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The media discovery catalogue is corrupt or has an unsupported schema.", ex);
        }
    }

    internal void Save(MediaDiscoveryCatalog catalog)
    {
        lock (WriteLock)
        {
            Validate(catalog);
            string json = JsonSerializer.Serialize(catalog, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new InvalidDataException("The media discovery catalogue exceeds its size limit.");
            _ = Load(); // never replace a document that is corrupt or from a newer schema
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void Validate(MediaDiscoveryCatalog? catalog)
    {
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Candidates is null || catalog.Inspected is null || catalog.SearchCache is null
            || catalog.Candidates.Count > MaximumCandidates || catalog.Inspected.Count > MaximumInspected
            || catalog.LastErrorCode is { Length: > 64 })
            throw new InvalidDataException("The media discovery catalogue has invalid or unsupported data.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaDiscoveredCandidate c in catalog.Candidates)
        {
            if (c is null || !ModelDiscoveryIdentity.IsSafeRepoId(c.RepoId) || !RevisionPattern.IsMatch(c.Revision ?? string.Empty)
                || !ModelDiscoveryIdentity.IsSafeRepoFilePath(c.Path) || !c.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                || c.FileName != c.Path[(c.Path.LastIndexOf('/') + 1)..] || c.SizeBytes <= 0
                || c.Key != MediaDiscoveredCandidate.MakeKey(c.RepoId, c.Revision, c.Path) || !keys.Add(c.Key)
                || c.Lane is not ("standard" or "uncensored") || (c.Lane == "uncensored" && string.IsNullOrWhiteSpace(c.LaneEvidence))
                || !MediaCatalogue.CapabilityIds.Contains(c.Capability) || string.IsNullOrWhiteSpace(c.RegistryFamilyId)
                || (c.Sha256 is not null && !Sha256Pattern.IsMatch(c.Sha256))
                || c.License is null || c.License.Length > 256 || c.Tags is null || c.Tags.Count > 32 || c.Strengths is null
                || c.Strengths.Any(s => !MediaCatalogue.Strengths.Contains(s)))
                throw new InvalidDataException("The media discovery catalogue contains an invalid candidate.");
        }
    }
}
