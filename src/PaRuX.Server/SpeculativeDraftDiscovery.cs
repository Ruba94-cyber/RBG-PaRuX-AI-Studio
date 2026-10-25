using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Finds explicitly compatible local speculative sidecars without ever downloading or guessing a
/// remote model. Discovery is deliberately conservative: a draft must live in a PaRuX-managed
/// model directory and its normalized filename must contain the normalized target family stem.
/// This prevents an unrelated EAGLE/DFlash checkpoint from being attached to a target merely
/// because both happen to share a tokenizer family.
/// </summary>
public static partial class SpeculativeDraftDiscovery
{
    public sealed record Candidate(string Path, string Type, int DraftMax, string Reason);

    public static IReadOnlyList<Candidate> Discover(string targetModelPath)
    {
        if (string.IsNullOrWhiteSpace(targetModelPath) || !File.Exists(targetModelPath)) return Array.Empty<Candidate>();
        string targetStem = NormalizeTargetStem(Path.GetFileNameWithoutExtension(targetModelPath));
        if (targetStem.Length < 4) return Array.Empty<Candidate>();

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetModelPath));
        if (!string.IsNullOrWhiteSpace(targetDirectory)) roots.Add(targetDirectory);
        roots.Add(Path.GetFullPath(LocalAiPaths.ModelsPath));
        roots.Add(Path.GetFullPath(ModelDownloadService.ModelsDirectory));

        var found = new List<Candidate>();
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (string file in files)
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(targetModelPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = Path.GetFileNameWithoutExtension(file);
                string lower = name.ToLowerInvariant();
                string type = DetectType(lower);
                if (type == "none") continue;
                string candidateStem = NormalizeTargetStem(RemoveSpeculatorMarkers(name));
                bool explicitSidecar = candidateStem.Contains(targetStem, StringComparison.OrdinalIgnoreCase)
                    || targetStem.Contains(candidateStem, StringComparison.OrdinalIgnoreCase);
                if (!explicitSidecar) continue;

                int draftMax = type switch
                {
                    "draft-dspark" => 7,
                    "draft-dflash" => 15,
                    "draft-eagle3" => 8,
                    "draft-mtp" => 8,
                    _ => 6
                };
                found.Add(new Candidate(Path.GetFullPath(file), type, draftMax,
                    "Local managed sidecar matched target family '" + targetStem + "'."));
            }
        }

        return found
            .OrderBy(candidate => Rank(candidate.Type))
            .ThenBy(candidate => new FileInfo(candidate.Path).Length)
            .Take(3)
            .ToList();
    }

    internal static string NormalizeTargetStem(string value)
    {
        value = value.ToLowerInvariant();
        value = QuantSuffixRegex().Replace(value, "");
        value = NoiseRegex().Replace(value, "-");
        return value.Trim('-','_','.');
    }

    private static string RemoveSpeculatorMarkers(string value) => SpeculatorMarkerRegex().Replace(value, "");

    private static string DetectType(string lower)
    {
        if (lower.Contains("eagle3")) return "draft-eagle3";
        if (lower.Contains("dflash")) return "draft-dflash";
        if (lower.Contains("dspark")) return "draft-dspark";
        if (lower.Contains("mtp")) return "draft-mtp";
        if (lower.Contains("draft") || lower.Contains("speculator")) return "draft-simple";
        return "none";
    }

    private static int Rank(string type) => type switch
    {
        "draft-eagle3" => 0,
        "draft-dspark" => 1,
        "draft-dflash" => 2,
        "draft-mtp" => 3,
        _ => 4
    };

    [GeneratedRegex(@"(?i)(?:[-_.](?:q\d(?:_\d)?|iq\d[^-_.]*|f16|bf16|fp16|fp32|k_[msl]|gguf))*$")]
    private static partial Regex QuantSuffixRegex();

    [GeneratedRegex(@"(?i)(?:eagle3|dflash|dspark|mtp|speculator|draft(?:-simple)?)")]
    private static partial Regex SpeculatorMarkerRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NoiseRegex();
}
