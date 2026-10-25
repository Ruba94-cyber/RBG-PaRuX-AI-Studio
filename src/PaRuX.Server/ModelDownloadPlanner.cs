using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Plans one quantisation of a model from a repository listing. The model is either a single .gguf or
/// a split set, and the vision projector its family needs travels with it as a second file. Pure
/// logic: names and sizes in, plan out. Nothing is downloaded and nothing is written.
/// </summary>
public static class ModelDownloadPlanner
{
    /// <summary>
    /// Chooses the model for <paramref name="quantization"/> (or the only model when none is named) and
    /// returns its plan. The plan may be incomplete; the caller must refuse to start it. Projectors are
    /// never chosen as the model. Throws <see cref="InvalidDataException"/> when no model matches or the
    /// choice is ambiguous.
    /// </summary>
    public static ModelDownloadPlan Plan(
        string repoId,
        string? revision,
        IEnumerable<GgufSplitSetGrouper.Entry> entries,
        string? quantization,
        VisionProjectorManifest? manifest = null,
        string? setName = null,
        string? directory = null,
        int? partCount = null)
    {
        manifest ??= VisionProjectorManifest.Default;
        List<GgufSplitSetGrouper.Entry> all = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .ToList();
        Dictionary<string, GgufSplitSetGrouper.Entry> byPath = all
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        List<GgufSplitSetGrouper.SetGroup> candidates = Candidates(all);

        string? wanted = string.IsNullOrWhiteSpace(quantization) ? null : quantization.Trim();
        string? wantedSet = string.IsNullOrWhiteSpace(setName) ? null : setName.Trim();
        string? wantedDirectory = directory == null ? null : NormalizeDirectory(directory);
        List<GgufSplitSetGrouper.SetGroup> matches = candidates
            .Where(group => (wanted == null || string.Equals(group.Quantization, wanted, StringComparison.OrdinalIgnoreCase))
                && (wantedSet == null || string.Equals(group.SetName, wantedSet, StringComparison.OrdinalIgnoreCase))
                && (wantedDirectory == null || string.Equals(NormalizeDirectory(group.Directory), wantedDirectory, StringComparison.OrdinalIgnoreCase))
                && (!partCount.HasValue || group.PartCount == partCount.Value))
            .OrderBy(group => group.Directory, StringComparer.Ordinal)
            .ThenBy(group => group.SetName, StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
            throw new InvalidDataException(wanted == null
                ? "No GGUF model was found in " + repoId + "."
                : "No GGUF model for " + wanted + " was found in " + repoId + ".");
        if (matches.Count > 1)
            throw new InvalidDataException("More than one model matches in " + repoId + ". Choose a quantisation to pick one.");

        GgufSplitSetGrouper.SetGroup chosen = matches[0];
        // A projector pairs only with a model of the same family, the same rule the managed library applies.
        VisionFamily? family = manifest.FamilyFor(chosen.Parts.FirstOrDefault()?.Path) ?? manifest.FamilyFor(repoId);
        List<string> repositoryProjectorPaths = all
            .Where(entry => GgufFileNaming.IsProjector(entry.Path))
            .Select(entry => entry.Path)
            .ToList();
        List<string> projectorPaths = repositoryProjectorPaths
            .Where(path => family != null && manifest.FamilyFor(path)?.Id == family.Id)
            .ToList();
        string? projectorPath = manifest.ChooseProjector(projectorPaths);
        bool needsProjectorNote = projectorPath == null && family is { VisionCapable: true };
        string? textOnlyNote = needsProjectorNote
            ? repositoryProjectorPaths.Count == 0
                ? "This " + family!.Id + " model needs a vision projector, and this repository lists none. It will run text-only."
                : "This " + family!.Id + " model needs a vision projector, but this repository lists projectors with no match for its family. It will run text-only."
            : null;

        return new ModelDownloadPlan
        {
            RepoId = repoId,
            Revision = revision,
            SetName = chosen.SetName,
            Directory = chosen.Directory,
            Quantization = chosen.Quantization,
            PartCount = chosen.PartCount,
            Parts = chosen.Parts.Select(member => PlanFile(repoId, revision, member.Path, member.Index, member.SizeBytes, member.Sha256)).ToList(),
            MissingPartIndexes = chosen.MissingPartIndexes.ToList(),
            ConflictingPaths = chosen.ConflictingPaths.ToList(),
            Projector = projectorPath == null
                ? null
                : PlanFile(repoId, revision, projectorPath, 0, byPath[projectorPath].SizeBytes, byPath[projectorPath].Sha256),
            TextOnlyNote = textOnlyNote
        };
    }

    /// <summary>
    /// Every model in a listing as one candidate: each split set (complete or not) counts once, and each
    /// single .gguf counts once. Projectors are not models and are left out.
    /// </summary>
    public static List<GgufSplitSetGrouper.SetGroup> Candidates(IEnumerable<GgufSplitSetGrouper.Entry> entries)
    {
        List<GgufSplitSetGrouper.Entry> models = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path)
                && entry.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                && !GgufFileNaming.IsProjector(entry.Path))
            .ToList();
        var candidates = new List<GgufSplitSetGrouper.SetGroup>(GgufSplitSetGrouper.Group(models));
        foreach (GgufSplitSetGrouper.Entry single in models.Where(e => !GgufFileNaming.TryParseSplitPart(e.Path, out _)))
            candidates.Add(SingleFileGroup(single));
        return candidates;
    }

    /// <summary>The quantisation choices a search row shows for one repository, in a stable order.</summary>
    public static List<ModelQuantChoice> BuildQuantChoices(IEnumerable<GgufSplitSetGrouper.Entry> entries) =>
        Candidates(entries)
            .Select(group => new ModelQuantChoice
            {
                Quantization = group.Quantization,
                SetName = group.SetName,
                Directory = group.Directory,
                IsSplit = group.PartCount > 1,
                PartCount = group.PartCount,
                Complete = group.Complete,
                SizeBytes = group.TotalBytes,
                FilePaths = group.Parts.Select(part => part.Path).ToList()
            })
            .OrderBy(choice => choice.Quantization, StringComparer.Ordinal)
            .ThenBy(choice => choice.SetName, StringComparer.Ordinal)
            .ThenBy(choice => choice.Directory, StringComparer.Ordinal)
            .ToList();

    private static string NormalizeDirectory(string directory) =>
        directory.Replace('\\', '/').Trim('/');

    private static GgufSplitSetGrouper.SetGroup SingleFileGroup(GgufSplitSetGrouper.Entry entry)
    {
        string normalized = entry.Path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        string fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
        return new GgufSplitSetGrouper.SetGroup
        {
            Directory = slash < 0 ? string.Empty : normalized[..slash],
            SetName = Path.GetFileNameWithoutExtension(fileName),
            Quantization = GgufFileNaming.ParseQuantizationFromRepoPath(entry.Path) ?? GgufFileNaming.UnknownQuantization,
            PartCount = 1,
            Parts = new[]
            {
                new GgufSplitSetGrouper.Member
                {
                    Path = entry.Path, Index = 1, SizeBytes = entry.SizeBytes, Sha256 = entry.Sha256
                }
            },
            MissingPartIndexes = Array.Empty<int>(),
            ConflictingPaths = Array.Empty<string>()
        };
    }

    private static ModelDownloadPlanFile PlanFile(string repoId, string? revision, string repoFilePath, int index, long sizeBytes, string? sha256) => new()
    {
        Index = index,
        FileName = Path.GetFileName(repoFilePath.Replace('\\', '/')),
        RepoFilePath = repoFilePath,
        SizeBytes = sizeBytes,
        Sha256 = sha256,
        DownloadUrl = revision == null ? string.Empty : ModelDiscoveryIdentity.PinnedUrl(repoId, revision, repoFilePath)
    };
}
