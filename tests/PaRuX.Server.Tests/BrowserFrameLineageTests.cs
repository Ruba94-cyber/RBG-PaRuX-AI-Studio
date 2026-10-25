using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserFrameLineageTests
{
    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Seeded_tree_yields_nearest_first_chain_and_top_frame()
    {
        var lineage = new BrowserFrameLineage();
        lineage.SeedFromFrameTree(Json("""
            {"frameTree":{"frame":{"id":"top","url":"http://127.0.0.1:8080/app"},
              "childFrames":[{"frame":{"id":"child","parentId":"top","url":"https://ads.example/frame"}}]}}
            """));

        Assert.Equal("top", lineage.TopFrameId);
        Assert.Equal(new string?[] { "https://ads.example/frame", "http://127.0.0.1:8080/app" },
            lineage.ChainFrom("child"));
        Assert.Equal(new string?[] { "http://127.0.0.1:8080/app" }, lineage.ChainFrom("top"));
    }

    [Fact]
    public void Navigation_replaces_the_url_so_a_missing_url_becomes_unknown()
    {
        var lineage = new BrowserFrameLineage();
        lineage.SeedFromFrameTree(Json("""{"frameTree":{"frame":{"id":"top","url":"about:blank"}}}"""));

        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"top","url":"https://example.org/"}}"""));
        Assert.Equal(new string?[] { "https://example.org/" }, lineage.ChainFrom("top"));

        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"top"}}"""));
        Assert.Equal(new string?[] { null }, lineage.ChainFrom("top"));
    }

    [Fact]
    public void Same_document_navigation_updates_the_frame_url()
    {
        var lineage = new BrowserFrameLineage();
        lineage.SeedFromFrameTree(Json("""{"frameTree":{"frame":{"id":"top","url":"http://127.0.0.1:5173/"}}}"""));

        lineage.Apply("Page.navigatedWithinDocument", Json("""{"frameId":"top","url":"https://example.org/#x"}"""));
        Assert.Equal(new string?[] { "https://example.org/#x" }, lineage.ChainFrom("top"));
    }

    [Fact]
    public void Attached_frame_is_unknown_until_it_navigates_and_detach_removes_its_subtree()
    {
        var lineage = new BrowserFrameLineage();
        lineage.SeedFromFrameTree(Json("""{"frameTree":{"frame":{"id":"top","url":"http://127.0.0.1:5173/"}}}"""));

        lineage.Apply("Page.frameAttached", Json("""{"frameId":"child","parentFrameId":"top"}"""));
        Assert.Equal(new string?[] { null, "http://127.0.0.1:5173/" }, lineage.ChainFrom("child"));

        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"child","parentId":"top","url":"http://127.0.0.1:5173/inner"}}"""));
        lineage.Apply("Page.frameAttached", Json("""{"frameId":"grandchild","parentFrameId":"child"}"""));
        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"grandchild","parentId":"child","url":"https://ads.example/"}}"""));
        Assert.Equal(new string?[] { "https://ads.example/", "http://127.0.0.1:5173/inner", "http://127.0.0.1:5173/" },
            lineage.ChainFrom("grandchild"));

        lineage.Apply("Page.frameDetached", Json("""{"frameId":"child","reason":"remove"}"""));
        Assert.Equal(new string?[] { null }, lineage.ChainFrom("child"));
        Assert.Equal(new string?[] { null }, lineage.ChainFrom("grandchild"));
        Assert.Equal(new string?[] { "http://127.0.0.1:5173/" }, lineage.ChainFrom("top"));
    }

    [Fact]
    public void Unknown_or_missing_frame_ids_fail_closed()
    {
        var lineage = new BrowserFrameLineage();
        Assert.Equal(new string?[] { null }, lineage.ChainFrom(null));
        Assert.Equal(new string?[] { null }, lineage.ChainFrom("never-seen"));
        Assert.Null(lineage.TopFrameId);
    }

    [Fact]
    public void Parent_cycles_terminate_with_unknown_ancestry()
    {
        var lineage = new BrowserFrameLineage();
        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"a","parentId":"b","url":"http://127.0.0.1/"}}"""));
        lineage.Apply("Page.frameNavigated", Json("""{"frame":{"id":"b","parentId":"a","url":"http://127.0.0.1/"}}"""));

        IReadOnlyList<string?> chain = lineage.ChainFrom("a");
        Assert.Null(chain[^1]);
        Assert.True(chain.Count <= 65);
    }

    [Fact]
    public void Clear_forgets_every_frame_and_the_top_frame()
    {
        var lineage = new BrowserFrameLineage();
        lineage.SeedFromFrameTree(Json("""{"frameTree":{"frame":{"id":"top","url":"http://127.0.0.1/"}}}"""));
        lineage.Clear();
        Assert.Null(lineage.TopFrameId);
        Assert.Equal(new string?[] { null }, lineage.ChainFrom("top"));
    }

    [Fact]
    public void Unrelated_and_malformed_events_are_ignored_without_throwing()
    {
        var lineage = new BrowserFrameLineage();
        Assert.False(lineage.Apply("Network.requestWillBeSent", Json("{}")));
        Assert.False(lineage.Apply("Page.loadEventFired", Json("{}")));
        Assert.True(lineage.Apply("Page.frameNavigated", Json("""{"frame":"not-an-object"}""")));
        Assert.True(lineage.Apply("Page.frameDetached", Json("{}")));
        Assert.Null(lineage.TopFrameId);
    }
}
