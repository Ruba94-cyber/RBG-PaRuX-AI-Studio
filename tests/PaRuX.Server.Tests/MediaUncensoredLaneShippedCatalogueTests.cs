using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The uncensored-capable families of the SHIPPED catalogue (Chroma1-HD, the uncensored SDXL finetunes) must be listed under the
/// Uncensored group and be what the router picks for the Uncensored lane, or for Automatic with explicit adult wording in English or Italian.
/// </summary>
public sealed class MediaUncensoredLaneShippedCatalogueTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static MediaHardware Big => new(24 * Gib, 64 * Gib);
    private static readonly Func<string, double?> NoSpeed = _ => null;

    private static ISet<string> Files(params string[] f) => new HashSet<string>(f, StringComparer.OrdinalIgnoreCase);

    private static MediaRouteResult Route(string prompt, string lane, params string[] installed) =>
        MediaModelRouter.Route(MediaCatalogue.Shipped, ModelCapabilityIds.ImageGenerate, prompt, lane, Big, Files(installed), NoSpeed);

    private const string Chroma = "Chroma1-HD.safetensors";
    private const string Standard = "z_image_turbo_bf16.safetensors";

    // ---- catalogue split ----

    [Fact]
    public void Every_uncensored_capable_family_of_the_catalogue_is_in_the_uncensored_group_and_none_in_standard()
    {
        MediaCatalogueResponse view = MediaCatalogueView.Build(MediaCatalogue.Shipped, Big, "test", "standard", Files(), NoSpeed);
        var image = MediaHubPresenter.Build(view, "uncensored").Single(m => m.CapabilityId == "image.generate");
        var standard = MediaHubPresenter.Build(view, "standard").Single(m => m.CapabilityId == "image.generate");

        string[] uncensored = image.Alternatives.Concat(image.Best is null ? Array.Empty<MediaHubCard>() : new[] { image.Best })
            .Select(c => c.VariantId).ToArray();
        Assert.Contains("chroma1-hd-bf16", uncensored);
        Assert.Contains("chroma1-hd-fp8mixed", uncensored);
        Assert.Contains("pony-diffusion-v6-xl", uncensored);
        Assert.Contains("noobai-xl-1.1", uncensored);

        string[] shownAsStandard = standard.Alternatives.Concat(standard.Best is null ? Array.Empty<MediaHubCard>() : new[] { standard.Best })
            .Select(c => c.VariantId).ToArray();
        Assert.DoesNotContain(shownAsStandard, id => id.StartsWith("chroma1-hd", StringComparison.Ordinal));
        // Everything the catalogue declares uncensored is in the uncensored group, nothing is lost between the two.
        foreach (MediaCatalogueVariant v in MediaCatalogue.Shipped.VariantsFor(ModelCapabilityIds.ImageGenerate).Select(x => x.Variant).Where(v => v.Lane == "uncensored"))
            Assert.Contains(v.Id, uncensored);
    }

    [Fact]
    public void The_standard_tab_points_at_the_uncensored_models_instead_of_hiding_them()
    {
        MediaCatalogueResponse view = MediaCatalogueView.Build(MediaCatalogue.Shipped, Big, "test", "standard", Files(), NoSpeed);

        string standardLane = MediaHubPresenter.UncensoredPointer(view, "Standard");
        string automatic = MediaHubPresenter.UncensoredPointer(view, "Automatic");
        string uncensored = MediaHubPresenter.UncensoredPointer(view, "Uncensored");

        Assert.Contains("Chroma1-HD", standardLane);
        Assert.Contains("Uncensored tab", standardLane);
        Assert.Contains("explicit adult prompt", standardLane);
        Assert.Contains("only when the prompt explicitly asks", automatic);
        Assert.Contains("only these are used", uncensored);
    }

    [Fact]
    public void No_pointer_when_nothing_uncensored_is_listed() =>
        Assert.Equal(string.Empty, MediaHubPresenter.UncensoredPointer(new MediaCatalogueResponse(), "Standard"));

    [Theory]
    [InlineData("Uncensored", "uncensored")]
    [InlineData("uncensored", "uncensored")]
    [InlineData("Automatic", "standard")]
    [InlineData("Standard", "standard")]
    [InlineData(null, "standard")]
    public void The_hub_opens_the_group_that_matches_the_global_lane(string? global, string expected) =>
        Assert.Equal(expected, MediaHubPresenter.InitialTabFor(global));

    // ---- routing ----

    [Fact]
    public void Uncensored_lane_picks_chroma_when_installed_for_any_prompt()
    {
        MediaRouteResult r = Route("a mountain lake at dawn", "Uncensored", Chroma, Standard);

        Assert.Equal("ok", r.Code);
        Assert.Equal("uncensored", r.EffectiveLane);
        Assert.Equal(Chroma, r.ChosenFile);
    }

    [Theory]
    [InlineData("nsfw portrait of a nude woman, photo")]
    [InlineData("una ragazza nuda sulla spiaggia, contenuto esplicito")]
    public void Automatic_with_an_explicit_english_or_italian_prompt_routes_to_the_installed_uncensored_family(string prompt)
    {
        MediaRouteResult r = Route(prompt, "Automatic", Chroma, Standard);

        Assert.True(r.AdultIntent);
        Assert.Equal("uncensored", r.EffectiveLane);
        Assert.Equal(Chroma, r.ChosenFile);
        Assert.Contains("Automatic lane chose uncensored", r.Reason);
    }

    [Theory]
    [InlineData("a quiet mountain lake")]
    [InlineData("un gatto sul divano")]
    public void Automatic_with_an_ordinary_prompt_and_the_standard_lane_never_pick_chroma(string prompt)
    {
        Assert.Equal(Standard, Route(prompt, "Automatic", Chroma, Standard).ChosenFile);
        Assert.Equal(Standard, Route(prompt, "Standard", Chroma, Standard).ChosenFile);
    }

    [Theory]
    [InlineData("nsfw portrait of a nude woman")]
    [InlineData("una ragazza nuda")]
    public void Automatic_with_an_explicit_prompt_but_nothing_uncensored_installed_says_what_to_install(string prompt)
    {
        MediaRouteResult r = Route(prompt, "Automatic", Standard);

        Assert.Equal(Standard, r.ChosenFile);
        Assert.NotNull(r.InstallHint);
        Assert.Contains("Install", r.InstallHint);
    }
}
