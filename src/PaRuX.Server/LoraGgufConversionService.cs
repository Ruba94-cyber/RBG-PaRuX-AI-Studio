using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Adapter Manager tooling for LoRA conversion and merge. The Python interpreter, uv executable,
/// venv and installed packages are all owned by PaRuX through <see cref="ManagedPythonRuntime"/>;
/// this service never probes PATH and never falls back to python/python3 installed by the user.
/// llama.cpp's conversion scripts remain vendored under llama-cpp-conversion/ and the merge script
/// is PaRuX-owned source written into the writable runtime directory.
/// </summary>
public sealed class LoraGgufConversionService
{
    public static readonly IReadOnlyList<string> KnownOutputTypes = new[] { "f32", "f16", "bf16", "q8_0", "auto" };

    private readonly string _runtimeRoot;
    private readonly string _scriptsDirectory;

    public LoraGgufConversionService() : this(DefaultRuntimeRoot, DefaultScriptsDirectory) { }

    /// <summary>Test seam for an isolated runtime and vendored-script directory.</summary>
    public LoraGgufConversionService(string runtimeRoot, string scriptsDirectory)
    {
        _runtimeRoot = runtimeRoot;
        _scriptsDirectory = scriptsDirectory;
    }

    private static string DefaultRuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "lora-conversion");

    private static string DefaultScriptsDirectory
    {
        get
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "llama-cpp-conversion");
            if (Directory.Exists(candidate)) return candidate;

            string? probe = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && probe != null; i++)
            {
                string repoCandidate = Path.Combine(probe, "llama-cpp-conversion");
                if (Directory.Exists(repoCandidate)) return repoCandidate;
                probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
            }
            return candidate;
        }
    }

    private string VenvDirectory => Path.Combine(_runtimeRoot, ".venv");
    private string VenvPython => ManagedPythonRuntime.VenvPython(VenvDirectory);
    private string ConvertScriptPath => Path.Combine(_scriptsDirectory, "convert_lora_to_gguf.py");
    private string MergeScriptPath => Path.Combine(_runtimeRoot, "merge_lora.py");

    public bool ScriptsAvailable => File.Exists(ConvertScriptPath);

    public async Task<LoraConversionEngineStatus> GetEngineStatusAsync(CancellationToken ct)
    {
        if (!ScriptsAvailable) return new LoraConversionEngineStatus { ScriptsAvailable = false, Installed = false };
        if (!File.Exists(VenvPython)) return new LoraConversionEngineStatus { ScriptsAvailable = true, Installed = false };

        try
        {
            (int exitCode, string stdout, _) = await RunCapturedAsync(VenvPython,
                new[] { "-c", "import torch,gguf,peft;print(torch.__version__)" }, ct, TimeSpan.FromSeconds(30));
            return new LoraConversionEngineStatus
            {
                ScriptsAvailable = true,
                Installed = true,
                DependenciesVerified = exitCode == 0,
                TorchVersion = exitCode == 0 ? stdout.Trim() : null
            };
        }
        catch
        {
            return new LoraConversionEngineStatus { ScriptsAvailable = true, Installed = true, DependenciesVerified = false };
        }
    }

    /// <summary>
    /// Creates the PaRuX-managed venv and installs the CPU-side conversion dependencies through uv.
    /// No system Python, py.exe, pip executable or PATH fallback is accepted.
    /// </summary>
    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        if (!ScriptsAvailable)
            throw new InvalidOperationException("The bundled LoRA-to-GGUF conversion scripts were not found next to the server. Reinstall PaRuX.");

        Directory.CreateDirectory(_runtimeRoot);
        TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
        TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
        if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);

        await ManagedPythonRuntime.EnsureVenvAsync(
            VenvDirectory, ManagedPythonRuntime.DefaultPythonVersion, ct).ConfigureAwait(false);
        var log = new System.Text.StringBuilder();
        log.AppendLine(torchPlan.Reason);
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvDirectory,
            torchPlan.PipArguments,
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvDirectory,
            new[] { "gguf", "transformers", "sentencepiece", "protobuf", "peft" },
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        return log.ToString();
    }

    public async Task<string> ConvertAsync(
        string adapterPath,
        string baseModelId,
        string outputGgufPath,
        string outtype,
        CancellationToken ct,
        string? baseModelConfigDir = null)
    {
        // Arguments first, environment second. A blank base model id or an unknown outtype is wrong
        // whether or not the Python tooling happens to be installed, and reporting "the engine is
        // not installed" for it sends the user off to fix the wrong thing - which is also why these
        // checks used to pass only on a machine that already had the venv.
        if (!Directory.Exists(adapterPath))
            throw new InvalidDataException("The adapter's PEFT directory does not exist: " + adapterPath);
        if (string.IsNullOrWhiteSpace(baseModelId))
            throw new InvalidDataException("The adapter has no recorded base model id to convert against.");
        if (!KnownOutputTypes.Contains(outtype))
            throw new InvalidDataException("Unknown output type \"" + outtype + "\". Known types: " + string.Join(", ", KnownOutputTypes) + ".");
        if (!string.IsNullOrWhiteSpace(baseModelConfigDir) && !Directory.Exists(baseModelConfigDir))
            throw new InvalidDataException("The given base model config directory does not exist: " + baseModelConfigDir);

        if (!ScriptsAvailable)
            throw new InvalidOperationException("The bundled LoRA-to-GGUF conversion scripts were not found next to the server. Reinstall PaRuX.");
        if (!File.Exists(VenvPython))
            throw new InvalidOperationException("The LoRA-to-GGUF conversion engine is not installed on this machine yet.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputGgufPath))!);
        List<string> args = BuildConvertArguments(
            ConvertScriptPath, adapterPath, baseModelId, outputGgufPath, outtype, baseModelConfigDir);

        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(
            VenvPython, args, ct, TimeSpan.FromMinutes(10));
        string log = stdout + stderr;
        if (exitCode != 0 || !File.Exists(outputGgufPath))
            throw new InvalidOperationException("LoRA-to-GGUF conversion failed:\n" + log);
        return log;
    }

    internal static List<string> BuildConvertArguments(
        string convertScriptPath,
        string adapterPath,
        string baseModelId,
        string outputGgufPath,
        string outtype,
        string? baseModelConfigDir = null)
    {
        var args = new List<string> { convertScriptPath, "--outfile", outputGgufPath, "--outtype", outtype };
        if (!string.IsNullOrWhiteSpace(baseModelConfigDir)) args.AddRange(new[] { "--base", baseModelConfigDir });
        else args.AddRange(new[] { "--base-model-id", baseModelId });
        args.Add(adapterPath);
        return args;
    }

    public async Task<string> MergeAsync(
        string adapterPath,
        string baseModelId,
        string outputDirectory,
        CancellationToken ct,
        string? baseModelDir = null)
    {
        // Same ordering rule as ConvertAsync: what the caller passed is checked before what the
        // machine has installed.
        if (!Directory.Exists(adapterPath))
            throw new InvalidDataException("The adapter's PEFT directory does not exist: " + adapterPath);
        if (string.IsNullOrWhiteSpace(baseModelId))
            throw new InvalidDataException("The adapter has no recorded base model id to merge against.");
        if (!string.IsNullOrWhiteSpace(baseModelDir) && !Directory.Exists(baseModelDir))
            throw new InvalidDataException("The given base model directory does not exist: " + baseModelDir);

        if (!File.Exists(VenvPython))
            throw new InvalidOperationException("The LoRA tooling engine is not installed on this machine yet.");

        Directory.CreateDirectory(_runtimeRoot);
        File.WriteAllText(MergeScriptPath, MergeScriptSource);
        Directory.CreateDirectory(outputDirectory);
        List<string> args = BuildMergeArguments(
            MergeScriptPath, adapterPath, baseModelId, outputDirectory, baseModelDir);

        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(
            VenvPython, args, ct, TimeSpan.FromMinutes(30));
        string log = stdout + stderr;
        if (exitCode != 0 || !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidOperationException("LoRA merge failed:\n" + log);
        return log;
    }

    internal static List<string> BuildMergeArguments(
        string mergeScriptPath,
        string adapterPath,
        string baseModelId,
        string outputDirectory,
        string? baseModelDir = null) => new()
    {
        mergeScriptPath,
        "--base", string.IsNullOrWhiteSpace(baseModelDir) ? baseModelId : baseModelDir,
        "--outdir", outputDirectory,
        adapterPath,
    };

    private const string MergeScriptSource = @"
import argparse
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel

def main():
    parser = argparse.ArgumentParser(description='Merge a PEFT LoRA adapter into its base model weights.')
    parser.add_argument('adapter_path', help='directory containing the PEFT adapter (adapter_config.json + weights)')
    parser.add_argument('--base', required=True, help='local directory or HF hub id for the base model')
    parser.add_argument('--outdir', required=True, help='directory to write the merged HF-format model to')
    parser.add_argument('--trust-remote-code', action='store_true')
    args = parser.parse_args()

    print(f'Loading base model: {args.base}', flush=True)
    base_model = AutoModelForCausalLM.from_pretrained(args.base, trust_remote_code=args.trust_remote_code)
    tokenizer = AutoTokenizer.from_pretrained(args.base, trust_remote_code=args.trust_remote_code)

    print(f'Loading adapter: {args.adapter_path}', flush=True)
    merged = PeftModel.from_pretrained(base_model, args.adapter_path)

    print('Merging adapter weights into the base model...', flush=True)
    merged = merged.merge_and_unload()

    print(f'Saving merged model to: {args.outdir}', flush=True)
    merged.save_pretrained(args.outdir, safe_serialization=True)
    tokenizer.save_pretrained(args.outdir)
    print('Done.', flush=True)

if __name__ == '__main__':
    main()
";

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        ManagedPythonRuntime.ApplyEnvironment(psi);

        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The PaRuX LoRA tooling process timed out.");
        }
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
