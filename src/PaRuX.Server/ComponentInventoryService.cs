using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Read-only discovery inventory for the first-run/runtime-manager surface. Discovery
/// never installs or mutates user/system software; ownership is explicit in the returned record.</summary>
public sealed class ComponentInventoryService
{
    private readonly RuntimeManagerService _runtime;
    public ComponentInventoryService(RuntimeManagerService runtime) => _runtime = runtime;

    /// <summary>
    /// PATH-detectable components the user may deliberately point PaRuX at. Python and uv are
    /// intentionally absent: all Python feature runtimes are provisioned exclusively through
    /// ManagedPythonRuntime and may never bind to a user/system interpreter or uv executable.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name, string[] Executables)> LocatableComponents = new[]
    {
        ("git", "Git", new[] { "git.exe", "git" }),
        ("ffmpeg", "FFmpeg", new[] { "ffmpeg.exe", "ffmpeg" }),
        ("node", "Node.js", new[] { "node.exe", "node" }),
        ("npm", "npm", new[] { "npm.cmd", "npm" })
    };

    public static readonly IReadOnlySet<string> ManagedPythonComponentIds =
        new HashSet<string>(new[] { "python", "uv" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Components for which the generic Skip/Unskip preference is meaningful. Managed
    /// Python/uv are deliberately excluded because feature installers provision them on demand.</summary>
    public static readonly IReadOnlySet<string> SkippableComponentIds =
        new HashSet<string>(
            new[] { "llama-server-cuda", "llama-server-vulkan", "comfyui" }
                .Concat(LocatableComponents.Select(c => c.Id)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Backward-compatible name used by the generic /skip endpoint. It intentionally means
    /// "component ids accepted by the generic optional-component flow", not every row returned by
    /// DiscoverAsync. Managed Python/uv are therefore absent and /skip rejects them.
    /// </summary>
    public static readonly IReadOnlyList<string> AllComponentIds = SkippableComponentIds.ToArray();

    public async Task<IReadOnlyList<ComponentStatus>> DiscoverAsync(CancellationToken ct)
    {
        RuntimeManagerStatus runtime = await _runtime.GetStatusAsync(ct);
        var result = new List<ComponentStatus>
        {
            Component("llama-server-cuda", "llama.cpp (CUDA)", LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable, runtime.Backends.FirstOrDefault(b => b.Backend == "text-cuda"), installSupported: true),
            Component("llama-server-vulkan", "llama.cpp (Vulkan)", LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable, runtime.Backends.FirstOrDefault(b => b.Backend == "text-vulkan"), installSupported: true),
            ComfyUiComponent(runtime.Backends.FirstOrDefault(b => b.Backend == "media-comfyui")),
            await ManagedPythonComponentAsync("uv", "uv Python runtime manager", ManagedPythonRuntime.UvExe, ct),
            await ManagedPythonComponentAsync("python", "Python runtime", ManagedPythonRuntime.TryGetInstalledPython(), ct)
        };

        foreach ((string id, string name, string[] executables) in LocatableComponents)
        {
            string? located = LocalAiPaths.GetComponentLocation(id);
            if (located != null)
            {
                bool stillThere = File.Exists(located);
                string? version = stillThere ? await ResolveVersionAsync(id, located, ct) : null;
                result.Add(new ComponentStatus
                {
                    Id = id,
                    DisplayName = name,
                    Ownership = ComponentOwnership.USER_MANAGED,
                    Detected = stillThere,
                    Path = located,
                    Version = version,
                    Health = stillThere ? "detected" : "missing",
                    ValidationStatus = ValidationStatus(stillThere, version),
                    ValidationMessage = ValidationMessage(stillThere, version, userSelected: true),
                    Actions = new[] { "relocate", "remove" }
                });
                continue;
            }

            bool skipped = LocalAiPaths.IsComponentSkipped(id);
            string? path = FindOnPath(executables);
            string? pathVersion = path == null ? null : await ResolveVersionAsync(id, path, ct);
            result.Add(new ComponentStatus
            {
                Id = id,
                DisplayName = name,
                Ownership = ComponentOwnership.SYSTEM_DETECTED,
                Detected = path != null,
                Path = path,
                Version = pathVersion,
                Health = path == null ? "missing" : "detected",
                ValidationStatus = ValidationStatus(path != null, pathVersion),
                ValidationMessage = ValidationMessage(path != null, pathVersion, userSelected: false),
                Skipped = skipped && path == null,
                Actions = path != null ? new[] { "locate", "skip" } : skipped ? new[] { "locate", "unskip" } : new[] { "locate", "skip" }
            });
        }
        return result;
    }

    private static async Task<ComponentStatus> ManagedPythonComponentAsync(
        string id, string displayName, string? path, CancellationToken ct)
    {
        bool installed = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        string? version = installed ? await ResolveVersionAsync(id, path!, ct) : null;
        return new ComponentStatus
        {
            Id = id,
            DisplayName = displayName,
            Ownership = ComponentOwnership.PARUX_MANAGED,
            Detected = installed,
            Path = installed ? Path.GetFullPath(path!) : null,
            Version = version,
            Health = installed ? "managed" : "available-on-demand",
            ValidationStatus = installed ? (version != null ? "validated" : "managed") : "managed-on-demand",
            ValidationMessage = installed
                ? "This executable belongs to the PaRuX-managed Python toolchain under the application runtime root; PATH and user-selected locations are ignored."
                : "PaRuX provisions this component on demand inside its managed runtime. A system/user Python or uv installation is never used as a fallback.",
            Skipped = false,
            Actions = Array.Empty<string>()
        };
    }

    private static string ValidationStatus(bool exists, string? version) =>
        !exists ? "missing" : !string.IsNullOrWhiteSpace(version) ? "validated" : "unverified";

    private static string ValidationMessage(bool exists, string? version, bool userSelected)
    {
        if (!exists) return userSelected
            ? "The configured path no longer exists. PaRuX did not alter or replace the user-managed installation."
            : "No compatible executable name was found on PATH.";
        if (!string.IsNullOrWhiteSpace(version)) return "The executable responded to an identity/version probe: " + version;
        return "The path exists, but PaRuX could not positively identify a version within the bounded probe. It remains detected but unverified.";
    }

    /// <summary>"llama.cpp (CUDA)" becomes "llama.cpp (CUDA 13.4)" from the installed folder name, so a 12.4 and a 13.4 build are distinguishable.</summary>
    internal static string WithCudaToolkit(string name, string runtimeFolder)
    {
        if (!name.EndsWith("(CUDA)", StringComparison.Ordinal)) return name;
        string? toolkit = LocalAiPaths.TryParseRuntimeFolder("text-cuda", Path.GetFileName(runtimeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), out _, out string? t) ? t : null;
        return toolkit == null ? name : name[..^1] + " " + toolkit + ")";
    }

    private static ComponentStatus Component(string id, string name, string root, string exe, RuntimeBackendStatus? status, bool installSupported)
    {
        bool installed = status?.Installed == true;
        bool skipped = !installed && LocalAiPaths.IsComponentSkipped(id);
        var actions = new List<string>();
        if (installSupported) actions.Add("install");
        if (installSupported && installed) actions.Add("rollback");
        if (!installed) actions.Add(skipped ? "unskip" : "skip");

        string validation = status?.Healthy == true ? "validated" : installed ? (status?.Running == true ? "unhealthy" : "unverified") : "missing";
        string validationMessage = status?.Healthy == true
            ? "The PaRuX-managed runtime is installed and its runtime health check passed."
            : installed
                ? (status?.Running == true
                    ? "The PaRuX-managed runtime is running but its runtime health check did not pass."
                    : "The PaRuX-managed runtime is installed and stopped. Its health will be checked when it starts.")
                : "The PaRuX-managed runtime is not installed.";

        return new ComponentStatus
        {
            Id = id,
            DisplayName = installed ? WithCudaToolkit(name, root) : name,
            Ownership = ComponentOwnership.PARUX_MANAGED,
            Detected = installed,
            Path = installed ? Path.Combine(root, exe) : null,
            Version = status?.Version,
            Health = status?.Healthy == true ? "healthy" : installed ? "installed" : "missing",
            ValidationStatus = validation,
            ValidationMessage = validationMessage,
            Skipped = skipped,
            Actions = actions.ToArray()
        };
    }

    private static ComponentStatus ComfyUiComponent(RuntimeBackendStatus? status)
    {
        string? located = LocalAiPaths.GetComponentLocation("comfyui");
        if (located != null)
        {
            bool stillThere = File.Exists(located) && string.Equals(Path.GetFileName(located), "main.py", StringComparison.OrdinalIgnoreCase);
            return new ComponentStatus
            {
                Id = "comfyui",
                DisplayName = "ComfyUI",
                Ownership = ComponentOwnership.USER_MANAGED,
                Detected = stillThere,
                Path = located,
                Version = null,
                Health = stillThere ? "detected" : "missing",
                ValidationStatus = stillThere ? "validated" : "missing",
                ValidationMessage = stillThere
                    ? "The selected checkout contains the expected ComfyUI main.py entry point. PaRuX does not own or modify it."
                    : "The configured ComfyUI main.py no longer exists.",
                Actions = new[] { "relocate", "remove", "install" }
            };
        }

        bool installed = status?.Installed == true;
        bool skipped = !installed && LocalAiPaths.IsComponentSkipped("comfyui");
        var store = new ComfyRuntimeStore();
        ComfyBuildIdentity? active = store.GetActiveIdentity();
        IReadOnlyList<ComfyBuildIdentity> builds = store.ListInstalled();
        bool rollbackAvailable = active != null && builds.Any(b => b != active);

        var actions = new List<string> { "install", "locate" };
        if (rollbackAvailable) actions.Add("rollback");
        if (!installed) actions.Add(skipped ? "unskip" : "skip");

        string? version = active is { Tag: not "legacy" } ? active.Tag : status?.Version;
        string variantText = active is { Tag: not "legacy" }
            ? " (" + active.Variant + ")"
            : string.Empty;
        return new ComponentStatus
        {
            Id = "comfyui",
            DisplayName = "ComfyUI",
            Ownership = ComponentOwnership.PARUX_MANAGED,
            Detected = installed,
            Path = installed ? LocalAiPaths.ComfyMain : null,
            Version = version,
            Health = status?.Healthy == true ? "healthy" : installed ? "installed" : "missing",
            ValidationStatus = status?.Healthy == true ? "validated" : installed ? (status?.Running == true ? "unhealthy" : "unverified") : "missing",
            ValidationMessage = status?.Healthy == true
                ? "ComfyUI " + (version ?? "runtime") + variantText + " is installed and its runtime health check passed."
                : installed
                    ? (status?.Running == true
                        ? "ComfyUI " + (version ?? "runtime") + variantText + " is running but its runtime health check did not pass."
                        : "ComfyUI is installed and stopped. Its health will be checked when it starts.")
                    : "ComfyUI is not configured; PaRuX can install an official Windows portable build or locate an existing checkout.",
            Skipped = skipped,
            Actions = actions.ToArray()
        };
    }

    public static string? TryResolveComfyMainPy(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (File.Exists(path) && string.Equals(Path.GetFileName(path), "main.py", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);
            string candidate = Path.Combine(path, "main.py");
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch { return null; }
    }

    private static string? FindOnPath(IEnumerable<string> names)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (string name in names)
                try
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
        return null;
    }

    private static async Task<string?> ResolveVersionAsync(string componentId, string path, CancellationToken ct)
    {
        string? stamped = TryGetFileVersion(path);
        if (stamped != null) return stamped;
        return await ProbeVersionAsync(componentId, path, ct);
    }

    private static string? TryGetFileVersion(string path)
    {
        try
        {
            string? version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch { return null; }
    }

    private static async Task<string?> ProbeVersionAsync(string componentId, string path, CancellationToken ct)
    {
        try
        {
            string versionArg = string.Equals(componentId, "ffmpeg", StringComparison.OrdinalIgnoreCase) ? "-version" : "--version";
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string extension = Path.GetExtension(path);
            if (OperatingSystem.IsWindows() && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(path);
                psi.ArgumentList.Add(versionArg);
            }
            else
            {
                psi.FileName = path;
                psi.ArgumentList.Add(versionArg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string combined = (await stdoutTask) + "\n" + (await stderrTask);
            string? firstLine = combined.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            if (string.IsNullOrWhiteSpace(firstLine)) return null;
            return firstLine.Length <= 300 ? firstLine : firstLine[..300];
        }
        catch { return null; }
    }
}
