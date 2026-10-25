using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Durable, prompt-independent evidence store for coding-agent tool output.
    ///
    /// Long build/test logs, diffs and source files must not be destroyed merely because only a
    /// small preview fits in the next model turn. Artifacts live beside the coding checkpoint,
    /// outside every repository/worktree, are addressed by opaque ids, and can be paged/searched
    /// without exposing their host path to the model.
    /// </summary>
    public static class CodingAgentArtifactStore
    {
        public const int MaxArtifactsPerSession = 256;
        public const int MaxArtifactCharacters = 16 * 1024 * 1024;
        public const int DefaultReadCharacters = 8_000;
        public const int MaxReadCharacters = 64_000;
        public const int MaxSearchMatches = 100;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public sealed class ArtifactMetadata
        {
            public string Id { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public int Characters { get; set; }
            public int Lines { get; set; }
            public string Sha256 { get; set; } = string.Empty;
            public bool WasTruncatedAtStorage { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        public sealed record ReadResult(
            ArtifactMetadata Metadata,
            int Offset,
            int ReturnedCharacters,
            bool HasMore,
            string Text);

        public sealed record SearchMatch(int LineNumber, string Line);

        public sealed record SearchResult(
            ArtifactMetadata Metadata,
            string Query,
            bool Regex,
            IReadOnlyList<SearchMatch> Matches);

        public static ArtifactMetadata SaveText(string sessionId, string kind, string source, string? text) =>
            SaveTextToDirectory(SessionDirectory(sessionId), kind, source, text);

        internal static ArtifactMetadata SaveTextToDirectory(
            string directory, string kind, string source, string? text)
        {
            Directory.CreateDirectory(directory);
            string normalized = text ?? string.Empty;
            bool truncated = normalized.Length > MaxArtifactCharacters;
            if (truncated) normalized = normalized[..MaxArtifactCharacters];

            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + hash[..12];
            string dataPath = Path.Combine(directory, id + ".txt");
            string metaPath = Path.Combine(directory, id + ".json");
            int suffix = 2;
            while (File.Exists(dataPath) || File.Exists(metaPath))
            {
                id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + hash[..12] + "-" + suffix++;
                dataPath = Path.Combine(directory, id + ".txt");
                metaPath = Path.Combine(directory, id + ".json");
            }

            var metadata = new ArtifactMetadata
            {
                Id = id,
                Kind = BoundLabel(kind, 80),
                Source = BoundLabel(source, 500),
                Characters = normalized.Length,
                Lines = CountLines(normalized),
                Sha256 = hash,
                WasTruncatedAtStorage = truncated,
                CreatedUtc = DateTime.UtcNow
            };

            AtomicWrite(dataPath, normalized);
            AtomicWrite(metaPath, JsonSerializer.Serialize(metadata, JsonOptions));
            TrimDirectory(directory);
            return metadata;
        }

        public static IReadOnlyList<ArtifactMetadata> List(string sessionId) =>
            ListFromDirectory(SessionDirectory(sessionId));

        internal static IReadOnlyList<ArtifactMetadata> ListFromDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return Array.Empty<ArtifactMetadata>();
            var result = new List<ArtifactMetadata>();
            foreach (string metaPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    ArtifactMetadata? metadata = JsonSerializer.Deserialize<ArtifactMetadata>(File.ReadAllText(metaPath), JsonOptions);
                    if (metadata != null && IsSafeId(metadata.Id)) result.Add(metadata);
                }
                catch { /* A corrupt artifact must not break the coding session. */ }
            }
            return result.OrderByDescending(item => item.CreatedUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        }

        public static ReadResult? Read(string sessionId, string artifactId, int offset = 0, int count = DefaultReadCharacters) =>
            ReadFromDirectory(SessionDirectory(sessionId), artifactId, offset, count);

        internal static ReadResult? ReadFromDirectory(
            string directory, string artifactId, int offset = 0, int count = DefaultReadCharacters)
        {
            if (!TryLoad(directory, artifactId, out ArtifactMetadata? metadata, out string text)) return null;
            int safeOffset = Math.Clamp(offset, 0, text.Length);
            int safeCount = Math.Clamp(count <= 0 ? DefaultReadCharacters : count, 1, MaxReadCharacters);
            int returned = Math.Min(safeCount, text.Length - safeOffset);
            string page = returned > 0 ? text.Substring(safeOffset, returned) : string.Empty;
            return new ReadResult(metadata!, safeOffset, returned, safeOffset + returned < text.Length, page);
        }

        public static SearchResult? Search(
            string sessionId, string artifactId, string query, bool regex = false, int maxMatches = 40) =>
            SearchFromDirectory(SessionDirectory(sessionId), artifactId, query, regex, maxMatches);

        internal static SearchResult? SearchFromDirectory(
            string directory, string artifactId, string query, bool regex = false, int maxMatches = 40)
        {
            if (string.IsNullOrWhiteSpace(query)
                || !TryLoad(directory, artifactId, out ArtifactMetadata? metadata, out string text)) return null;

            int limit = Math.Clamp(maxMatches, 1, MaxSearchMatches);
            Regex? compiled = null;
            if (regex)
            {
                try
                {
                    compiled = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(300));
                }
                catch (ArgumentException) { return new SearchResult(metadata!, query, true, Array.Empty<SearchMatch>()); }
            }

            var matches = new List<SearchMatch>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length && matches.Count < limit; index++)
            {
                string line = lines[index];
                bool hit;
                try
                {
                    hit = regex
                        ? compiled!.IsMatch(line)
                        : line.Contains(query, StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException) { break; }
                if (hit) matches.Add(new SearchMatch(index + 1, line.Length <= 2_000 ? line : line[..2_000] + "…"));
            }
            return new SearchResult(metadata!, query, regex, matches);
        }

        public static ReadResult? ReadFileRange(string path, int offset, int count)
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            var metadata = new ArtifactMetadata
            {
                Id = "workspace-file",
                Kind = "workspace-file",
                Source = Path.GetFileName(path),
                Characters = text.Length,
                Lines = CountLines(text),
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                CreatedUtc = File.GetLastWriteTimeUtc(path)
            };
            int safeOffset = Math.Clamp(offset, 0, text.Length);
            int safeCount = Math.Clamp(count <= 0 ? DefaultReadCharacters : count, 1, MaxReadCharacters);
            int returned = Math.Min(safeCount, text.Length - safeOffset);
            return new ReadResult(metadata, safeOffset, returned, safeOffset + returned < text.Length,
                returned == 0 ? string.Empty : text.Substring(safeOffset, returned));
        }

        internal static string SessionDirectory(string sessionId)
        {
            string safe = SanitizeSessionId(sessionId);
            return Path.Combine(AppPaths.CodingAgentStateDir, "artifacts", safe);
        }

        internal static string SanitizeSessionId(string? value)
        {
            string input = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            var sb = new StringBuilder(Math.Min(input.Length, 96));
            foreach (char ch in input)
            {
                if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') sb.Append(ch);
                else sb.Append('_');
                if (sb.Length >= 96) break;
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        private static bool TryLoad(
            string directory, string artifactId,
            out ArtifactMetadata? metadata, out string text)
        {
            metadata = null;
            text = string.Empty;
            if (!IsSafeId(artifactId)) return false;
            string metaPath = Path.Combine(directory, artifactId + ".json");
            string dataPath = Path.Combine(directory, artifactId + ".txt");
            if (!File.Exists(metaPath) || !File.Exists(dataPath)) return false;
            try
            {
                ArtifactMetadata? stored = JsonSerializer.Deserialize<ArtifactMetadata>(File.ReadAllText(metaPath), JsonOptions);
                if (stored == null || !string.Equals(stored.Id, artifactId, StringComparison.Ordinal)) return false;
                // Serve only the bytes that were stored. A torn, truncated or edited .txt must not reach
                // read_artifact or search_artifact as genuine tool output. A record without a digest
                // cannot be verified, so it is refused rather than trusted.
                byte[] bytes = File.ReadAllBytes(dataPath);
                string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                string decoded = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrEmpty(stored.Sha256)
                    || !string.Equals(digest, stored.Sha256, StringComparison.OrdinalIgnoreCase)
                    || decoded.Length != stored.Characters)
                    return false;
                metadata = stored;
                text = decoded;
                return true;
            }
            catch { return false; }
        }

        private static bool IsSafeId(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');

        private static int CountLines(string text)
        {
            if (text.Length == 0) return 0;
            int lines = 1;
            foreach (char ch in text) if (ch == '\n') lines++;
            return lines;
        }

        private static string BoundLabel(string? value, int max)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= max ? normalized : normalized[..max];
        }

        private static void AtomicWrite(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                // Flushed before publication, so a power loss cannot leave an empty or torn file under the final name.
                CodingDurableFile.WriteText(temp, content);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void TrimDirectory(string directory)
        {
            List<ArtifactMetadata> all = ListFromDirectory(directory).ToList();
            foreach (ArtifactMetadata old in all.Skip(MaxArtifactsPerSession))
            {
                try { File.Delete(Path.Combine(directory, old.Id + ".json")); } catch { }
                try { File.Delete(Path.Combine(directory, old.Id + ".txt")); } catch { }
            }
        }
    }
}
