using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Small desktop-owned journal for model sources whose bytes may survive as a .partial file.
    /// ModelDownloadService already resumes any normal request from an existing partial via HTTP
    /// Range; this catalog preserves the stable repo/file/resolve-URL tuple across Core restarts so
    /// Model Hub can submit that normal request again instead of depending on an in-memory job id.
    ///
    /// It never starts network work and never fabricates a completed model. A catalog row is exposed
    /// as resumable only while the corresponding .partial file actually exists. Completed/missing
    /// transfers are pruned opportunistically.
    /// </summary>
    public sealed class ModelDownloadResumeCatalog
    {
        private sealed class Entry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string RepoId { get; set; } = string.Empty;
            public string? Revision { get; set; }
            public string FileName { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public ModelDownloadLicenseEvidence? LicenseEvidence { get; set; }
            public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        }

        public const string SyntheticIdPrefix = "persisted-resume:";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private readonly object _gate = new();
        private readonly string _modelsDirectory;
        private readonly string _catalogPath;

        public static ModelDownloadResumeCatalog Current { get; } = new ModelDownloadResumeCatalog(DefaultModelsDirectory());

        public ModelDownloadResumeCatalog(string modelsDirectory)
        {
            _modelsDirectory = Path.GetFullPath(modelsDirectory);
            _catalogPath = Path.Combine(_modelsDirectory, ".download-resume.json");
        }

        public void Remember(ModelDownloadRequest request)
        {
            if (!IsStableSource(request)) return;
            lock (_gate)
            {
                List<Entry> entries = LoadUnsafe();
                Entry? existing = entries.FirstOrDefault(e =>
                    string.Equals(e.FileName, request.FileName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new Entry { FileName = request.FileName.Trim() };
                    entries.Add(existing);
                }
                existing.RepoId = request.RepoId?.Trim() ?? string.Empty;
                existing.Revision = ModelRevisionIdentity.Normalize(request.Revision);
                existing.DownloadUrl = request.DownloadUrl.Trim();
                existing.SizeBytes = Math.Max(0, request.SizeBytes);
                existing.LicenseEvidence = CloneLicenseEvidence(request.LicenseEvidence);
                existing.UpdatedUtc = DateTime.UtcNow;
                SaveUnsafe(entries);
            }
        }

        public bool TryResolve(string syntheticId, out ModelDownloadRequest request)
        {
            request = new ModelDownloadRequest();
            if (string.IsNullOrWhiteSpace(syntheticId)
                || !syntheticId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal))
                return false;
            string id = syntheticId[SyntheticIdPrefix.Length..];
            lock (_gate)
            {
                Entry? entry = LoadUnsafe().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
                if (entry == null || !PartialExists(entry.FileName)) return false;
                request = new ModelDownloadRequest
                {
                    RepoId = entry.RepoId,
                    Revision = entry.Revision,
                    FileName = entry.FileName,
                    DownloadUrl = entry.DownloadUrl,
                    SizeBytes = entry.SizeBytes,
                    LicenseEvidence = CloneLicenseEvidence(entry.LicenseEvidence)
                };
                return true;
            }
        }

        /// <summary>
        /// Returns synthetic cancelled/resumable statuses only for partials not already represented
        /// by the current Core's real job list. That prevents duplicate rows while the Core session
        /// is still alive; after restart the synthetic row becomes the handoff back into Start.
        /// </summary>
        public IReadOnlyList<ModelDownloadStatus> ListResumable(IReadOnlyCollection<ModelDownloadStatus> serverJobs)
        {
            lock (_gate)
            {
                List<Entry> entries = LoadUnsafe();
                var kept = new List<Entry>();
                var statuses = new List<ModelDownloadStatus>();
                foreach (Entry entry in entries)
                {
                    string partial = PartialPath(entry.FileName);
                    string final = FinalPath(entry.FileName);
                    if (File.Exists(final) && !File.Exists(partial))
                        continue; // completed successfully: no resume metadata is needed anymore
                    if (!File.Exists(partial))
                        continue; // cancelled before first byte or manually removed

                    kept.Add(entry);
                    if (serverJobs.Any(j => string.Equals(j.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    long downloaded = 0;
                    try { downloaded = new FileInfo(partial).Length; } catch { }
                    statuses.Add(new ModelDownloadStatus
                    {
                        Id = SyntheticIdPrefix + entry.Id,
                        Status = "cancelled",
                        FileName = entry.FileName,
                        Revision = entry.Revision,
                        BytesDownloaded = downloaded,
                        TotalBytes = Math.Max(entry.SizeBytes, downloaded),
                        Error = "Transfer was interrupted by a previous PaRuX Core session; the partial file is preserved and resumable.",
                        CreatedUtc = entry.CreatedUtc,
                        UpdatedUtc = File.GetLastWriteTimeUtc(partial),
                        VerificationStatus = "pending"
                    });
                }

                if (kept.Count != entries.Count) SaveUnsafe(kept);
                return statuses;
            }
        }

        public static bool IsSyntheticId(string? id) =>
            !string.IsNullOrWhiteSpace(id) && id.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal);

        private bool PartialExists(string fileName)
        {
            try { return File.Exists(PartialPath(fileName)); }
            catch { return false; }
        }

        private string FinalPath(string fileName) => Path.Combine(_modelsDirectory, SafeFileName(fileName));
        private string PartialPath(string fileName) => FinalPath(fileName) + ".partial";

        private List<Entry> LoadUnsafe()
        {
            try
            {
                if (!File.Exists(_catalogPath)) return new List<Entry>();
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_catalogPath), JsonOptions) ?? new List<Entry>();
            }
            catch { return new List<Entry>(); }
        }

        private void SaveUnsafe(List<Entry> entries)
        {
            Directory.CreateDirectory(_modelsDirectory);
            string temp = _catalogPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temp, _catalogPath, overwrite: true);
        }

        private static bool IsStableSource(ModelDownloadRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.DownloadUrl))
                return false;
            if (request.Revision != null && !ModelRevisionIdentity.IsValid(request.Revision)) return false;
            try
            {
                string fileName = SafeFileName(request.FileName);
                if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;
                return Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
            }
            catch { return false; }
        }

        private static ModelDownloadLicenseEvidence? CloneLicenseEvidence(ModelDownloadLicenseEvidence? evidence) => evidence == null
            ? null
            : new ModelDownloadLicenseEvidence
            {
                MetadataStatus = evidence.MetadataStatus,
                PublisherDeclaredLicense = evidence.PublisherDeclaredLicense,
                MetadataObservedUtc = evidence.MetadataObservedUtc,
                ReviewRequired = evidence.ReviewRequired,
                ReviewAcknowledged = evidence.ReviewAcknowledged,
                ReviewAcknowledgedUtc = evidence.ReviewAcknowledgedUtc
            };

        private static string SafeFileName(string fileName)
        {
            string trimmed = (fileName ?? string.Empty).Trim();
            string safe = Path.GetFileName(trimmed);
            if (safe.Length == 0 || !string.Equals(safe, trimmed, StringComparison.Ordinal))
                throw new InvalidDataException("A resumable model must use a simple file name.");
            return safe;
        }

        private static string DefaultModelsDirectory() => Path.Combine(
            PaRuX.Contracts.PaRuXDataRoot.Path, "models");
    }
}
