using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Read-only adapter for the public Hugging Face model API, restricted to LoRA adapter repositories that ship
/// .safetensors files. It never downloads anything: it reports the repository, its declared licence, base model and
/// pinned-revision file URLs. Actual downloads must be started through the existing ModelDownloadService HF lane,
/// which re-validates the revision, size and hash. A file without a pinned commit SHA gets no download URL at all.
/// </summary>
public sealed class LoraHuggingFaceAdapter : ILoraCatalogAdapter
{
    public const int MaxLimit = 20;
    public const int MaxPage = 5;
    public const long MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxFilesPerRepo = 10;
    private static readonly Uri Base = new("https://huggingface.co/");
    private static readonly Regex RepoId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9._-]{1,96}$", RegexOptions.Compiled);
    private static readonly Regex Revision = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    public LoraHuggingFaceAdapter(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));
    public string SourceId => "huggingface";

    public async Task<LoraSearchPage> SearchAsync(LoraSearchQuery query, CancellationToken ct)
    {
        string text = (query.Query ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 100) throw new InvalidDataException("Enter a search of 1-100 characters.");
        int page = Math.Clamp(query.Page, 1, MaxPage);
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        int fetch = limit * page; // the list endpoint has no offset: fetch a bounded prefix and slice the requested page
        string url = new Uri(Base, "api/models").ToString() + "?search=" + Uri.EscapeDataString(text)
            + "&filter=lora&sort=downloads&direction=-1&full=true&limit=" + fetch;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long len && len > MaxResponseBytes) throw new InvalidDataException("Hugging Face response is too large.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes) throw new InvalidDataException("Hugging Face response is too large.");
        }
        buffer.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(buffer, new JsonDocumentOptions { MaxDepth = 16 });
        return Parse(doc.RootElement, page, limit, query.BaseModel, query.IncludeNsfw);
    }

    internal static LoraSearchPage Parse(JsonElement root, int page, int limit, string? baseModelFilter, bool includeNsfw)
    {
        var all = new List<LoraCatalogItem>();
        bool serverFilled = false;
        if (root.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement m in root.EnumerateArray())
            {
                count++;
                if (ParseRepo(m, baseModelFilter, includeNsfw) is { } item) all.Add(item);
            }
            serverFilled = count >= limit * page;
        }
        var slice = all.Skip((page - 1) * limit).Take(limit).ToList();
        bool more = page < MaxPage && (all.Count > page * limit || serverFilled);
        return new LoraSearchPage(slice, page, more);
    }

    private static LoraCatalogItem? ParseRepo(JsonElement m, string? baseModelFilter, bool includeNsfw)
    {
        if (m.ValueKind != JsonValueKind.Object) return null;
        string? id = Str(m, "id") ?? Str(m, "modelId");
        if (id == null || !RepoId.IsMatch(id)) return null;
        if (Flag(m, "private") || Flag(m, "disabled")) return null;

        var tags = new List<string>();
        if (m.TryGetProperty("tags", out JsonElement t) && t.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in t.EnumerateArray().Take(200))
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) tags.Add(s);

        JsonElement card = m.TryGetProperty("cardData", out JsonElement c) && c.ValueKind == JsonValueKind.Object ? c : default;
        string? license = tags.FirstOrDefault(x => x.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?["license:".Length..]
            ?? (card.ValueKind == JsonValueKind.Object ? Str(card, "license") : null);
        string? baseModel = BaseModelFrom(tags, card);
        bool nsfw = tags.Contains("not-for-all-audiences", StringComparer.OrdinalIgnoreCase);
        if (nsfw && !includeNsfw) return null;
        if (!string.IsNullOrWhiteSpace(baseModelFilter)
            && (baseModel is null || !baseModel.Contains(baseModelFilter.Trim(), StringComparison.OrdinalIgnoreCase))) return null;

        string? sha = Str(m, "sha")?.ToLowerInvariant();
        bool pinned = sha != null && Revision.IsMatch(sha);
        var files = new List<LoraFileInfo>();
        if (m.TryGetProperty("siblings", out JsonElement sibs) && sibs.ValueKind == JsonValueKind.Array)
            foreach (JsonElement f in sibs.EnumerateArray())
            {
                if (files.Count >= MaxFilesPerRepo) break;
                string? name = Str(f, "rfilename");
                if (name == null || !SafeFileName(name) || !name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) continue;
                long size = f.TryGetProperty("size", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out long l) && l > 0 ? l : 0;
                string? dl = pinned ? new Uri(Base, id + "/resolve/" + sha + "/" + string.Join('/', name.Split('/').Select(Uri.EscapeDataString))).ToString() : null;
                files.Add(new LoraFileInfo(name, name, size, null, "SafeTensor", dl));
            }
        if (files.Count == 0) return null; // not a safetensors LoRA repository

        var words = new List<string>();
        if (card.ValueKind == JsonValueKind.Object && Str(card, "instance_prompt") is { Length: > 0 } ip) words.Add(ip);
        var version = new LoraVersionInfo(pinned ? sha! : "unpinned", pinned ? sha![..12] : "unpinned", baseModel, words, files);
        return new LoraCatalogItem("huggingface", id, id, Str(m, "author") ?? id[..id.IndexOf('/')], nsfw,
            new LoraLicense(Array.Empty<string>(), null, null, null, license), new[] { version },
            "https://huggingface.co/" + id);
    }

    private static string? BaseModelFrom(List<string> tags, JsonElement card)
    {
        foreach (string tag in tags)
        {
            if (tag.StartsWith("base_model:adapter:", StringComparison.OrdinalIgnoreCase)) return Clip(tag["base_model:adapter:".Length..], 200);
        }
        foreach (string tag in tags)
        {
            if (tag.StartsWith("base_model:", StringComparison.OrdinalIgnoreCase) && tag.Count(ch => ch == ':') == 1) return Clip(tag["base_model:".Length..], 200);
        }
        if (card.ValueKind == JsonValueKind.Object && card.TryGetProperty("base_model", out JsonElement bm))
        {
            if (bm.ValueKind == JsonValueKind.String) return Clip(bm.GetString()!, 200);
            if (bm.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in bm.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) return Clip(e.GetString()!, 200);
        }
        return null;
    }

    private static bool SafeFileName(string name) =>
        name.Length <= 200 && !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':')
        && name.Split('/').All(p => p.Length > 0 && p != "." && p != ".." && !p.Any(char.IsControl));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? Clip(p.GetString()!, 200) : null;
    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
