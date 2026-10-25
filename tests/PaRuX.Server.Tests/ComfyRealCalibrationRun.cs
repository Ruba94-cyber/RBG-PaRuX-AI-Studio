using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-GPU measurement driver. Does nothing unless PARUX_COMFY_REAL_ROOT is set, so the normal suite stays hermetic.
/// PARUX_COMFY_REAL_ROOT = folder holding comfy/ (ComfyUI code copy), venv/ (uv env) and data/ (isolated output).
/// PARUX_COMFY_REAL_MODE = "single" (default-flag smoke), "plain" (ComfyUI with no tuning flags) or "calibrate".
/// Models are reached read-only through extra_model_paths (PARUX_COMFY_REAL_MODELS = the ComfyUI folder that owns them).
/// </summary>
[Trait("Category", "RealGpu")]
public sealed class ComfyRealCalibrationRun
{
    [Fact]
    public async Task Real_run_on_this_pc()
    {
        string? root = Environment.GetEnvironmentVariable("PARUX_COMFY_REAL_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        string mode = Environment.GetEnvironmentVariable("PARUX_COMFY_REAL_MODE") ?? "single";
        string models = Environment.GetEnvironmentVariable("PARUX_COMFY_REAL_MODELS")!;
        string workflow = Environment.GetEnvironmentVariable("PARUX_COMFY_REAL_WORKFLOW")!;
        string outFile = Environment.GetEnvironmentVariable("PARUX_COMFY_REAL_OUT") ?? Path.Combine(root, "result-" + mode + ".json");
        string yaml = Path.Combine(root, "extra_model_paths.yaml");
        string baseDir = models.Replace('\\', '/').TrimEnd('/') + "/";
        File.WriteAllText(yaml,
            "parux_readonly:\n  base_path: " + baseDir + "\n  diffusion_models: models/diffusion_models/\n  text_encoders: models/text_encoders/\n  vae: models/vae/\n");

        var harness = new ComfyCalibrationHarness(new ComfyHarnessOptions
        {
            PythonExe = Path.Combine(root, "venv", "Scripts", "python.exe"),
            MainPy = Path.Combine(root, "comfy", "main.py"),
            WorkingDirectory = Path.Combine(root, "comfy"),
            DataRoot = Path.Combine(root, "data"),
            ModelRoots = new[] { Path.Combine(models, "models") },
            ExtraLaunchArguments = new[] { "--extra-model-paths-config", yaml },
            WorkflowTemplatePath = workflow
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(55));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        if (mode == "calibrate")
        {
            string state = Path.Combine(root, "state");
            Directory.CreateDirectory(state);
            var opt = new ComfyMediaOptimizer(harness, new ComfyOptimizationStore(state), new LocalBenchmarkHistoryStore(state));
            ComfyOptimizationRecord r = await opt.RunAsync(force: true, cts.Token);
            File.WriteAllText(outFile, JsonSerializer.Serialize(r, json));
            return;
        }

        ComfyEngineSnapshot snap = await harness.InspectAsync(cts.Token);
        IReadOnlyList<string> args = mode == "plain" ? Array.Empty<string>() : ComfyLaunchPlanner.Legacy(snap.Caps).Arguments;
        ComfyCandidateRun run = await harness.RunAsync(args, ComfyMediaOptimizer.WarmRuns, cts.Token);
        File.WriteAllText(outFile, JsonSerializer.Serialize(new { snapshot = snap, args, run }, json));
    }
}
