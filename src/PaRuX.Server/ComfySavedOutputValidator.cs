using System.Text.Json;

namespace PaRuX.Server;

// Shared saved-output selection for live jobs and restart recovery. ComfyUI may report a PreviewImage
// beside a saved output; only the latter is safe to collect as a completed media artifact.
internal static class ComfySavedOutputValidator
{
    private static readonly string[] MediaCollections = { "images", "gifs", "videos" };

    internal static bool TryFirstSavedMedia(JsonElement saveOutput, out JsonElement asset)
    {
        foreach (string collectionName in MediaCollections)
        {
            if (!saveOutput.TryGetProperty(collectionName, out JsonElement media) || media.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement item in media.EnumerateArray())
            {
                string? kind = item.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;
                if (!IsSavedOutputType(kind)) continue;
                if (!item.TryGetProperty("filename", out JsonElement filename) || string.IsNullOrWhiteSpace(filename.GetString())) continue;
                asset = item;
                return true;
            }
        }
        asset = default;
        return false;
    }

    internal static string? FindSavedMediaPath(JsonElement outputs, string outputRoot, string? saveNodeId)
    {
        if (outputs.ValueKind != JsonValueKind.Object) return null;
        if (!string.IsNullOrWhiteSpace(saveNodeId))
        {
            if (!outputs.TryGetProperty(saveNodeId, out JsonElement selectedNode)) return null;
            return SavedPathForNode(selectedNode, outputRoot);
        }

        foreach (JsonProperty node in outputs.EnumerateObject())
        {
            string? path = SavedPathForNode(node.Value, outputRoot);
            if (path != null) return path;
        }
        return null;
    }

    private static string? SavedPathForNode(JsonElement node, string outputRoot)
    {
        if (!TryFirstSavedMedia(node, out JsonElement asset)) return null;
        string fileName = asset.GetProperty("filename").GetString()!;
        string subfolder = asset.TryGetProperty("subfolder", out JsonElement sub) ? sub.GetString() ?? string.Empty : string.Empty;
        try { return ResolvePath(fileName, subfolder, outputRoot); }
        catch (InvalidDataException) { return null; }
    }

    internal static string ResolvePath(string fileName, string subfolder, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            throw new InvalidDataException("The saved media filename is invalid.");

        string root = Path.GetFullPath(outputRoot);
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(root, subfolder, fileName)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidDataException("The saved media output path is invalid.", ex);
        }

        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The saved media output path is outside the ComfyUI output folder.");
        return candidate;
    }

    private static bool IsSavedOutputType(string? kind) => kind == null || kind == "output";
}
