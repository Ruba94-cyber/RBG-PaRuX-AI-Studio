using System.Text.Json;

namespace PaRuX.Server;

internal sealed class WindowsMlGenAiWorkerEvent
{
    public int ApiVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? Error { get; set; }
    public string? Provider { get; set; }
    public string? ProviderLibraryPath { get; set; }
    public string? ModelPath { get; set; }
    public string? ThinkingControl { get; set; }
    public string? CancellationControl { get; set; }
    public string? PromptCountingControl { get; set; }
    public string? DeviceEvidence { get; set; }
    public string? PinnedAdapter { get; set; }
    public string? AdapterEvidence { get; set; }
    public List<string> CompatibleDevices { get; set; } = new();
    public int PromptTokens { get; set; }
    public int GeneratedTokens { get; set; }
}

/// <summary>
/// Shared, platform-neutral validation for the Windows-only WinML worker. Keeping protocol and
/// identity rules in Server lets the normal regression suite prove fail-closed behaviour without
/// loading WinRT or requiring the test machine to have an NPU/GPU execution provider installed.
/// </summary>
internal static class WindowsMlGenAiProtocol
{
    /// <summary>v4 adds exact rendered-prompt token counting on the resident tokenizer/template.</summary>
    public const int WorkerApiVersion = 4;
    public const string GenAiRuntimeVersion = "0.15.2";
    public const string ThinkingControlJinja = "jinja-enable-thinking";
    public const string RequestCancellationControl = "request-id-cancel-drain";
    public const string PromptCountingControl = "exact-rendered-prompt-count";
    public const string SingleCompatibleDeviceEvidence = "single-compatible-ep-device";
    private const string IdentityPrefix = "ort-genai-winml/" + GenAiRuntimeVersion;
    private static readonly IReadOnlyDictionary<string, string> GenAiProviderNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NvTensorRtRtxExecutionProvider"] = "NvTensorRtRtx",
            ["VitisAIExecutionProvider"] = "VitisAI",
            ["OpenVINOExecutionProvider"] = "OpenVINO",
            ["QNNExecutionProvider"] = "QNN",
            // ONNX Runtime built-in providers reported by the worker only for an explicit provider-named
            // request; they are never produced by Windows ML discovery.
            ["CPUExecutionProvider"] = "cpu",
            ["DmlExecutionProvider"] = "dml"
        };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string ResolveWorkerPath(string? explicitPath = null)
    {
        foreach (string? candidate in new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable(WindowsMlPerformancePackService.WorkerEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, "winml", "PaRuX.WinMlWorker.exe"),
            Path.Combine(AppContext.BaseDirectory, "PaRuX.WinMlWorker.exe")
        })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        throw new FileNotFoundException("PaRuX Windows ML worker is not installed.");
    }

    public static bool HasServingLayout(string artifactPath, out string reason)
    {
        try
        {
            string path = Path.GetFullPath(artifactPath ?? string.Empty);
            if (!Directory.Exists(path))
            {
                reason = "Windows ML GenAI serving requires an ONNX model directory.";
                return false;
            }
            if (!File.Exists(Path.Combine(path, "genai_config.json")))
            {
                reason = "Windows ML GenAI model directory is missing genai_config.json.";
                return false;
            }
            if (!File.Exists(Path.Combine(path, "chat_template.jinja")))
            {
                reason = "Windows ML GenAI model directory is missing chat_template.jinja.";
                return false;
            }
            bool graph = Directory.EnumerateFiles(path, "*.onnx", SearchOption.AllDirectories).Any();
            if (!graph)
            {
                reason = "Windows ML GenAI model directory contains no ONNX graph.";
                return false;
            }
            reason = "ONNX Runtime GenAI layout is complete.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static string ValidateProviderName(string? value)
    {
        string provider = (value ?? string.Empty).Trim();
        if (provider.Length is < 3 or > 128
            || provider.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new InvalidDataException("An exact Windows ML execution-provider name is required.");
        return provider;
    }

    public static string ToGenAiProviderName(string? windowsMlProviderName)
    {
        string provider = ValidateProviderName(windowsMlProviderName);
        if (!GenAiProviderNames.TryGetValue(provider, out string? genAi))
            throw new NotSupportedException(
                "Windows ML provider " + provider + " is not currently supported by ONNX Runtime GenAI "
                + GenAiRuntimeVersion + ". PaRuX will not advertise or benchmark it as a generative engine.");
        return genAi;
    }

    public static bool IsSupportedGenAiProvider(string? windowsMlProviderName)
    {
        try { _ = ToGenAiProviderName(windowsMlProviderName); return true; }
        catch { return false; }
    }

    public const string DirectMlProviderName = "DmlExecutionProvider";
    public const string AdapterExecutionEvidencePrefix = "process-video-memory ";

    /// <summary>Adapter pin grammar: lowercase hex PCI vendor and device id, "vvvv:dddd". Only the
    /// DirectML built-in provider accepts a pin, and it requires one (see ValidateReadyHandshake).</summary>
    public static string ValidateAdapterPin(string? pin)
    {
        string value = (pin ?? string.Empty).Trim().ToLowerInvariant();
        if (!TryParseAdapterPin(value, out _, out _))
            throw new InvalidDataException("A DirectML adapter pin must look like vvvv:dddd (hex PCI vendor:device).");
        return value;
    }

    public static bool TryParseAdapterPin(string? pin, out uint vendorId, out uint deviceId)
    {
        vendorId = 0;
        deviceId = 0;
        string[] parts = (pin ?? string.Empty).Trim().Split(':');
        return parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 4
            && uint.TryParse(parts[0], System.Globalization.NumberStyles.AllowHexSpecifier, null, out vendorId)
            && uint.TryParse(parts[1], System.Globalization.NumberStyles.AllowHexSpecifier, null, out deviceId);
    }

    public static string BuildRuntimeIdentity(WindowsMlProviderInfo provider, string? adapterPin = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string name = ValidateProviderName(provider.Name);
        _ = ToGenAiProviderName(name);
        string version = NormalizePart(provider.LibraryFileVersion, "unknown");
        string sha = NormalizeSha(provider.LibrarySha256) ?? "unknown";
        string identity = string.Join('|', IdentityPrefix, name, version, sha);
        if (!string.IsNullOrWhiteSpace(adapterPin))
        {
            if (!string.Equals(name, DirectMlProviderName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only the DirectML provider accepts an adapter pin.");
            identity += "|adapter=" + ValidateAdapterPin(adapterPin);
        }
        return identity;
    }

    public static bool TryParseRuntimeIdentity(
        string? value,
        out string providerName,
        out string providerVersion,
        out string providerSha) =>
        TryParseRuntimeIdentity(value, out providerName, out providerVersion, out providerSha, out _);

    public static bool TryParseRuntimeIdentity(
        string? value,
        out string providerName,
        out string providerVersion,
        out string providerSha,
        out string? adapterPin)
    {
        providerName = string.Empty;
        providerVersion = string.Empty;
        providerSha = string.Empty;
        adapterPin = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Trim().Split('|');
        if (parts.Length is not (4 or 5) || !string.Equals(parts[0], IdentityPrefix, StringComparison.Ordinal)) return false;
        try
        {
            providerName = ValidateProviderName(parts[1]);
            _ = ToGenAiProviderName(providerName);
            if (parts.Length == 5)
            {
                if (!parts[4].StartsWith("adapter=", StringComparison.Ordinal)
                    || !string.Equals(providerName, DirectMlProviderName, StringComparison.OrdinalIgnoreCase))
                    return false;
                adapterPin = ValidateAdapterPin(parts[4]["adapter=".Length..]);
            }
        }
        catch { return false; }
        providerVersion = parts[2];
        providerSha = parts[3];
        return providerVersion.Length > 0 && providerSha.Length > 0;
    }

    public static WindowsMlGenAiWorkerEvent ParseWorkerEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidDataException("Windows ML GenAI worker returned an empty protocol line.");
        WindowsMlGenAiWorkerEvent evt;
        try
        {
            evt = JsonSerializer.Deserialize<WindowsMlGenAiWorkerEvent>(line, JsonOptions)
                ?? throw new InvalidDataException("Windows ML GenAI worker returned an empty event.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Windows ML GenAI worker returned invalid JSON.", ex);
        }
        if (evt.ApiVersion != WorkerApiVersion)
            throw new InvalidDataException("Unsupported Windows ML GenAI worker protocol version: " + evt.ApiVersion + ".");
        if (string.IsNullOrWhiteSpace(evt.Type))
            throw new InvalidDataException("Windows ML GenAI worker event has no type.");
        if (evt.Type == "delta" && evt.Content == null)
            throw new InvalidDataException("Windows ML GenAI delta is missing content.");
        if (evt.Type is "delta" or "done" or "cancelled" or "prompt_count"
            && string.IsNullOrWhiteSpace(evt.Id))
            throw new InvalidDataException("Windows ML GenAI request event is missing its request id.");
        if (evt.Type == "prompt_count" && evt.PromptTokens <= 0)
            throw new InvalidDataException("Windows ML GenAI prompt_count event has no positive token count.");
        return evt;
    }

    public static string ValidateReadyHandshake(
        WindowsMlGenAiWorkerEvent ready,
        string requestedProvider,
        bool requireUnambiguousDevice = true,
        bool requireThinkingControl = true,
        bool requireCancellationControl = true,
        bool requirePromptCountingControl = true,
        string? expectedAdapterPin = null)
    {
        ArgumentNullException.ThrowIfNull(ready);
        string provider = ValidateProviderName(requestedProvider);
        if (!string.Equals(ready.Type, "ready", StringComparison.Ordinal))
            throw new InvalidDataException("Windows ML GenAI worker did not complete its ready handshake.");
        if (!string.Equals(ready.Provider, provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows ML GenAI worker started on a different execution provider.");
        if (requireThinkingControl
            && !string.Equals(ready.ThinkingControl, ThinkingControlJinja, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The selected model chat template does not expose a verified enable_thinking control; "
                + "PaRuX will not benchmark thinking=false as if it had been applied.");
        if (requireCancellationControl
            && !string.Equals(ready.CancellationControl, RequestCancellationControl, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The Windows ML worker does not expose verified request-id cancellation/drain; "
                + "PaRuX will not keep it resident after cancellation.");
        if (requirePromptCountingControl
            && !string.Equals(ready.PromptCountingControl, PromptCountingControl, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The Windows ML worker does not expose exact rendered-prompt token counting; "
                + "PaRuX will not use it near the physical context limit.");

        List<string> devices = (ready.CompatibleDevices ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requireUnambiguousDevice && devices.Count != 1)
            throw new InvalidDataException(
                "Windows ML provider readiness does not identify one unambiguous compatible ORT device "
                + "(" + devices.Count + " observed). Pin/validate the physical device before promotion.");
        if (devices.Count == 1
            && !string.Equals(ready.DeviceEvidence, SingleCompatibleDeviceEvidence, StringComparison.Ordinal))
            throw new InvalidDataException("Windows ML worker device evidence is inconsistent with its device enumeration.");
        VerifyAdapterPin(ready, provider, devices, expectedAdapterPin);
        return devices.Count == 1 ? devices[0] : string.Empty;
    }

    /// <summary>DirectML enumerates every adapter, so it is admitted only when the worker was told an
    /// exact adapter, echoed it, and enumerated exactly that PCI vendor:device as the single device.
    /// Re-run on every worker spawn: a removed/changed adapter fails the handshake.</summary>
    private static void VerifyAdapterPin(
        WindowsMlGenAiWorkerEvent ready, string provider, List<string> devices, string? expectedAdapterPin)
    {
        bool directMl = string.Equals(provider, DirectMlProviderName, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(expectedAdapterPin))
        {
            if (directMl)
                throw new InvalidDataException(
                    "DirectML requires an explicit adapter pin (vendor:device); PaRuX will not let it pick an adapter implicitly.");
            if (!string.IsNullOrWhiteSpace(ready.PinnedAdapter))
                throw new InvalidDataException("Windows ML worker reported an adapter pin that was not requested.");
            return;
        }
        string pin = ValidateAdapterPin(expectedAdapterPin);
        if (!directMl) throw new InvalidDataException("Only the DirectML provider accepts an adapter pin.");
        if (!string.Equals(ready.PinnedAdapter?.Trim(), pin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows ML worker did not confirm the requested DirectML adapter pin " + pin + ".");
        // ORT's device list is only a view of what exists. The worker must also prove, from this
        // process's own per-adapter video-memory usage, that the pinned adapter holds the model.
        if (ready.AdapterEvidence == null
            || !ready.AdapterEvidence.StartsWith(AdapterExecutionEvidencePrefix, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The DirectML worker did not attest that the pinned adapter " + pin + " actually holds the model.");
        TryParseAdapterPin(pin, out uint vendor, out uint device);
        // Device description format: EpName|Type|Vendor|vendorIdHex|deviceIdHex
        string[] fields = devices.Count == 1 ? devices[0].Split('|') : Array.Empty<string>();
        if (devices.Count != 1 || fields.Length < 5
            || !string.Equals(fields[3], vendor.ToString("x4"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields[4], device.ToString("x4"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The pinned DirectML adapter " + pin + " is not present as exactly one ORT device ("
                + devices.Count + " observed); refusing to serve on a different adapter.");
    }

    private static string NormalizePart(string? value, string fallback)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return fallback;
        normalized = normalized.Replace('|', '_');
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }
}