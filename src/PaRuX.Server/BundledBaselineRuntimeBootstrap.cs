using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Makes the baseline llama.cpp build harvested into the MSI usable by the existing mutable
/// LocalAiPaths layout. The verified archive itself is staged under Program Files; on first server
/// load only, this bootstrap verifies the staged file manifest again and atomically copies the
/// immutable baseline into the current user's LocalAI/runtime folder. Existing/user-managed
/// runtimes are never overwritten.
/// </summary>
internal static class BundledBaselineRuntimeBootstrap
{
    private const string RuntimeFolder = "llama.cpp-b10549-vulkan";
    private const string ManifestRelative = "runtime-baseline/staging-manifest.json";
    private const string PayloadRelative = "runtime-baseline/llama.cpp-b10549-vulkan";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;

        string source = Path.Combine(AppContext.BaseDirectory, PayloadRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(Path.Combine(source, LocalAiPaths.LlamaServerExecutable))) return; // dev build / no MSI staging

        string root = ResolveLocalAiRoot();
        string target = Path.Combine(root, "runtime", RuntimeFolder);
        if (File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable))) return;

        try
        {
            VerifyBundledPayload(source);
            string parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            string temp = Path.Combine(parent, "." + RuntimeFolder + ".installing-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(source, temp);
                if (File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable)))
                {
                    Directory.Delete(temp, recursive: true);
                    return;
                }
                Directory.Move(temp, target);
            }
            catch
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
                if (!File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable))) throw;
            }

            string errorMarker = Path.Combine(
                PaRuX.Contracts.PaRuXDataRoot.Path, "baseline-bootstrap.error.txt");
            try { File.Delete(errorMarker); } catch { }
        }
        catch (Exception ex)
        {
            // A module initializer must not brick the server process. Fail closed by leaving the
            // runtime absent; normal runtime status then reports it unavailable. Persist a local
            // diagnostic so this never becomes a silent partial-copy failure.
            try
            {
                string state = PaRuX.Contracts.PaRuXDataRoot.Path;
                Directory.CreateDirectory(state);
                File.WriteAllText(Path.Combine(state, "baseline-bootstrap.error.txt"),
                    DateTime.UtcNow.ToString("o") + Environment.NewLine + ex);
            }
            catch { }
        }
    }

    private static void VerifyBundledPayload(string source)
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, ManifestRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("The MSI baseline runtime manifest is missing.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 1)
            throw new InvalidDataException("Unsupported MSI baseline runtime manifest schema.");
        if (!document.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The MSI baseline runtime manifest has no file inventory.");

        string prefix = PayloadRelative + "/";
        int verified = 0;
        bool serverVerified = false;
        foreach (JsonElement entry in files.EnumerateArray())
        {
            string? relative = entry.TryGetProperty("path", out JsonElement pathElement) ? pathElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(relative) || !relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string insidePayload = relative[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(source, insidePayload));
            string sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                throw new InvalidDataException("Bundled runtime file is missing or escaped its payload root: " + relative);

            long expectedSize = entry.GetProperty("sizeBytes").GetInt64();
            string expectedHash = entry.GetProperty("sha256").GetString() ?? string.Empty;
            var info = new FileInfo(candidate);
            if (info.Length != expectedSize)
                throw new InvalidDataException("Bundled runtime size verification failed: " + relative);
            using FileStream stream = File.OpenRead(candidate);
            string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundled runtime SHA-256 verification failed: " + relative);
            verified++;
            if (string.Equals(insidePayload, LocalAiPaths.LlamaServerExecutable, StringComparison.OrdinalIgnoreCase))
                serverVerified = true;
        }

        if (verified == 0 || !serverVerified)
            throw new InvalidDataException("The MSI baseline runtime manifest did not verify llama-server.exe.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static string ResolveLocalAiRoot()
    {
        // Through PaRuXDataRoot, so this follows the one-time folder rename instead of reading a
        // path that no longer exists after it.
        string config = PaRuX.Contracts.PaRuXDataRoot.Combine("localai-paths.json");
        try
        {
            if (File.Exists(config))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("Root", out JsonElement rootElement) ||
                    doc.RootElement.TryGetProperty("root", out rootElement))
                {
                    string? configured = rootElement.GetString();
                    if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
                }
            }
        }
        catch { }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string legacy = Path.Combine(documents, "LocalAI");
        if (Directory.Exists(Path.Combine(legacy, "runtime")) || Directory.Exists(Path.Combine(legacy, "models")))
            return legacy;

        return PaRuX.Contracts.PaRuXDataRoot.Combine("LocalAI");
    }
}
