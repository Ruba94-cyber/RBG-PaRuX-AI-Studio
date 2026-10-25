using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local image-to-3D draft mesh generation in a PaRuX-managed Python environment outside the repo.
/// No system Python or user-installed uv is required.
/// </summary>
public sealed class ThreedService
{
    private static string RuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "3d-studio");

    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string ScriptPath => Path.Combine(RuntimeRoot, "parux_3d.py");
    private static string LastErrorPath => Path.Combine(RuntimeRoot, "last-install-error.txt");

    public async Task<ThreedEngineStatus> GetStatusAsync(CancellationToken ct)
    {
        string? lastError = File.Exists(LastErrorPath) ? await File.ReadAllTextAsync(LastErrorPath, ct) : null;
        if (!File.Exists(VenvPython)) return new ThreedEngineStatus { Installed = false, LastError = lastError };
        try
        {
            (int exitCode, string stdout, _) = await RunCapturedAsync(VenvPython,
                "-c \"import torch, transformers, trimesh; print('PARUX_STATUS:' + torch.__version__); print('PARUX_CUDA:' + str(torch.cuda.is_available()))\"",
                ct, TimeSpan.FromSeconds(60));
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? versionLine = lines.FirstOrDefault(l => l.StartsWith("PARUX_STATUS:"));
            string? cudaLine = lines.FirstOrDefault(l => l.StartsWith("PARUX_CUDA:"));
            bool installed = exitCode == 0 && versionLine != null && cudaLine != null;
            return new ThreedEngineStatus
            {
                Installed = installed,
                TorchVersion = installed ? versionLine!.Substring("PARUX_STATUS:".Length) : null,
                CudaVerified = installed && cudaLine == "PARUX_CUDA:True",
                LastError = installed ? null : lastError
            };
        }
        catch
        {
            return new ThreedEngineStatus { Installed = false, LastError = lastError };
        }
    }

    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        var log = new System.Text.StringBuilder();
        try
        {
            TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
            TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
            if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);

            await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct)
                .ConfigureAwait(false);
            log.AppendLine(torchPlan.Reason);
            log.Append(await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot,
                torchPlan.PipArguments,
                ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
            log.Append(await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot,
                new[] { "transformers", "trimesh", "pillow", "numpy", "scipy" },
                ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false));

            try { File.Delete(LastErrorPath); } catch { }
            return log.ToString();
        }
        catch (Exception ex)
        {
            string detail = Trim(ex.Message);
            try { await File.WriteAllTextAsync(LastErrorPath, detail, ct); } catch { }
            throw;
        }
    }

    public async Task<ThreedGenerateResponse> GenerateAsync(ThreedGenerateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceImageBase64)) throw new InvalidDataException("sourceImageBase64 is required.");
        if (!File.Exists(VenvPython)) throw new InvalidOperationException("The 3D Studio engine is not installed on this machine yet.");
        WriteScriptIfMissing();

        byte[] sourceBytes;
        try { sourceBytes = Convert.FromBase64String(request.SourceImageBase64); }
        catch (FormatException) { throw new InvalidDataException("sourceImageBase64 is not valid Base64 image data."); }
        if (sourceBytes.Length == 0) throw new InvalidDataException("sourceImageBase64 decoded to an empty image payload.");

        string extension;
        try { extension = Path.GetExtension(request.FileName); }
        catch (ArgumentException) { extension = string.Empty; }
        extension = extension.ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) extension = ".png";

        string imagePath = Path.Combine(Path.GetTempPath(), "parux-3d-src-" + Guid.NewGuid().ToString("N") + extension);
        string outputPath = Path.Combine(Path.GetTempPath(), "parux-3d-out-" + Guid.NewGuid().ToString("N") + ".glb");
        await File.WriteAllBytesAsync(imagePath, sourceBytes, ct);
        try
        {
            (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
                "\"" + ScriptPath + "\" \"" + imagePath + "\" \"" + outputPath + "\" " + request.Seed,
                ct, TimeSpan.FromMinutes(4));
            if (exitCode != 0)
            {
                string detail = Trim(stderr.Length > 0 ? stderr : stdout);
                try { await File.WriteAllTextAsync(LastErrorPath, detail, ct); } catch { }
                throw new InvalidOperationException("3D generation failed: " + detail);
            }
            if (!File.Exists(outputPath)) throw new InvalidOperationException("The 3D engine produced no output file.");

            JsonElement result = ParseLastJsonObject(stdout);
            byte[] bytes = await File.ReadAllBytesAsync(outputPath, ct);
            return new ThreedGenerateResponse
            {
                FileName = "model_" + Guid.NewGuid().ToString("N")[..8] + ".glb",
                MediaType = "model/gltf-binary",
                Base64Data = Convert.ToBase64String(bytes),
                DurationSeconds = result.TryGetProperty("duration", out JsonElement dur) ? dur.GetDouble() : 0
            };
        }
        finally
        {
            try { File.Delete(imagePath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }

    private static JsonElement ParseLastJsonObject(string output)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate = line.Trim();
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}')) continue;
            try { return JsonDocument.Parse(candidate).RootElement.Clone(); } catch (JsonException) { }
        }
        throw new InvalidDataException("The 3D engine returned an unreadable result.");
    }

    private static void WriteScriptIfMissing()
    {
        Directory.CreateDirectory(RuntimeRoot);
        File.WriteAllText(ScriptPath, ScriptSource);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan timeout,
        string? workingDirectory = null, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        if (env != null) foreach (KeyValuePair<string, string> kv in env) psi.Environment[kv.Key] = kv.Value;
        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 2000 ? value : "…" + value[^2000..];
    }

    private const string ScriptSource = @"
import sys, json, time
import numpy as np
from PIL import Image
import torch
from transformers import pipeline
import trimesh

image_path = sys.argv[1]
output_path = sys.argv[2]

start = time.monotonic()
image = Image.open(image_path).convert('RGB')

max_dim = 512
w, h = image.size
scale = min(1.0, max_dim / max(w, h))
if scale < 1.0:
    image = image.resize((max(1, int(w * scale)), max(1, int(h * scale))))
w, h = image.size

device = 0 if torch.cuda.is_available() else -1
depth_pipe = pipeline(task='depth-estimation', model='depth-anything/Depth-Anything-V2-Small-hf', device=device)
result = depth_pipe(image)
depth = np.array(result['depth'], dtype=np.float32)
depth_range = depth.max() - depth.min()
depth = (depth - depth.min()) / depth_range if depth_range > 1e-6 else np.zeros_like(depth)

step = max(1, w // 400)
xs = np.arange(0, w, step)
ys = np.arange(0, h, step)
gw, gh = len(xs), len(ys)
scale_dim = max(w, h)
relief_strength = 0.35

vertices = np.zeros((gh * gw, 3), dtype=np.float32)
uvs = np.zeros((gh * gw, 2), dtype=np.float32)
for j, y in enumerate(ys):
    for i, x in enumerate(xs):
        z = float(depth[min(int(y), h - 1), min(int(x), w - 1)]) * relief_strength
        idx = j * gw + i
        vertices[idx] = [(x - w / 2) / scale_dim, -(y - h / 2) / scale_dim, z]
        uvs[idx] = [x / w, 1 - y / h]

faces = []
for j in range(gh - 1):
    for i in range(gw - 1):
        a = j * gw + i
        b = j * gw + i + 1
        c = (j + 1) * gw + i
        d = (j + 1) * gw + i + 1
        faces.append([a, b, c])
        faces.append([b, d, c])
faces = np.array(faces, dtype=np.int64)

material = trimesh.visual.material.PBRMaterial(baseColorTexture=image)
visual = trimesh.visual.TextureVisuals(uv=uvs, material=material)
mesh = trimesh.Trimesh(vertices=vertices, faces=faces, visual=visual, process=False)
mesh.export(output_path)

elapsed = time.monotonic() - start
print(json.dumps({'duration': elapsed}))
";
}
