using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum BundledBaselineRuntimeKind
{
    None,
    Vulkan,
    Cuda13
}

/// <summary>
/// Makes the baseline llama.cpp build harvested into the MSI usable by the existing mutable
/// LocalAiPaths layout. The verified archive itself is staged under Program Files; on first server
/// load only, this bootstrap verifies the staged file manifest again and atomically copies the
/// immutable baseline into the current user's LocalAI/runtime folder. Existing/user-managed
/// runtimes are never overwritten.
/// </summary>
internal static class BundledBaselineRuntimeBootstrap
{
    private const string ManifestRelative = "runtime-baseline/staging-manifest.json";
    private const string VulkanRuntimeFolder = "llama.cpp-b10549-vulkan";
    private const string VulkanPayloadRelative = "runtime-baseline/llama.cpp-b10549-vulkan";
    private const string CudaRuntimeFolder = "llama.cpp-b11871-cuda13.4";
    private const string CudaPayloadRelative = "runtime-baseline/llama.cpp-b11871-cuda13.4";
    private const string CudaBuildTag = "b11871";
    private const string CudaToolkit = "13.4";

    /// <summary>
    /// Called explicitly from Program before anything else. It must NOT be a module initializer:
    /// hardware detection awaits async work whose continuations touch types of this assembly, and
    /// those block on the module-initializer lock held by the initializing thread, so the Server
    /// never started on a clean first run (found by real clean-state testing).
    /// </summary>
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Keep developer/test launches cheap. MSI staging creates this manifest beside the
            // Server; only then is there a bundled baseline worth selecting or probing for.
            string manifestPath = Path.Combine(
                AppContext.BaseDirectory, ManifestRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath)) return;

            BundledBaselineRuntimeKind kind = DetectRuntimeTarget();
            string? runtimeFolder = kind switch
            {
                BundledBaselineRuntimeKind.Vulkan => VulkanRuntimeFolder,
                BundledBaselineRuntimeKind.Cuda13 => CudaRuntimeFolder,
                _ => null
            };
            string? payloadRelative = kind switch
            {
                BundledBaselineRuntimeKind.Vulkan => VulkanPayloadRelative,
                BundledBaselineRuntimeKind.Cuda13 => CudaPayloadRelative,
                _ => null
            };
            if (runtimeFolder is null || payloadRelative is null) return;

            // Do not replace a selected or legacy CUDA installation. Older NVIDIA drivers keep
            // the existing CUDA 12.4 on-demand path; they must not be routed to bundled Vulkan.
            if (kind == BundledBaselineRuntimeKind.Cuda13 &&
                (LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda") is not null ||
                 File.Exists(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable))))
                return;

            string source = Path.Combine(AppContext.BaseDirectory, payloadRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(Path.Combine(source, LocalAiPaths.LlamaServerExecutable))) return; // dev build / no MSI staging

            string root = ResolveLocalAiRoot();
            string target = Path.Combine(root, "runtime", runtimeFolder);
            bool installedByThisBootstrap = false;
            if (!File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable)))
            {
                // A pre-existing incomplete directory may belong to the user. Preserve it and let
                // the runtime status surface the missing baseline rather than replacing its files.
                if (Directory.Exists(target)) return;

                string[] requiredFiles = kind == BundledBaselineRuntimeKind.Cuda13
                    ? new[] { LocalAiPaths.LlamaServerExecutable, "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll" }
                    : new[] { LocalAiPaths.LlamaServerExecutable };
                VerifyBundledPayload(source, payloadRelative, requiredFiles);
                string parent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(parent);
                string temp = Path.Combine(parent, "." + runtimeFolder + ".installing-" + Guid.NewGuid().ToString("N"));
                try
                {
                    CopyDirectory(source, temp);
                    if (File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable)))
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                    else
                    {
                        Directory.Move(temp, target);
                    }
                    installedByThisBootstrap = true;
                }
                catch
                {
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
                    if (!File.Exists(Path.Combine(target, LocalAiPaths.LlamaServerExecutable))) throw;
                }
            }

            // The CUDA baseline has a distinct version/toolkit folder. Select it only when this
            // bootstrap just installed it and no user-selected build already existed.
            if (kind == BundledBaselineRuntimeKind.Cuda13 && installedByThisBootstrap &&
                LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda") is null)
                LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", CudaBuildTag, CudaToolkit);

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

    internal static BundledBaselineRuntimeKind SelectRuntimeTarget(
        bool inventoryAvailable,
        bool hasNvidiaAdapter,
        double? computeCapability,
        string? driverVersion)
    {
        if (!inventoryAvailable) return BundledBaselineRuntimeKind.None;
        if (!hasNvidiaAdapter) return BundledBaselineRuntimeKind.Vulkan;
        return RuntimeBuildCatalog.PreferredCudaMajor(computeCapability, driverVersion) == 13
            ? BundledBaselineRuntimeKind.Cuda13
            : BundledBaselineRuntimeKind.None;
    }

    private static BundledBaselineRuntimeKind DetectRuntimeTarget()
    {
        GpuInventoryStatus inventory = new GpuInventoryService().GetStatus();
        GpuAdapterStatus? nvidia = inventory.Adapters
            .Where(adapter => adapter.VendorId == RuntimeBackendAdvisor.NvidiaVendor && !adapter.IsSoftware)
            .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
            .FirstOrDefault();
        if (nvidia is null)
            return SelectRuntimeTarget(inventory.Available, hasNvidiaAdapter: false, null, null);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        List<GpuPerformanceTelemetryDto> telemetry = new GpuPerformanceTelemetryService()
            .QueryAsync(timeout.Token).GetAwaiter().GetResult();
        double? computeCapability = telemetry.FirstOrDefault(device => device.ComputeCapability is not null &&
            nvidia.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase))?.ComputeCapability;
        return SelectRuntimeTarget(inventory.Available, hasNvidiaAdapter: true, computeCapability, nvidia.DriverVersion);
    }

    private static void VerifyBundledPayload(string source, string payloadRelative, IReadOnlyCollection<string> requiredFiles)
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

        string prefix = payloadRelative + "/";
        int verified = 0;
        var verifiedRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (requiredFiles.Contains(insidePayload, StringComparer.OrdinalIgnoreCase))
                verifiedRequired.Add(insidePayload);
        }

        if (verified == 0 || requiredFiles.Any(required => !verifiedRequired.Contains(required)))
            throw new InvalidDataException("The MSI baseline runtime manifest did not verify every required file.");
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
