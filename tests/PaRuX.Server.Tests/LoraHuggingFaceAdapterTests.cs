using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LoraHuggingFaceAdapterTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string Body = $$"""
    [
     {"id":"acme/flux-style-lora","author":"acme","sha":"{{Sha}}","private":false,
      "tags":["lora","license:apache-2.0","base_model:adapter:black-forest-labs/FLUX.1-dev"],
      "cardData":{"instance_prompt":"acmestyle"},
      "siblings":[{"rfilename":"README.md"},{"rfilename":"style.safetensors","size":1024},{"rfilename":"../evil.safetensors"},{"rfilename":"weights.bin"}]},
     {"id":"acme/spicy","sha":"{{Sha}}","tags":["lora","not-for-all-audiences","base_model:stabilityai/sdxl"],"siblings":[{"rfilename":"s.safetensors"}]},
     {"id":"acme/not-lora","sha":"{{Sha}}","tags":["lora"],"siblings":[{"rfilename":"model.bin"}]},
     {"id":"acme/private","private":true,"sha":"{{Sha}}","siblings":[{"rfilename":"p.safetensors"}]},
     {"id":"bad id with spaces","sha":"{{Sha}}","siblings":[{"rfilename":"x.safetensors"}]},
     {"id":"acme/floating","tags":["lora"],"cardData":{"license":"mit","base_model":["Qwen/Qwen-Image"]},"siblings":[{"rfilename":"sub/f.safetensors"}]}
    ]
    """;

    private sealed class Fake : HttpMessageHandler
    {
        public Uri? Last; public string Body = "[]"; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request.RequestUri; Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Search_is_bounded_and_reports_license_base_model_trigger_and_pinned_url()
    {
        var h = new Fake { Body = Body };
        var page = await new LoraHuggingFaceAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("flux style", Page: 99, Limit: 500), default);
        string q = h.Last!.Query;
        Assert.Equal("huggingface.co", h.Last.Host);
        Assert.Contains("filter=lora", q);
        Assert.Contains("search=flux%20style", q);
        Assert.Contains("limit=100", q); // 20 x page 5: bounded
        Assert.Equal(1, h.Calls);

        // NSFW, non-safetensors, private and malformed ids are dropped; the page was clamped to 5 so the slice is empty.
        Assert.Empty(page.Items);

        page = await new LoraHuggingFaceAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("flux", Page: 1, Limit: 10), default);
        Assert.Equal(new[] { "acme/flux-style-lora", "acme/floating" }, page.Items.Select(i => i.ModelId));
        var item = page.Items[0];
        Assert.Equal("apache-2.0", item.License.LicenseId);
        Assert.False(item.Nsfw);
        var v = item.Versions.Single();
        Assert.Equal("black-forest-labs/FLUX.1-dev", v.BaseModel);
        Assert.Equal(new[] { "acmestyle" }, v.TrainedWords);
        var file = Assert.Single(v.Files); // traversal name and .bin excluded
        Assert.Equal("style.safetensors", file.Name);
        Assert.Equal($"https://huggingface.co/acme/flux-style-lora/resolve/{Sha}/style.safetensors", file.DownloadUrl);
        Assert.Equal(1024, file.SizeBytes);
        Assert.Null(file.Sha256);
    }

    [Fact]
    public async Task Unpinned_repo_gets_no_download_url_and_card_fallbacks_are_used()
    {
        var h = new Fake { Body = Body };
        var page = await new LoraHuggingFaceAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("flux"), default);
        var floating = page.Items.Single(i => i.ModelId == "acme/floating");
        Assert.Null(floating.Versions[0].Files[0].DownloadUrl);
        Assert.Equal("unpinned", floating.Versions[0].VersionId);
        Assert.Equal("mit", floating.License.LicenseId);
        Assert.Equal("Qwen/Qwen-Image", floating.Versions[0].BaseModel);
    }

    [Fact]
    public async Task Nsfw_is_hidden_unless_requested_and_base_model_filters()
    {
        var h = new Fake { Body = Body };
        var adapter = new LoraHuggingFaceAdapter(new HttpClient(h));
        var withNsfw = await adapter.SearchAsync(new LoraSearchQuery("x", IncludeNsfw: true), default);
        Assert.Contains(withNsfw.Items, i => i.ModelId == "acme/spicy" && i.Nsfw);
        var filtered = await adapter.SearchAsync(new LoraSearchQuery("x", BaseModel: "qwen"), default);
        Assert.Equal("acme/floating", Assert.Single(filtered.Items).ModelId);
    }

    [Fact]
    public async Task Empty_query_and_oversized_response_are_refused()
    {
        var h = new Fake { Body = Body };
        var adapter = new LoraHuggingFaceAdapter(new HttpClient(h));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SearchAsync(new LoraSearchQuery(" "), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SearchAsync(new LoraSearchQuery(new string('a', 101)), default));
        h.Body = "[" + new string(' ', (int)LoraHuggingFaceAdapter.MaxResponseBytes + 1) + "]";
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SearchAsync(new LoraSearchQuery("a"), default));
        Assert.Equal("huggingface", adapter.SourceId);
    }
}
