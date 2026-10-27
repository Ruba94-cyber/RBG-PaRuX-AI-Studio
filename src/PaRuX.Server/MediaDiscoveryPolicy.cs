using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>Why a discovered repository or file is kept out. Pure and unit-tested; no network.</summary>
internal static class MediaDiscoveryPolicy
{
    /// <summary>Absolute ceiling for one checkpoint file. Hardware fit is judged separately, per PC, when shown.</summary>
    internal const long MaxFileBytes = 48L * 1024 * 1024 * 1024;
    /// <summary>Anything smaller is an adapter, VAE or text-encoder fragment, not a diffusion checkpoint.</summary>
    internal const long MinFileBytes = 200L * 1024 * 1024;
    internal const int MaxFilesPerRepo = 6;
    internal const int MaxCandidatesPerRepo = 4;

    private static readonly string[] BlockedLicenseMarkers =
        { "proprietary", "no-redist", "noredist", "research-only", "unknown", "none", "all-rights-reserved", "no-license" };

    /// <summary>Refusal reason for a repository, or null when it may be inspected.</summary>
    internal static string? RepoRefusal(MediaRepoSummary repo)
    {
        if (repo.Private) return "private";
        if (repo.Disabled) return "disabled";
        if (!string.Equals(repo.Gated, "false", StringComparison.OrdinalIgnoreCase)) return "gated";
        return LicenseRefusal(repo.License, repo.LicenseName);
    }

    /// <summary>"unknown licence" or "non-redistributable licence" as a reason; null when the licence is declared and not blocked.</summary>
    internal static string? LicenseRefusal(string? license, string? licenseName)
    {
        string id = (license ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0) return "unknown_license";
        if (id == "other")
        {
            // "other" is only usable when the card names the licence, and the name must not say it forbids redistribution.
            string name = (licenseName ?? string.Empty).Trim().ToLowerInvariant();
            if (name.Length == 0) return "unknown_license";
            return BlockedLicenseMarkers.Any(name.Contains) ? "non_redistributable_license" : null;
        }
        return BlockedLicenseMarkers.Any(id.Contains) ? "non_redistributable_license" : null;
    }

    internal static string DisplayLicense(string? license, string? licenseName) =>
        string.Equals(license, "other", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(licenseName)
            ? "other (" + licenseName!.Trim() + ")"
            : (license ?? string.Empty).Trim();

    private static readonly Regex GenericName = new(
        @"^(model|diffusion_pytorch_model|pytorch_model|unet|transformer|checkpoint|ckpt|weights|consolidated|ema|model_ema)([._\-]?(fp16|fp32|bf16|fp8|ema|nonema|non_ema|safe|v\d+|\d+))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A file called model.safetensors would collide with every other such file in the managed checkpoint folder and make
    /// "installed" ambiguous, so discovery does not offer it.</summary>
    internal static bool IsGenericFileName(string fileName) =>
        GenericName.IsMatch(Path.GetFileNameWithoutExtension(fileName));

    private static readonly Regex ShardName = new(@"-\d{3,6}-of-\d{3,6}\.safetensors$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Safetensors files worth a header read: big enough to be a checkpoint, small enough to be usable, not obviously an adapter.</summary>
    internal static IReadOnlyList<MediaRepoFile> SelectCheckpointFiles(IEnumerable<MediaRepoFile> files, out string? skipReason)
    {
        skipReason = null;
        var all = files.Where(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0) { skipReason = "no_safetensors_file"; return Array.Empty<MediaRepoFile>(); }
        // "model-00001-of-00005.safetensors" is one slice of a multi-file checkpoint: alone it is not a runnable model, and PaRuX loads
        // single-file checkpoints only (a live check found Qwen-Image repositories shipped only as shards).
        all.RemoveAll(f => ShardName.IsMatch(f.Path));
        if (all.Count == 0) { skipReason = "sharded_checkpoint"; return Array.Empty<MediaRepoFile>(); }
        var sized = all.Where(f => f.SizeBytes >= MinFileBytes && f.SizeBytes <= MaxFileBytes).ToList();
        if (sized.Count == 0) { skipReason = all.Any(f => f.SizeBytes > MaxFileBytes) ? "file_too_large" : "no_checkpoint_sized_file"; return sized; }
        var usable = sized.Where(f => !IsGenericFileName(f.Path[(f.Path.LastIndexOf('/') + 1)..])
            && f.Path.All(c => c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '+' or '(' or ')'))).ToList();
        if (usable.Count == 0) { skipReason = "generic_or_unsafe_file_name"; return usable; }
        return usable.OrderByDescending(f => f.SizeBytes).Take(MaxFilesPerRepo).ToList();
    }

    /// <summary>Capabilities a Hugging Face pipeline tag implies, in priority order.</summary>
    internal static IReadOnlyList<string> CapabilitiesForPipelineTag(string? tag) => tag switch
    {
        "text-to-image" => new[] { "image.generate" },
        "image-to-image" => new[] { "image.edit" },
        "text-to-video" or "image-to-video" => new[] { "video.generate" },
        "text-to-audio" => new[] { "audio.music" },
        _ => Array.Empty<string>()
    };

    /// <summary>The capability a registry family runs for a repository's pipeline tag, or null when the family does not run that kind
    /// of job (for example an edit-only family in a text-to-image repository).</summary>
    internal static string? ResolveCapability(RegistryFamily family, string? pipelineTag)
    {
        foreach (string capability in CapabilitiesForPipelineTag(pipelineTag))
        {
            string? pipeline = MediaCatalogue.PipelineOf(capability);
            if (pipeline is not null && family.Pipelines.Contains(pipeline, StringComparer.Ordinal)) return capability;
        }
        return null;
    }

    /// <summary>Strengths from the closed vocabulary, inferred only from the repository's own tags and name.</summary>
    internal static List<string> InferStrengths(IEnumerable<string> tags, string repoId)
    {
        string text = " " + string.Join(' ', tags).ToLowerInvariant() + " " + repoId.ToLowerInvariant().Replace('/', ' ').Replace('-', ' ').Replace('_', ' ') + " ";
        var result = new List<string>();
        if (Regex.IsMatch(text, @"\b(anime|manga|waifu)\b")) result.Add("anime");
        if (Regex.IsMatch(text, @"\b(illustration|illustrious|comic|painting)\b")) result.Add("illustration");
        if (Regex.IsMatch(text, @"\b(photoreal|photorealistic|realistic|realism|photography)\b")) result.Add("photoreal");
        if (Regex.IsMatch(text, @"\b(turbo|lightning|schnell|distilled|lcm|fast)\b")) result.Add("fast");
        if (Regex.IsMatch(text, @"\b(cinematic)\b")) result.Add("cinematic");
        if (Regex.IsMatch(text, @"\b(typography|text rendering)\b")) result.Add("text-rendering");
        return result;
    }
}

/// <summary>Lane classification from the repository's own words. Standard is the default; Uncensored needs explicit, affirmative
/// evidence and that evidence is stored. A negated mention ("does not generate NSFW", "safety filter enabled") is not evidence.</summary>
internal static class MediaLaneClassifier
{
    private static readonly string[] TagEvidence = { "uncensored", "nsfw", "abliterated", "unfiltered", "not-for-all-audiences" };

    private static readonly Regex Evidence = new(
        @"\b(uncensored|abliterated|unfiltered|no (?:safety|content|nsfw) filters?|without (?:any )?(?:safety|content|nsfw) filters?|no safety checker|safety checker (?:removed|disabled)|nsfw)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Negation = new(
        @"(\bno\b|\bnot\b|n't\b|\bnever\b|\bavoid|\bprohibit|\bforbid|\bfilter(?:ed|s|ing)?\b|\bblock|\bsafe\b|\bsfw\b|\bsafety\b|\bwithout\b|\bexclude|\bremov|\bban|\bmoderat|\brefus|\bnon[- ])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static (string Lane, string? Evidence) Classify(IEnumerable<string> tags, string? modelCard, string repoId)
    {
        foreach (string tag in tags)
        {
            string t = tag.Trim().ToLowerInvariant();
            if (TagEvidence.Contains(t)) return ("uncensored", "repository tag \"" + t + "\"");
        }
        string name = repoId[(repoId.IndexOf('/') + 1)..];
        Match nameMatch = Evidence.Match(name.Replace('-', ' ').Replace('_', ' '));
        if (nameMatch.Success)
            return ("uncensored", "repository name contains \"" + nameMatch.Value.ToLowerInvariant() + "\"");

        if (!string.IsNullOrWhiteSpace(modelCard))
        {
            string text = modelCard!.Length > 64 * 1024 ? modelCard[..(64 * 1024)] : modelCard;
            foreach (Match m in Evidence.Matches(text))
            {
                if (NegatedIn(text, m)) continue;
                return ("uncensored", "model card says \"" + Snippet(text, m) + "\"");
            }
        }
        return ("standard", null);
    }

    private static bool NegatedIn(string text, Match m)
    {
        int start = Math.Max(0, m.Index - 48);
        string before = text.Substring(start, m.Index - start);
        // Only the clause the phrase sits in counts, not an earlier sentence.
        int cut = Math.Max(before.LastIndexOfAny(new[] { '.', '\n', ';', '!', '?' }), -1);
        before = before[(cut + 1)..];
        int endLen = Math.Min(24, text.Length - (m.Index + m.Length));
        string after = text.Substring(m.Index + m.Length, endLen);
        int cutAfter = after.IndexOfAny(new[] { '.', '\n', ';', '!', '?' });
        if (cutAfter >= 0) after = after[..cutAfter];
        return Negation.IsMatch(before) || Regex.IsMatch(after, @"\b(is|are) (?:not |un)?(?:allowed|supported|filtered|prohibited|blocked|removed|excluded)|\bfilter|\bnot\b|\bprohibit", RegexOptions.IgnoreCase);
    }

    private static string Snippet(string text, Match m)
    {
        int from = Math.Max(0, m.Index - 30), to = Math.Min(text.Length, m.Index + m.Length + 30);
        return Regex.Replace(text[from..to], @"\s+", " ").Trim();
    }
}
