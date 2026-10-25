using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Vision-projector pairing as data. The families that take a projector, the tokens that identify
/// them, bundled role/model/projector launch pairs and the quantisation preference all live in
/// Assets/models/vision-projector-families.json, embedded in the build. Nothing in code names a
/// projector file.
///
/// A family matches a repository or file name when one of its tokens appears there at a token
/// boundary. The first family in manifest order that matches wins. Pairing is by family: a projector
/// pairs with a model only when both resolve to the same family, so an unrelated projector never
/// attaches by accident.
/// </summary>
public sealed class VisionProjectorManifest
{
    public const string EmbeddedResourceName = "PaRuX.Server.Assets.models.vision-projector-families.json";

    private const int SupportedSchemaVersion = 1;
    private const string Separators = @"-_. /\\\[\]()+";

    private static readonly Lazy<VisionProjectorManifest> DefaultManifest = new(LoadEmbedded);

    private readonly IReadOnlyList<VisionFamily> _families;
    private readonly List<string> _preference;
    private readonly IReadOnlyList<(VisionFamily Family, Regex Pattern)> _matchers;
    private readonly IReadOnlyList<BundledRoleModel> _bundledRoleModels;

    private VisionProjectorManifest(
        IReadOnlyList<VisionFamily> families,
        List<string> preference,
        IReadOnlyList<BundledRoleModel> bundledRoleModels)
    {
        _families = families;
        _preference = preference;
        _bundledRoleModels = bundledRoleModels;
        _matchers = families
            .Select(family => (family, new Regex(
                @"(?<=^|[" + Separators + @"])(?:" + string.Join('|', family.Tokens.Select(Regex.Escape)) + @")(?=$|[" + Separators + "])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .ToList();
    }

    /// <summary>The manifest embedded in this build.</summary>
    public static VisionProjectorManifest Default => DefaultManifest.Value;

    public IReadOnlyList<VisionFamily> Families => _families;

    /// <summary>Returns the bundled model/projector pair declared for a text role, if any.</summary>
    public BundledRoleModel? BundledPairForRole(string? role)
    {
        string normalized = (role ?? string.Empty).Trim();
        if (normalized.Equals("main", StringComparison.OrdinalIgnoreCase)) normalized = "standard";
        if (normalized.Length == 0) return null;
        return _bundledRoleModels.FirstOrDefault(pair =>
            pair.Role.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record BundledRoleModel(string Role, string ModelFileName, string ProjectorFileName);

    /// <summary>Parses and validates a manifest. Throws <see cref="InvalidDataException"/> on any
    /// unknown schema, empty family, unsafe token or non-canonical quantisation label.</summary>
    public static VisionProjectorManifest Parse(string json)
    {
        ManifestDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("The vision projector manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The vision projector manifest is not valid JSON.", exception);
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException("Unsupported vision projector manifest schema version " + document.SchemaVersion + ".");

        var preference = new List<string>();
        foreach (string label in document.ProjectorQuantizationPreference ?? new List<string>())
        {
            string canonical = (label ?? string.Empty).Trim().ToUpperInvariant();
            if (GgufFileNaming.ParseQuantization(canonical) != canonical)
                throw new InvalidDataException("Projector quantisation preference '" + label + "' is not a recognised quantisation.");
            preference.Add(canonical);
        }
        if (preference.Count == 0)
            throw new InvalidDataException("The vision projector manifest needs at least one projector quantisation preference.");

        var families = new List<VisionFamily>();
        var bundledRoleModels = new List<BundledRoleModel>();
        var bundledRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FamilyDocument entry in document.Families ?? new List<FamilyDocument>())
        {
            string id = (entry.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("Every vision projector family needs a unique id.");
            var tokens = (entry.Tokens ?? new List<string>())
                .Select(token => (token ?? string.Empty).Trim().ToLowerInvariant())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (tokens.Count == 0 || tokens.Any(token => token.Length < 3 || !SafeTokenPattern.IsMatch(token)))
                throw new InvalidDataException("Vision projector family '" + id + "' needs safe tokens of at least three characters.");
            families.Add(new VisionFamily(id, entry.VisionCapable, tokens));

            foreach (BundledRoleModelDocument? bundled in entry.BundledModels ?? new List<BundledRoleModelDocument?>())
            {
                if (bundled == null)
                    throw new InvalidDataException("Vision projector family '" + id + "' has an empty bundled model entry.");
                string role = (bundled.Role ?? string.Empty).Trim().ToLowerInvariant();
                if (role == "main") role = "standard";
                string modelFileName = (bundled.ModelFileName ?? string.Empty).Trim();
                string projectorFileName = (bundled.ProjectorFileName ?? string.Empty).Trim();
                if (!entry.VisionCapable
                    || role.Length == 0
                    || !SafeRolePattern.IsMatch(role)
                    || !bundledRoles.Add(role))
                    throw new InvalidDataException("Bundled vision role mappings must use unique roles on vision-capable families.");
                if (!SafeGgufFileNamePattern.IsMatch(modelFileName)
                    || !SafeGgufFileNamePattern.IsMatch(projectorFileName))
                    throw new InvalidDataException("Bundled vision role mappings need safe GGUF file names.");
                bundledRoleModels.Add(new BundledRoleModel(role, modelFileName, projectorFileName));
            }
        }
        if (families.Count == 0)
            throw new InvalidDataException("The vision projector manifest lists no families.");

        return new VisionProjectorManifest(families, preference, bundledRoleModels);
    }

    /// <summary>The first family (manifest order) whose token appears in <paramref name="text"/> at a token boundary.</summary>
    public VisionFamily? FamilyFor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (family, pattern) in _matchers)
            if (pattern.IsMatch(text)) return family;
        return null;
    }

    /// <summary>
    /// Chooses one projector from the candidates: the best-ranked quantisation first, then the name, so
    /// the choice is stable. Returns null when there are no candidates.
    /// </summary>
    public string? ChooseProjector(IEnumerable<string> projectorPaths)
    {
        return projectorPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => Rank(GgufFileNaming.ParseQuantizationFromRepoPath(path)))
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private int Rank(string? quantization)
    {
        int index = quantization == null ? -1 : _preference.IndexOf(quantization);
        return index < 0 ? _preference.Count : index;
    }

    private static VisionProjectorManifest LoadEmbedded()
    {
        using Stream stream = typeof(VisionProjectorManifest).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("The vision projector manifest is missing from this build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static readonly Regex SafeTokenPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeRolePattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeGgufFileNamePattern = new("^[a-z0-9][a-z0-9._-]*\\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ManifestDocument
    {
        public int SchemaVersion { get; set; }
        public List<string>? ProjectorQuantizationPreference { get; set; }
        public List<FamilyDocument>? Families { get; set; }
    }

    private sealed class FamilyDocument
    {
        public string? Id { get; set; }
        public bool VisionCapable { get; set; }
        public List<string>? Tokens { get; set; }
        public List<BundledRoleModelDocument?>? BundledModels { get; set; }
    }

    private sealed class BundledRoleModelDocument
    {
        public string? Role { get; set; }
        public string? ModelFileName { get; set; }
        public string? ProjectorFileName { get; set; }
    }
}

/// <summary>One vision family. VisionCapable means a model of this family is expected to need a projector.</summary>
public sealed record VisionFamily(string Id, bool VisionCapable, IReadOnlyList<string> Tokens);
