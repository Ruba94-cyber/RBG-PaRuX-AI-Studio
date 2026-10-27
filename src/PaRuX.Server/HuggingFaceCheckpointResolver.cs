using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Resolves "owner/name" plus a file path inside the repository into a pinned, sized checkpoint download. The client never
/// supplies a URL: PaRuX reads the repository listing from huggingface.co itself, requires the file to be listed there,
/// takes size and commit from the listing and builds the resolve URL. The result is then posted to the normal download
/// endpoint, whose own validation and (after the transfer) media family gate still apply.
/// </summary>
public sealed class HuggingFaceCheckpointResolver
{
    private readonly HttpClient _http;

    public HuggingFaceCheckpointResolver(HttpClient http) => _http = http;

    public async Task<HuggingFaceCheckpointResolution> ResolveAsync(string repoId, string filePath, CancellationToken ct)
    {
        (string safeRepo, string safePath) = ValidateInputs(repoId, filePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/models/" + safeRepo + "?blobs=true");
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException("Hugging Face has no public repository named \"" + safeRepo + "\".");
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException("Hugging Face answered " + (int)response.StatusCode + " for \"" + safeRepo + "\"; try again later.");
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return BuildResolution(safeRepo, safePath, json);
    }

    /// <summary>Strict shape check on what the user typed. Returns the normalised repo id and file path.</summary>
    public static (string RepoId, string FilePath) ValidateInputs(string? repoId, string? filePath)
    {
        string[] parts = (repoId ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || parts.Any(p => !IsSafeRepoPart(p)))
            throw new InvalidDataException("Repository must look like owner/name (letters, digits, - _ . only).");

        string path = (filePath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.Length > 400)
            throw new InvalidDataException("Enter the path of a .safetensors file inside the repository.");
        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || !segment.All(IsSafePathChar))
                throw new InvalidDataException("The file path may only contain letters, digits, - _ . + ( ) and / and no \"..\" segments.");
        }
        if (!path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .safetensors checkpoints can be added here.");
        if (!ModelDownloadProvenanceManifestStore.IsSafeFileName(segments[^1]))
            throw new InvalidDataException("The file name is not a valid managed model file name.");
        return (parts[0] + "/" + parts[1], path);
    }

    /// <summary>Pure: turns a Hugging Face model-detail document (blobs=true) into the resolution, or refuses.</summary>
    public static HuggingFaceCheckpointResolution BuildResolution(string repoId, string filePath, string detailJson)
    {
        HfDetail? detail;
        try { detail = JsonSerializer.Deserialize<HfDetail>(detailJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { throw new InvalidDataException("Hugging Face returned a listing PaRuX could not read."); }

        string? revision = ModelRevisionIdentity.Normalize(detail?.Sha);
        if (detail == null || revision == null)
            throw new InvalidDataException("The repository did not report an immutable commit, so the download cannot be pinned.");

        HfFile? file = detail.Siblings?.FirstOrDefault(s => string.Equals(s.RFilename, filePath, StringComparison.Ordinal));
        if (file == null)
            throw new InvalidDataException("\"" + filePath + "\" is not listed in " + repoId + " at the current commit. Check the exact path.");
        long size = file.Lfs?.Size ?? file.Size ?? 0;
        if (size <= 0)
            throw new InvalidDataException("Hugging Face did not report a size for this file, so it cannot be downloaded safely.");

        string fileName = filePath[(filePath.LastIndexOf('/') + 1)..];
        string url = "https://huggingface.co/" + repoId + "/resolve/" + revision + "/"
            + string.Join('/', filePath.Split('/').Select(Uri.EscapeDataString));
        return new HuggingFaceCheckpointResolution
        {
            RepoId = repoId, Revision = revision, FilePath = filePath, FileName = fileName,
            SizeBytes = size, DownloadUrl = url, License = ReadLicense(detail)
        };
    }

    private static string? ReadLicense(HfDetail detail)
    {
        JsonElement license = detail.CardData?.License ?? default;
        return license.ValueKind == JsonValueKind.Undefined ? null : DatasetDownloadService.NormalizeLicense(license);
    }

    private static bool IsSafeRepoPart(string part) =>
        part.Length is > 0 and <= 100 && part is not "." and not ".."
        && part.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c is '-' or '_' or '.');

    private static bool IsSafePathChar(char c) =>
        (c < 128 && char.IsLetterOrDigit(c)) || c is '-' or '_' or '.' or '+' or '(' or ')';

    private sealed class HfDetail
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("siblings")] public List<HfFile>? Siblings { get; set; }
        [JsonPropertyName("cardData")] public HfCard? CardData { get; set; }
    }

    private sealed class HfFile
    {
        [JsonPropertyName("rfilename")] public string RFilename { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("lfs")] public HfLfs? Lfs { get; set; }
    }

    private sealed class HfLfs { [JsonPropertyName("size")] public long? Size { get; set; } }

    private sealed class HfCard { [JsonPropertyName("license")] public JsonElement License { get; set; } }
}
