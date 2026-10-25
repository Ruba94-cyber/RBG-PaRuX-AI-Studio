using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Optional decompiler adapters used by the local Binary Analysis agent. The inspected binary
    /// is never changed. ILSpy writes decompiled C# to stdout; Ghidra uses an ephemeral project and
    /// script under the OS temp directory, exports decompiled C, then deletes that project.
    /// External tools remain user-managed and are never silently installed or updated by PaRuX.
    /// </summary>
    public static class BinaryDecompilerTools
    {
        public const int MaxOutputCharacters = 60_000;

        public sealed class DecompileResult
        {
            public string ToolId { get; set; } = "none";
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = string.Empty;
            public string? Diagnostic { get; set; }
        }

        public static async Task<DecompileResult> DecompileAsync(
            string workspaceRoot,
            string relativePath,
            string? preferredTool,
            CancellationToken ct)
        {
            string target = BinaryWorkspacePathPolicy.ResolveSafeFile(workspaceRoot, relativePath);
            BinaryAnalysisTools.BinaryAnalysisResult analysis = await BinaryAnalysisTools
                .AnalyzeAsync(workspaceRoot, relativePath, ct).ConfigureAwait(false);

            string? ilspy = await FindExecutableAsync("ilspycmd.exe", ct).ConfigureAwait(false)
                ?? await FindExecutableAsync("ilspycmd", ct).ConfigureAwait(false);
            string? ghidra = await FindGhidraHeadlessAsync(ct).ConfigureAwait(false);

            string preference = preferredTool?.Trim().ToLowerInvariant() ?? string.Empty;
            if ((preference.Length == 0 || preference is "ilspy" or "ilspycmd")
                && analysis.IsManagedDotNet && ilspy != null)
            {
                return await DecompileWithIlSpyAsync(ilspy, target, workspaceRoot, ct).ConfigureAwait(false);
            }

            if ((preference.Length == 0 || preference is "ghidra" or "ghidra-headless") && ghidra != null)
            {
                return await DecompileWithGhidraAsync(ghidra, target, ct).ConfigureAwait(false);
            }

            if (analysis.IsManagedDotNet && ilspy == null && preference.Length == 0)
            {
                return new DecompileResult
                {
                    Diagnostic = "The target is a managed .NET assembly, but ilspycmd was not found. Install ILSpy/ilspycmd or configure it on PATH. Ghidra Headless was also unavailable."
                };
            }

            return new DecompileResult
            {
                Diagnostic = preference.Length > 0
                    ? "The requested decompiler '" + preferredTool + "' was not found or is incompatible with this target."
                    : "No supported decompiler was found. PaRuX can use ilspycmd for managed .NET assemblies and Ghidra analyzeHeadless for native binaries."
            };
        }

        public static async Task<string> RenderStatusAsync(CancellationToken ct)
        {
            string? ilspy = await FindExecutableAsync("ilspycmd.exe", ct).ConfigureAwait(false)
                ?? await FindExecutableAsync("ilspycmd", ct).ConfigureAwait(false);
            string? ghidra = await FindGhidraHeadlessAsync(ct).ConfigureAwait(false);
            return "- ILSpy command line: " + (ilspy ?? "not found") + "\n"
                 + "- Ghidra analyzeHeadless: " + (ghidra ?? "not found");
        }

        private static async Task<DecompileResult> DecompileWithIlSpyAsync(
            string executable,
            string target,
            string workingDirectory,
            CancellationToken ct)
        {
            (int exitCode, string output) = await RunProcessAsync(
                executable,
                workingDirectory,
                new[] { "--disable-updatecheck", target },
                TimeSpan.FromMinutes(5),
                ct).ConfigureAwait(false);

            return new DecompileResult
            {
                ToolId = "ilspycmd",
                ExitCode = exitCode,
                Output = Truncate(output, MaxOutputCharacters),
                Diagnostic = exitCode == 0 ? null : "ILSpy returned a non-zero exit code."
            };
        }

        private static async Task<DecompileResult> DecompileWithGhidraAsync(
            string analyzeHeadlessBat,
            string target,
            CancellationToken ct)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "parux-ghidra-" + Guid.NewGuid().ToString("N"));
            string projectRoot = Path.Combine(tempRoot, "project");
            string scriptRoot = Path.Combine(tempRoot, "scripts");
            string outputPath = Path.Combine(tempRoot, "decompiled.txt");
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(scriptRoot);
            string scriptPath = Path.Combine(scriptRoot, "PaRuXExportDecompilation.java");
            await File.WriteAllTextAsync(scriptPath, GhidraExportScript, new UTF8Encoding(false), ct).ConfigureAwait(false);

            try
            {
                string projectName = "PaRuX_" + Guid.NewGuid().ToString("N");
                var args = new List<string>
                {
                    projectRoot,
                    projectName,
                    "-import", target,
                    "-analysisTimeoutPerFile", "180",
                    "-scriptPath", scriptRoot,
                    "-postScript", "PaRuXExportDecompilation.java", outputPath,
                    "-deleteProject"
                };

                (int exitCode, string console) = await RunBatchAsync(
                    analyzeHeadlessBat, args, TimeSpan.FromMinutes(12), ct).ConfigureAwait(false);

                string decompiled = File.Exists(outputPath)
                    ? await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false)
                    : string.Empty;
                string combined = string.IsNullOrWhiteSpace(decompiled)
                    ? console
                    : decompiled + (string.IsNullOrWhiteSpace(console) ? string.Empty : "\n\n[ghidra console]\n" + console);

                return new DecompileResult
                {
                    ToolId = "ghidra-headless",
                    ExitCode = exitCode,
                    Output = Truncate(combined, MaxOutputCharacters),
                    Diagnostic = exitCode == 0 && !string.IsNullOrWhiteSpace(decompiled)
                        ? null
                        : "Ghidra did not produce a complete decompilation export; inspect its console diagnostics."
                };
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        private static async Task<string?> FindExecutableAsync(string fileName, CancellationToken ct)
        {
            (int exit, string output) = await RunProcessAsync(
                "where.exe", Environment.CurrentDirectory, new[] { fileName }, TimeSpan.FromSeconds(5), ct)
                .ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(File.Exists);
        }

        private static async Task<string?> FindGhidraHeadlessAsync(CancellationToken ct)
        {
            string? onPath = await FindExecutableAsync("analyzeHeadless.bat", ct).ConfigureAwait(false);
            if (onPath != null) return onPath;

            string? ghidraHome = Environment.GetEnvironmentVariable("GHIDRA_HOME");
            if (!string.IsNullOrWhiteSpace(ghidraHome))
            {
                string candidate = Path.Combine(ghidraHome, "support", "analyzeHeadless.bat");
                if (File.Exists(candidate)) return candidate;
            }

            string[] commonRoots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ghidra"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ghidra")
            };
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string root in commonRoots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                try
                {
                    string? candidate = Directory.EnumerateFiles(root, "analyzeHeadless.bat", options).FirstOrDefault();
                    if (candidate != null) return candidate;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        private static async Task<(int ExitCode, string Output)> RunBatchAsync(
            string batchPath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            string command = QuoteCmd(batchPath) + " " + string.Join(" ", arguments.Select(QuoteCmd));
            return await RunProcessAsync(
                Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Environment.CurrentDirectory,
                new[] { "/d", "/s", "/c", command },
                timeout,
                ct).ConfigureAwait(false);
        }

        private static string QuoteCmd(string value)
        {
            if (value.Contains('"'))
                throw new InvalidOperationException("Tool path contains an unsupported quote character.");
            // Percent variables expand even inside quoted cmd.exe arguments. Doubling '%' keeps a
            // user-controlled workspace/target path literal. Delayed expansion is not enabled.
            return "\"" + value.Replace("%", "%%", StringComparison.Ordinal) + "\"";
        }

        private static async Task<(int ExitCode, string Output)> RunProcessAsync(
            string executable,
            string workingDirectory,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (string arg in arguments) psi.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = psi };
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (-1, "Decompiler timed out after "
                        + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " seconds and was terminated.");
                }

                string output = await stdout.ConfigureAwait(false);
                string error = await stderr.ConfigureAwait(false);
                return (process.ExitCode,
                    (output + (string.IsNullOrWhiteSpace(error) ? string.Empty : "\n[stderr]\n" + error)).Trim());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (-1, "Could not execute decompiler: " + ex.Message);
            }
        }

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text.Length <= max ? text : text[..max] + "\n… (decompiler output truncated by PaRuX)";
        }

        private const string GhidraExportScript = @"import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;

public class PaRuXExportDecompilation extends GhidraScript {
    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            throw new IllegalArgumentException(""Output path argument is required"");
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.toggleCCode(true);
        decompiler.toggleSyntaxTree(false);
        decompiler.setSimplificationStyle(""decompile"");
        if (!decompiler.openProgram(currentProgram)) {
            throw new IllegalStateException(""Could not open the imported program in Ghidra decompiler"");
        }

        File output = new File(args[0]);
        int exported = 0;
        try (PrintWriter writer = new PrintWriter(new OutputStreamWriter(new FileOutputStream(output), ""UTF-8""))) {
            writer.println(""Program: "" + currentProgram.getName());
            writer.println(""Language: "" + currentProgram.getLanguageID());
            writer.println();

            FunctionIterator functions = currentProgram.getFunctionManager().getFunctions(true);
            while (functions.hasNext() && !monitor.isCancelled() && exported < 500) {
                Function function = functions.next();
                writer.println(""/* ===== "" + function.getName(true) + "" @ "" + function.getEntryPoint() + "" ===== */"");
                DecompileResults result = decompiler.decompileFunction(function, 30, monitor);
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    writer.println(result.getDecompiledFunction().getC());
                }
                else {
                    writer.println(""/* decompilation unavailable for this function */"");
                }
                writer.println();
                exported++;
            }
            if (functions.hasNext()) {
                writer.println(""/* PaRuX export stopped after 500 functions; narrow the target/function set in Ghidra for deeper analysis. */"");
            }
        }
        finally {
            decompiler.dispose();
        }
    }
}
";
    }
}
