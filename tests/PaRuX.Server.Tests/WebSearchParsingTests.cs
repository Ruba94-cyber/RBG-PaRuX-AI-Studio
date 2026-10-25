using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The scraped markup is the fragile half of web search, and it failed silently once already:
/// measured on the physical PC, a GET carrying a "PaRuXLocalAI/1.0" agent was answered with
/// DuckDuckGo's anti-bot challenge page - HTTP 200, no result markup - so every search returned
/// zero results without raising anything, and the model answered "I cannot search the web" while
/// the user believed a search had run. These fixtures are shaped exactly like the responses both
/// endpoints actually return, so a parser that stops matching fails here instead of in the field.
/// </summary>
public sealed class WebSearchParsingTests
{
    /// <summary>
    /// The shape observed live on the physical PC: DuckDuckGo returns its advertisements as
    /// /y.js click trackers, which carry no uddg= redirect. The parser used to accept them because
    /// they start with "http", so a question about the history of Rome cited an amazon.it ad
    /// tracker as its first source - hundreds of characters of tracking parameters in the model's
    /// context and in the user's Sources footer.
    /// </summary>
    private const string HtmlWithAdvertisement = """
        <div class="results">
          <div class="result results_links">
            <a class="result__a" href="https://duckduckgo.com/y.js?ad_domain=amazon.it&amp;ad_provider=bingv7aa&amp;ad_type=txad&amp;click_metadata=JNcViJpqG0s0mG3zvPewR2sq2Rvvet28aEQ613JLMCYehBTa30PR&amp;u3=https%3A%2F%2Fwww.bing.com%2Faclick%3Fld%3De8sNXHGCam">Libri sulla <b>storia</b> di Roma</a>
            <a class="result__snippet" href="#">Acquista ora su Amazon.</a>
          </div>
          <div class="result results_links">
            <a class="result__a" href="/l/?uddg=https%3A%2F%2Fit.wikipedia.org%2Fwiki%2FStoria_di_Roma&amp;rut=7f56198eb8">Storia di <b>Roma</b></a>
            <a class="result__snippet" href="#">La storia di Roma dalle origini.</a>
          </div>
        </div>
        """;

    [Fact]
    public void An_advertisement_click_tracker_is_not_returned_as_a_search_result()
    {
        List<WebSearchResult> results = WebSearchService.ParsePrimaryHtml(HtmlWithAdvertisement, 10);

        Assert.DoesNotContain(results, result => result.Url.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, result => result.Url.Contains("ad_domain", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://it.wikipedia.org/wiki/Storia_di_Roma", Assert.Single(results).Url);
    }

    [Fact]
    public void The_search_engines_own_hosts_are_never_a_destination()
    {
        Assert.True(WebSearchService.IsSearchEngineHost("duckduckgo.com"));
        Assert.True(WebSearchService.IsSearchEngineHost("html.duckduckgo.com"));
        Assert.True(WebSearchService.IsSearchEngineHost("lite.duckduckgo.com"));
        Assert.False(WebSearchService.IsSearchEngineHost("it.wikipedia.org"));
        Assert.False(WebSearchService.IsSearchEngineHost("notduckduckgo.com"));
    }

    private const string PrimaryHtml = """
        <div class="results">
          <div class="result results_links">
            <a class="result__a" href="https://docs.python.org/3/whatsnew/3.13.html">What's New In <b>Python</b> 3.13</a>
            <a class="result__snippet" href="#">This article explains the new features in <b>Python</b> 3.13.</a>
          </div>
          <div class="result results_links">
            <a class="result__a" href="https://www.python.org/downloads/release/python-3130/">Python Release 3.13.0</a>
            <a class="result__snippet" href="#">Python 3.13.0 is the newest major release.</a>
          </div>
        </div>
        """;

    private const string LiteHtml = """
        <table>
          <tr><td><a rel="nofollow" href="https://docs.python.org/3/whatsnew/3.13.html" class='result-link'>What's New In Python 3.13</a></td></tr>
          <tr><td>&nbsp;</td><td class='result-snippet'>This article explains the new features in <b>Python</b> 3.13.</td></tr>
          <tr><td><a rel="nofollow" href="https://www.python.org/downloads/release/python-3130/" class='result-link'>Python Release 3.13.0</a></td></tr>
          <tr><td>&nbsp;</td><td class='result-snippet'>Python 3.13.0 is the newest major release.</td></tr>
        </table>
        """;

    private const string ChallengeHtml = """
        <html><head><title>DuckDuckGo</title></head>
        <body><div id="anomaly-modal__mask"><p>challenge</p></div></body></html>
        """;

    [Fact]
    public void Primary_endpoint_markup_yields_real_titles_urls_and_snippets()
    {
        List<WebSearchResult> results = WebSearchService.ParsePrimaryHtml(PrimaryHtml, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("https://docs.python.org/3/whatsnew/3.13.html", results[0].Url);
        Assert.Contains("What's New In Python 3.13", results[0].Title, StringComparison.Ordinal);
        Assert.Contains("new features", results[0].Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Lite_endpoint_markup_is_parsed_by_its_own_pattern()
    {
        List<WebSearchResult> results = WebSearchService.ParseLiteHtml(LiteHtml, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("https://docs.python.org/3/whatsnew/3.13.html", results[0].Url);
        Assert.Contains("Python 3.13", results[0].Title, StringComparison.Ordinal);
        Assert.Contains("new features", results[0].Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_results_is_honoured()
    {
        Assert.Single(WebSearchService.ParsePrimaryHtml(PrimaryHtml, 1));
        Assert.Single(WebSearchService.ParseLiteHtml(LiteHtml, 1));
    }

    [Fact]
    public void An_anti_bot_challenge_page_yields_nothing_rather_than_bogus_results()
    {
        Assert.Empty(WebSearchService.ParsePrimaryHtml(ChallengeHtml, 5));
        Assert.Empty(WebSearchService.ParseLiteHtml(ChallengeHtml, 5));
    }
}
