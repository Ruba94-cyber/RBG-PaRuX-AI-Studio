using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal static class MediaJobProvenanceFactory
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static MediaJobProvenance Create(
        string engineFamily,
        string runtimeIdentity,
        string? modelFilePath,
        string? fallbackReason = null,
        Exception? failure = null,
        string? modelSelectionReason = null)
    {
        string identity = string.IsNullOrWhiteSpace(runtimeIdentity) ? "unknown" : runtimeIdentity.Trim();
        return new MediaJobProvenance
        {
            EngineFamily = FamilyFor(engineFamily),
            RuntimeIdentityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            ModelFileName = FileNameOnly(modelFilePath),
            Locality = "local",
            FallbackReason = SafeReason(fallbackReason),
            ModelSelectionReason = SafeReason(modelSelectionReason),
            FailureClass = failure == null ? null : ClassifyFailure(failure)
        };
    }

    public static MediaJobProvenance FromRaceResult(MediaEngineRaceResult result, string? modelFilePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        MediaEngineRaceMeasurement? selected = result.WinnerEngineId == null
            ? result.Measurements.FirstOrDefault(item => !item.Success) ?? result.Measurements.FirstOrDefault()
            : result.Measurements.FirstOrDefault(item =>
                string.Equals(item.EngineId, result.WinnerEngineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.EngineVersion, result.WinnerEngineVersion, StringComparison.OrdinalIgnoreCase));

        string engineId = result.WinnerEngineId ?? selected?.EngineId ?? "unknown";
        string engineVersion = result.WinnerEngineVersion ?? selected?.EngineVersion ?? "unknown";
        return new MediaJobProvenance
        {
            EngineFamily = FamilyFor(engineId),
            RuntimeIdentityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(engineId + "@" + engineVersion))),
            ModelFileName = FileNameOnly(modelFilePath),
            Locality = "local",
            FallbackReason = SafeReason(result.FallbackReason),
            FailureClass = result.WinnerEngineId == null ? SafeFailureClass(selected?.FailureClass) : null
        };
    }

    public static MediaJobProvenance WithFailure(MediaJobProvenance provenance, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(failure);
        return new MediaJobProvenance
        {
            EngineFamily = FamilyFor(provenance.EngineFamily),
            RuntimeIdentityHash = IsHash(provenance.RuntimeIdentityHash)
                ? provenance.RuntimeIdentityHash
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provenance.RuntimeIdentityHash ?? "unknown"))),
            ModelFileName = FileNameOnly(provenance.ModelFileName),
            Locality = "local",
            FallbackReason = SafeReason(provenance.FallbackReason),
            ModelSelectionReason = SafeReason(provenance.ModelSelectionReason),
            FailureClass = ClassifyFailure(failure)
        };
    }

    public static MediaJobProvenance Sanitize(MediaJobProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        string? runtimeIdentityHash = provenance.RuntimeIdentityHash;
        return new MediaJobProvenance
        {
            EngineFamily = FamilyFor(provenance.EngineFamily),
            RuntimeIdentityHash = IsHash(runtimeIdentityHash)
                ? runtimeIdentityHash!.ToUpperInvariant()
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeIdentityHash ?? "unknown"))),
            ModelFileName = FileNameOnly(provenance.ModelFileName),
            Locality = "local",
            FallbackReason = SafeReason(provenance.FallbackReason),
            ModelSelectionReason = SafeReason(provenance.ModelSelectionReason),
            FailureClass = SafeFailureClass(provenance.FailureClass)
        };
    }

    private static string FamilyFor(string? engineId)
    {
        engineId ??= string.Empty;
        if (engineId.Contains("comfy", StringComparison.OrdinalIgnoreCase)) return "ComfyUI";
        if (engineId.Contains("diffusers", StringComparison.OrdinalIgnoreCase)) return "Diffusers";
        if (engineId.Contains("whisper", StringComparison.OrdinalIgnoreCase)) return "Whisper";
        if (engineId.Contains("onnx", StringComparison.OrdinalIgnoreCase)) return "ONNX Runtime";
        if (engineId.Contains("audio", StringComparison.OrdinalIgnoreCase) || engineId.Contains("tts", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (engineId.Contains("3d", StringComparison.OrdinalIgnoreCase) || engineId.Contains("mesh", StringComparison.OrdinalIgnoreCase)) return "3D";
        return "ManagedMedia";
    }

    private static string? FileNameOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string normalized = path.Trim().Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        string name = separator < 0 ? normalized : normalized[(separator + 1)..];
        name = name.Replace(':', '_').Trim();
        if (name is "." or "..") return null;
        return name.Length == 0 ? null : name;
    }

    private static string? SafeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        string trimmed = reason.Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('/') || Regex.IsMatch(trimmed, @"\b[A-Za-z]:"))
            return "Selection reason omitted because it contained a path.";
        string normalized = Regex.Replace(trimmed, @"[^A-Za-z0-9 .,;()_\-\[\]#]", " ");
        normalized = Whitespace.Replace(normalized, " ").Trim();
        return normalized.Length == 0 ? null : normalized[..Math.Min(normalized.Length, 240)];
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? SafeFailureClass(string? value) => value switch
    {
        nameof(MediaDependencyException) => value,
        nameof(TimeoutException) => value,
        nameof(InvalidDataException) => value,
        nameof(InvalidOperationException) => value,
        nameof(IOException) => value,
        nameof(System.Net.Http.HttpRequestException) => value,
        "Exception" => value,
        _ => null
    };

    private static string ClassifyFailure(Exception failure) => failure switch
    {
        MediaDependencyException => nameof(MediaDependencyException),
        TimeoutException => nameof(TimeoutException),
        InvalidDataException => nameof(InvalidDataException),
        InvalidOperationException => nameof(InvalidOperationException),
        IOException => nameof(IOException),
        System.Net.Http.HttpRequestException => nameof(System.Net.Http.HttpRequestException),
        _ => "Exception"
    };
}

public sealed class MediaGenerationOutcome
{
    private MediaGenerationOutcome(
        MediaAsset? asset,
        MediaJobProvenance provenance,
        string? failureMessage,
        MediaDependencyError? errorDetail,
        string? errorCode)
    {
        Asset = asset;
        Provenance = provenance;
        FailureMessage = failureMessage;
        ErrorDetail = errorDetail;
        ErrorCode = errorCode;
    }

    public MediaAsset? Asset { get; }
    public MediaJobProvenance Provenance { get; }
    public string? FailureMessage { get; }
    public MediaDependencyError? ErrorDetail { get; }
    public string? ErrorCode { get; }
    public bool Succeeded => Asset != null && FailureMessage == null;

    public static MediaGenerationOutcome Success(MediaAsset asset, MediaJobProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(provenance);
        return new MediaGenerationOutcome(asset, provenance, null, null, null);
    }

    public static MediaGenerationOutcome Failure(
        string message,
        MediaJobProvenance provenance,
        MediaDependencyError? errorDetail = null,
        string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return new MediaGenerationOutcome(null, provenance, message ?? string.Empty, errorDetail, errorCode);
    }

    public static MediaGenerationOutcome FromRaceResult(MediaEngineRaceResult result, string? modelFilePath)
    {
        ArgumentNullException.ThrowIfNull(result);
        MediaJobProvenance provenance = MediaJobProvenanceFactory.FromRaceResult(result, modelFilePath);
        return result.WinnerAsset is { } asset
            ? Success(asset, provenance)
            : Failure(result.SelectionReason, provenance);
    }
}
