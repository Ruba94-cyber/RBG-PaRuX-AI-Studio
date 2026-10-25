using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyWorkflowImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-cwi-" + Guid.NewGuid().ToString("N"));
    public ComfyWorkflowImporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string ImageWorkflow = """
    {"3":{"class_type":"KSampler","inputs":{"seed":5,"steps":20,"cfg":7.0,"denoise":1,"model":["4",0],"positive":["6",0],"negative":["7",0],"latent_image":["5",0]}},
     "4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sdxl.safetensors"}},
     "5":{"class_type":"EmptyLatentImage","inputs":{"width":1024,"height":1024,"batch_size":1}},
     "6":{"class_type":"CLIPTextEncode","inputs":{"text":"a cat","clip":["4",1]}},
     "7":{"class_type":"CLIPTextEncode","inputs":{"text":"blurry","clip":["4",1]}},
     "9":{"class_type":"SaveImage","inputs":{"filename_prefix":"out","images":["3",0]}}}
    """;

    private static ComfyNodeCatalog Catalog(params string[] classes) =>
        ComfyLivePreflight.ParseCatalog("{" + string.Join(",", classes.Select(c => "\"" + c + "\":{\"input\":{\"required\":{}}}")) + "}", null);

    private static readonly string[] AllImage = { "KSampler", "CheckpointLoaderSimple", "EmptyLatentImage", "CLIPTextEncode", "SaveImage" };

    [Fact]
    public void Image_workflow_is_classified_with_typed_parameters()
    {
        var a = ComfyWorkflowImporter.Analyze(ImageWorkflow, Catalog(AllImage));
        Assert.True(a.Ok, string.Join(";", a.Errors));
        Assert.Equal("image", a.WorkflowType);
        Assert.Equal("9", a.SaveNodeId);
        Assert.True(a.VerifiedAgainstComfyUi);
        string[] kinds = a.Parameters.Select(p => p.Kind).ToArray();
        foreach (string k in new[] { "prompt", "negative_prompt", "seed", "steps", "cfg", "width", "height", "model_file" }) Assert.Contains(k, kinds);
        Assert.Equal("negative_prompt", a.Parameters.Single(p => p.Key == "7.text").Kind);
        Assert.Equal("checkpoints", a.Parameters.Single(p => p.Kind == "model_file").ModelFolder);
    }

    [Fact]
    public void Prompt_wrapper_is_accepted_and_ui_format_is_refused_with_guidance()
    {
        Assert.True(ComfyWorkflowImporter.Analyze("{\"prompt\":" + ImageWorkflow + "}").Ok);
        var ui = ComfyWorkflowImporter.Analyze("{\"nodes\":[],\"links\":[]}");
        Assert.False(ui.Ok);
        Assert.Contains("Export (API)", ui.Errors[0]);
    }

    [Fact]
    public void Missing_custom_node_is_refused_when_catalogue_known_and_only_warned_when_not()
    {
        var refused = ComfyWorkflowImporter.Analyze(ImageWorkflow, Catalog("KSampler", "CheckpointLoaderSimple", "EmptyLatentImage", "CLIPTextEncode"));
        Assert.False(refused.Ok);
        Assert.Contains(refused.Errors, e => e.Contains("SaveImage") && e.Contains("not installed"));
        var offline = ComfyWorkflowImporter.Analyze(ImageWorkflow, null);
        Assert.True(offline.Ok);
        Assert.False(offline.VerifiedAgainstComfyUi);
        Assert.NotEmpty(offline.Warnings);
    }

    [Theory]
    [InlineData("\"ckpt_name\":\"../../evil.safetensors\"")]
    [InlineData("\"ckpt_name\":\"C:\\\\Windows\\\\x.safetensors\"")]
    [InlineData("\"ckpt_name\":\"/etc/passwd\"")]
    [InlineData("\"ckpt_name\":\"https://example.com/m.safetensors\"")]
    [InlineData("\"ckpt_name\":\"\\\\\\\\server\\\\share\\\\m.safetensors\"")]
    public void Unsafe_file_inputs_are_refused(string input)
    {
        string wf = ImageWorkflow.Replace("\"ckpt_name\":\"sdxl.safetensors\"", input);
        var a = ComfyWorkflowImporter.Analyze(wf);
        Assert.False(a.Ok);
        Assert.Contains(a.Errors, e => e.Contains("outside ComfyUI"));
    }

    [Fact]
    public void Output_prefix_traversal_and_code_or_url_nodes_are_refused()
    {
        Assert.False(ComfyWorkflowImporter.Analyze(ImageWorkflow.Replace("\"out\"", "\"../../startup/x\"")).Ok);
        string withExec = ImageWorkflow.Replace("\"9\":{", "\"10\":{\"class_type\":\"ExecutePythonCode\",\"inputs\":{}},\"9\":{");
        Assert.Contains(ComfyWorkflowImporter.Analyze(withExec).Errors, e => e.Contains("run code"));
        string withUrl = ImageWorkflow.Replace("\"9\":{", "\"10\":{\"class_type\":\"LoadImageFromUrl\",\"inputs\":{\"url\":\"http://x/y.png\"}},\"9\":{");
        Assert.False(ComfyWorkflowImporter.Analyze(withUrl).Ok);
    }

    [Fact]
    public void Malformed_structure_and_bounds_are_refused()
    {
        Assert.False(ComfyWorkflowImporter.Analyze("not json").Ok);
        Assert.False(ComfyWorkflowImporter.Analyze("{\"1\":{\"inputs\":{}}}").Ok);                       // no class_type
        Assert.False(ComfyWorkflowImporter.Analyze("{\"1\":{\"class_type\":\"X\"}}").Ok);                // no inputs
        Assert.False(ComfyWorkflowImporter.Analyze(ImageWorkflow.Replace("[\"4\",0]", "[\"99\",0]")).Ok); // dangling link
        Assert.False(ComfyWorkflowImporter.Analyze(ImageWorkflow.Replace("[\"4\",0]", "[\"4\"]")).Ok);   // malformed link
        string many = "{" + string.Join(",", Enumerable.Range(1, ComfyWorkflowImporter.MaxNodes + 1)
            .Select(i => "\"" + i + "\":{\"class_type\":\"SaveImage\",\"inputs\":{}}")) + "}";
        Assert.Contains("more than", ComfyWorkflowImporter.Analyze(many).Errors[0]);
        string big = "{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{\"text\":\"" + new string('a', ComfyWorkflowImporter.MaxBytes) + "\"}}}";
        Assert.Contains("larger", ComfyWorkflowImporter.Analyze(big).Errors[0]);
    }

    [Fact]
    public void Video_and_edit_types_are_detected_from_node_classes()
    {
        string video = """
        {"1":{"class_type":"WanImageToVideo","inputs":{"width":832,"height":480,"length":81}},
         "2":{"class_type":"VHS_VideoCombine","inputs":{"frame_rate":16,"filename_prefix":"v","images":["1",0],"save_output":true}}}
        """;
        var v = ComfyWorkflowImporter.Analyze(video, null, true);
        Assert.Equal("video", v.WorkflowType);
        Assert.Equal("VHS_VideoCombine", v.SaveNodeClass);
        Assert.Contains(v.Parameters, p => p.Kind == "frames");
        Assert.False(ComfyWorkflowImporter.Analyze(video.Replace("\"save_output\":true", "\"save_output\":false"), null, true).Ok);

        string edit = ImageWorkflow.Replace("\"9\":{", "\"11\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"in.png\"}},\"9\":{");
        var e = ComfyWorkflowImporter.Analyze(edit);
        Assert.Equal("edit", e.WorkflowType);
        Assert.True(e.RequiresSourceImage);
        Assert.Contains(e.Parameters, p => p.Kind == "source_image");
    }

    [Fact]
    public void Overrides_are_validated_and_applied_atomically()
    {
        var a = ComfyWorkflowImporter.Analyze(ImageWorkflow);
        var req = new JsonObject { ["prompt"] = a.Graph!.DeepClone() };
        ComfyWorkflowImporter.ApplyOverrides(req, a.Parameters, new Dictionary<string, JsonNode?>
        { ["6.text"] = "a dog", ["3.seed"] = 42, ["3.steps"] = 8, ["3.cfg"] = 3.5 });
        Assert.Equal("a dog", (string)req["prompt"]!["6"]!["inputs"]!["text"]!);
        Assert.Equal(42L, (long)req["prompt"]!["3"]!["inputs"]!["seed"]!);

        string before = req.ToJsonString();
        foreach (var bad in new Dictionary<string, JsonNode?>[]
        {
            new() { ["6.text"] = "ok", ["3.steps"] = 9999 },
            new() { ["3.steps"] = 1.5 },
            new() { ["nope.field"] = 1 },
            new() { ["4.ckpt_name"] = "../x.safetensors" },
            new() { ["6.text"] = new string('a', ComfyWorkflowImporter.MaxPromptChars + 1) },
            new() { ["5.width"] = "wide" },
        })
            Assert.Throws<InvalidDataException>(() => ComfyWorkflowImporter.ApplyOverrides(req, a.Parameters, bad));
        Assert.Equal(before, req.ToJsonString()); // nothing half-applied
    }

    [Fact]
    public void Service_stores_named_template_never_takes_builtin_names_and_prepares_run()
    {
        var svc = new ComfyWorkflowImportService(_dir);
        var info = svc.Save("My SDXL", ImageWorkflow, Catalog(AllImage));
        Assert.Equal("user-my-sdxl", info.Id);
        Assert.Throws<InvalidDataException>(() => svc.Save("z-image-smoke", ImageWorkflow, null));
        Assert.Throws<InvalidDataException>(() => svc.Save("Upscale", ImageWorkflow, null));
        Assert.Throws<InvalidDataException>(() => svc.Save("my sdxl", ImageWorkflow, null)); // needs explicit replace
        svc.Save("my sdxl", ImageWorkflow, null, replaceOwnTemplate: true);
        Assert.Throws<InvalidDataException>(() => svc.Save("bad", "{\"1\":{\"class_type\":\"ExecCode\",\"inputs\":{}}}", null));
        Assert.Single(svc.List());

        var (path, saveNode, mutate) = svc.PrepareRun(info.Id, new Dictionary<string, JsonNode?> { ["6.text"] = "hello", ["3.seed"] = 7 });
        Assert.Equal("9", saveNode);
        var request = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        mutate(request); // same shape SubmitAndWaitAsync hands to its mutate callback
        Assert.Equal("hello", (string)request["prompt"]!["6"]!["inputs"]!["text"]!);
        Assert.Throws<InvalidDataException>(() => svc.PrepareRun(info.Id, new Dictionary<string, JsonNode?> { ["3.steps"] = -1 }));

        Assert.True(new ComfyWorkflowImportService(_dir).Delete(info.Id));
        Assert.Empty(svc.List());
    }
}
