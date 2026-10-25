using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Groups llama.cpp split parts ("model-00001-of-00003.gguf") into sets, and plans one quantisation
/// of a set from a repository listing. The managed library and the Hugging Face planner both use
/// this class, so a set is complete or incomplete in exactly one way on every screen.
///
/// Pure logic over the names and sizes it is given. It touches no disk and no network.
/// </summary>
public static class GgufSplitSetGrouper
{
    /// <summary>One file known to the grouper.</summary>
    /// <param name="Path">Repository-relative path, or a bare file name for the flat managed library.</param>
    public readonly record struct Entry(string Path, long SizeBytes, string? Sha256);

    /// <summary>One present part, with the path it came from.</summary>
    public sealed class Member
    {
        public required string Path { get; init; }
        public required int Index { get; init; }
        public required long SizeBytes { get; init; }
        public string? Sha256 { get; init; }
    }

    /// <summary>One set: a directory, a stem and a part count. Parts are grouped by stem, so a
    /// quantisation that sits in the stem or in the folder still keeps its parts together.</summary>
    public sealed class SetGroup
    {
        public required string Directory { get; init; }
        public required string SetName { get; init; }
        public required string Quantization { get; init; }
        public required int PartCount { get; init; }
        /// <summary>Present parts, one per index, in index order.</summary>
        public required IReadOnlyList<Member> Parts { get; init; }
        public required IReadOnlyList<int> MissingPartIndexes { get; init; }
        /// <summary>Extra files that claim an index already present. The set cannot be trusted.</summary>
        public required IReadOnlyList<string> ConflictingPaths { get; init; }
        public bool Complete => MissingPartIndexes.Count == 0 && ConflictingPaths.Count == 0;
        public long TotalBytes => Parts.Sum(part => part.SizeBytes);
    }

    private sealed record Bucket(string Directory, string SetName, int PartCount)
    {
        public List<(int Index, Entry Entry)> Members { get; } = new();
    }

    /// <summary>
    /// Returns every split set in <paramref name="entries"/>, sorted by directory and set name.
    /// Projector files and single-file models are not split parts and are skipped.
    /// </summary>
    public static IReadOnlyList<SetGroup> Group(IEnumerable<Entry> entries)
    {
        var buckets = new Dictionary<(string Directory, string SetName, int PartCount), Bucket>();
        foreach (Entry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || GgufFileNaming.IsProjector(entry.Path)) continue;
            if (!GgufFileNaming.TryParseSplitPart(entry.Path, out GgufFileNaming.SplitPart part)) continue;
            string directory = DirectoryOf(entry.Path);
            var key = (directory.ToLowerInvariant(), part.SetKey.ToLowerInvariant(), part.Total);
            if (!buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket = new Bucket(directory, part.SetKey, part.Total);
                buckets[key] = bucket;
            }
            bucket.Members.Add((part.Index, entry));
        }

        var groups = new List<SetGroup>(buckets.Count);
        foreach (Bucket bucket in buckets.Values)
        {
            // Deterministic: the first path per index (in path order) is the part, later ones conflict.
            var present = new Dictionary<int, Member>();
            var conflicts = new List<string>();
            foreach (var (index, entry) in bucket.Members.OrderBy(m => m.Entry.Path, StringComparer.Ordinal))
            {
                if (present.ContainsKey(index))
                {
                    conflicts.Add(entry.Path);
                    continue;
                }
                present[index] = new Member
                {
                    Path = entry.Path, Index = index, SizeBytes = entry.SizeBytes, Sha256 = entry.Sha256
                };
            }

            var missing = Enumerable.Range(1, bucket.PartCount).Where(index => !present.ContainsKey(index)).ToList();
            string firstPath = present.Values.OrderBy(m => m.Index).First().Path;
            groups.Add(new SetGroup
            {
                Directory = bucket.Directory,
                SetName = bucket.SetName,
                Quantization = GgufFileNaming.ParseQuantizationFromRepoPath(firstPath) ?? GgufFileNaming.UnknownQuantization,
                PartCount = bucket.PartCount,
                Parts = present.Values.OrderBy(m => m.Index).ToList(),
                MissingPartIndexes = missing,
                ConflictingPaths = conflicts
            });
        }

        return groups
            .OrderBy(g => g.Directory, StringComparer.Ordinal)
            .ThenBy(g => g.SetName, StringComparer.Ordinal)
            .ThenBy(g => g.PartCount)
            .ToList();
    }

    private static string DirectoryOf(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}
