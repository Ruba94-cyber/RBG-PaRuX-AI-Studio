using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

public sealed record ImportedComfyTemplateInfo(
    string Id, string Name, string WorkflowType, string SaveNodeId, bool RequiresSourceImage,
    IReadOnlyList<ComfyWorkflowParameter> Parameters, DateTime CreatedUtc);

/// <summary>
/// Stores vetted user ComfyUI workflows as named templates in the local state folder, separate from the shipped
/// templates (which are never written here and whose names cannot be taken). A stored template runs through
/// <c>ComfyWorkflowRunner.SubmitAndWaitAsync</c> with parameter overrides from <see cref="PrepareRun"/>.
/// </summary>
public sealed class ComfyWorkflowImportService
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,63}$", RegexOptions.Compiled);
    private const int MaxTemplates = 100;
    private const string IdPrefix = "user-";

    // Names of the shipped templates and workflow types; a user template may not take them.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "z-image-smoke", "z-image-img2img", "z-image-inpaint", "wan-video-smoke", "flux2-klein-edit", "upscale", "llama-ui",
        "image", "video", "edit", "img2img", "inpaint", "custom"
    };

    private readonly object _gate = new();
    private readonly string _root;

    public ComfyWorkflowImportService() : this(ServerState.StateDirectory) { }
    public ComfyWorkflowImportService(string stateDirectory) => _root = Path.Combine(stateDirectory, "imported-comfy-workflows");

    private string IndexPath => Path.Combine(_root, "index.json");
    private string GraphPath(string id) => Path.Combine(_root, id + ".json");

    internal ComfyWorkflowAnalysis Preview(string? workflowJson, ComfyNodeCatalog? catalog, bool acknowledgeUnverifiedNodes = false) =>
        ComfyWorkflowImporter.Analyze(workflowJson, catalog, acknowledgeUnverifiedNodes);

    internal ImportedComfyTemplateInfo Save(string name, string? workflowJson, ComfyNodeCatalog? catalog, bool replaceOwnTemplate = false, bool acknowledgeUnverifiedNodes = false)
    {
        name = (name ?? string.Empty).Trim();
        if (!NamePattern.IsMatch(name)) throw new InvalidDataException("Name the template with 1-64 letters, digits, spaces, dots, dashes or underscores.");
        string slug = Slug(name);
        if (ReservedNames.Contains(slug) || ReservedNames.Contains(name))
            throw new InvalidDataException("That name belongs to a built-in template. Choose another name.");
        ComfyWorkflowAnalysis analysis = ComfyWorkflowImporter.Analyze(workflowJson, catalog, acknowledgeUnverifiedNodes);
        if (!analysis.Ok) throw new InvalidDataException(string.Join(" ", analysis.Errors));

        string id = IdPrefix + slug;
        lock (_gate)
        {
            List<ImportedComfyTemplateInfo> index = LoadIndex();
            bool exists = index.Any(t => t.Id == id);
            if (exists && !replaceOwnTemplate) throw new InvalidDataException("You already have a template with that name. Confirm replacing it or choose another name.");
            if (!exists && index.Count >= MaxTemplates) throw new InvalidDataException("Too many imported templates. Delete one first.");
            var info = new ImportedComfyTemplateInfo(id, name, analysis.WorkflowType!, analysis.SaveNodeId!,
                analysis.RequiresSourceImage, analysis.Parameters.ToList(), DateTime.UtcNow);
            Directory.CreateDirectory(_root);
            WriteAtomic(GraphPath(id), new JsonObject { ["prompt"] = analysis.Graph!.DeepClone() }.ToJsonString());
            index.RemoveAll(t => t.Id == id);
            index.Add(info);
            WriteAtomic(IndexPath, JsonSerializer.Serialize(index));
            return info;
        }
    }

    public IReadOnlyList<ImportedComfyTemplateInfo> List()
    {
        lock (_gate) return LoadIndex().OrderByDescending(t => t.CreatedUtc).ToList();
    }

    public ImportedComfyTemplateInfo? Get(string id)
    {
        lock (_gate) return LoadIndex().FirstOrDefault(t => t.Id == id);
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            List<ImportedComfyTemplateInfo> index = LoadIndex();
            if (index.RemoveAll(t => t.Id == id) == 0) return false;
            WriteAtomic(IndexPath, JsonSerializer.Serialize(index));
            try { File.Delete(GraphPath(id)); } catch (IOException) { /* index is authoritative */ }
            return true;
        }
    }

    /// <summary>Resolves a stored template for <c>SubmitAndWaitAsync(templatePath, mutate, saveNode, ...)</c>. The overrides are
    /// checked before anything is queued; a bad override throws <see cref="InvalidDataException"/>.</summary>
    internal (string TemplatePath, string SaveNodeId, Action<JsonObject> Mutate) PrepareRun(
        string id, IReadOnlyDictionary<string, JsonNode?> overrides)
    {
        ImportedComfyTemplateInfo info = Get(id) ?? throw new InvalidDataException("Unknown imported workflow.");
        if (!File.Exists(GraphPath(id))) throw new InvalidDataException("The imported workflow file is missing.");
        // Dry-run on a copy so a bad override fails here, not after the job has been admitted.
        var probe = (JsonObject)JsonNode.Parse(File.ReadAllText(GraphPath(id)))!;
        ComfyWorkflowImporter.ApplyOverrides(probe, info.Parameters, overrides);
        return (GraphPath(id), info.SaveNodeId, request => ComfyWorkflowImporter.ApplyOverrides(request, info.Parameters, overrides));
    }

    /// <summary>Template file of a stored workflow, or null when unknown (used for model provenance).</summary>
    internal string? TemplatePathFor(string id) => Get(id) != null && File.Exists(GraphPath(id)) ? GraphPath(id) : null;

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private List<ImportedComfyTemplateInfo> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<List<ImportedComfyTemplateInfo>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* corrupt index: start empty */ }
        return new();
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }
}
