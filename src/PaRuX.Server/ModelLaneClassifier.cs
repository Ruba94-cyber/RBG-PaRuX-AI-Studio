using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The lane a candidate is shown in and the declared evidence for it.</summary>
internal sealed record ModelLaneAssignment(string Lane, string? Evidence);

/// <summary>Standard versus Uncensored lane. A model is uncensored only when its repository id, file name or
/// publisher tags declare it; the matched marker is returned as evidence. Nothing is guessed from popularity,
/// size or family, and "roleplay"/"nsfw" alone do not make a model uncensored.</summary>
internal static class ModelLaneClassifier
{
    internal const string Standard = ModelContentProfileIds.Standard;
    internal const string Uncensored = ModelContentProfileIds.Uncensored;

    // Order is the evidence priority; the first declared marker wins.
    private static readonly string[] Markers =
    {
        "uncensored", "abliterated", "abliterate", "obliterated", "no-refusal", "no_refusal", "norefusal", "unfiltered", "huihui", "dolphin"
    };

    internal static ModelLaneAssignment Assign(string? repoId, string? fileName, IEnumerable<string>? tags)
    {
        foreach (string tag in tags ?? Array.Empty<string>())
        {
            string? m = Find(tag);
            if (m is not null) return new ModelLaneAssignment(Uncensored, "tag: " + m);
        }
        string? inRepo = Find(repoId);
        if (inRepo is not null) return new ModelLaneAssignment(Uncensored, "repository name: " + inRepo);
        string? inFile = Find(fileName);
        if (inFile is not null) return new ModelLaneAssignment(Uncensored, "file name: " + inFile);
        if (ModelCategoryClassifier.Classify(repoId ?? string.Empty, new[] { fileName ?? string.Empty }, tags) == ModelCategoryClassifier.Abliterated)
            return new ModelLaneAssignment(Uncensored, "category: abliterated");
        return new ModelLaneAssignment(Standard, null);
    }

    private static string? Find(string? text)
    {
        string t = (text ?? string.Empty).ToLowerInvariant();
        return Markers.FirstOrDefault(k => t.Contains(k, StringComparison.Ordinal));
    }
}
