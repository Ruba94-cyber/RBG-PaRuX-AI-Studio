using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Turns a raw ComfyUI/Generate-Media.ps1 failure into a clear "this file is
/// missing" message instead of a wall of node_errors JSON. Deliberately a text
/// scan for known model file extensions rather than a full parse of ComfyUI's
/// node_errors schema: that schema was not verified against a real ComfyUI
/// failure in this session (none was reproduced), so this only claims what it
/// can back up - a filename actually present in the error text - not which
/// node or workflow needed it.
/// </summary>
internal static class ComfyUiErrorParser
{
    private static readonly Regex ModelFilePattern = new(
        @"[\w][\w .\-]*\.(safetensors|ckpt|pt|pth|bin|gguf|onnx|vae|clip)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingHint = new(
        @"not in list|not found|missing|does not exist|no such file",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns distinct model file names mentioned in the error, only when
    /// the surrounding text actually reads as a missing-file report.</summary>
    public static List<string> ExtractMissingAssetNames(string rawError)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(rawError) || !MissingHint.IsMatch(rawError)) return result;

        foreach (Match match in ModelFilePattern.Matches(rawError))
        {
            string name = match.Value.Trim().Trim('\'', '"');
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        return result;
    }

    public static string BuildFriendlyMessage(List<string> missingAssets)
    {
        string files = string.Join(", ", missingAssets);
        return missingAssets.Count == 1
            ? "The ComfyUI workflow needs a file PaRuX couldn't find: " + files
              + ". Add it to the matching ComfyUI models folder and try again."
            : "The ComfyUI workflow needs files PaRuX couldn't find: " + files
              + ". Add them to the matching ComfyUI models folders and try again.";
    }
}
