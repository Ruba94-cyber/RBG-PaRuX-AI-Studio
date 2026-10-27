using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Model Hub presenter pieces for discovered (Hugging Face) media models.</summary>
public sealed class MediaHubDiscoveryPresenterTests
{
    private static MediaCatalogueEntryDto E(string id, string lane, bool best, bool discovered = true) => new()
    {
        VariantId = id, Title = id, CapabilityId = "image.generate", Lane = lane, Discovered = discovered, IsBestProposed = best,
        Support = "supported", FitClass = "fits", SizeBasis = "exact_listing", SizeBytes = 8L << 30, License = "apache-2.0",
        Downloads = 12345, Likes = 7, Downloadable = true, DownloadUrl = "https://huggingface.co/a/b/resolve/" + new string('a', 40) + "/x.safetensors",
        Repository = "a/b", FileName = id + ".safetensors", LaneEvidence = lane == "uncensored" ? "repository tag \"uncensored\"" : string.Empty
    };

    private static MediaCatalogueResponse Resp(string status, params MediaCatalogueEntryDto[] entries)
    {
        var cap = new MediaCatalogueCapabilityDto { CapabilityId = "image.generate", Label = "Image generation" };
        cap.Entries.AddRange(entries);
        return new MediaCatalogueResponse { DiscoveryStatus = status, Capabilities = { cap } };
    }

    [Fact]
    public void Discovered_cards_are_listed_apart_best_first_with_badge_and_label_per_lane()
    {
        var m = MediaHubPresenter.Build(Resp("ok", E("curated", "standard", false, discovered: false), E("d1", "standard", false), E("d2", "standard", true), E("u1", "uncensored", true)), "standard").Single();
        Assert.Equal(new[] { "d2", "d1" }, m.DiscoveredCards.Select(c => c.VariantId));
        Assert.Equal("Best for this PC", m.DiscoveredCards[0].Badge);
        Assert.Equal(string.Empty, m.DiscoveredCards[1].Badge);
        Assert.Contains("discovered, not curated", m.DiscoveredCards[0].Subtitle);
        Assert.Contains("12,345 downloads", m.DiscoveredCards[0].Subtitle);
        Assert.Equal(MediaHubAction.Download, m.DiscoveredCards[0].Action);
        Assert.NotNull(MediaHubPresenter.ToDownload(m.DiscoveredCards[0].Entry)); // same consent + pinned download lane
        Assert.DoesNotContain(m.Alternatives, c => c.Entry.Discovered);

        var u = MediaHubPresenter.Build(Resp("ok", E("u1", "uncensored", true)), "uncensored").Single();
        Assert.Contains("lane evidence", u.DiscoveredCards[0].Subtitle);
    }

    [Fact]
    public void Empty_messages_and_status_lines_are_honest()
    {
        var never = MediaHubPresenter.Build(Resp("never_refreshed"), "standard").Single();
        Assert.Contains("Refresh from Hugging Face", never.DiscoveredMessage);
        var after = MediaHubPresenter.Build(Resp("ok"), "uncensored").Single();
        Assert.Contains("found on Hugging Face", after.DiscoveredMessage);
        Assert.Contains("never downloads", MediaHubPresenter.DiscoveryStatusLine(new MediaCatalogueResponse()));
        Assert.StartsWith("Offline", MediaHubPresenter.DiscoveryStatusLine(new MediaCatalogueResponse { DiscoveryStatus = "offline_cached" }));
        Assert.Contains("kept", MediaHubPresenter.RefreshResultLine(new MediaDiscoveryRefreshResultDto { Outcome = "offline_cached" }));
        Assert.Contains("3 model(s)", MediaHubPresenter.RefreshResultLine(new MediaDiscoveryRefreshResultDto { Outcome = "ok", CandidateCount = 3 }));
    }
}
