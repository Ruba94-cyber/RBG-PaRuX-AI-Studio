using System.Text.Json;

namespace PaRuX.Server;

public static class RoleOverrideOrigin
{
    /// <summary>Chosen by the user or an explicit persist-this-load request.</summary>
    public const string User = "user";
    public const string AutoAssigned = "auto-assigned";
    /// <summary>The origin cannot be verified because its sidecar is absent or does not match the override.</summary>
    public const string UnknownLegacy = "unknown-legacy";
    public const string UnknownLegacyReason = "PaRuX cannot tell whether this choice was yours, so it keeps the choice until you confirm or release it.";
}

/// <summary>
/// Resolves where the local AI runtime (llama.cpp builds, ComfyUI, GGUF models, ComfyUI
/// checkpoints) lives on disk. Configurable via Settings rather than hardcoded, so a fresh
/// clone of the public repo on a different machine/username isn't broken - the only thing
/// that previously made this portable was luck (MyDocuments resolves per-user, but the actual
/// PowerShell scripts this replaces had the path baked in as a literal string).
///
/// Default: auto-detects an existing "LocalAI" folder next to Documents (this machine's real,
/// already-populated installation - multi-GB of downloaded runtimes/models nobody should have
/// to redownload) before falling back to a fresh %LOCALAPPDATA%\PaRuX\LocalAI for a genuinely
/// new install. Either way, the directory layout underneath (runtime\, models\, config\, logs\)
/// is unchanged, so this is a drop-in replacement, not a new convention.
/// </summary>
public static class LocalAiPaths
{
    /// <summary>
    /// The on-disk name of a native tool, for the platform Core is running on.
    ///
    /// PaRuX hardcoded ".exe" in 30-odd places because it only ever ran on Windows. That is one of
    /// the concrete things stopping a headless Core elsewhere: the same llama.cpp build ships as
    /// "llama-server" on Linux and macOS, and a hardcoded ".exe" makes an installed runtime look
    /// absent. Windows keeps exactly the name it had, so nothing about an existing install changes.
    /// </summary>
    public static string NativeExecutable(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    /// <summary>The text inference server binary: LlamaServerExecutable on Windows, "llama-server"
    /// elsewhere.</summary>
    public static string LlamaServerExecutable { get; } = NativeExecutable("llama-server");

    /// <summary>llama.cpp's benchmark binary, named the same way.</summary>
    public static string LlamaBenchExecutable { get; } = NativeExecutable("llama-bench");

    private static string ConfigPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "localai-paths.json");

    private static string? _cachedRoot;

    /// <summary>
    /// A root that applies only to the logical call context that established it.
    ///
    /// The process-wide cache below is deliberate for the product: one machine has one local AI
    /// installation and re-resolving it per access would re-read config on every path lookup. It is
    /// also what made this class untestable in isolation - a test that needs its own installation
    /// had no way to get one without swapping the shared static, which every other concurrently
    /// running test then saw too.
    ///
    /// An <see cref="AsyncLocal{T}"/> is the correct shape for that: it flows into the async work
    /// started inside the scope (the in-process host, its hosted services, the requests driven
    /// through it) and is invisible everywhere else, so one suite can drive a complete isolated
    /// installation while the rest of the run keeps resolving the real one. It is null in the
    /// product, where nothing ever opens a scope, so the shipped behaviour is byte-identical.
    /// </summary>
    private static readonly AsyncLocal<string?> ScopedRoot = new();
    private static volatile string? _testRootFallback;

    public static string Root
    {
        get
        {
            string? scoped = ScopedRoot.Value ?? _testRootFallback;
            if (scoped != null) return scoped;
            if (_cachedRoot != null) return _cachedRoot;
            _cachedRoot = ResolveRoot();
            return _cachedRoot;
        }
    }

    /// <summary>Test seam: resolves <see cref="Root"/> to <paramref name="path"/> for the current
    /// call context until the returned scope is disposed. Nested scopes restore the enclosing
    /// value rather than clearing it.</summary>
    internal static IDisposable UseRootForTests(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A scoped root is required.", nameof(path));
        Directory.CreateDirectory(path);
        return new RootScope(path);
    }

    private sealed class RootScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string? _previousFallback;
        private bool _disposed;

        internal RootScope(string path)
        {
            _previous = ScopedRoot.Value;
            _previousFallback = _testRootFallback;
            ScopedRoot.Value = path;
            _testRootFallback = path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedRoot.Value = _previous;
            _testRootFallback = _previousFallback;
        }
    }

    /// <summary>Persists a new root path (validated to exist) and clears the cache so the next
    /// access picks it up. Used by the Settings UI.</summary>
    public static void SetRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new InvalidDataException("The local AI root folder must be an existing directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new PathsConfig { Root = path }));
        _cachedRoot = path;
    }

    // ---- Runtime build install/update/rollback (2026-08-27) ----
    // Every build this project has ever shipped or installed keeps its own uniquely-tagged
    // folder (this exact naming convention already existed for the one build this machine had
    // pre-installed - "llama.cpp-b10549-cuda12.4"/"llama.cpp-b10549-vulkan" - RuntimeInstallService
    // just generalizes the hardcoded tag into a persisted, switchable one). Nothing is ever
    // deleted on install, so "rollback" is only ever repointing this small JSON pointer back to a
    // tag whose folder is still on disk - no separate "keep a backup" step is needed.
    public const string DefaultCudaBuildTag = "b10549";
    public const string DefaultVulkanBuildTag = "b10549";
    private const string CudaFolderSuffix = "cuda12.4";
    private const string VulkanFolderSuffix = "vulkan";
    public static readonly string[] KnownRuntimeBackends = { "text-cuda", "text-vulkan" };

    public static string RuntimeRoot => Path.Combine(Root, "runtime");
    public static string RuntimeCudaPath
    {
        get
        {
            string tag = GetActiveRuntimeBuildTag("text-cuda") ?? DefaultCudaBuildTag;
            return Path.Combine(RuntimeRoot, RuntimeFolderName("text-cuda", tag, ResolveCudaToolkit(tag)));
        }
    }
    public static string RuntimeVulkanPath => Path.Combine(RuntimeRoot, RuntimeFolderName("text-vulkan", GetActiveRuntimeBuildTag("text-vulkan") ?? DefaultVulkanBuildTag));

    /// <summary>The runtime folder a GPU text launch should use. CUDA when it is installed; otherwise the
    /// installed Vulkan runtime, so a machine with an NVIDIA card but only the baseline Vulkan runtime
    /// still starts (and Runtime Manager keeps suggesting CUDA). When neither exists the CUDA folder is
    /// returned so the "not found" message names the runtime PaRuX would install.</summary>
    public static string ResolveGpuTextRuntimePath(bool preferCuda)
    {
        if (!preferCuda) return RuntimeVulkanPath;
        string cuda = RuntimeCudaPath;
        if (File.Exists(Path.Combine(cuda, LlamaServerExecutable))) return cuda;
        string vulkan = RuntimeVulkanPath;
        return File.Exists(Path.Combine(vulkan, LlamaServerExecutable)) ? vulkan : cuda;
    }

    /// <summary>Throws InvalidDataException for anything outside KnownRuntimeBackends - the one
    /// validation gate every runtime-build method below routes through, so an unknown backend
    /// name always fails the same honest way instead of silently resolving to a wrong folder.</summary>
    public static string RuntimeFolderSuffix(string backend) => backend switch
    {
        "text-cuda" => CudaFolderSuffix,
        "text-vulkan" => VulkanFolderSuffix,
        _ => throw new InvalidDataException("Unknown runtime backend \"" + backend + "\". Known backends: " + string.Join(", ", KnownRuntimeBackends) + ".")
    };

    public static string DefaultRuntimeBuildTag(string backend) => backend switch
    {
        "text-cuda" => DefaultCudaBuildTag,
        "text-vulkan" => DefaultVulkanBuildTag,
        _ => throw new InvalidDataException("Unknown runtime backend \"" + backend + "\". Known backends: " + string.Join(", ", KnownRuntimeBackends) + ".")
    };

    /// <summary>CUDA toolkit of builds that predate toolkit-aware folder names ("...-cuda12.4").</summary>
    public const string DefaultCudaToolkit = "12.4";
    private const string CudaToolkitConfigKey = "text-cuda.toolkit";
    /// <summary>Sidecar PaRuX installs write next to llama-server; records the asset facts the folder name encodes.</summary>
    public const string RuntimeSidecarFileName = "parux-runtime.json";

    /// <summary>"13.4" from "llama-b11871-bin-win-cuda-13.4-x64.zip", "12.4" from "...cuda12.4..."; null when the
    /// name carries no CUDA toolkit version (Vulkan or CPU assets).</summary>
    public static string? ParseCudaToolkit(string? assetOrFolderName)
    {
        if (string.IsNullOrWhiteSpace(assetOrFolderName)) return null;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            assetOrFolderName, @"cuda[-_]?(?:cu)?(\d{1,2})\.(\d{1,2})(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : null;
    }

    public static string RuntimeFolderName(string backend, string tag) => RuntimeFolderName(backend, tag, null);

    /// <summary>Folder for a build. For CUDA the suffix carries the real toolkit ("cuda13.4"); null keeps the
    /// legacy "cuda12.4" so existing folders and callers are unaffected.</summary>
    public static string RuntimeFolderName(string backend, string tag, string? cudaToolkit) =>
        "llama.cpp-" + tag + "-" + (backend == "text-cuda" && !string.IsNullOrWhiteSpace(cudaToolkit)
            ? "cuda" + cudaToolkit
            : RuntimeFolderSuffix(backend));

    /// <summary>CUDA toolkits of the installed folders for a tag (any llama.cpp-{tag}-cuda{N.M} folder holding llama-server).</summary>
    public static List<string> ListInstalledCudaToolkits(string tag)
    {
        var found = new List<string>();
        if (!Directory.Exists(RuntimeRoot)) return found;
        foreach (string dir in Directory.EnumerateDirectories(RuntimeRoot))
        {
            if (!TryParseRuntimeFolder("text-cuda", Path.GetFileName(dir), out string? t, out string? toolkit)
                || t != tag || toolkit == null) continue;
            if (File.Exists(Path.Combine(dir, LlamaServerExecutable))) found.Add(toolkit);
        }
        return found;
    }

    /// <summary>Toolkit the CUDA tag runs on: the persisted choice when that folder is installed, otherwise the
    /// legacy 12.4 folder, otherwise any installed one, otherwise the persisted/legacy value.</summary>
    public static string ResolveCudaToolkit(string tag)
    {
        string? persisted = GetActiveCudaToolkit();
        List<string> installed = ListInstalledCudaToolkits(tag);
        if (persisted != null && installed.Contains(persisted)) return persisted;
        if (installed.Contains(DefaultCudaToolkit)) return DefaultCudaToolkit;
        if (installed.Count > 0) return installed[0];
        return persisted ?? DefaultCudaToolkit;
    }

    /// <summary>Records which toolkit folder a tag should resolve to before the tag itself is activated.</summary>
    internal static void SetCudaToolkitHint(string toolkit) =>
        MutateOverrides(RuntimeBuildConfigPath, working => { working[CudaToolkitConfigKey] = toolkit; return true; });

    /// <summary>The CUDA toolkit persisted for text-cuda (null when nothing was ever persisted).</summary>
    public static string? GetActiveCudaToolkit() =>
        LoadOverrides(RuntimeBuildConfigPath).TryGetValue(CudaToolkitConfigKey, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Splits a runtime folder name into build tag and (CUDA only) toolkit; false when it is not this backend's.</summary>
    public static bool TryParseRuntimeFolder(string backend, string folderName, out string? tag, out string? cudaToolkit)
    {
        tag = null; cudaToolkit = null;
        const string prefix = "llama.cpp-";
        if (!folderName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (backend == "text-cuda")
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                folderName, @"^llama\.cpp-(.+)-cuda(\d{1,2}\.\d{1,2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!m.Success || string.IsNullOrWhiteSpace(m.Groups[1].Value)) return false;
            tag = m.Groups[1].Value; cudaToolkit = m.Groups[2].Value;
            return true;
        }
        string expectedSuffix = "-" + RuntimeFolderSuffix(backend);
        if (!folderName.EndsWith(expectedSuffix, StringComparison.Ordinal)) return false;
        string t = folderName.Substring(prefix.Length, folderName.Length - prefix.Length - expectedSuffix.Length);
        if (string.IsNullOrWhiteSpace(t)) return false;
        tag = t;
        return true;
    }

    /// <summary>Inverse of RuntimeFolderName - null if the folder name doesn't match this
    /// backend's naming convention (e.g. it belongs to the other backend, or isn't a runtime
    /// build folder at all). CUDA folders of any toolkit version match.</summary>
    public static string? TryParseRuntimeBuildTag(string backend, string folderName) =>
        TryParseRuntimeFolder(backend, folderName, out string? tag, out _) ? tag : null;

    private static string RuntimeBuildConfigPath => Path.Combine(ConfigDir, "runtime-build-config.json");

    /// <summary>Null when this backend has never had an install/rollback performed against it -
    /// callers should treat null as DefaultRuntimeBuildTag(backend), matching this machine's
    /// pre-2026-08-27 hardcoded behavior exactly.</summary>
    public static string? GetActiveRuntimeBuildTag(string backend)
    {
        RuntimeFolderSuffix(backend); // validates
        return LoadOverrides(RuntimeBuildConfigPath).TryGetValue(backend, out string? tag) && !string.IsNullOrWhiteSpace(tag) ? tag : null;
    }

    /// <summary>Only ever repoints to a tag that is verifiably already installed (a real
    /// llama-server.exe present under that tag's folder) - this is what makes both a fresh
    /// install's final step and a rollback safe: it can never leave the pointer aimed at a build
    /// that isn't actually there.</summary>
    public static void SetActiveRuntimeBuildTag(string backend, string tag, string? cudaToolkit = null)
    {
        RuntimeFolderSuffix(backend); // validates
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("tag is required.");
        string? toolkit = backend == "text-cuda" ? cudaToolkit ?? ResolveCudaToolkit(tag) : null;
        string folder = Path.Combine(RuntimeRoot, RuntimeFolderName(backend, tag, toolkit));
        if (!File.Exists(Path.Combine(folder, LlamaServerExecutable)))
            throw new InvalidDataException("No installed build \"" + tag + "\" for \"" + backend + "\" was found at " + folder + ".");
        MutateOverrides(RuntimeBuildConfigPath, working =>
        {
            working[backend] = tag;
            if (toolkit != null) working[CudaToolkitConfigKey] = toolkit;
            return true;
        });
    }

    /// <summary>Resets a backend back to DefaultRuntimeBuildTag (this machine's originally
    /// hardcoded build) without requiring that tag's folder to still exist under the new
    /// per-tag-folder scheme - the default build predates this file having ever tracked tags at
    /// all, so it is exempt from SetActiveRuntimeBuildTag's usual "must already be installed"
    /// check. Also the mechanism hermetic tests use to undo a test-only install/rollback against
    /// this machine's real shared config file.</summary>
    public static void ClearActiveRuntimeBuildTag(string backend)
    {
        RuntimeFolderSuffix(backend); // validates
        MutateOverrides(RuntimeBuildConfigPath, working =>
        {
            bool removed = working.Remove(backend);
            if (backend == "text-cuda") removed |= working.Remove(CudaToolkitConfigKey);
            return removed;
        });
    }

    /// <summary>Every locally installed build for a backend - any runtime\ subfolder matching its
    /// naming convention that actually contains llama-server.exe (a partially-extracted or
    /// manually-deleted-binary folder is not "installed").</summary>
    public static List<string> ListInstalledRuntimeBuildTags(string backend)
    {
        RuntimeFolderSuffix(backend); // validates
        if (!Directory.Exists(RuntimeRoot)) return new List<string>();
        var tags = new List<string>();
        foreach (string dir in Directory.EnumerateDirectories(RuntimeRoot))
        {
            string? tag = TryParseRuntimeBuildTag(backend, Path.GetFileName(dir));
            if (tag != null && File.Exists(Path.Combine(dir, LlamaServerExecutable)) && !tags.Contains(tag))
                tags.Add(tag);
        }
        return tags;
    }

    public static string ComfyRoot => Path.Combine(Root, "runtime", "ComfyUI_windows_portable");
    public static string ComfyApp => Path.Combine(ComfyRoot, "ComfyUI");
    public static string ComfyPython => Path.Combine(ComfyRoot, "python_embeded", "python.exe");
    public static string ComfyMain => Path.Combine(ComfyApp, "main.py");
    public static string ComfyOutputRoot => Path.Combine(ComfyApp, "output");
    public static string ModelsPath => Path.Combine(Root, "models");
    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LogsPath => Path.Combine(Root, "logs");
    // These must remain properties rather than static readonly paths: tests scope Root per logical
    // call context, and capturing ModelsPath during type initialization can pin a later isolated
    // runtime start to the developer's real LocalAI model directory.
    private static string DefaultStandardModel => DefaultBundledModelPath("standard");
    private static string DefaultUncensoredModel => DefaultBundledModelPath("uncensored");

    private static string DefaultBundledModelPath(string role)
    {
        VisionProjectorManifest.BundledRoleModel pair = VisionProjectorManifest.Default.BundledPairForRole(role)
            ?? throw new InvalidOperationException("No bundled vision model is configured for the " + role + " role.");
        return Path.Combine(ModelsPath, pair.ModelFileName);
    }

    /// <summary>Resolved file for the Standard role: the user's override if one is set (see
    /// SetRoleOverride), otherwise the built-in default. Never the default when overridden, even
    /// if the overridden file has since gone missing - RuntimeResult failing loudly with a real
    /// path is more honest than silently falling back to a model the user deliberately moved away
    /// from.</summary>
    public static string StandardModel => GetRoleOverride("standard") ?? DefaultStandardModel;
    public static string UncensoredModel => GetRoleOverride("uncensored") ?? DefaultUncensoredModel;

    /// <summary>Null whenever the role is overridden: an arbitrary user-picked GGUF has no known
    /// matching vision projector, so StartTextAsync treats null here as "load this profile
    /// text-only" rather than failing startup over a vision file that was never going to exist for
    /// a model PaRuX didn't pick. Only the built-in default pairing is vision-capable for now.</summary>
    public static string? StandardVision => GetRoleOverride("standard") != null
        ? null : GetBundledVisionProjectorForModel("standard", DefaultStandardModel);
    public static string? UncensoredVision => GetRoleOverride("uncensored") != null
        ? null : GetBundledVisionProjectorForModel("uncensored", DefaultUncensoredModel);

    public static string SmallModel => Path.Combine(ModelsPath, "Qwen3.5-4B-Q4_K_M.gguf");
    public static string SmallUncensoredModel => Path.Combine(ModelsPath, "Qwen3.5-4B-Uncensored-Q4_K_M.gguf");

    /// <summary>The bundled 4B pair exists only for the explicit no-heavy-GPU/media fallback
    /// paths. It must never be persisted as the user's primary Standard role.</summary>
    public static bool IsFallbackOnlyModel(string modelPath) =>
        PathsEqual(modelPath, SmallModel) || PathsEqual(modelPath, SmallUncensoredModel);

    // ---- Generic small-dictionary override persistence, shared by the text-model role
    // overrides below and the media-checkpoint overrides further down. Each caller gets its own
    // file and its own in-memory cache (keyed by the file path itself, so two different override
    // kinds never collide even though the load/save logic is identical). ----
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>>
        _overrideCaches = new(StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> LoadOverrides(string path)
    {
        return _overrideCaches.GetOrAdd(path, static cachePath =>
        {
            try
            {
                return File.Exists(cachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cachePath)) ?? new()
                    : new();
            }
            catch { return new(); }
        });
    }

    private static Dictionary<string, string> ReadOverridesFromDiskOrCache(string path)
    {
        if (TryReadJsonDictionary(path, out Dictionary<string, string>? fresh))
            return fresh ?? new Dictionary<string, string>();
        return LoadOverrides(path);
    }

    private static void SaveOverrides(string path, Dictionary<string, string> overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Crash-safe: a torn write would read back as "no overrides" and silently drop a user's pin.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(overrides));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            // A transient sharing violation (antivirus/indexer/backup reader) must not lose a pin write.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(30 * (attempt + 1)); }
            }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        _overrideCaches[path] = overrides;
    }

    /// <summary>
    /// Serializes the read-modify-write these override files are updated with.
    ///
    /// The copy-on-write shape (build a new dictionary, then swap the cache entry) makes readers
    /// safe without a lock, but it does nothing for two writers: assigning two different roles
    /// concurrently had both threads read the same starting dictionary, each add its own key, and
    /// the second write silently discard the first. That is a lost update to persisted user
    /// configuration, and it is reachable - POST /api/v1/models/roles/{role} and
    /// POST /api/v1/runtime/components/{id}/locate are ordinary concurrent HTTP handlers, and
    /// RuntimeInstallService writes the build-tag file from a background job.
    ///
    /// One lock rather than one per path: these are small, infrequent configuration writes, and
    /// per-path granularity would buy nothing measurable for real contention this low.
    /// </summary>
    private static readonly object OverrideWriteGate = ManualSelectionGate.Sync;

    /// <summary>Loads, mutates and saves under <see cref="OverrideWriteGate"/>. The mutation
    /// receives a private copy, so readers holding the previous dictionary are never disturbed;
    /// returning false means nothing changed and no write is performed.</summary>
    internal static void MutateOverrides(string path, Func<Dictionary<string, string>, bool> mutate)
    {
        lock (OverrideWriteGate)
        {
            // Start from the file, not the per-process cache: another process may have written since this cache
            // entry was filled, and a cache-based read-modify-write would silently drop that process's pin.
            var working = new Dictionary<string, string>(ReadOverridesFromDiskOrCache(path), StringComparer.Ordinal);
            if (!mutate(working)) return;
            SaveOverrides(path, working);
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static string RoleOverridesPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "model-role-overrides.json");

    /// <summary>Per-model context choices, written by <see cref="ContextPreferenceStore"/>. Its own
    /// file rather than a section of the role overrides: a context preference outlives any role
    /// assignment, and a model can carry one without being assigned to a role at all.</summary>
    internal static string ContextPreferencesPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "model-context-preferences.json");

    /// <summary>The full set of role keys the text-model role system accepts, generalized from
    /// the original "standard"/"uncensored" pair per the directive's 16-role taxonomy (the
    /// remaining roles - IMAGE/IMAGE_EDIT/VIDEO/3D/STT/TTS/EMBEDDING/RERANKER - are backed by
    /// different mechanisms; see PaRuX.Contracts.ModelRoleCatalog). Only "standard" and
    /// "uncensored" have a built-in default file; the rest start unassigned (GetRoleOverride
    /// returns null, and there is no fallback) until a user explicitly assigns one.</summary>
    public static readonly string[] KnownTextRoles =
        { "standard", "uncensored", "fast", "reasoning", "planner", "coding", "reviewer", "vision" };

    /// <summary>"main" is the directive's name for the role this codebase already calls
    /// "standard" - accepted as an input alias so the API/UI can speak the new taxonomy without
    /// orphaning any real, already-persisted model-role-overrides.json entry keyed "standard".
    /// Returns null (not throwing) for a name outside KnownTextRoles so callers can decide how to
    /// report an unknown role.</summary>
    public static string? NormalizeTextRole(string role)
    {
        string trimmed = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed == "main") trimmed = "standard";
        return Array.IndexOf(KnownTextRoles, trimmed) >= 0 ? trimmed : null;
    }

    /// <summary>Known role keys: any of <see cref="KnownTextRoles"/> (accepts the "main" alias).
    /// Returns null when that role has never been overridden, or is unknown.</summary>
    public static string? GetRoleOverride(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return null;
        if (!LoadOverrides(RoleOverridesPath).TryGetValue(normalized, out string? path) || string.IsNullOrWhiteSpace(path))
            return null;
        return normalized == "standard" && IsFallbackOnlyModel(path) ? null : path;
    }

    /// <summary>The file currently backing a role, or null if unassigned. Unlike
    /// <see cref="StandardModel"/>/<see cref="UncensoredModel"/> (which always resolve to a real
    /// file via the built-in default), the newer roles have no default - returning null here is
    /// the honest "nothing assigned yet" state, not a fabricated fallback.</summary>
    public static string? GetRoleModelPathOrNull(string role)
    {
        string? normalized = NormalizeTextRole(role);
        return normalized switch
        {
            null => null,
            "standard" => StandardModel,
            "uncensored" => UncensoredModel,
            _ => GetRoleOverride(normalized)
        };
    }

    /// <summary>Returns a built-in projector only when the requested role/model pair is the
    /// matching bundled model. This deliberately depends on the requested path, not the persisted
    /// role override, so one-off loads retain the correct projector without changing preferences.</summary>
    public static string? GetBundledVisionProjectorForModel(string role, string modelPath)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return null;
        VisionProjectorManifest.BundledRoleModel? pair = VisionProjectorManifest.Default.BundledPairForRole(normalized);
        if (pair == null || !PathsEqual(modelPath, Path.Combine(ModelsPath, pair.ModelFileName))) return null;
        return Path.Combine(ModelsPath, "multimodal", pair.ProjectorFileName);
    }

    /// <summary>Persists which real GGUF file backs a role from now on.</summary>
    public static void SetRoleOverride(string role, string modelPath, string origin = RoleOverrideOrigin.User)
    {
        if (origin != RoleOverrideOrigin.User && origin != RoleOverrideOrigin.AutoAssigned)
            throw new InvalidDataException("Unknown role override origin.");
        string? normalized = NormalizeTextRole(role);
        if (normalized == null)
            throw new InvalidDataException("Unknown role \"" + role + "\". Known roles: " + string.Join(", ", KnownTextRoles) + ".");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidDataException("The selected model file does not exist.");
        if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .gguf model files can be assigned to a role.");
        if (normalized == "standard" && IsFallbackOnlyModel(modelPath))
            throw new InvalidDataException("Qwen3.5-4B is a fallback-only model and cannot replace the Qwen3.8-27B Standard profile.");

        using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite();
        SetRoleOverrideUnderLease(normalized, modelPath, origin);
    }

    private static void SetRoleOverrideUnderLease(string normalized, string modelPath, string origin)
    {
        string? defaultPath = normalized == "uncensored" ? DefaultUncensoredModel : normalized == "standard" ? DefaultStandardModel : null;
        if (defaultPath != null && PathsEqual(modelPath, defaultPath))
        {
            ClearRoleOverrideUnderLease(normalized);
            return;
        }

        lock (OverrideWriteGate)
        {
            // Origin first: a crash between the two writes then leaves an override that is at worst labelled
            // more strictly (user), never a user pin labelled as auto-assigned.
            MutateOverrides(RoleOverrideOriginsPath, working => { working[normalized] = OriginTag(origin, modelPath); return true; });
            MutateOverrides(RoleOverridesPath, working => { working[normalized] = modelPath; return true; });
        }
    }

    /// <summary>
    /// Origin bound to the override VALUE ("origin:hash(path)"): restoring or editing only the sidecar, or
    /// replacing only the override file, can never make a user pin read as auto-assigned. A bare legacy
    /// label or any hash mismatch has unknown provenance and stays pinned (fail closed).
    /// </summary>
    internal static string OriginTag(string origin, string modelPath)
    {
        string normalized;
        try { normalized = Path.GetFullPath(modelPath).ToLowerInvariant(); } catch (Exception) { normalized = modelPath.ToLowerInvariant(); }
        return origin + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    internal static bool IsAutoAssignedTag(string? tag, string? currentOverridePath) =>
        tag is not null && currentOverridePath is not null
        && string.Equals(tag, OriginTag(RoleOverrideOrigin.AutoAssigned, currentOverridePath), StringComparison.Ordinal);

    internal static string RoleOverrideOriginsPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "model-role-override-origins.json");

    /// <summary>"user", "auto-assigned", or "unknown-legacy" for an existing override. Null when the role has no override.</summary>
    public static string? GetRoleOverrideOrigin(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized is null) return null;
        return GetRoleOverrideOrigin(normalized, GetRoleOverride(normalized));
    }

    private static string? GetRoleOverrideOrigin(string normalized, string? currentOverridePath)
    {
        if (currentOverridePath is null) return null;
        if (!LoadOverrides(RoleOverrideOriginsPath).TryGetValue(normalized, out string? tag))
            return RoleOverrideOrigin.UnknownLegacy;
        if (IsAutoAssignedTag(tag, currentOverridePath)) return RoleOverrideOrigin.AutoAssigned;
        return string.Equals(tag, OriginTag(RoleOverrideOrigin.User, currentOverridePath), StringComparison.Ordinal)
            ? RoleOverrideOrigin.User
            : RoleOverrideOrigin.UnknownLegacy;
    }

    /// <summary>
    /// Auto-assignment: never replaces an override the user (or legacy data) owns. Returns false with a bounded
    /// reason when skipped or rejected; true when the auto-assigned override was written.
    /// </summary>
    public static bool TrySetAutoAssignedRoleOverride(string role, string modelPath, out string? skippedReason)
    {
        skippedReason = null;
        string? normalized = NormalizeTextRole(role);
        if (normalized is null) { skippedReason = "unknown_role"; return false; }
        try
        {
            using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite();
            lock (OverrideWriteGate)
            {
                string? currentOverridePath = GetRoleOverride(normalized);
                if (currentOverridePath is not null)
                {
                    string? currentOrigin = GetRoleOverrideOrigin(normalized, currentOverridePath);
                    if (currentOrigin != RoleOverrideOrigin.AutoAssigned)
                    {
                        skippedReason = currentOrigin == RoleOverrideOrigin.UnknownLegacy
                            ? RoleOverrideOrigin.UnknownLegacyReason
                            : "user_override_kept";
                        return false;
                    }
                }
                try
                {
                    ValidateRoleOverrideTarget(normalized, modelPath);
                    SetRoleOverrideUnderLease(normalized, modelPath, RoleOverrideOrigin.AutoAssigned);
                    return true;
                }
                catch (InvalidDataException) { skippedReason = "model_not_assignable"; return false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { skippedReason = "override_not_saved"; return false; }
            }
        }
        catch (ManualSelectionGateBusyException exception)
        { skippedReason = exception.Code; return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The lease itself can fail (unwritable data root, denied lock file, reparse-point lock). Report the
            // role as skipped like the write path does instead of aborting the whole auto-assignment run.
            skippedReason = "override_not_saved";
            return false;
        }
    }

    private static void ValidateRoleOverrideTarget(string normalized, string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidDataException("The selected model file does not exist.");
        if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .gguf model files can be assigned to a role.");
        if (normalized == "standard" && IsFallbackOnlyModel(modelPath))
            throw new InvalidDataException("Qwen3.5-4B is a fallback-only model and cannot replace the Qwen3.8-27B Standard profile.");
    }

    /// <summary>
    /// Authoritative, uncached disk read of whether a USER-origin override pins <paramref name="normalizedRole"/>:
    /// null (none / only auto-assigned), "manual_pin_role_override", or "manual_pin_unreadable" when the state
    /// cannot be trusted. An IOException is retried a bounded number of times before it is treated as unreadable.
    /// </summary>
    internal static string? UserRoleOverridePinReason(string normalizedRole, string? overridesPath = null,
        string? originsPath = null)
    {
        const string Unreadable = "manual_pin_unreadable";
        if (!TryReadJsonDictionary(overridesPath ?? RoleOverridesPath, out Dictionary<string, string>? overrides))
            return Unreadable;
        if (overrides is null || !overrides.TryGetValue(normalizedRole, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;
        if (!TryReadJsonDictionary(originsPath ?? RoleOverrideOriginsPath, out Dictionary<string, string>? origins))
            return Unreadable;
        return origins is not null && origins.TryGetValue(normalizedRole, out string? tag)
            && IsAutoAssignedTag(tag, value) ? null : "manual_pin_role_override";
    }

    private static bool TryReadJsonDictionary(string path, out Dictionary<string, string>? result)
    {
        result = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                result = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                return result is not null;
            }
            catch (JsonException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { Thread.Sleep(40); }
        }
        return false;
    }

    public static void ClearRoleOverride(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return;
        using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite();
        ClearRoleOverrideUnderLease(normalized);
    }

    private static void ClearRoleOverrideUnderLease(string normalized)
    {
        lock (OverrideWriteGate)
        {
            MutateOverrides(RoleOverridesPath, working => working.Remove(normalized));
            MutateOverrides(RoleOverrideOriginsPath, working => working.Remove(normalized));
        }
    }

    private static string CandidatePoolPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "model-role-candidate-pools.json");

    private static Dictionary<string, List<string>>? _candidatePoolCache;

    private static Dictionary<string, List<string>> LoadCandidatePools()
    {
        if (_candidatePoolCache != null) return _candidatePoolCache;
        Dictionary<string, List<string>> loaded;
        try
        {
            loaded = File.Exists(CandidatePoolPath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(CandidatePoolPath)) ?? new()
                : new();
        }
        catch { loaded = new(); }
        _candidatePoolCache = loaded;
        return loaded;
    }

    private static void SaveCandidatePools(Dictionary<string, List<string>> pools)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CandidatePoolPath)!);
        File.WriteAllText(CandidatePoolPath, JsonSerializer.Serialize(pools));
        _candidatePoolCache = pools;
    }

    public static List<string> ListCandidates(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return new List<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void AddIfReal(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (seen.Add(Path.GetFullPath(path))) result.Add(path);
        }

        if (LoadCandidatePools().TryGetValue(normalized, out List<string>? pool))
            foreach (string path in pool) AddIfReal(path);
        AddIfReal(GetRoleModelPathOrNull(normalized));
        return result;
    }

    public static void AddCandidate(string role, string modelPath)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null)
            throw new InvalidDataException("Unknown role \"" + role + "\". Known roles: " + string.Join(", ", KnownTextRoles) + ".");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidDataException("The selected model file does not exist.");
        if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only a .gguf model file can be added as a candidate.");
        if (normalized == "standard" && IsFallbackOnlyModel(modelPath))
            throw new InvalidDataException("Qwen3.5-4B is a fallback-only model and cannot be a Standard-role candidate.");

        Dictionary<string, List<string>> pools = LoadCandidatePools();
        List<string> existing = pools.TryGetValue(normalized, out List<string>? found) ? new List<string>(found) : new List<string>();
        if (!existing.Any(p => PathsEqual(p, modelPath))) existing.Add(modelPath);
        Dictionary<string, List<string>> updated = new(pools) { [normalized] = existing };
        SaveCandidatePools(updated);
    }

    public static void RemoveCandidate(string role, string modelPath)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return;
        Dictionary<string, List<string>> pools = LoadCandidatePools();
        if (!pools.TryGetValue(normalized, out List<string>? existing)) return;
        List<string> updated = existing.Where(p => !PathsEqual(p, modelPath)).ToList();
        Dictionary<string, List<string>> saved = new(pools) { [normalized] = updated };
        SaveCandidatePools(saved);
    }

    private static string DefaultConfigRoot => PaRuX.Contracts.PaRuXDataRoot.Path;

    public static Dictionary<string, string> ExportBackupConfig(string? root = null)
    {
        string configRoot = root ?? DefaultConfigRoot;
        var result = new Dictionary<string, string>();
        void AddIfExists(string key, string fileName)
        {
            string path = Path.Combine(configRoot, fileName);
            if (File.Exists(path)) result[key] = File.ReadAllText(path);
        }
        AddIfExists("role-overrides", "model-role-overrides.json");
        // The origin sidecar is bound to the override values; the two are backed up and restored as one unit.
        AddIfExists("role-override-origins", "model-role-override-origins.json");
        AddIfExists("media-checkpoint-overrides", "media-checkpoint-overrides.json");
        AddIfExists("candidate-pools", "model-role-candidate-pools.json");
        AddIfExists("role-adapters", "model-role-adapters.json");
        AddIfExists("component-locations", "component-locations.json");
        AddIfExists("component-skips", "component-skips.json");
        return result;
    }

    public static void ValidateBackupConfig(Dictionary<string, string> payload)
    {
        if (payload.TryGetValue("role-overrides", out string? roleOverrides))
            JsonSerializer.Deserialize<Dictionary<string, string>>(roleOverrides);
        if (payload.TryGetValue("role-override-origins", out string? roleOverrideOrigins))
            JsonSerializer.Deserialize<Dictionary<string, string>>(roleOverrideOrigins);
        if (payload.TryGetValue("media-checkpoint-overrides", out string? mediaOverrides))
            JsonSerializer.Deserialize<Dictionary<string, string>>(mediaOverrides);
        if (payload.TryGetValue("candidate-pools", out string? candidatePools))
            JsonSerializer.Deserialize<Dictionary<string, List<string>>>(candidatePools);
        if (payload.TryGetValue("role-adapters", out string? roleAdapters))
            JsonSerializer.Deserialize<Dictionary<string, string>>(roleAdapters);
        if (payload.TryGetValue("component-locations", out string? componentLocations))
            JsonSerializer.Deserialize<Dictionary<string, string>>(componentLocations);
        if (payload.TryGetValue("component-skips", out string? componentSkips))
            JsonSerializer.Deserialize<Dictionary<string, string>>(componentSkips);
    }

    public static void ImportBackupConfig(Dictionary<string, string> payload, string? root = null)
    {
        string configRoot = root ?? DefaultConfigRoot;
        void WriteIfPresent(string key, string fileName)
        {
            if (payload.TryGetValue(key, out string? json) && !string.IsNullOrEmpty(json))
            {
                Directory.CreateDirectory(configRoot);
                File.WriteAllText(Path.Combine(configRoot, fileName), json);
            }
        }

        WriteIfPresent("role-overrides", "model-role-overrides.json");
        if (payload.ContainsKey("role-overrides"))
        {
            // Restore overrides and origins together: a backup that has overrides but no origins must not leave a
            // newer local sidecar behind (an absent sidecar reads as user-owned, i.e. fail closed).
            if (payload.ContainsKey("role-override-origins")) WriteIfPresent("role-override-origins", "model-role-override-origins.json");
            else File.Delete(Path.Combine(configRoot, "model-role-override-origins.json"));
        }
        WriteIfPresent("media-checkpoint-overrides", "media-checkpoint-overrides.json");
        WriteIfPresent("candidate-pools", "model-role-candidate-pools.json");
        WriteIfPresent("role-adapters", "model-role-adapters.json");
        WriteIfPresent("component-locations", "component-locations.json");
        WriteIfPresent("component-skips", "component-skips.json");

        _overrideCaches.Clear();
        _candidatePoolCache = null;
    }

    // ---- Managed component discovery: user-located paths for SYSTEM_DETECTED components ----
    private static string ComponentLocationsPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "component-locations.json");

    private static bool IsManagedOnlyComponentId(string componentId) =>
        string.Equals(componentId, "python", StringComparison.OrdinalIgnoreCase)
        || string.Equals(componentId, "uv", StringComparison.OrdinalIgnoreCase);

    /// <summary>User-provided path for a genuinely USER_MANAGED component. PaRuX-owned Python/uv
    /// are hard-denied here as a second-line invariant below the API/ComponentInventory surface,
    /// so stale sidecar entries or future callers cannot silently re-enable a system fallback.</summary>
    public static string? GetComponentLocation(string componentId)
    {
        string id = (componentId ?? string.Empty).Trim();
        if (IsManagedOnlyComponentId(id)) return null;
        return LoadOverrides(ComponentLocationsPath).TryGetValue(id, out string? path) && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    public static void SetComponentLocation(string componentId, string path)
    {
        string id = (componentId ?? string.Empty).Trim();
        if (id.Length == 0) throw new InvalidDataException("Unknown component id.");
        if (IsManagedOnlyComponentId(id))
            throw new InvalidDataException(id + " is PaRuX-managed and cannot be bound to a user/system executable.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException("The selected path does not exist.");
        string fullPath = Path.GetFullPath(path);
        MutateOverrides(ComponentLocationsPath, working => { working[id] = fullPath; return true; });
    }

    public static void ClearComponentLocation(string componentId)
    {
        string id = (componentId ?? string.Empty).Trim();
        if (IsManagedOnlyComponentId(id)) return;
        MutateOverrides(ComponentLocationsPath, working => working.Remove(id));
    }

    private static string ComponentSkipsPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "component-skips.json");

    public static bool IsComponentSkipped(string componentId)
    {
        string id = (componentId ?? string.Empty).Trim();
        return !IsManagedOnlyComponentId(id) && LoadOverrides(ComponentSkipsPath).ContainsKey(id);
    }

    public static void SetComponentSkipped(string componentId)
    {
        string id = (componentId ?? string.Empty).Trim();
        if (id.Length == 0) throw new InvalidDataException("Unknown component id.");
        if (IsManagedOnlyComponentId(id))
            throw new InvalidDataException(id + " is PaRuX-managed and cannot be skipped as an external prerequisite.");
        string stamp = DateTime.UtcNow.ToString("o");
        MutateOverrides(ComponentSkipsPath, working => { working[id] = stamp; return true; });
    }

    public static void ClearComponentSkipped(string componentId)
    {
        string id = (componentId ?? string.Empty).Trim();
        if (IsManagedOnlyComponentId(id)) return;
        MutateOverrides(ComponentSkipsPath, working => working.Remove(id));
    }

    // ---- LoRA-at-inference role adapter assignment ----
    private static string RoleAdapterPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "model-role-adapters.json");

    public static string? GetRoleAdapter(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return null;
        return LoadOverrides(RoleAdapterPath).TryGetValue(normalized, out string? path) && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    public static void SetRoleAdapter(string role, string adapterPath)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null)
            throw new InvalidDataException("Unknown role \"" + role + "\". Known roles: " + string.Join(", ", KnownTextRoles) + ".");
        if (string.IsNullOrWhiteSpace(adapterPath) || !File.Exists(adapterPath))
            throw new InvalidDataException("The selected adapter file does not exist.");
        if (!string.Equals(Path.GetExtension(adapterPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Only a GGUF-format LoRA adapter can be attached at inference time. This file is not a .gguf - "
                + "if it came from Training Lab, convert it first with llama.cpp's convert_lora_to_gguf.py.");

        MutateOverrides(RoleAdapterPath, working => { working[normalized] = adapterPath; return true; });
    }

    public static void ClearRoleAdapter(string role)
    {
        string? normalized = NormalizeTextRole(role);
        if (normalized == null) return;
        MutateOverrides(RoleAdapterPath, working => working.Remove(normalized));
    }

    public static string UiConfig => Path.Combine(ConfigDir, "llama-ui.json");
    public static string ImageWorkflowTemplate => Path.Combine(ConfigDir, "z-image-smoke-api.json");
    public static string VideoWorkflowTemplate => Path.Combine(ConfigDir, "wan-video-smoke-api.json");
    public static string EditWorkflowTemplate => Path.Combine(ConfigDir, "flux2-klein-edit-api.json");
    public static string Img2ImgWorkflowTemplate => Path.Combine(ConfigDir, "z-image-img2img-api.json");
    public static string UpscaleWorkflowTemplate => Path.Combine(ConfigDir, "upscale-api.json");

    public static string DiffusionModelsPath => Path.Combine(ComfyApp, "models", "diffusion_models");

    public static List<string> ListDiffusionModelCheckpoints()
    {
        if (!Directory.Exists(DiffusionModelsPath)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(DiffusionModelsPath)
                .Where(f => new FileInfo(f).Length > 0)
                .Select(f => Path.GetFileName(f)!)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string MediaCheckpointOverridesPath => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "media-checkpoint-overrides.json");

    public static string? GetMediaCheckpointOverride(string type) =>
        LoadOverrides(MediaCheckpointOverridesPath).TryGetValue(type, out string? file) && !string.IsNullOrWhiteSpace(file) ? file : null;

    public static void SetMediaCheckpointOverride(string type, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !ListDiffusionModelCheckpoints().Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("That checkpoint file was not found in the diffusion_models folder.");
        // Family gate: an override is saved only if this workflow can run the file's family. A wrong-family
        // file would otherwise fail late, inside ComfyUI.
        MediaCheckpointResolution.EnsureFamilyAllowed(type, Path.Combine(DiffusionModelsPath, fileName));

        MutateOverrides(MediaCheckpointOverridesPath, working => { working[type] = fileName; return true; });
    }

    public static void ClearMediaCheckpointOverride(string type)
    {
        MutateOverrides(MediaCheckpointOverridesPath, working => working.Remove(type));
    }

    /// <summary>Test seam mirroring <see cref="ScopedRoot"/>: StateDir is a real, machine-global
    /// location (mode/active-role/PID/game-marker files) intentionally independent of Root's
    /// installation-directory resolution, so it needs its own async-local override rather than
    /// reusing ScopedRoot. Process-launched UI/integration tests use the
    /// PARUX_LOCALAI_STATE_DIR environment override from PaRuXLocalAiStateRoot. Without these seams,
    /// a hermetic test that starts/stops "runtime actions" (Ensure,
    /// GameStart/GameStop, role changes) reads and writes the operator's real
    /// %LOCALAPPDATA%\LocalAI\*.txt/*.json state files - observed on a physical run: an interactive
    /// GameStart/GameStop probe against the real app left a stray game-active.json marker that then
    /// made an unrelated, supposedly-isolated xUnit test see "a game is currently active" for real.</summary>
    private static readonly AsyncLocal<string?> ScopedStateDir = new();
    private static volatile string? _testStateDirFallback;

    internal static IDisposable UseStateDirForTests(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A scoped state directory is required.", nameof(path));
        Directory.CreateDirectory(path);
        return new StateDirScope(path);
    }

    private sealed class StateDirScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        internal StateDirScope(string path)
        {
            _previous = ScopedStateDir.Value;
            ScopedStateDir.Value = path;
            _testStateDirFallback = path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedStateDir.Value = _previous;
            _testStateDirFallback = _previous;
        }
    }

    public static string StateDir => ScopedStateDir.Value ?? _testStateDirFallback
        ?? PaRuX.Contracts.PaRuXLocalAiStateRoot.Path;

    private static readonly AsyncLocal<int?> ScopedTextPort = new();
    private static volatile int _testTextPortFallback;
    // The port the last text launch chose after skipping listeners that other programs hold. 0 means the preferred port.
    private static volatile int _chosenTextPort;

    /// <summary>The port the text runtime listens on: the port the last launch chose, else the preferred port. Health, ownership,
    /// the chat proxy and token counting all read this value, so a launch that moved off the preferred port is seen everywhere.</summary>
    internal static int TextPort => _chosenTextPort is > 0 and <= 65535 ? _chosenTextPort : PreferredTextPort;

    /// <summary>The port a launch tries first: the shipped 1234 unless a test seam says otherwise.</summary>
    internal static int PreferredTextPort => ScopedTextPort.Value ?? (_testTextPortFallback is > 0 and <= 65535
        ? _testTextPortFallback : 1234);

    /// <summary>Records the port a launch chose. The preferred port is stored as 0, so a free preferred port leaves TextPort unchanged.</summary>
    internal static void SetChosenTextPort(int port)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _chosenTextPort = port == PreferredTextPort ? 0 : port;
    }

    /// <summary>Isolates loopback runtime tests from a user's real listener without changing the
    /// shipped default port. The suite is serialized because this seam also supports in-process
    /// web-host execution contexts that cannot inherit the caller's AsyncLocal directly.</summary>
    internal static IDisposable UseTextPortForTests(int port)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new TextPortScope(port);
    }

    private sealed class TextPortScope : IDisposable
    {
        private readonly int? _previous;
        private readonly int _previousFallback;
        private readonly int _previousChosen;
        private bool _disposed;

        internal TextPortScope(int port)
        {
            _previous = ScopedTextPort.Value;
            _previousFallback = _testTextPortFallback;
            _previousChosen = _chosenTextPort;
            ScopedTextPort.Value = port;
            _testTextPortFallback = port;
            _chosenTextPort = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedTextPort.Value = _previous;
            _testTextPortFallback = _previousFallback;
            _chosenTextPort = _previousChosen;
        }
    }
    public static string ModeFile => Path.Combine(StateDir, "mode.txt");
    public static string TextProfileFile => Path.Combine(StateDir, "text-profile.txt");
    public static string GameMarkerFile => Path.Combine(StateDir, "game-active.json");
    public static string TextPidFile => Path.Combine(StateDir, "llama-server.pid");
    public static string MediaPidFile => Path.Combine(StateDir, "comfyui.pid");
    public static string ActiveProfileFile => Path.Combine(StateDir, "active-profile.txt");
    public static string ActiveRoleFile => Path.Combine(StateDir, "active-role.txt");
    public static string ActiveModelPathFile => Path.Combine(StateDir, "active-model-path.txt");
    public static string ActiveModelSha256File => Path.Combine(StateDir, "active-model-sha256.txt");
    public static string RuntimeInstallJobsFile => Path.Combine(StateDir, "runtime-install-jobs.json");

    /// <summary>The vision projector (--mmproj) the running text runtime was ACTUALLY launched
    /// with, or absent when it is serving text-only. Written by LocalAiProcessManager from the
    /// real argument list it hands to llama-server, never from what a role is configured to want:
    /// on a machine with no heavy GPU the flagship 27B+projector profile is deliberately replaced
    /// by the text-only 4B fallback, and anything that needs to send an image (Vision, Computer
    /// Use) must be able to see that substitution instead of trusting the role's configuration.
    /// </summary>
    public static string ActiveVisionProjectorFile => Path.Combine(StateDir, "active-vision-projector.txt");

    /// <summary>A LocalAI root is operationally configured when either managed text backend is
    /// present or ComfyUI is present. Vulkan is intentionally included: the zero-prerequisite MSI
    /// baseline is Vulkan, so a clean first launch must not remain stuck in "not configured" after
    /// the verified baseline bootstrap has installed llama-server.exe.</summary>
    public static bool LooksConfigured =>
        File.Exists(Path.Combine(RuntimeCudaPath, LlamaServerExecutable))
        || File.Exists(Path.Combine(RuntimeVulkanPath, LlamaServerExecutable))
        || File.Exists(ComfyMain);

    /// <summary>True once a user has explicitly picked a root via SetRoot (Settings), as opposed
    /// to the auto-detected default.</summary>
    public static bool IsExplicitlyConfigured => File.Exists(ConfigPath);

    private static string ResolveRoot()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                PathsConfig? saved = JsonSerializer.Deserialize<PathsConfig>(File.ReadAllText(ConfigPath));
                if (saved != null && !string.IsNullOrWhiteSpace(saved.Root) && Directory.Exists(saved.Root))
                    return saved.Root;
            }
        }
        catch { /* fall through to auto-detect */ }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string legacyCandidate = Path.Combine(documents, "LocalAI");
        if (Directory.Exists(Path.Combine(legacyCandidate, "runtime")) || Directory.Exists(Path.Combine(legacyCandidate, "models")))
            return legacyCandidate;

        return Path.Combine(PaRuX.Contracts.PaRuXDataRoot.Path, "LocalAI");
    }

    private sealed class PathsConfig
    {
        public string Root { get; set; } = string.Empty;
    }
}
