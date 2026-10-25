using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>One editable value of an imported ComfyUI workflow.</summary>
public sealed record ComfyWorkflowParameter(
    string Key, string NodeId, string Field, string Kind, string ValueType,
    string? Label, string? Default, double? Min, double? Max, string? ModelFolder);

/// <summary>Result of analysing an API-format ComfyUI workflow. Nothing here has been executed.</summary>
public sealed class ComfyWorkflowAnalysis
{
    public bool Ok => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>image | edit | video (null when refused).</summary>
    public string? WorkflowType { get; set; }
    public string? SaveNodeId { get; set; }
    public string? SaveNodeClass { get; set; }
    public bool RequiresSourceImage { get; set; }
    public int NodeCount { get; set; }
    /// <summary>True when the node classes were checked against a live ComfyUI catalogue.</summary>
    public bool VerifiedAgainstComfyUi { get; set; }
    public List<ComfyWorkflowParameter> Parameters { get; } = new();
    /// <summary>The normalised bare graph (node id to node). Null when refused.</summary>
    internal JsonObject? Graph { get; set; }
}

/// <summary>
/// Parses and vets a user-supplied ComfyUI "API format" workflow. Pure and bounded: it only reads JSON, never
/// contacts a URL, opens a path, or runs a node. Refuses structure that is malformed, nodes the running ComfyUI
/// does not have, file inputs that point outside ComfyUI's own folders (traversal, absolute paths, URLs), and
/// node classes that execute code or fetch from the network.
/// </summary>
public static class ComfyWorkflowImporter
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxNodes = 500;
    public const int MaxPromptChars = 8000;

    private static readonly string[] SaveClasses =
        { "SaveImage", "VHS_VideoCombine", "SaveVideo", "SaveAnimatedWEBP", "SaveAnimatedPNG", "SaveWEBM" };
    private static readonly string[] VideoSaveClasses = { "VHS_VideoCombine", "SaveVideo", "SaveAnimatedWEBP", "SaveAnimatedPNG", "SaveWEBM" };

    // Substrings of class names that run code or fetch remote content. Refused regardless of installation.
    // Matched case-insensitively. Core nodes (below) are exempt so SaveAnimatedWEBP / SaveWEBM keep working.
    private static readonly string[] UnsafeClassMarkers =
    {
        "Exec", "Eval", "PythonScript", "ShellCommand", "RunCommand", "Subprocess", "SystemCommand", "Terminal",
        "FromUrl", "LoadImageUrl", "URLLoad", "Download", "LoadVideoPath", "LoadImageFromPath", "LoadImagePath",
        "Http", "Request", "Fetch", "Curl", "Web", "Shell"
    };

    /// <summary>Node classes used by the shipped Assets/config templates plus standard ComfyUI core nodes. Any other class
    /// needs the caller's explicit acknowledgement, even when ComfyUI has it installed.</summary>
    internal static readonly IReadOnlySet<string> CoreNodeClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        // shipped templates
        "CFGGuider", "CLIPLoader", "CLIPTextEncode", "ConditioningZeroOut", "CreateVideo", "EmptyFlux2LatentImage",
        "EmptySD3LatentImage", "Flux2Scheduler", "GetImageSize", "ImageScaleToTotalPixels", "ImageUpscaleWithModel", "KSampler",
        "KSamplerSelect", "LoadImage", "ModelSamplingAuraFlow", "ModelSamplingSD3", "RandomNoise", "ReferenceLatent",
        "SamplerCustomAdvanced", "SaveImage", "SaveVideo", "UNETLoader", "UpscaleModelLoader", "VAEDecode", "VAEEncode",
        "VAELoader", "Wan22ImageToVideoLatent",
        // standard core
        "CheckpointLoaderSimple", "EmptyLatentImage", "KSamplerAdvanced", "LoraLoader", "LoraLoaderModelOnly", "CLIPSetLastLayer",
        "DualCLIPLoader", "ConditioningCombine", "ConditioningConcat", "ConditioningSetTimestepRange", "FluxGuidance", "BasicGuider",
        "BasicScheduler", "VAEDecodeTiled", "VAEEncodeTiled", "VAEEncodeForInpaint", "SetLatentNoiseMask", "LatentUpscale",
        "LatentUpscaleBy", "ImageScale", "ImageScaleBy", "ImageInvert", "ImageBatch", "PreviewImage", "LoadImageMask",
        "SaveAnimatedWEBP", "SaveAnimatedPNG", "SaveWEBM", "EmptyHunyuanLatentVideo", "ImageOnlyCheckpointLoader",
        "ControlNetLoader", "ControlNetApply", "ControlNetApplyAdvanced", "CLIPVisionLoader", "CLIPVisionEncode"
    };

    private static readonly Regex UrlSchemePattern = new(@"^\s*[a-z][a-z0-9+.-]*://", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> FileKeys = new(StringComparer.Ordinal)
    {
        "image", "video", "audio", "mask", "file", "path", "directory", "folder", "filename_prefix", "output_path"
    };
    private static readonly string[] FileKeySuffixes = { "_name", "_path", "_file", "_dir" };

    private static readonly Regex NodeIdPattern = new("^[A-Za-z0-9_.-]{1,32}$", RegexOptions.Compiled);

    internal static ComfyWorkflowAnalysis Analyze(string? workflowJson, ComfyNodeCatalog? catalog = null, bool acknowledgeUnverifiedNodes = false)
    {
        var result = new ComfyWorkflowAnalysis();
        if (string.IsNullOrWhiteSpace(workflowJson)) { result.Errors.Add("The workflow JSON is empty."); return result; }
        if (System.Text.Encoding.UTF8.GetByteCount(workflowJson) > MaxBytes)
        {
            result.Errors.Add("The workflow is larger than " + MaxBytes / 1024 + " KB.");
            return result;
        }

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(workflowJson, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { result.Errors.Add("That file isn't valid JSON."); return result; }
        if (parsed is not JsonObject root) { result.Errors.Add("The workflow must be a JSON object."); return result; }

        if (root["nodes"] is JsonArray && root["links"] is JsonArray)
        {
            result.Errors.Add("This is a ComfyUI UI-format workflow. Export it with Workflow > Export (API) and import that file.");
            return result;
        }
        JsonObject graph = root["prompt"] as JsonObject ?? root;
        if (graph.Count == 0) { result.Errors.Add("No nodes found in that workflow."); return result; }
        if (graph.Count > MaxNodes) { result.Errors.Add("The workflow has more than " + MaxNodes + " nodes."); return result; }
        result.NodeCount = graph.Count;

        ValidateStructure(graph, result);
        if (!result.Ok) return result;

        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, node) in graph) classes[id] = node!["class_type"]!.GetValue<string>();

        foreach (var (id, cls) in classes)
            if (!CoreNodeClasses.Contains(cls) && UnsafeClassMarkers.Any(m => cls.Contains(m, StringComparison.OrdinalIgnoreCase)))
                result.Errors.Add("Node " + id + " (" + cls + ") can run code or fetch remote content and is not allowed.");
        ValidateFileInputs(graph, result);
        ValidateNoUrlValues(graph, result);

        string[] nonCore = classes.Values.Where(c => !CoreNodeClasses.Contains(c)).Distinct(StringComparer.Ordinal).ToArray();
        if (nonCore.Length > 0)
        {
            if (!acknowledgeUnverifiedNodes)
                result.Errors.Add("This workflow uses nodes outside ComfyUI's core set (" + string.Join(", ", nonCore.Take(5))
                    + "). Custom nodes run code inside ComfyUI; acknowledge that you trust them to import this workflow.");
            else
                result.Warnings.Add("Custom nodes acknowledged by you: " + string.Join(", ", nonCore.Take(5)) + ".");
        }

        if (catalog != null)
        {
            result.VerifiedAgainstComfyUi = true;
            foreach (var (id, cls) in classes)
                if (!catalog.NodeClasses.Contains(cls))
                    result.Errors.Add("Node " + id + " uses " + cls + ", which is not installed in ComfyUI (custom node missing).");
            var known = new JsonObject();
            foreach (var (id, node) in graph) if (catalog.NodeClasses.Contains(classes[id])) known[id] = node!.DeepClone();
            foreach (var error in ComfyLivePreflight.Validate(known, catalog))
                if (error.ExpectedFileName != null) result.Warnings.Add(error.Message);
        }
        else
        {
            result.Warnings.Add("ComfyUI was not reachable, so node classes and model files were not checked. They are checked again at run time.");
        }
        if (!result.Ok) return result;

        PickSaveNode(graph, classes, result);
        if (!result.Ok) return result;

        result.RequiresSourceImage = classes.Values.Contains("LoadImage");
        result.WorkflowType = Classify(classes, result);
        ExtractParameters(graph, classes, result);
        result.Graph = (JsonObject)graph.DeepClone();
        return result;
    }

    private static void ValidateStructure(JsonObject graph, ComfyWorkflowAnalysis result)
    {
        foreach (var (id, value) in graph)
        {
            if (!NodeIdPattern.IsMatch(id)) { result.Errors.Add("Invalid node id."); continue; }
            if (value is not JsonObject node) { result.Errors.Add("Node " + id + " is not an object."); continue; }
            if (node["class_type"] is not JsonValue cv || !cv.TryGetValue(out string? cls) || string.IsNullOrWhiteSpace(cls) || cls.Length > 128)
            { result.Errors.Add("Node " + id + " has no valid class_type."); continue; }
            if (node["inputs"] is not JsonObject inputs) { result.Errors.Add("Node " + id + " has no inputs object."); continue; }
            foreach (var (key, input) in inputs)
            {
                if (input is JsonArray link)
                {
                    string? target = link.Count == 2 ? LinkTarget(link) : null;
                    bool ok = target != null && graph.ContainsKey(target)
                        && link[1] is JsonValue slot && slot.TryGetValue(out long s) && s >= 0 && s < 64;
                    if (!ok) result.Errors.Add("Node " + id + " input " + key + " is a malformed or dangling link.");
                }
                else if (input is JsonObject) result.Errors.Add("Node " + id + " input " + key + " has an unsupported object value.");
            }
        }
    }

    internal static bool IsSafeRelativeName(string value)
    {
        if (value.Length == 0 || value.Length > 260) return false;
        if (value.Any(char.IsControl)) return false;
        if (value.Contains("://", StringComparison.Ordinal)) return false;
        if (value[0] is '/' or '\\' or '~') return false;
        if (value.Length >= 2 && value[1] == ':') return false;
        return !value.Split('/', '\\').Any(part => part == ".." || part == ".");
    }

    private static bool IsFileKey(string key) =>
        FileKeys.Contains(key) || FileKeySuffixes.Any(s => key.EndsWith(s, StringComparison.Ordinal));

    private static void ValidateNoUrlValues(JsonObject graph, ComfyWorkflowAnalysis result)
    {
        foreach (var (id, node) in graph)
            foreach (var (key, input) in node!["inputs"]!.AsObject())
                if (input is JsonValue v && v.TryGetValue(out string? text) && text != null && UrlSchemePattern.IsMatch(text))
                    result.Errors.Add("Node " + id + " input " + key + " contains a URL, which is not allowed.");
    }

    private static void ValidateFileInputs(JsonObject graph, ComfyWorkflowAnalysis result)
    {
        foreach (var (id, node) in graph)
            foreach (var (key, input) in node!["inputs"]!.AsObject())
                if (IsFileKey(key) && input is JsonValue v && v.TryGetValue(out string? text) && text != null && !IsSafeRelativeName(text))
                    result.Errors.Add("Node " + id + " input " + key + " points outside ComfyUI's folders (absolute path, URL or '..').");
    }

    private static void PickSaveNode(JsonObject graph, Dictionary<string, string> classes, ComfyWorkflowAnalysis result)
    {
        var saves = classes.Where(c => SaveClasses.Contains(c.Value))
            .OrderBy(c => long.TryParse(c.Key, out long n) ? n : long.MaxValue).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        if (saves.Count == 0)
        {
            result.Errors.Add("No save node (SaveImage, VHS_VideoCombine, SaveVideo, ...) found, so PaRuX could not collect an output.");
            return;
        }
        var chosen = saves[0];
        if (saves.Count > 1) result.Warnings.Add("Several save nodes found; PaRuX collects the output of node " + chosen.Key + ".");
        if (graph[chosen.Key]!["inputs"]!["save_output"] is JsonValue so && so.TryGetValue(out bool keep) && !keep)
            result.Errors.Add("Save node " + chosen.Key + " has save_output off, so it writes only temporary files.");
        result.SaveNodeId = chosen.Key;
        result.SaveNodeClass = chosen.Value;
    }

    private static string Classify(Dictionary<string, string> classes, ComfyWorkflowAnalysis result)
    {
        if (VideoSaveClasses.Contains(result.SaveNodeClass!)
            || classes.Values.Any(c => c.Contains("VideoLatent", StringComparison.Ordinal) || c.StartsWith("Wan", StringComparison.Ordinal)
                || c.StartsWith("LTXV", StringComparison.Ordinal) || c.Contains("ImageToVideo", StringComparison.Ordinal)))
            return "video";
        return result.RequiresSourceImage ? "edit" : "image";
    }

    private static string? LinkTarget(JsonNode? input) =>
        input is JsonArray { Count: 2 } a && a[0] is JsonValue v
            ? (v.TryGetValue(out string? s) ? s : v.TryGetValue(out long n) ? n.ToString() : null)
            : null;

    private static void ExtractParameters(JsonObject graph, Dictionary<string, string> classes, ComfyWorkflowAnalysis result)
    {
        var negatives = new HashSet<string>();
        foreach (var (id, node) in graph)
        {
            if (!classes[id].Contains("Sampler", StringComparison.Ordinal) && !classes[id].Contains("Guider", StringComparison.Ordinal)) continue;
            if (LinkTarget(node!["inputs"]!.AsObject()["negative"]) is { } n) negatives.Add(n);
        }
        foreach (var (id, node) in graph.OrderBy(g => long.TryParse(g.Key, out long n) ? n : long.MaxValue))
        {
            string cls = classes[id];
            foreach (var (field, input) in node!["inputs"]!.AsObject())
            {
                if (input is not JsonValue v) continue;
                string key = id + "." + field;
                string? text = v.TryGetValue(out string? s) ? s : null;
                bool isNumber = v.TryGetValue(out double _) || v.TryGetValue(out long _);
                switch (field)
                {
                    case "text" when cls == "CLIPTextEncode" && text != null:
                        string kind = negatives.Contains(id) ? "negative_prompt" : "prompt";
                        Add(result, key, id, field, kind, "string", kind == "prompt" ? "Prompt" : "Negative prompt", text, null, null, null);
                        break;
                    case "seed" or "noise_seed" when isNumber:
                        Add(result, key, id, field, "seed", "int", "Seed", v.ToJsonString(), 0, long.MaxValue, null); break;
                    case "steps" when isNumber: Add(result, key, id, field, "steps", "int", "Steps", v.ToJsonString(), 1, 200, null); break;
                    case "cfg" when isNumber: Add(result, key, id, field, "cfg", "number", "CFG", v.ToJsonString(), 0, 30, null); break;
                    case "denoise" when isNumber: Add(result, key, id, field, "denoise", "number", "Denoise", v.ToJsonString(), 0, 1, null); break;
                    case "width" when isNumber: Add(result, key, id, field, "width", "int", "Width", v.ToJsonString(), 64, 4096, null); break;
                    case "height" when isNumber: Add(result, key, id, field, "height", "int", "Height", v.ToJsonString(), 64, 4096, null); break;
                    case "length" or "frame_count" or "frames" when isNumber:
                        Add(result, key, id, field, "frames", "int", "Frames", v.ToJsonString(), 1, 1000, null); break;
                    case "image" when cls == "LoadImage" && text != null:
                        Add(result, key, id, field, "source_image", "file", "Source image", text, null, null, "input"); break;
                    case "lora_name" when text != null: Add(result, key, id, field, "lora", "file", "LoRA", text, null, null, "loras"); break;
                    case "strength_model" or "strength_clip" when cls.StartsWith("LoraLoader", StringComparison.Ordinal) && isNumber:
                        Add(result, key, id, field, "lora_strength", "number", "LoRA strength", v.ToJsonString(), -2, 2, null); break;
                    case "ckpt_name" when text != null: Add(result, key, id, field, "model_file", "file", "Checkpoint", text, null, null, "checkpoints"); break;
                    case "unet_name" when text != null: Add(result, key, id, field, "model_file", "file", "Diffusion model", text, null, null, "diffusion_models"); break;
                    case "vae_name" when text != null: Add(result, key, id, field, "model_file", "file", "VAE", text, null, null, "vae"); break;
                    case "clip_name" or "clip_name1" or "clip_name2" when text != null:
                        Add(result, key, id, field, "model_file", "file", "Text encoder", text, null, null, "text_encoders"); break;
                }
            }
        }
    }

    private static void Add(ComfyWorkflowAnalysis r, string key, string nodeId, string field, string kind, string type,
        string label, string? def, double? min, double? max, string? folder) =>
        r.Parameters.Add(new ComfyWorkflowParameter(key, nodeId, field, kind, type, label, def, min, max, folder));

    /// <summary>Applies user overrides (parameter key to value) to a graph or ComfyUI request body in place.
    /// Unknown keys, wrong types, out-of-range numbers and unsafe file names are refused before anything is changed.</summary>
    public static void ApplyOverrides(JsonObject requestOrGraph, IReadOnlyList<ComfyWorkflowParameter> schema,
        IReadOnlyDictionary<string, JsonNode?> overrides)
    {
        ArgumentNullException.ThrowIfNull(requestOrGraph);
        JsonObject graph = requestOrGraph["prompt"] as JsonObject ?? requestOrGraph;
        var planned = new List<(JsonObject Inputs, string Field, JsonNode Value)>();
        foreach (var (key, raw) in overrides)
        {
            ComfyWorkflowParameter p = schema.FirstOrDefault(x => x.Key == key)
                ?? throw new InvalidDataException("Unknown parameter '" + key + "'.");
            if (raw is not JsonValue val) throw new InvalidDataException("Parameter " + key + " needs a plain value.");
            JsonNode value;
            switch (p.ValueType)
            {
                case "string":
                    string str = val.TryGetValue(out string? s0) && s0 != null ? s0 : throw new InvalidDataException(key + " must be text.");
                    if (str.Length > MaxPromptChars) throw new InvalidDataException(key + " is longer than " + MaxPromptChars + " characters.");
                    value = JsonValue.Create(str); break;
                case "file":
                    string name = val.TryGetValue(out string? f) && f != null ? f : throw new InvalidDataException(key + " must be a file name.");
                    if (!IsSafeRelativeName(name)) throw new InvalidDataException(key + " must be a plain file name inside ComfyUI's folders.");
                    value = JsonValue.Create(name); break;
                case "int":
                    if (!TryNumber(val, out double d) || d != Math.Floor(d)) throw new InvalidDataException(key + " must be a whole number.");
                    CheckRange(key, d, p); value = JsonValue.Create((long)d); break;
                default:
                    if (!TryNumber(val, out double dn)) throw new InvalidDataException(key + " must be a number.");
                    CheckRange(key, dn, p); value = JsonValue.Create(dn); break;
            }
            JsonObject inputs = graph[p.NodeId]?["inputs"] as JsonObject
                ?? throw new InvalidDataException("Parameter " + key + " no longer matches the workflow.");
            planned.Add((inputs, p.Field, value));
        }
        foreach (var (inputs, field, value) in planned) inputs[field] = value;
    }

    private static bool TryNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue(out long l)) { d = l; return true; }
        if (v.TryGetValue(out int i)) { d = i; return true; }
        if (v.TryGetValue(out double x) && double.IsFinite(x)) { d = x; return true; }
        if (v.TryGetValue(out float f) && float.IsFinite(f)) { d = f; return true; }
        if (v.TryGetValue(out decimal m)) { d = (double)m; return true; }
        d = 0; return false;
    }

    private static void CheckRange(string key, double d, ComfyWorkflowParameter p)
    {
        if ((p.Min is double lo && d < lo) || (p.Max is double hi && d > hi))
            throw new InvalidDataException(key + " must be between " + p.Min + " and " + p.Max + ".");
    }
}
