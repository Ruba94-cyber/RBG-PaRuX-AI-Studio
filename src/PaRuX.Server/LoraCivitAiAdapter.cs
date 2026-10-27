using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Read-only adapter for CivitAI's public API (GET /api/v1/models?types=LORA). It never downloads a model file:
/// it only reports what the catalogue says, bounded in pages, response size and string lengths. The NSFW flag is
/// passed through; whether NSFW results are requested at all is the caller's explicit choice.
/// </summary>
public sealed class LoraCivitAiAdapter : ILoraCatalogAdapter
{
    public const int MaxLimit = 20;
    public const int MaxPage = 5;
    public const long MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly Uri Base = new("https://civitai.com/");

    private readonly HttpClient _http;
    public LoraCivitAiAdapter(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));
    public string SourceId => "civitai";

    public async Task<LoraSearchPage> SearchAsync(LoraSearchQuery query, CancellationToken ct)
    {
        string text = (query.Query ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 100) throw new InvalidDataException("Enter a search of 1-100 characters.");
        int page = Math.Clamp(query.Page, 1, MaxPage);
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        string url = new Uri(Base, "api/v1/models").ToString() + "?types=LORA&limit=" + limit
            // CivitAI rejects the page parameter on a query search (HTTP 400, cursor pagination only), so it is not sent.
            + "&query=" + Uri.EscapeDataString(text) + (query.IncludeNsfw ? "&nsfw=true" : "&nsfw=false");
        if (!string.IsNullOrWhiteSpace(query.BaseModel)) url += "&baseModels=" + Uri.EscapeDataString(query.BaseModel.Trim());

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long len && len > MaxResponseBytes) throw new InvalidDataException("CivitAI response is too large.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes) throw new InvalidDataException("CivitAI response is too large.");
        }
        buffer.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(buffer, new JsonDocumentOptions { MaxDepth = 16 });
        return Parse(doc.RootElement, page, limit);
    }

    internal static LoraSearchPage Parse(JsonElement root, int page, int limit)
    {
        var items = new List<LoraCatalogItem>();
        if (root.TryGetProperty("items", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement m in array.EnumerateArray())
            {
                if (items.Count >= limit) break;
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (Str(m, "type") is { } type && !type.Equals("LORA", StringComparison.OrdinalIgnoreCase)) continue;
                string? id = Id(m, "id"); string? name = Str(m, "name");
                if (id == null || name == null) continue;
                var versions = new List<LoraVersionInfo>();
                if (m.TryGetProperty("modelVersions", out JsonElement vs) && vs.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement v in vs.EnumerateArray().Take(10)) if (ParseVersion(v) is { } pv) versions.Add(pv);
                items.Add(new LoraCatalogItem("civitai", id, name, m.TryGetProperty("creator", out var c) ? Str(c, "username") : null,
                    m.TryGetProperty("nsfw", out var n) && n.ValueKind == JsonValueKind.True, ParseLicense(m), versions,
                    "https://civitai.com/models/" + Uri.EscapeDataString(id)));
            }
        }
        bool more = root.TryGetProperty("metadata", out JsonElement md) && md.ValueKind == JsonValueKind.Object
            && md.TryGetProperty("nextPage", out JsonElement np) && np.ValueKind == JsonValueKind.String && page < MaxPage;
        return new LoraSearchPage(items, page, more);
    }

    private static LoraVersionInfo? ParseVersion(JsonElement v)
    {
        string? id = Id(v, "id");
        if (id == null) return null;
        var words = new List<string>();
        if (v.TryGetProperty("trainedWords", out JsonElement tw) && tw.ValueKind == JsonValueKind.Array)
            foreach (JsonElement w in tw.EnumerateArray())
                if (w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } s && words.Count < 32)
                    words.Add(Clip(s, 100));
        var files = new List<LoraFileInfo>();
        if (v.TryGetProperty("files", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (JsonElement f in fs.EnumerateArray().Take(10))
            {
                string? fid = Id(f, "id"), fname = Str(f, "name");
                if (fid == null || fname == null) continue;
                double kb = f.TryGetProperty("sizeKB", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetDouble() : 0;
                string? sha = null;
                if (f.TryGetProperty("hashes", out var h) && h.ValueKind == JsonValueKind.Object) sha = Str(h, "SHA256")?.ToLowerInvariant();
                if (sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) sha = null;
                string? format = f.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? Str(meta, "format") : null;
                string? dl = SafeDownloadUrl(Str(f, "downloadUrl"));
                files.Add(new LoraFileInfo(fid, Clip(fname, 200), (long)(kb * 1024), sha, format, dl));
            }
        return new LoraVersionInfo(id, Str(v, "name") ?? id, Str(v, "baseModel"), words, files);
    }

    private static LoraLicense ParseLicense(JsonElement m)
    {
        var commercial = new List<string>();
        if (m.TryGetProperty("allowCommercialUse", out var c))
        {
            if (c.ValueKind == JsonValueKind.Array) commercial.AddRange(c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(8));
            else if (c.ValueKind == JsonValueKind.String) commercial.Add(c.GetString()!);
            else if (c.ValueKind is JsonValueKind.True or JsonValueKind.False) commercial.Add(c.GetBoolean() ? "Sell" : "None");
        }
        return new LoraLicense(commercial, Bool(m, "allowDerivatives"), Bool(m, "allowNoCredit"), Bool(m, "allowDifferentLicense"));
    }

    /// <summary>Only an https civitai.com download URL is carried forward; anything else is dropped.</summary>
    internal static string? SafeDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps
            && (u.Host.Equals("civitai.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.civitai.com", StringComparison.OrdinalIgnoreCase))
            ? u.ToString() : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? Clip(p.GetString()!, 200) : null;
    private static string? Id(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long l) && l > 0 ? l.ToString() : null;
    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
