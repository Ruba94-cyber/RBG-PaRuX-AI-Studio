using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Lets a user bring their OWN ComfyUI "API format" workflow instead of being limited to
/// PaRuX's small built-in set (image/video/edit/img2img/upscale). Auto-detects, by node
/// class_type alone, whichever of a prompt-text node, a seed-bearing sampler node, a
/// LoadImage node, and a SaveImage node the graph actually has - each detection is
/// independently optional, and a workflow that leaves one undetected still runs, just always
/// using that node's own baked-in value rather than the caller's. Deliberately does NOT
/// attempt riskier guesses (which of several CLIPTextEncode nodes is "the" prompt when there's
/// more than one, which of several LoadImage nodes is "the" source image) - an ambiguous
/// workflow still imports and runs, it just can't be steered on that particular axis, which is
/// an honest degrade rather than a silent wrong guess. Only SaveImage-based (still image)
/// workflows are supported for now - ComfyWorkflowRunner.SubmitAndWaitAsync's output reading
/// assumes a "images" output key, which video-save nodes (VHS_VideoCombine etc.) don't use.
/// </summary>
public sealed class CustomWorkflowService
{
    internal sealed class StoredWorkflow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? PromptNodeId { get; set; }
        public string? SeedNodeId { get; set; }
        public string? SeedField { get; set; }
        public string SaveNodeId { get; set; } = string.Empty;
        public string? LoadImageNodeId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    internal sealed record RunnableWorkflow(
        string TemplatePath, string SaveNodeId, string? PromptNodeId,
        string? SeedNodeId, string? SeedField, string? LoadImageNodeId);

    /// <summary>Backup &amp; Restore support (2026-08-27 re-audit) - the index alone isn't enough
    /// to restore a working custom workflow, since GetRunnable reads the real ComfyUI graph JSON
    /// from its own per-id file on demand rather than caching it in memory; this bundle carries
    /// both.</summary>
    internal sealed class CustomWorkflowBackupBundle
    {
        public List<StoredWorkflow> Index { get; set; } = new();
        public Dictionary<string, string> Templates { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private List<StoredWorkflow>? _indexCache;

    public CustomWorkflowService() : this(ServerState.StateDirectory) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point this service at an isolated temp directory instead of the real
    /// ServerState.StateDirectory - production code always goes through the parameterless
    /// constructor above (the DI singleton registered in Program.cs).</summary>
    public CustomWorkflowService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string StoreDir => Path.Combine(_stateDirectory, "custom-workflows");
    private string IndexPath => Path.Combine(StoreDir, "index.json");
    private string TemplateFile(string id) => Path.Combine(StoreDir, id + ".json");

    public List<CustomWorkflowInfo> List()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _indexCache!.Select(ToInfo).OrderByDescending(w => w.CreatedUtc).ToList();
        }
    }

    public CustomWorkflowInfo Import(string name, string workflowJson)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A workflow needs a name.");
        if (string.IsNullOrWhiteSpace(workflowJson)) throw new InvalidDataException("The workflow JSON is empty.");

        JsonObject root;
        try { root = JsonNode.Parse(workflowJson) as JsonObject ?? throw new InvalidDataException(); }
        catch (JsonException) { throw new InvalidDataException("That file isn't valid JSON."); }

        // Real ComfyUI "Export (API)" nests the node graph under a top-level "prompt" key; a
        // hand-copied node dict (or some third-party tools) may just be the bare dict itself.
        // Accept both real shapes rather than silently failing on one of them.
        JsonObject nodes = root["prompt"] as JsonObject ?? root;
        if (nodes.Count == 0) throw new InvalidDataException("No nodes found in that workflow.");

        string? saveNodeId = null, promptNodeId = null, seedNodeId = null, seedField = null, loadImageNodeId = null;
        var clipTextEncodeIds = new List<string>();
        var loadImageIds = new List<string>();

        foreach ((string nodeId, JsonNode? nodeValue) in nodes)
        {
            if (nodeValue is not JsonObject node) continue;
            string? classType = node["class_type"]?.GetValue<string>();
            if (classType == null) continue;
            JsonObject? inputs = node["inputs"] as JsonObject;

            switch (classType)
            {
                case "SaveImage":
                    saveNodeId ??= nodeId;
                    break;
                case "CLIPTextEncode":
                    clipTextEncodeIds.Add(nodeId);
                    break;
                case "LoadImage":
                    loadImageIds.Add(nodeId);
                    break;
                case "KSampler" or "KSamplerAdvanced" when inputs != null && inputs.ContainsKey("seed") && seedNodeId == null:
                    seedNodeId = nodeId; seedField = "seed";
                    break;
                case "RandomNoise" when inputs != null && inputs.ContainsKey("noise_seed") && seedNodeId == null:
                    seedNodeId = nodeId; seedField = "noise_seed";
                    break;
            }
        }

        if (saveNodeId == null)
            throw new InvalidDataException("No SaveImage node found - only still-image workflows can be imported right now.");
        // Only unambiguous when exactly one candidate exists - two CLIPTextEncode nodes (a very
        // common positive/negative pair) can't be told apart from class_type alone, so leaving
        // this unset (no prompt override) is safer than guessing which one is "the" prompt.
        if (clipTextEncodeIds.Count == 1) promptNodeId = clipTextEncodeIds[0];
        if (loadImageIds.Count == 1) loadImageNodeId = loadImageIds[0];

        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(TemplateFile(id), root.ToJsonString());

        var stored = new StoredWorkflow
        {
            Id = id, Name = name.Trim(), PromptNodeId = promptNodeId,
            SeedNodeId = seedNodeId, SeedField = seedField, SaveNodeId = saveNodeId,
            LoadImageNodeId = loadImageNodeId, CreatedUtc = DateTime.UtcNow
        };
        lock (_gate)
        {
            EnsureLoaded();
            _indexCache!.Add(stored);
            Save();
        }
        return ToInfo(stored);
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_indexCache!.RemoveAll(w => w.Id == id) > 0) Save();
        }
        try { File.Delete(TemplateFile(id)); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Server-internal detail ComfyWorkflowRunner needs to actually run the workflow -
    /// node ids are never exposed over the public API surface (CustomWorkflowInfo carries only
    /// booleans).</summary>
    internal RunnableWorkflow GetRunnable(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            StoredWorkflow? w = _indexCache!.FirstOrDefault(x => x.Id == id);
            if (w == null) throw new InvalidDataException("Unknown custom workflow id.");
            return new RunnableWorkflow(TemplateFile(id), w.SaveNodeId, w.PromptNodeId, w.SeedNodeId, w.SeedField, w.LoadImageNodeId);
        }
    }

    private static CustomWorkflowInfo ToInfo(StoredWorkflow w) => new()
    {
        Id = w.Id, Name = w.Name, HasPromptControl = w.PromptNodeId != null,
        HasSeedControl = w.SeedNodeId != null, RequiresSourceImage = w.LoadImageNodeId != null,
        CreatedUtc = w.CreatedUtc
    };

    private void EnsureLoaded()
    {
        if (_indexCache != null) return;
        _indexCache = new List<StoredWorkflow>();
        try
        {
            if (File.Exists(IndexPath))
            {
                List<StoredWorkflow>? loaded = JsonSerializer.Deserialize<List<StoredWorkflow>>(File.ReadAllText(IndexPath));
                if (loaded != null) _indexCache = loaded;
            }
        }
        catch
        {
            // A corrupt index falls back to an empty list.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            string temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_indexCache));
            File.Move(temp, IndexPath, true);
        }
        catch
        {
            // Best-effort persistence; the in-memory cache still reflects the request for this session.
        }
    }

    /// <summary>Live snapshot for backup - null if no custom workflow has ever been imported,
    /// matching every other backup-config method's "don't fabricate an empty entry" convention.
    /// Captures the real ComfyUI graph JSON for every indexed workflow, not just the index, since
    /// that's what GetRunnable actually reads at run time.</summary>
    internal CustomWorkflowBackupBundle? ExportForBackup()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_indexCache!.Count == 0) return null;
            var templates = new Dictionary<string, string>();
            foreach (StoredWorkflow w in _indexCache)
            {
                string path = TemplateFile(w.Id);
                if (File.Exists(path)) templates[w.Id] = File.ReadAllText(path);
            }
            return new CustomWorkflowBackupBundle { Index = new List<StoredWorkflow>(_indexCache!), Templates = templates };
        }
    }

    /// <summary>Restores custom workflows from a backup: writes every real per-id template file
    /// back to disk, then updates the live in-memory index cache immediately (so an already-running
    /// server serves the restored list without a restart) and persists the index to disk too.</summary>
    internal void ImportForBackup(CustomWorkflowBackupBundle bundle)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(StoreDir);
            foreach ((string id, string json) in bundle.Templates)
                File.WriteAllText(TemplateFile(id), json);
            _indexCache = new List<StoredWorkflow>(bundle.Index);
            Save();
        }
    }
}
