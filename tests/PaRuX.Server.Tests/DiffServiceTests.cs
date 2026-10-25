using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers Universal Diff: the line-level LCS diff
/// algorithm itself, plus endpoint auth.</summary>
public sealed class DiffServiceTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public DiffServiceTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public void Compute_reports_identical_text_as_entirely_unchanged()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("a\nb\nc", "a\nb\nc");

        Assert.Equal(3, result.UnchangedCount);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.RemovedCount);
        Assert.All(result.Lines, l => Assert.Equal("unchanged", l.Kind));
    }

    [Fact]
    public void Compute_detects_a_single_line_change_as_a_removal_plus_an_addition()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("a\nb\nc", "a\nX\nc");

        Assert.Equal(2, result.UnchangedCount); // "a" and "c"
        Assert.Equal(1, result.RemovedCount);   // "b"
        Assert.Equal(1, result.AddedCount);     // "X"
        Assert.Contains(result.Lines, l => l.Kind == "removed" && l.Text == "b");
        Assert.Contains(result.Lines, l => l.Kind == "added" && l.Text == "X");
    }

    [Fact]
    public void Compute_detects_pure_additions_with_no_removals()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("a\nb", "a\nb\nc\nd");

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, result.UnchangedCount);
    }

    [Fact]
    public void Compute_detects_pure_removals_with_no_additions()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("a\nb\nc\nd", "a\nb");

        Assert.Equal(2, result.RemovedCount);
        Assert.Equal(0, result.AddedCount);
    }

    [Fact]
    public void Compute_treats_crlf_and_lf_line_endings_as_equivalent()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("a\r\nb\r\nc", "a\nb\nc");

        Assert.Equal(3, result.UnchangedCount);
        Assert.Equal(0, result.AddedCount + result.RemovedCount);
    }

    [Fact]
    public void Compute_treats_winui_cr_only_line_endings_as_real_lines()
    {
        var diff = new DiffService();
        DiffResult result = diff.Compute("alpha\rbeta\rgamma", "alpha\rbeta updated\rdelta");

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, result.RemovedCount);
        Assert.Contains(result.Lines, line => line.Kind == "removed" && line.Text == "beta");
        Assert.Contains(result.Lines, line => line.Kind == "added" && line.Text == "beta updated");
    }

    [Fact]
    public void Compute_handles_empty_inputs_without_throwing()
    {
        var diff = new DiffService();
        Assert.Empty(diff.Compute("", "").Lines);
        Assert.Equal(1, diff.Compute("", "a").AddedCount);
        Assert.Equal(1, diff.Compute("a", "").RemovedCount);
    }

    [Fact]
    public void Compute_rejects_text_larger_than_the_line_limit_rather_than_hanging_or_exhausting_memory()
    {
        var diff = new DiffService();
        string huge = string.Join('\n', Enumerable.Range(0, 3001).Select(i => "line " + i));
        Assert.Throws<InvalidDataException>(() => diff.Compute(huge, "a"));
    }

    [Fact]
    public async Task Diff_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/diff/text", new DiffRequest { OldText = "a", NewText = "b" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diff_endpoint_accepts_the_desktop_session_token_and_returns_a_real_diff()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/diff/text", new DiffRequest { OldText = "a\nb", NewText = "a\nc" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        DiffResult? result = await response.Content.ReadFromJsonAsync<DiffResult>();
        Assert.Equal(1, result?.AddedCount);
        Assert.Equal(1, result?.RemovedCount);
    }
}
