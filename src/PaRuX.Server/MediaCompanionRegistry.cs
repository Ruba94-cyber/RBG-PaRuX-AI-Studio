using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

internal sealed record RegistrySource(string? Repository, string? Path, string? Revision, long? SizeBytes, string? Sha256)
{
    /// <summary>True only when every field the fetch needs to verify the bytes is pinned.</summary>
    public bool IsPinned => Repository != null && Path != null && Revision != null && SizeBytes != null && Sha256 != null;
}

/// <summary>Companion-file match rule. A pattern is only used when the exact file name is absent, and only
/// when it matches exactly one file that no exclusion pattern matches. Bare substring matching does not exist.</summary>
internal sealed record RegistryMatchRule(string Pattern, IReadOnlyList<string> Exclude);

/// <summary>One companion file. License is the publisher's declared licence for that exact file, recorded only once
/// it has been checked; null means the consent plan reports that no licence evidence is held.</summary>
internal sealed record RegistryCompanion(
    string Role,
    string Folder,
    string FileName,
    string? ClipType,
    IReadOnlyList<string> SharedWith,
    string? Notes,
    RegistryMatchRule? Match,
    RegistrySource Source,
    string? License = null);

internal sealed record RegistryShapeCheck(string Tensor, int Dim, long Expected);

internal sealed record RegistrySignature(
    IReadOnlyList<string> RequiredStems,
    IReadOnlyList<string> ForbiddenStems,
    IReadOnlyList<RegistryShapeCheck> ShapeChecks);

internal sealed record RegistryMainLoader(string Node, string ClassType, string Input, string Folder);

internal sealed record RegistryFamily(
    string Id,
    string DisplayName,
    string Support,
    string? Reason,
    IReadOnlyList<string> Pipelines,
    string? TemplateFile,
    RegistryMainLoader? MainLoader,
    IReadOnlyList<string> RequiredNodes,
    RegistrySignature Signature,
    IReadOnlyList<RegistryCompanion> Companions,
    string? MinComfyUiVersion = null)
{
    public bool IsSupported => Support == "supported";
}

internal sealed record RegistryEmbeddedStems(IReadOnlyList<string> TextEncoder, IReadOnlyList<string> Vae);

/// <summary>
/// The per-family companion manifest (embedded JSON, schema v1). It is validated completely at load:
/// unknown keys, bad identifiers, unpinned-but-claimed sources, shared-file drift and unsupported
/// families that claim a pipeline all fail the load, so a broken manifest never reaches a job.
/// </summary>
internal sealed class MediaCompanionRegistry
{
    public const string ResourceName = "PaRuX.Server.Assets.media.media-companion-registry.json";

    private static readonly Regex FamilyIdPattern = new(@"^[a-z0-9][a-z0-9.\-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ComfyVersionPattern = new(@"^\d{1,4}(\.\d{1,4}){1,2}$", RegexOptions.CultureInvariant);
    private static readonly string[] Roles = { "text_encoder", "vae", "clip_vision", "lora", "upscaler" };
    private static readonly string[] Folders = { "diffusion_models", "text_encoders", "clip", "vae", "clip_vision", "loras", "upscale_models" };
    private static readonly string[] Pipelines = { "image", "img2img", "inpaint", "edit", "video" };
    private static readonly string[] Supports = { "supported", "resolver_only", "unsupported" };

    /// <summary>ComfyUI loader inputs that name a model file, mapped to the models folder they read from.
    /// Used to say where a missing file belongs. Keys are "ClassType.input".</summary>
    public static readonly IReadOnlyDictionary<string, string> LoaderFolders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UNETLoader.unet_name"] = "diffusion_models",
        ["CLIPLoader.clip_name"] = "text_encoders",
        ["DualCLIPLoader.clip_name1"] = "text_encoders",
        ["DualCLIPLoader.clip_name2"] = "text_encoders",
        ["VAELoader.vae_name"] = "vae",
        ["UpscaleModelLoader.model_name"] = "upscale_models",
        ["LoraLoader.lora_name"] = "loras",
        ["CheckpointLoaderSimple.ckpt_name"] = "checkpoints"
    };

    private static readonly Lazy<MediaCompanionRegistry> ShippedRegistry = new(LoadShipped);

    private MediaCompanionRegistry(
        RegistryEmbeddedStems embeddedStems,
        IReadOnlyList<RegistryFamily> families)
    {
        EmbeddedStems = embeddedStems;
        Families = families;
    }

    public RegistryEmbeddedStems EmbeddedStems { get; }
    public IReadOnlyList<RegistryFamily> Families { get; }

    /// <summary>The manifest compiled into this assembly. Loaded and validated once; a malformed build fails here.</summary>
    public static MediaCompanionRegistry Shipped => ShippedRegistry.Value;

    public RegistryFamily? FindFamily(string id) =>
        Families.FirstOrDefault(family => string.Equals(family.Id, id, StringComparison.Ordinal));

    /// <summary>The ComfyUI version a supported family's workflow needs, found by its workflow template file name.
    /// Null when the family declares no minimum; the shipped manifest declares none until a minimum is verified.</summary>
    public string? MinimumComfyUiVersionFor(string templateFile) =>
        Families.FirstOrDefault(family => family.IsSupported && string.Equals(family.TemplateFile, templateFile, StringComparison.Ordinal))
            ?.MinComfyUiVersion;

    /// <summary>Families a media pipeline can run, such as those that list "image" in their pipelines.</summary>
    public IReadOnlyList<RegistryFamily> SupportedFor(string pipeline) =>
        Families.Where(family => family.IsSupported && family.Pipelines.Contains(pipeline, StringComparer.Ordinal)).ToList();

    /// <summary>Finds the companion that declares this exact file name, preferring a supported family.</summary>
    public (RegistryFamily Family, RegistryCompanion Companion)? FindCompanionByFileName(string folder, string fileName)
    {
        RegistryFamily? best = null;
        RegistryCompanion? bestCompanion = null;
        foreach (RegistryFamily family in Families)
        {
            foreach (RegistryCompanion companion in family.Companions)
            {
                if (!string.Equals(companion.Folder, folder, StringComparison.Ordinal)
                    || !string.Equals(companion.FileName, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || (family.IsSupported && !best.IsSupported))
                {
                    best = family;
                    bestCompanion = companion;
                }
            }
        }
        return best == null || bestCompanion == null ? null : (best, bestCompanion);
    }

    public static MediaCompanionRegistry Parse(string json, string sourceName = "media companion registry")
    {
        JsonObject root;
        try
        {
            JsonNode? parsed = JsonNode.Parse(json);
            root = parsed as JsonObject ?? throw new InvalidDataException(sourceName + ": the document must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(sourceName + ": not valid JSON (" + exception.Message + ").", exception);
        }

        try
        {
            AllowOnly(root, "root", "_comment", "schemaVersion", "embeddedStems", "families");
            if (Int(root, "root", "schemaVersion") != 1) throw Invalid("root.schemaVersion must be 1.");

            JsonObject stems = Obj(root["embeddedStems"], "embeddedStems");
            AllowOnly(stems, "embeddedStems", "textEncoder", "vae");
            var embedded = new RegistryEmbeddedStems(StringArray(stems, "embeddedStems", "textEncoder", required: true, maxItems: 16),
                StringArray(stems, "embeddedStems", "vae", required: true, maxItems: 16));

            JsonArray families = Arr(root["families"], "families");
            if (families.Count == 0) throw Invalid("families must not be empty.");
            var parsed = new List<RegistryFamily>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? node in families)
            {
                RegistryFamily family = ParseFamily(Obj(node, "families[]"));
                if (!ids.Add(family.Id)) throw Invalid("family id \"" + family.Id + "\" appears more than once.");
                parsed.Add(family);
            }

            ValidateCrossReferences(parsed);
            return new MediaCompanionRegistry(embedded, parsed);
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith(sourceName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(sourceName + ": " + exception.Message, exception);
        }
    }

    private static MediaCompanionRegistry LoadShipped()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded media companion registry is missing from this build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), ResourceName);
    }

    private static RegistryFamily ParseFamily(JsonObject family)
    {
        AllowOnly(family, "family", "id", "displayName", "support", "reason", "pipelines", "templateFile",
            "mainLoader", "requiredNodes", "signature", "companions", "minComfyUiVersion");

        string id = Str(family, "family", "id", 40);
        if (!FamilyIdPattern.IsMatch(id)) throw Invalid("family id \"" + id + "\" is not a lowercase slug.");
        string where = "family " + id;
        string displayName = Str(family, where, "displayName", 80);
        string support = Str(family, where, "support", 20);
        if (!Supports.Contains(support, StringComparer.Ordinal)) throw Invalid(where + ": unknown support \"" + support + "\".");
        string? reason = OptStr(family, where, "reason", 400);
        string? minComfyUi = OptStr(family, where, "minComfyUiVersion", 16);
        if (minComfyUi != null && !ComfyVersionPattern.IsMatch(minComfyUi))
            throw Invalid(where + ": minComfyUiVersion must be a dotted version such as 0.37.0.");
        if (support != "supported" && string.IsNullOrWhiteSpace(reason))
            throw Invalid(where + ": a family that is not supported must say why.");
        if (support == "supported" && reason != null) throw Invalid(where + ": a supported family must not carry a reason.");

        List<string> pipelines = StringArray(family, where, "pipelines", required: true, maxItems: 4).ToList();
        foreach (string pipeline in pipelines)
            if (!Pipelines.Contains(pipeline, StringComparer.Ordinal)) throw Invalid(where + ": unknown pipeline \"" + pipeline + "\".");
        if (support == "supported" && pipelines.Count == 0) throw Invalid(where + ": a supported family needs at least one pipeline.");
        if (support != "supported" && pipelines.Count > 0) throw Invalid(where + ": only a supported family may list pipelines.");

        string? templateFile = OptStr(family, where, "templateFile", 120);
        RegistryMainLoader? mainLoader = null;
        if (family["mainLoader"] != null)
        {
            JsonObject loader = Obj(family["mainLoader"], where + ".mainLoader");
            AllowOnly(loader, where + ".mainLoader", "node", "classType", "input", "folder");
            mainLoader = new RegistryMainLoader(
                Str(loader, where + ".mainLoader", "node", 8),
                Str(loader, where + ".mainLoader", "classType", 64),
                Str(loader, where + ".mainLoader", "input", 64),
                Str(loader, where + ".mainLoader", "folder", 32));
            if (mainLoader.Folder != "diffusion_models") throw Invalid(where + ": the main loader must read diffusion_models.");
        }
        if (support == "supported" && (templateFile == null || !templateFile.EndsWith(".json", StringComparison.Ordinal) || mainLoader == null))
            throw Invalid(where + ": a supported family needs a workflow template and a main loader.");

        List<string> requiredNodes = StringArray(family, where, "requiredNodes", required: false, maxItems: 64).ToList();
        if (support == "supported" && requiredNodes.Count == 0) throw Invalid(where + ": a supported family must list the ComfyUI nodes it needs.");

        JsonObject signature = Obj(family["signature"], where + ".signature");
        AllowOnly(signature, where + ".signature", "requiredStems", "forbiddenStems", "shapeChecks");
        var required = StringArray(signature, where + ".signature", "requiredStems", required: true, maxItems: 64);
        var forbidden = StringArray(signature, where + ".signature", "forbiddenStems", required: false, maxItems: 64);
        var shapeChecks = new List<RegistryShapeCheck>();
        if (signature["shapeChecks"] is JsonArray checks)
        {
            foreach (JsonNode? node in checks)
            {
                JsonObject check = Obj(node, where + ".signature.shapeChecks[]");
                AllowOnly(check, where + ".signature.shapeChecks[]", "tensor", "dim", "equals");
                // Range-check the 64-bit value before narrowing: (int) would wrap 4294967296 to 0 and pass.
                long dimension = Int(check, where + ".signature.shapeChecks[]", "dim");
                if (dimension < 0 || dimension > 7) throw Invalid(where + ": a shape check dimension must be 0 to 7.");
                int dim = (int)dimension;
                long equals = Int(check, where + ".signature.shapeChecks[]", "equals");
                if (equals < 0) throw Invalid(where + ": a shape check must compare to a non-negative size.");
                shapeChecks.Add(new RegistryShapeCheck(Str(check, where, "tensor", 256), dim, equals));
            }
        }

        var companions = new List<RegistryCompanion>();
        var fileKeys = new HashSet<string>(StringComparer.Ordinal);
        if (family["companions"] is JsonArray companionArray)
        {
            foreach (JsonNode? node in companionArray)
            {
                RegistryCompanion companion = ParseCompanion(Obj(node, where + ".companions[]"), where, support);
                if (!fileKeys.Add(companion.Folder + "/" + companion.FileName))
                    throw Invalid(where + ": " + companion.Folder + "/" + companion.FileName + " is listed twice.");
                companions.Add(companion);
            }
        }

        return new RegistryFamily(id, displayName, support, reason, pipelines, templateFile, mainLoader,
            requiredNodes, new RegistrySignature(required, forbidden, shapeChecks), companions, minComfyUi);
    }

    private static RegistryCompanion ParseCompanion(JsonObject companion, string where, string support)
    {
        AllowOnly(companion, where + ".companions[]", "role", "folder", "fileName", "clipType", "sharedWith", "notes", "license", "match", "source");
        string role = Str(companion, where, "role", 32);
        if (!Roles.Contains(role, StringComparer.Ordinal)) throw Invalid(where + ": unknown companion role \"" + role + "\".");
        string folder = Str(companion, where, "folder", 32);
        if (!Folders.Contains(folder, StringComparer.Ordinal)) throw Invalid(where + ": unknown ComfyUI folder \"" + folder + "\".");
        string fileName = Str(companion, where, "fileName", 200);
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':') || fileName.Contains("..") || fileName.Trim() != fileName)
            throw Invalid(where + ": companion file name \"" + fileName + "\" must be a plain file name.");
        if (!(fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
              || fileName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)
              || fileName.EndsWith(".pt", StringComparison.OrdinalIgnoreCase)))
            throw Invalid(where + ": companion file \"" + fileName + "\" must be a .safetensors, .pth or .pt file.");
        string? clipType = OptStr(companion, where, "clipType", 40);
        if (support == "supported" && role == "text_encoder" && clipType == null)
            throw Invalid(where + ": a supported text encoder must declare its CLIP type.");
        List<string> sharedWith = StringArray(companion, where, "sharedWith", required: false, maxItems: 8).ToList();
        string? notes = OptStr(companion, where, "notes", 400);
        string? license = OptStr(companion, where, "license", 120);

        RegistryMatchRule? match = null;
        if (companion["match"] != null)
        {
            JsonObject rule = Obj(companion["match"], where + ".match");
            AllowOnly(rule, where + ".match", "pattern", "exclude");
            string pattern = Str(rule, where + ".match", "pattern", 200);
            if (pattern.Contains('/') || pattern.Contains('\\') || !pattern.Contains('*'))
                throw Invalid(where + ": a match pattern must be a file-name glob containing '*'.");
            var exclude = StringArray(rule, where + ".match", "exclude", required: false, maxItems: 16).ToList();
            match = new RegistryMatchRule(pattern, exclude);
        }

        RegistrySource source = ParseSource(Obj(companion["source"], where + ".source"), where);
        return new RegistryCompanion(role, folder, fileName, clipType, sharedWith, notes, match, source, license);
    }

    private static RegistrySource ParseSource(JsonObject source, string where)
    {
        AllowOnly(source, where + ".source", "repository", "path", "revision", "sizeBytes", "sha256");
        string? repository = OptStr(source, where + ".source", "repository", 120);
        if (repository != null && !RepositoryPattern.IsMatch(repository))
            throw Invalid(where + ": repository \"" + repository + "\" is not an owner/name id.");
        string? path = OptStr(source, where + ".source", "path", 300);
        if (path != null && (path.StartsWith('/') || path.Contains("..") || path.Contains('\\')))
            throw Invalid(where + ": repository path must be relative and must not traverse.");
        string? revision = OptStr(source, where + ".source", "revision", 64);
        if (revision != null && !RevisionPattern.IsMatch(revision))
            throw Invalid(where + ": revision must be an immutable lowercase commit SHA.");
        string? sha256 = OptStr(source, where + ".source", "sha256", 64);
        if (sha256 != null && !Sha256Pattern.IsMatch(sha256)) throw Invalid(where + ": sha256 must be 64 lowercase hex characters.");
        long? sizeBytes = OptInt(source, where + ".source", "sizeBytes");
        if (sizeBytes is <= 0) throw Invalid(where + ": sizeBytes must be positive when present.");
        return new RegistrySource(repository, path, revision, sizeBytes, sha256);
    }

    /// <summary>Checks rules that span families: shared files must be declared by every family that uses them,
    /// with the same source, and a family must not claim a file another family pins differently.</summary>
    private static void ValidateCrossReferences(IReadOnlyList<RegistryFamily> families)
    {
        var byKey = new Dictionary<string, List<(RegistryFamily Family, RegistryCompanion Companion)>>(StringComparer.Ordinal);
        foreach (RegistryFamily family in families)
        {
            foreach (RegistryCompanion companion in family.Companions)
            {
                string key = companion.Folder + "/" + companion.FileName.ToLowerInvariant();
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new();
                list.Add((family, companion));

                foreach (string other in companion.SharedWith)
                {
                    if (string.Equals(other, family.Id, StringComparison.Ordinal))
                        throw Invalid("family " + family.Id + " lists itself in sharedWith.");
                    if (families.All(candidate => candidate.Id != other))
                        throw Invalid("family " + family.Id + " shares a file with unknown family \"" + other + "\".");
                }
            }
        }

        foreach ((string key, var members) in byKey)
        {
            if (members.Count == 1) continue;
            RegistrySource first = members[0].Companion.Source;
            foreach (var (family, companion) in members)
            {
                if (!companion.Source.Equals(first))
                    throw Invalid("shared file " + key + " has different sources in different families.");
                foreach (var (otherFamily, _) in members)
                {
                    if (otherFamily.Id == family.Id) continue;
                    if (!companion.SharedWith.Contains(otherFamily.Id, StringComparer.Ordinal))
                        throw Invalid("shared file " + key + " is used by " + family.Id + " and " + otherFamily.Id
                            + "; both families must list each other in sharedWith.");
                }
            }
        }
    }

    private static Exception Invalid(string message) => new InvalidDataException(message);

    private static void AllowOnly(JsonObject obj, string where, params string[] allowed)
    {
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (Array.IndexOf(allowed, property.Key) < 0)
                throw Invalid(where + " has an unknown key \"" + property.Key + "\".");
        }
    }

    private static JsonObject Obj(JsonNode? node, string where) =>
        node as JsonObject ?? throw Invalid(where + " must be a JSON object.");

    private static JsonArray Arr(JsonNode? node, string where) =>
        node as JsonArray ?? throw Invalid(where + " must be a JSON array.");

    private static string Str(JsonObject obj, string where, string key, int maxLength)
    {
        string? value = OptStr(obj, where, key, maxLength);
        return value ?? throw Invalid(where + "." + key + " is required.");
    }

    private static string? OptStr(JsonObject obj, string where, string key, int maxLength)
    {
        if (obj[key] == null) return null;
        if (obj[key] is not JsonValue value || !value.TryGetValue(out string? text))
            throw Invalid(where + "." + key + " must be a string.");
        if (text == null || text.Trim().Length == 0 || text.Length > maxLength)
            throw Invalid(where + "." + key + " must be non-empty and at most " + maxLength + " characters.");
        return text;
    }

    private static long Int(JsonObject obj, string where, string key)
    {
        return OptInt(obj, where, key) ?? throw Invalid(where + "." + key + " is required.");
    }

    private static long? OptInt(JsonObject obj, string where, string key)
    {
        if (obj[key] == null) return null;
        if (obj[key] is not JsonValue value || !value.TryGetValue(out long number))
            throw Invalid(where + "." + key + " must be an integer.");
        return number;
    }

    private static IReadOnlyList<string> StringArray(JsonObject obj, string where, string key, bool required, int maxItems)
    {
        if (obj[key] == null)
        {
            if (required) throw Invalid(where + "." + key + " is required.");
            return Array.Empty<string>();
        }
        JsonArray array = Arr(obj[key], where + "." + key);
        if (array.Count > maxItems) throw Invalid(where + "." + key + " has more than " + maxItems + " items.");
        var result = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text) || text.Length > 200)
                throw Invalid(where + "." + key + " must contain non-empty strings.");
            result.Add(text);
        }
        return result;
    }
}
