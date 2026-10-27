using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyCalibrationWorkflowTests
{
    private static ComfyCalibrationHarness Harness(string root, string template) => new(new ComfyHarnessOptions
    {
        PythonExe = "python", MainPy = "main.py", WorkingDirectory = root, DataRoot = Path.Combine(root, "data"), WorkflowTemplatePath = template
    });

    private static string Template(string root)
    {
        string path = Path.Combine(root, "wf.json");
        File.WriteAllText(path, "{\"prompt\":{\"1\":{\"class_type\":\"UNETLoader\",\"inputs\":{\"unet_name\":\"z_image_turbo_nvfp4.safetensors\"}}}}");
        return path;
    }

    [Fact]
    public void Calibration_workflow_uses_the_installed_zimage_precision_when_the_template_file_is_absent()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "models", "diffusion_models"));
            File.WriteAllBytes(Path.Combine(root, "models", "diffusion_models", "z_image_turbo_bf16.safetensors"), new byte[8]);
            JsonObject wf = Harness(root, Template(root)).LoadWorkflow();
            Assert.Equal("z_image_turbo_bf16.safetensors", wf["prompt"]!["1"]!["inputs"]!["unet_name"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Calibration_workflow_keeps_the_template_file_when_nothing_is_installed()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            JsonObject wf = Harness(root, Template(root)).LoadWorkflow();
            Assert.Equal("z_image_turbo_nvfp4.safetensors", wf["prompt"]!["1"]!["inputs"]!["unet_name"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
