using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>A plain file that ComfyUI would find by name in one of its models folders.</summary>
internal sealed record InstalledComfyFile(string Name, long SizeBytes);

/// <summary>
/// Computes the exact companion set a checkpoint needs, compared with what is really installed in ComfyUI.
/// Matching is exact file name first (case-insensitive, as Windows is), then an explicit registry pattern
/// with its exclusions. There is no substring fallback. A file that another family shares is found by the
/// same exact-name rule, so it is never listed as missing twice. Unrelated families contribute nothing.
/// </summary>
internal static class MediaCompanionResolver
{
    /// <summary>Lists plain files directly inside each ComfyUI models folder. Subfolders, reparse points and
    /// empty files are ignored, since ComfyUI would not load them by name.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<InstalledComfyFile>> ScanFolders(
        string modelsRoot, IEnumerable<string> folders)
    {
        var result = new Dictionary<string, IReadOnlyList<InstalledComfyFile>>(StringComparer.Ordinal);
        foreach (string folder in folders.Distinct(StringComparer.Ordinal))
        {
            var files = new List<InstalledComfyFile>();
            string directory = Path.Combine(modelsRoot, folder);
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (string path in Directory.EnumerateFiles(directory))
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 0 && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                            files.Add(new InstalledComfyFile(info.Name, info.Length));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            result[folder] = files;
        }
        return result;
    }

    /// <summary>Builds the consent plan for one checkpoint. Nothing is downloaded or written here.</summary>
    public static MediaCompanionPlan BuildPlan(
        string type,
        string mainFileName,
        MediaFamilyDetection detection,
        MediaCompanionRegistry registry,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledComfyFile>> installed)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installed);

        var plan = new MediaCompanionPlan
        {
            Type = type,
            MainFileName = mainFileName,
            FamilyId = detection.FamilyId,
            FamilyName = detection.FamilyName,
            Support = detection.FamilyId == null ? "unidentified" : detection.Support,
            Reason = detection.Reason,
            EmbedsTextEncoder = detection.EmbedsTextEncoder,
            EmbedsVae = detection.EmbedsVae,
            SupportedFamilies = registry.SupportedFor(type).Select(family => family.DisplayName).ToList()
        };

        RegistryFamily? family = detection.FamilyId == null ? null : registry.FindFamily(detection.FamilyId);
        if (family == null)
        {
            plan.PlanHash = ComputePlanHash(plan);
            return plan;
        }

        if (family.IsSupported && !family.Pipelines.Contains(type, StringComparer.Ordinal))
        {
            plan.Reason = family.DisplayName + " cannot run the " + type + " pipeline. PaRuX runs it for: "
                + string.Join(", ", family.Pipelines) + ".";
            plan.PlanHash = ComputePlanHash(plan);
            return plan;
        }

        foreach (RegistryCompanion companion in family.Companions)
        {
            MediaCompanionItem item = ToItem(companion, family, installed);
            plan.Companions.Add(item);
            if (item.State != "installed") plan.Missing.Add(item);
        }

        plan.MissingApproxBytes = plan.Missing.Sum(item => item.ApproxSizeBytes ?? 0);
        bool allFetchable = plan.Missing.All(item => item.Fetchable);
        // A same-name file of another size is never overwritten, so nothing is installable until it is moved aside.
        List<MediaCompanionItem> blockedBySize = plan.Missing.Where(item => item.State == "size_mismatch").ToList();
        plan.Installable = family.IsSupported && allFetchable && blockedBySize.Count == 0;
        if (!family.IsSupported)
        {
            plan.Reason = family.Reason;
        }
        else if (!allFetchable)
        {
            plan.Reason = "Install these manually, because PaRuX has no pinned source for them: "
                + string.Join("; ", plan.Missing.Where(item => !item.Fetchable).Select(item => item.Folder + "/" + item.FileName
                    + (item.Repository == null ? string.Empty : " from " + item.Repository)));
        }
        else if (blockedBySize.Count > 0)
        {
            plan.Reason = "Move these aside first, because a file with the same name but another size is already installed: "
                + string.Join("; ", blockedBySize.Select(item => item.Folder + "/" + item.FileName)) + ".";
        }
        plan.PlanHash = ComputePlanHash(plan);
        return plan;
    }

    private static MediaCompanionItem ToItem(
        RegistryCompanion companion,
        RegistryFamily family,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledComfyFile>> installed)
    {
        var item = new MediaCompanionItem
        {
            Role = companion.Role,
            Folder = companion.Folder,
            FileName = companion.FileName,
            ClipType = companion.ClipType,
            License = companion.License,
            SharedWith = companion.SharedWith.ToList(),
            ApproxSizeBytes = companion.Source.SizeBytes,
            Repository = companion.Source.Repository,
            RepositoryPath = companion.Source.Path,
            Revision = companion.Source.Revision,
            Sha256 = companion.Source.Sha256,
            PinnedSizeBytes = companion.Source.SizeBytes,
            Fetchable = companion.Source.IsPinned
        };

        IReadOnlyList<InstalledComfyFile> files = installed.TryGetValue(companion.Folder, out IReadOnlyList<InstalledComfyFile>? listed)
            ? listed
            : Array.Empty<InstalledComfyFile>();

        InstalledComfyFile? exact = files.FirstOrDefault(file => string.Equals(file.Name, companion.FileName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            item.MatchedBy = "exact";
            item.InstalledSizeBytes = exact.SizeBytes;
            item.State = companion.Source.SizeBytes is long pinned && pinned != exact.SizeBytes ? "size_mismatch" : "installed";
        }
        else if (companion.Match != null)
        {
            List<InstalledComfyFile> matches = files
                .Where(file => GlobMatch(companion.Match.Pattern, file.Name)
                    && !companion.Match.Exclude.Any(exclusion => GlobMatch(exclusion, file.Name)))
                .ToList();
            if (matches.Count == 1)
            {
                item.MatchedBy = "pattern";
                item.InstalledSizeBytes = matches[0].SizeBytes;
                item.FileName = matches[0].Name;
                item.State = companion.Source.SizeBytes is long pinnedSize && pinnedSize != matches[0].SizeBytes ? "size_mismatch" : "installed";
            }
        }

        item.Why = item.State == "installed"
            ? "Installed as " + (item.FileName) + " (" + item.MatchedBy + " match)."
            : item.State == "size_mismatch"
                ? "A file with this name is installed, but its size differs from the pinned size."
                : "Needed by " + family.DisplayName + " as its " + companion.Role.Replace('_', ' ')
                  + (companion.ClipType == null ? string.Empty : " (CLIP type " + companion.ClipType + ")") + ".";
        return item;
    }

    /// <summary>Whole-name wildcard match: '*' spans any run, '?' one character. Everything else is literal.
    /// Used only for registry-declared patterns, never as a general substring test.</summary>
    internal static bool GlobMatch(string pattern, string name)
    {
        var builder = new StringBuilder("^");
        foreach (char character in pattern)
        {
            builder.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString())
            });
        }
        builder.Append('$');
        return Regex.IsMatch(name, builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Digest over the parts of the plan a user consents to. A fetch must present this value, so a plan
    /// that changed since it was shown is refused instead of silently executed.</summary>
    private static string ComputePlanHash(MediaCompanionPlan plan)
    {
        var text = new StringBuilder();
        text.Append("v1|").Append(plan.Type).Append('|').Append(plan.MainFileName).Append('|')
            .Append(plan.FamilyId ?? "-").Append('|').Append(plan.Support).Append('|').Append(plan.Installable ? "1" : "0");
        foreach (MediaCompanionItem missing in plan.Missing)
        {
            text.Append('|').Append(missing.Folder).Append('/').Append(missing.FileName)
                .Append('@').Append(missing.Repository ?? "-").Append(':').Append(missing.RepositoryPath ?? "-")
                .Append('@').Append(missing.Revision ?? "-").Append('#').Append(missing.Sha256 ?? "-")
                .Append('=').Append(missing.PinnedSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append(missing.Fetchable ? "+fetch" : "-manual");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
