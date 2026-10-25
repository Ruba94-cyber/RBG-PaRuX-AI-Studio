using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

internal sealed record MediaJobShape(double Megapixels, int Frames, double Seconds);

internal sealed record MediaCatalogueCapability(string Id, string Label, MediaJobShape DefaultJob);

internal sealed record MediaCatalogueVariant(
    string Id, string Title, string Lane, string? LaneEvidence, string Precision, string? Repository, string? Path,
    string FileName, long? SizeBytes, string SizeBasis, string License, int Tier, bool ListingVerified, bool Bundled,
    string BaseCompat, IReadOnlyList<string> AdditionalFiles)
{
    internal string? DownloadUrl => Repository is not null && Path is not null
        ? $"https://huggingface.co/{Repository}/resolve/main/{Path}" : null;
}

internal sealed record MediaCatalogueFamily(
    string Id, string DisplayName, string Capability, string Support, string? Reason, string? RegistryFamily,
    double ParametersBillion, double TextEncoderGb, double VaeGb, IReadOnlyList<MediaCatalogueVariant> Variants)
{
    internal bool IsSupported => Support == "supported";
}

/// <summary>
/// The versioned, additive media catalogue (embedded JSON). Validated completely at load so a broken entry never reaches the UI:
/// supported families must map to a supported companion-registry family with a workflow template, resolver-only families must
/// give a reason, uncensored entries must carry lane evidence, and an exact size may only be claimed for a verified listing.
/// </summary>
internal sealed class MediaCatalogue
{
    public const string ResourceName = "PaRuX.Server.Assets.media.media-catalogue.json";
    internal const int SupportedSchemaVersion = 1;
    internal static readonly string[] Lanes = { "standard", "uncensored" };
    internal static readonly string[] CapabilityIds = { "image.generate", "image.edit", "video.generate", "audio.music", "audio.speak" };
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9.\-]{0,59}$", RegexOptions.CultureInvariant);
    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant);
    private static readonly Lazy<MediaCatalogue> ShippedCatalogue = new(LoadShipped);

    public int Version { get; }
    public IReadOnlyList<MediaCatalogueCapability> Capabilities { get; }
    public IReadOnlyList<MediaCatalogueFamily> Families { get; }

    private MediaCatalogue(int version, IReadOnlyList<MediaCatalogueCapability> caps, IReadOnlyList<MediaCatalogueFamily> families)
    { Version = version; Capabilities = caps; Families = families; }

    public static MediaCatalogue Shipped => ShippedCatalogue.Value;

    private static MediaCatalogue LoadShipped()
    {
        using Stream? s = typeof(MediaCatalogue).Assembly.GetManifestResourceStream(ResourceName);
        if (s is null) throw new InvalidDataException("The embedded media catalogue is missing.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd(), MediaCompanionRegistry.Shipped);
    }

    internal MediaCatalogueCapability? CapabilityOf(string id) => Capabilities.FirstOrDefault(c => c.Id == id);

    internal IEnumerable<(MediaCatalogueFamily Family, MediaCatalogueVariant Variant)> VariantsFor(string capability) =>
        Families.Where(f => f.Capability == capability).SelectMany(f => f.Variants.Select(v => (f, v)));

    public static MediaCatalogue Parse(string json, MediaCompanionRegistry registry)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new InvalidDataException("Media catalogue is not valid JSON: " + ex.Message); }

        if (Int(root, "schemaVersion", "catalogue") != SupportedSchemaVersion)
            throw new InvalidDataException("Media catalogue schemaVersion is not supported.");
        int version = Int(root, "catalogueVersion", "catalogue");
        if (version < 1) throw new InvalidDataException("catalogueVersion must be at least 1.");

        var caps = new List<MediaCatalogueCapability>();
        foreach (JsonElement c in Arr(root, "capabilities", "catalogue"))
        {
            string id = Str(c, "id", "capability");
            if (!CapabilityIds.Contains(id)) throw new InvalidDataException($"Unknown capability \"{id}\".");
            if (caps.Any(x => x.Id == id)) throw new InvalidDataException($"Duplicate capability \"{id}\".");
            JsonElement job = c.GetProperty("defaultJob");
            caps.Add(new(id, Str(c, "label", id), new(Dbl(job, "megapixels", id), Int(job, "frames", id), Dbl(job, "seconds", id))));
        }

        var families = new List<MediaCatalogueFamily>();
        var variantIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement f in Arr(root, "families", "catalogue"))
        {
            string fid = Str(f, "id", "family");
            string where = "family " + fid;
            if (!IdPattern.IsMatch(fid)) throw new InvalidDataException($"Bad family id \"{fid}\".");
            if (families.Any(x => x.Id == fid)) throw new InvalidDataException($"Duplicate family \"{fid}\".");
            string cap = Str(f, "capability", where);
            if (caps.All(c => c.Id != cap)) throw new InvalidDataException($"{where}: capability \"{cap}\" is not declared.");
            string support = Str(f, "support", where);
            if (support is not ("supported" or "resolver_only")) throw new InvalidDataException($"{where}: support must be supported or resolver_only.");
            string? reason = OptStr(f, "reason");
            string? regFamily = OptStr(f, "registryFamily");
            if (support == "supported")
            {
                RegistryFamily? rf = registry.Families.FirstOrDefault(x => x.Id == regFamily);
                if (rf is null || !rf.IsSupported || string.IsNullOrEmpty(rf.TemplateFile))
                    throw new InvalidDataException($"{where}: a supported family must map to a supported companion-registry family with a workflow template.");
            }
            else if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"{where}: a resolver_only family must state an honest reason.");

            double paramsB = Dbl(f, "parametersBillion", where);
            if (paramsB <= 0) throw new InvalidDataException($"{where}: parametersBillion must be positive.");
            var variants = new List<MediaCatalogueVariant>();
            foreach (JsonElement v in Arr(f, "variants", where))
            {
                string vid = Str(v, "id", where);
                string vw = $"{where} variant {vid}";
                if (!IdPattern.IsMatch(vid) || !variantIds.Add(vid)) throw new InvalidDataException($"{vw}: id is invalid or duplicated.");
                string lane = Str(v, "lane", vw);
                if (!Lanes.Contains(lane)) throw new InvalidDataException($"{vw}: lane must be standard or uncensored.");
                string? laneEvidence = OptStr(v, "laneEvidence");
                if (lane == "uncensored" && string.IsNullOrWhiteSpace(laneEvidence))
                    throw new InvalidDataException($"{vw}: an uncensored entry must carry laneEvidence from the repository itself.");
                string precision = Str(v, "precision", vw);
                if (MediaFitEstimator.BytesPerParameter(precision) is null) throw new InvalidDataException($"{vw}: precision \"{precision}\" has no size rule.");
                string? repo = OptStr(v, "repository");
                if (repo is not null && !RepoPattern.IsMatch(repo)) throw new InvalidDataException($"{vw}: repository is not owner/name.");
                string? path = OptStr(v, "path");
                string fileName = Str(v, "fileName", vw);
                if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
                    throw new InvalidDataException($"{vw}: fileName must be a bare file name.");
                if (path is not null && (path.Contains("..") || path.StartsWith('/') || !path.EndsWith(fileName, StringComparison.Ordinal)))
                    throw new InvalidDataException($"{vw}: path must be a relative repository path ending in fileName.");
                bool verified = v.TryGetProperty("listingVerified", out JsonElement lv) && lv.GetBoolean();
                bool bundled = v.TryGetProperty("bundled", out JsonElement bu) && bu.GetBoolean();
                long? size = v.TryGetProperty("sizeBytes", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null;
                string sizeBasis = Str(v, "sizeBasis", vw);
                if (sizeBasis is not ("exact_listing" or "derived_from_parameters")) throw new InvalidDataException($"{vw}: unknown sizeBasis.");
                if ((sizeBasis == "exact_listing") != (size is > 0)) throw new InvalidDataException($"{vw}: an exact size needs exact_listing basis and the reverse.");
                if (verified && (repo is null || path is null || size is null)) throw new InvalidDataException($"{vw}: a verified listing needs repository, path and size.");
                if (!verified && size is not null) throw new InvalidDataException($"{vw}: an unverified listing must not claim an exact size.");
                string license = Str(v, "license", vw);
                int tier = Int(v, "tier", vw);
                if (tier is < 1 or > 5) throw new InvalidDataException($"{vw}: tier must be 1-5.");
                var extra = new List<string>();
                if (v.TryGetProperty("additionalFiles", out JsonElement af))
                    foreach (JsonElement e in af.EnumerateArray()) extra.Add(e.GetString() ?? string.Empty);
                variants.Add(new(vid, Str(v, "title", vw), lane, laneEvidence, precision, repo, path, fileName, size, sizeBasis, license, tier,
                    verified, bundled, Str(v, "baseCompat", vw), extra));
            }
            if (variants.Count == 0) throw new InvalidDataException($"{where}: has no variants.");
            families.Add(new(fid, Str(f, "displayName", where), cap, support, reason, regFamily, paramsB,
                Dbl(f, "textEncoderGb", where), Dbl(f, "vaeGb", where), variants));
        }
        return new MediaCatalogue(version, caps, families);
    }

    private static string Str(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()! : throw new InvalidDataException($"{where}: missing \"{name}\".");
    private static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    private static int Int(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : throw new InvalidDataException($"{where}: missing number \"{name}\".");
    private static double Dbl(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new InvalidDataException($"{where}: missing number \"{name}\".");
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : throw new InvalidDataException($"{where}: missing array \"{name}\".");
}
