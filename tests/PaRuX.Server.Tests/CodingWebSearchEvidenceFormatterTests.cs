using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWebSearchEvidenceFormatterTests
{
    [Fact]
    public void Produces_only_five_bounded_source_references_and_untrusted_label()
    {
        string input = "{\"results\":[" + string.Join(",", Enumerable.Range(1, 8)
            .Select(number =>
                "{\"title\":\"Guide " + number + "\",\"url\":\"https://example.org/" + number
                + "\",\"snippet\":\"Untrusted excerpt\"}")) + "]}";
        using JsonDocument document = JsonDocument.Parse(input);
        var formatted = CodingWebSearchEvidenceFormatter.Render(document.RootElement);

        Assert.True(formatted.Success);
        Assert.Contains("UNTRUSTED WEB SEARCH SOURCES", formatted.Evidence);
        Assert.Contains("SOURCE 5 TITLE", formatted.Evidence);
        Assert.DoesNotContain("SOURCE 6", formatted.Evidence);
        Assert.Contains("https://example.org/1", formatted.Evidence);
    }

    [Fact]
    public void Rejects_non_http_urls_and_basic_auth_credentials()
    {
        const string json = """
            {"results":[
                {"title":"Local file","url":"file:///C:/secrets.txt","snippet":"not allowed"},
                {"title":"Credential-bearing","url":"https://alice:password@example.org/private","snippet":"bad"},
                {"title":"Approved web source","url":"https://example.org/docs","snippet":"source data"}
            ]}
            """;
        using JsonDocument document = JsonDocument.Parse(json);
        var formatted = CodingWebSearchEvidenceFormatter.Render(document.RootElement);

        Assert.True(formatted.Success);
        Assert.DoesNotContain("C:/secrets", formatted.Evidence);
        Assert.DoesNotContain("alice:password", formatted.Evidence);
        Assert.Contains("SOURCE 1 TITLE: Approved web source", formatted.Evidence);
    }

    [Fact]
    public void Web_source_projection_rejects_private_network_and_overlong_references()
    {
        const string sources = """
            {"results":[
                {"title":"Internal service","url":"http://127.0.0.1:4000/data"},
                {"title":"Local printer","url":"http://192.168.1.2/status"},
                {"title":"Valid public docs","url":"https://example.org/api"}
            ]}
            """;
        using JsonDocument document = JsonDocument.Parse(sources);
        var formatted = CodingWebSearchEvidenceFormatter.Render(document.RootElement);
        Assert.True(formatted.Success);
        Assert.DoesNotContain("127.0.0.1", formatted.Evidence);
        Assert.DoesNotContain("192.168.1.2", formatted.Evidence);
        Assert.Contains("Valid public docs", formatted.Evidence);

        string overlong = "{\"results\":[{\"title\":\"Long\",\"url\":\"https://example.org/"
            + new string('a', 2_100) + "\"}]}";
        using JsonDocument longDocument = JsonDocument.Parse(overlong);
        Assert.Equal("No web-search sources returned.",
            CodingWebSearchEvidenceFormatter.Render(longDocument.RootElement).Evidence);
    }

    [Fact]
    public void Malformed_response_is_not_fabricated_as_success()
    {
        using JsonDocument document = JsonDocument.Parse("""{"error":"offline"}""");
        var formatted = CodingWebSearchEvidenceFormatter.Render(document.RootElement);
        Assert.False(formatted.Success);
        Assert.Contains("Invalid Core web-search response", formatted.Evidence);
    }

    [Fact]
    public void Empty_result_set_is_not_a_verified_external_source()
    {
        using JsonDocument document = JsonDocument.Parse("""{"results":[]}""");
        var formatted = CodingWebSearchEvidenceFormatter.Render(document.RootElement);
        Assert.True(formatted.Success);
        Assert.Equal("No web-search sources returned.", formatted.Evidence);
    }
}
