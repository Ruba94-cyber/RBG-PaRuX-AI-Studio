using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Search sort and per-repository quant grouping. The Hugging Face listing is answered by a fake handler,
/// so these tests need no network. They pin the sort the server asks for, the order it returns, and that a
/// split set is one quant choice per repository with its parts counted once and its projector left out.
/// </summary>
public sealed class ModelSearchSortTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("relevance", null)]
    [InlineData("Downloads", "downloads")]
    [InlineData(" likes ", "likes")]
    public void Known_sort_keys_normalise_and_an_empty_key_keeps_the_listing_order(string? requested, string? expected)
    {
        Assert.Equal(expected, ModelDownloadService.NormalizeSearchSort(requested));
    }

    [Fact]
    public void An_unknown_sort_key_is_refused_rather_than_ignored()
    {
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.NormalizeSearchSort("stars"));
    }

    [Fact]
    public async Task Sorting_by_downloads_asks_the_listing_for_it_and_returns_the_rows_by_downloads()
    {
        var handler = new ListingHandler(Listing(
            Repo("org/small", downloads: 10, likes: 1, ("model-Q8_0.gguf", 100)),
            Repo("org/big", downloads: 500, likes: 5, ("model-Q4_K_M.gguf", 50))));
        var service = new ModelDownloadService(new HttpClient(handler));

        List<ModelSearchResult> rows = await service.SearchAsync("model", 15, CancellationToken.None, "downloads");

        Assert.Contains("sort=downloads&direction=-1", handler.LastUrl);
        Assert.Equal(new[] { "org/big", "org/small" }, rows.Select(r => r.RepoId));
    }

    [Fact]
    public async Task Sorting_by_likes_returns_the_most_liked_repository_first_even_when_the_listing_disagrees()
    {
        var handler = new ListingHandler(Listing(
            Repo("org/a", downloads: 900, likes: 2, ("a-Q8_0.gguf", 100)),
            Repo("org/b", downloads: 1, likes: 40, ("b-Q8_0.gguf", 100))));
        var service = new ModelDownloadService(new HttpClient(handler));

        List<ModelSearchResult> rows = await service.SearchAsync("model", 15, CancellationToken.None, "likes");

        Assert.Contains("sort=likes&direction=-1", handler.LastUrl);
        Assert.Equal(new[] { "org/b", "org/a" }, rows.Select(r => r.RepoId));
    }

    [Fact]
    public async Task Relevance_keeps_the_listing_order_and_sends_no_sort_parameter()
    {
        var handler = new ListingHandler(Listing(
            Repo("org/small", downloads: 10, likes: 1, ("model-Q8_0.gguf", 100)),
            Repo("org/big", downloads: 500, likes: 5, ("model-Q4_K_M.gguf", 50))));
        var service = new ModelDownloadService(new HttpClient(handler));

        List<ModelSearchResult> rows = await service.SearchAsync("model", 15, CancellationToken.None, null);

        Assert.DoesNotContain("sort=", handler.LastUrl);
        Assert.Equal(new[] { "org/small", "org/big" }, rows.Select(r => r.RepoId));
    }

    [Fact]
    public async Task An_unknown_sort_is_refused_before_any_request_is_sent()
    {
        var handler = new ListingHandler(Listing());
        var service = new ModelDownloadService(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SearchAsync("model", 15, CancellationToken.None, "stars"));
        Assert.Null(handler.LastUrl);
    }

    [Fact]
    public async Task A_split_set_is_one_quant_choice_and_its_projector_is_not_a_choice()
    {
        var handler = new ListingHandler(Listing(Repo("org/vision", downloads: 1, likes: 0,
            ("Q4_K_M/model-00001-of-00002.gguf", 100),
            ("Q4_K_M/model-00002-of-00002.gguf", 50),
            ("Q8_0/model-Q8_0.gguf", 300),
            ("mmproj-model-f16.gguf", 10))));
        var service = new ModelDownloadService(new HttpClient(handler));

        ModelSearchResult row = Assert.Single(await service.SearchAsync("vision", 15, CancellationToken.None));

        Assert.Equal(2, row.QuantChoices.Count);
        ModelQuantChoice q4 = row.QuantChoices.Single(c => c.Quantization == "Q4_K_M");
        Assert.True(q4.IsSplit);
        Assert.Equal(2, q4.PartCount);
        Assert.True(q4.Complete);
        Assert.Equal(150, q4.SizeBytes);
        Assert.Equal(2, q4.FilePaths.Count);
        ModelQuantChoice q8 = row.QuantChoices.Single(c => c.Quantization == "Q8_0");
        Assert.False(q8.IsSplit);
        Assert.Equal(300, q8.SizeBytes);
        // The raw file list keeps every .gguf, including the projector, for the existing callers.
        Assert.Equal(4, row.GgufFiles.Count);
    }

    [Fact]
    public async Task A_listed_split_set_with_a_part_missing_is_shown_as_an_incomplete_choice()
    {
        var handler = new ListingHandler(Listing(Repo("org/partial", downloads: 1, likes: 0,
            ("Q5_K_M/model-00001-of-00003.gguf", 100),
            ("Q5_K_M/model-00003-of-00003.gguf", 50))));
        var service = new ModelDownloadService(new HttpClient(handler));

        ModelSearchResult row = Assert.Single(await service.SearchAsync("partial", 15, CancellationToken.None));

        ModelQuantChoice choice = Assert.Single(row.QuantChoices);
        Assert.Equal("Q5_K_M", choice.Quantization);
        Assert.True(choice.IsSplit);
        Assert.Equal(3, choice.PartCount);
        Assert.False(choice.Complete);
    }

    private static string Listing(params object[] repos) => JsonSerializer.Serialize(repos);

    private static object Repo(string id, int downloads, int likes, params (string Path, long Size)[] files) => new
    {
        id,
        downloads,
        likes,
        tags = Array.Empty<string>(),
        siblings = files.Select(f => new { rfilename = f.Path, size = f.Size }).ToArray()
    };

    private sealed class ListingHandler(string json) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
