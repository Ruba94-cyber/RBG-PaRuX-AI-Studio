using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// The one GGUF file-name grammar PaRuX uses: quantisation labels, llama.cpp split-part tokens
/// ("-00001-of-00003") and vision projector files ("mmproj"). The Hugging Face catalogue, the
/// managed library and <see cref="GgufMetadataReader"/> all call this instead of keeping their own
/// parsers, so one file name can never get two different answers on two screens.
///
/// Pure string logic with no I/O. A token only matches when it is delimited by the start or end of
/// the name or by one of the separators "-", "_", ".", space or brackets. That is what makes BF16
/// never yield F16, and Q4_K_MAX never yield Q4_K_M.
/// </summary>
public static class GgufFileNaming
{
    /// <summary>Label used everywhere a quantisation could not be recognised. Never a guess.</summary>
    public const string UnknownQuantization = "unknown";

    private const string Separators = @"-_. \[\]()+";

    // Recognised forms, most specific first: IQ forms, K-quants with a size suffix (Q4_K_M,
    // Q3_K_XL), bare K-quants (Q6_K) that may not be followed by "_<letters>" (so Q4_K_MAX is not
    // Q4_K), legacy 0/1 quants (Q4_0, Q8_0), then the floating-point types. FP16 and FP32 are common
    // spellings of F16 and F32 and are canonicalised in MatchQuantization.
    private static readonly Regex QuantizationToken = new(
        @"(?<=^|[" + Separators + @"])(IQ[1-4]_(?:XXS|XS|NL|S|M)|Q[2-8]_K_(?:XXS|XS|XL|S|M|L)|Q[2-8]_K(?!_[A-Za-z])|Q[2-8]_\d|BF16|FP16|F16|FP32|F32)(?=$|[" + Separators + "])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // llama.cpp writes split parts as "<stem>-%05d-of-%05d.gguf". Exactly five digits, so a
    // number such as "Llama-2-of-3" in a model name is never read as a part. The separator before
    // the index is consumed, so removing the token also removes the join it leaves behind.
    private static readonly Regex SplitPartToken = new(
        @"(?:^|[" + Separators + @"])(?<index>\d{5})-of-(?<total>\d{5})(?=$|[" + Separators + "])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProjectorToken = new(
        @"(?<=^|[" + Separators + @"])mmproj(?=$|[" + Separators + "])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AuxiliaryHeadToken = new(
        @"(?<=^|[" + Separators + @"])(?:mtp|draft)(?=$|[" + Separators + "])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True for a speculative-decoding head published beside a model ("MTP/mtp-*.gguf", "*-draft-*.gguf").
    /// It carries the model's name and often a real quantisation label but cannot chat on its own, so it
    /// is never a model to download or route to.
    /// </summary>
    public static bool IsAuxiliaryHead(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
        string[] segments = fileNameOrPath.Replace((char)92, '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
            if (segments[i].Equals("mtp", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("draft", StringComparison.OrdinalIgnoreCase))
                return true;
        return AuxiliaryHeadToken.IsMatch(StripGgufExtension(BaseName(fileNameOrPath)));
    }

    /// <summary>One split-part token read from a file name.</summary>
    /// <param name="SetKey">The name with the part token removed, extension kept off. Every part of
    /// one set has the same key. Compare it case-insensitively.</param>
    /// <param name="Index">1-based position of this part.</param>
    /// <param name="Total">Number of parts in the complete set.</param>
    public readonly record struct SplitPart(string SetKey, int Index, int Total);

    /// <summary>Canonical quantisation label for a file name or path, or null when no recognised form
    /// matches. The first recognised token wins, and the part token is removed first, so
    /// "model-Q4_K_M-00001-of-00002.gguf" reads as Q4_K_M.</summary>
    public static string? ParseQuantization(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return null;
        string name = RemoveSplitPartToken(StripGgufExtension(BaseName(fileNameOrPath)));
        return MatchQuantization(name);
    }

    /// <summary><see cref="ParseQuantization"/>, or <see cref="UnknownQuantization"/> when nothing matches.</summary>
    public static string QuantizationOrUnknown(string fileNameOrPath) =>
        ParseQuantization(fileNameOrPath) ?? UnknownQuantization;

    /// <summary>
    /// Quantisation for a file inside a Hugging Face repository, where the quantisation is often only
    /// in the folder name ("Q4_K_M/model-00001-of-00002.gguf"). The file name is tried first, then the
    /// folders from nearest to furthest. Returns null when none of them names one.
    /// </summary>
    public static string? ParseQuantizationFromRepoPath(string repoFilePath)
    {
        if (string.IsNullOrWhiteSpace(repoFilePath)) return null;
        string[] segments = repoFilePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string text = i == segments.Length - 1
                ? RemoveSplitPartToken(StripGgufExtension(segments[i]))
                : segments[i];
            string? found = MatchQuantization(text);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Reads a llama.cpp split-part token. Returns false when the name has none, or when the
    /// index is zero or larger than the total, which a split tool never writes.</summary>
    public static bool TryParseSplitPart(string fileNameOrPath, out SplitPart part)
    {
        part = default;
        string stem = StripGgufExtension(BaseName(fileNameOrPath));
        Match match = SplitPartToken.Match(stem);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["index"].ValueSpan, out int index)
            || !int.TryParse(match.Groups["total"].ValueSpan, out int total)
            || total < 1 || index < 1 || index > total)
            return false;
        part = new SplitPart(stem.Remove(match.Index, match.Length), index, total);
        return true;
    }

    /// <summary>True for a vision projector ("mmproj-*.gguf"). A projector is never a model in its own
    /// right: it is never a split part, never routed to, and only ever pairs with a model.</summary>
    public static bool IsProjector(string fileNameOrPath) =>
        ProjectorToken.IsMatch(StripGgufExtension(BaseName(fileNameOrPath)));

    private static string? MatchQuantization(string text)
    {
        Match match = QuantizationToken.Match(text);
        if (!match.Success) return null;
        string upper = match.Value.ToUpperInvariant();
        return upper switch
        {
            "FP16" => "F16",
            "FP32" => "F32",
            _ => upper
        };
    }

    private static string RemoveSplitPartToken(string stem)
    {
        Match match = SplitPartToken.Match(stem);
        return match.Success ? stem.Remove(match.Index, match.Length) : stem;
    }

    private static string StripGgufExtension(string name) =>
        name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;

    private static string BaseName(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }
}
