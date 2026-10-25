using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Server;

/// <summary>
/// Versioned (<c>ld1</c>) semantic identity of one single-model llama-server launch. It is derived
/// from the exact argument vector and launch environment that the production planner hands to the
/// process, never from a client-supplied description. Ephemeral routing details (model path, alias,
/// port, loopback host, web-UI presentation) are excluded from the fingerprint; the model is bound
/// separately: <see cref="ModelPath"/> records which file this launch serves (never hashed into the
/// configuration identity) and its exact bytes are verified by the exact-identity proof. Every other argument is semantic and must match exactly.
///
/// Fail-closed by construction: an unknown flag, a duplicated flag, a non-loopback host, a router
/// launch, or an argument that silently pulls in a second file (vision projector, LoRA adapter,
/// draft model) yields no descriptor, so no evidence can be called comparable to that launch.
/// </summary>
internal sealed record TextLaunchDescriptor(
    int Version,
    string Backend,
    ImmutableArray<string> Arguments,
    ImmutableArray<string> Environment,
    string Fingerprint,
    string ModelPath)
{
    internal const int CurrentVersion = 1;
    internal const string FingerprintPrefix = "ld1-sha256:";
    private const string DigestDomain = "parux-text-launch-descriptor-v1\n";
    private const int MaximumArguments = 256;
    private const int MaximumValueLength = 128;
    private const int MaximumEnvironmentValueLength = 1024;

    /// <summary>Flags whose next token is their value and which change inference/residency.</summary>
    private static readonly HashSet<string> SemanticValueFlags = new(StringComparer.Ordinal)
    {
        "--ctx-size", "--batch-size", "--ubatch-size", "--cache-type-k", "--cache-type-v",
        "--flash-attn", "--gpu-layers", "--fit", "--fit-target", "--slot-prompt-similarity",
        "--reasoning", "--reasoning-effort", "--reasoning-budget", "--load-mode", "--prio", "--poll",
        "--rope-scaling", "--rope-scale", "--parallel", "--threads", "--threads-batch",
        "--cpu-strict", "--cpu-strict-batch", "--numa", "--split-mode", "--main-gpu", "--device",
        "--tensor-split", "--spec-type", "--spec-draft-n-max", "--spec-draft-device",
        "--sleep-idle-seconds", "--temp", "--top-p", "--top-k", "--min-p", "--presence-penalty",
        "--repeat-penalty", "--image-min-tokens", "--image-max-tokens"
    };

    /// <summary>Boolean flags that change inference/residency.</summary>
    private static readonly HashSet<string> SemanticSwitches = new(StringComparer.Ordinal)
    {
        "--jinja", "--cont-batching", "--no-cont-batching", "--no-kv-offload", "--backend-sampling",
        "--reasoning-preserve"
    };

    /// <summary>Ephemeral per-launch values; excluded from the identity (model bytes are bound separately).</summary>
    private static readonly HashSet<string> EphemeralValueFlags = new(StringComparer.Ordinal)
    {
        "--model", "--alias", "--port", "--host", "--ui-config-file"
    };

    private static readonly HashSet<string> EphemeralSwitches = new(StringComparer.Ordinal) { "--ui" };

    /// <summary>
    /// Arguments that bind another file or a multi-model router. Their identity is not captured by
    /// the Candidate's exact bytes, so such a launch is unsupported rather than "approximately equal".
    /// </summary>
    private static readonly HashSet<string> UnsupportedIdentityFlags = new(StringComparer.Ordinal)
    {
        "--mmproj", "--media-path", "--lora", "--spec-draft-model", "--models-preset", "--models-max",
        "--models-autoload", "--models-dir", "--chat-template-file", "--grammar-file", "--system-prompt-file"
    };

    /// <summary>
    /// Environment prefixes that steer llama.cpp, its backends, device/driver selection or CPU math
    /// libraries. A child inherits Core's environment, so these are as semantic as flags.
    /// </summary>
    private static readonly string[] LaunchEnvironmentPrefixes =
    {
        "LLAMA_", "GGML_", "CUDA_", "VK_", "HSA_", "HIP_", "ROCR_", "GPU_", "ONEAPI_", "ZE_", "SYCL_",
        "OMP_", "KMP_", "MKL_", "OPENBLAS_", "__NV_"
    };

    /// <summary>Exact credential names that never change inference; dropped, never copied.</summary>
    private static readonly HashSet<string> CredentialEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLAMA_API_KEY", "LLAMA_ARG_API_KEY", "LLAMA_ARG_API_KEY_FILE", "LLAMA_ARG_SSL_KEY_FILE",
        "LLAMA_ARG_SSL_CERT_FILE", "LLAMA_ARG_HF_TOKEN"
    };

    private static readonly HashSet<string> CredentialSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "CREDENTIALS", "AUTH"
    };

    /// <summary>PaRuX-pinned server endpoint switches with no flag equivalent in the planner tables.</summary>
    private static readonly HashSet<string> SemanticArgumentEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLAMA_ARG_ENDPOINT_SLOTS", "LLAMA_ARG_ENDPOINT_METRICS", "LLAMA_ARG_ENDPOINT_PROPS"
    };

    /// <summary>llama.cpp's environment spellings that differ from the flag spelling.</summary>
    private static readonly Dictionary<string, string> ArgumentEnvironmentAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LLAMA_ARG_N_GPU_LAYERS"] = "--gpu-layers", ["LLAMA_ARG_N_PARALLEL"] = "--parallel",
        ["LLAMA_ARG_BATCH"] = "--batch-size", ["LLAMA_ARG_UBATCH"] = "--ubatch-size",
        ["LLAMA_ARG_MMPROJ"] = "--mmproj", ["LLAMA_ARG_MODEL_DRAFT"] = "--spec-draft-model"
    };

        // Operator note: the descriptor hashes every launch-relevant environment variable (CUDA_*, GGML_*, OMP_*,
    // LLAMA_*, GPU selection). Changing any of them, or the driver/runtime build, changes the descriptor/host
    // attestation and therefore invalidates previously collected evidence by design; re-evaluate afterwards.
internal static bool IsLaunchRelevantEnvironment(string name) =>
        LaunchEnvironmentPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && !CredentialEnvironment.Contains(name);

    /// <summary>
    /// Classifies one launch-relevant variable: null means hash it as semantic identity; otherwise the
    /// refusal reason. <c>LLAMA_ARG_*</c> is mapped onto the flag tables so a file-binding, router,
    /// download or unknown argument cannot enter through the environment.
    /// </summary>
    private static string? ClassifyEnvironment(string name)
    {
        if (name.Split('_', StringSplitOptions.RemoveEmptyEntries).Any(CredentialSegments.Contains))
            return "credential_environment";
        if (!name.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase)) return null;
        if (SemanticArgumentEnvironment.Contains(name)) return null;
        string flag = ArgumentEnvironmentAliases.TryGetValue(name, out string? alias)
            ? alias
            : "--" + name["LLAMA_ARG_".Length..].ToLowerInvariant().Replace('_', '-');
        if (UnsupportedIdentityFlags.Contains(flag)) return "unbound_file_or_router_environment";
        return SemanticValueFlags.Contains(flag) || SemanticSwitches.Contains(flag) ? null : "unknown_argument_environment";
    }
    /// <summary>The launch-relevant environment a child started now would see: this process's
    /// environment overlaid with the planner's explicit overrides.</summary>
    internal static IReadOnlyDictionary<string, string> EffectiveEnvironment(
        IReadOnlyDictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string name && entry.Value is string value && IsLaunchRelevantEnvironment(name))
                result[name] = value;
        if (overrides is not null)
            foreach ((string name, string value) in overrides) result[name] = value;
        return result;
    }

    /// <summary>The launch-relevant part of a ProcessStartInfo environment, after overrides were applied.</summary>
    internal static IReadOnlyDictionary<string, string> RelevantEnvironment(IDictionary<string, string?> environment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string? value) in environment)
            if (value is not null && IsLaunchRelevantEnvironment(name)) result[name] = value;
        return result;
    }

    internal static bool IsActualLaunchFingerprint(string? value) =>
        value is { Length: 75 } && value.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
        && value.AsSpan(FingerprintPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static string? BackendForRuntimeDirectory(string? runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory)) return null;
        try
        {
            string full = Path.GetFullPath(runtimeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, Path.GetFullPath(LocalAiPaths.RuntimeCudaPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return "text-cuda";
            if (string.Equals(full, Path.GetFullPath(LocalAiPaths.RuntimeVulkanPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return "text-vulkan";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    internal static TextLaunchDescriptorResult Create(string? backend, IReadOnlyList<string>? arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (backend is not ("text-cuda" or "text-vulkan")) return Refuse("backend_unsupported");
        if (arguments is null || arguments.Count == 0 || arguments.Count > MaximumArguments)
            return Refuse("arguments_unavailable");

        var semantic = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? modelPath = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string flag = arguments[index];
            if (!seen.Add(flag)) return Refuse("duplicate_argument");
            if (UnsupportedIdentityFlags.Contains(flag)) return Refuse("unbound_file_or_router_argument");
            bool switchFlag = SemanticSwitches.Contains(flag) || EphemeralSwitches.Contains(flag);
            bool valueFlag = SemanticValueFlags.Contains(flag) || EphemeralValueFlags.Contains(flag);
            if (!switchFlag && !valueFlag) return Refuse("unknown_argument");
            if (switchFlag)
            {
                if (SemanticSwitches.Contains(flag)) semantic[flag] = null;
                continue;
            }
            if (index + 1 >= arguments.Count) return Refuse("missing_value");
            string value = arguments[++index];
            // Values are bounded printable text. Negative numbers such as "-1" are legitimate
            // values, but a "--" token is a flag, which means this flag's value is missing.
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal))
                return Refuse("missing_value");
            if (flag == "--model") { modelPath = value; continue; }
            if (flag == "--host")
            {
                if (value != "127.0.0.1") return Refuse("non_loopback_host");
                continue;
            }
            if (EphemeralValueFlags.Contains(flag)) continue;
            if (value.Length > MaximumValueLength || value.Any(ch => ch < 0x21 || ch > 0x7e))
                return Refuse("invalid_value");
            semantic[flag] = value;
        }
        if (modelPath is null) return Refuse("single_model_launch_required");

        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
            {
                if (!IsLaunchRelevantEnvironment(name)) continue;
                if (ClassifyEnvironment(name) is { } refusal) return Refuse(refusal);
                // Windows variable names are case-insensitive; canonicalize so case cannot split identity.
                string canonicalName = name.ToUpperInvariant();
                if (value is null || value.Length > MaximumEnvironmentValueLength || value.Any(ch => ch < 0x20 || ch > 0x7e)
                    || !env.TryAdd(canonicalName, value))
                    return Refuse("invalid_environment");
            }
        }

        ImmutableArray<string> canonical = semantic
            .Select(pair => pair.Value is null ? pair.Key : pair.Key + "=" + pair.Value).ToImmutableArray();
        ImmutableArray<string> canonicalEnvironment = env.Select(pair => pair.Key + "=" + pair.Value).ToImmutableArray();
        string fingerprint = FingerprintPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            DigestDomain + "backend=" + backend + "\nargs\n" + string.Join('\n', canonical)
            + "\nenv\n" + string.Join('\n', canonicalEnvironment)))).ToLowerInvariant();
        return new TextLaunchDescriptorResult(
            new TextLaunchDescriptor(CurrentVersion, backend, canonical, canonicalEnvironment, fingerprint, modelPath), null);
    }

    private static TextLaunchDescriptorResult Refuse(string reason) => new(null, reason);
}

internal sealed record TextLaunchDescriptorResult(TextLaunchDescriptor? Descriptor, string? Reason);
