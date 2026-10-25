using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The evidence an agent gets from web_research is built from web pages, so it must lead with the
/// untrusted-content header. The worker builds it through this one helper on both the sequential and the
/// parallel path, which is what these tests exercise.
/// </summary>
public sealed class WebResearchEvidenceLabelTests
{
    [Fact]
    public void Web_research_evidence_leads_with_the_untrusted_content_header()
    {
        var sources = new List<ResearchSource>
        {
            new() { Title = "Hash table", Url = "https://en.wikipedia.example/wiki/Hash_table", Domain = "en.wikipedia.example", Snippet = "s" }
        };

        string evidence = AgentWorkerService.WebResearchEvidence(
            "web_research query: hash tables", "Hash tables map keys to buckets [1].", sources, 4000);

        Assert.StartsWith(WebSearchService.UntrustedContentHeader, evidence, StringComparison.Ordinal);
        Assert.Contains("Sources:", evidence, StringComparison.Ordinal);
        Assert.Contains("https://en.wikipedia.example/wiki/Hash_table", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_without_sources_still_carries_the_header_and_the_answer()
    {
        string evidence = AgentWorkerService.WebResearchEvidence(
            "parallel web_research for task goal", "No source could be read.", new List<ResearchSource>(), 4000);

        Assert.StartsWith(WebSearchService.UntrustedContentHeader, evidence, StringComparison.Ordinal);
        Assert.Contains("No source could be read.", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Sources:", evidence, StringComparison.Ordinal);
    }
}
