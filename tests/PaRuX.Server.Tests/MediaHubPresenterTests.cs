using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Model Hub media section view model: best first, lanes, honest actions. Built from real catalogue view data.</summary>
public sealed class MediaHubPresenterTests
{
    private const long Gib = 1024L * 1024 * 1024;

    // A 16 GB card: Z-Image bf16 fits, Qwen-Image fp8 (about 20 GB) needs offloading, so the best fit stays Z-Image.
    private static MediaCatalogueResponse Catalogue(params string[] installed) => Catalogue(16, installed);

    private static MediaCatalogueResponse Catalogue(int vramGb, params string[] installed) =>
        MediaCatalogueView.Build(MediaCatalogue.Shipped, new MediaHardware(vramGb * Gib, 64 * Gib), "test PC", "standard",
            new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase), _ => null);

    [Fact]
    public void Qwen_image_is_downloadable_with_its_pinned_listing_and_is_the_best_fit_on_a_24_gb_card()
    {
        var model = MediaHubPresenter.Build(Catalogue(24), "standard");
        MediaHubCard best = model.Single(m => m.CapabilityId == "image.generate").Best!;
        Assert.Equal("qwen-image-fp8", best.VariantId);
        Assert.Equal(MediaHubAction.Download, best.Action);
        RecommendedCheckpoint dl = MediaHubPresenter.ToDownload(best.Entry)!;
        Assert.Equal("Comfy-Org/Qwen-Image_ComfyUI", dl.HuggingFaceRepoId);
        Assert.Equal("qwen_image_fp8_e4m3fn.safetensors", dl.FileName);
        Assert.Equal("image", dl.Type);
        Assert.Equal(20_430_635_136, dl.EstimatedSizeBytes);
        Assert.Equal("https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/1f12b17be14c89b026c51a91d67c32f84bb047bc/split_files/diffusion_models/qwen_image_fp8_e4m3fn.safetensors",
            dl.DownloadUrl);

        // On 16 GB it is still listed and downloadable, as an alternative that runs with offloading.
        MediaHubCard alt = Assert.Single(
            MediaHubPresenter.Build(Catalogue(16), "standard").Single(m => m.CapabilityId == "image.generate").Alternatives,
            a => a.VariantId == "qwen-image-fp8");
        Assert.Equal(MediaHubAction.Download, alt.Action);
        Assert.Equal("offload", alt.Entry.FitClass);
    }

    [Fact]
    public void Each_capability_lists_the_best_fit_first_with_fit_size_licence_and_declared_tier()
    {
        var model = MediaHubPresenter.Build(Catalogue(), "standard");
        MediaHubCapabilityModel img = model.Single(m => m.CapabilityId == "image.generate");
        Assert.NotNull(img.Best);
        Assert.True(img.Best!.IsBest);
        Assert.Equal("z-image-turbo-bf16", img.Best.VariantId);
        Assert.Contains("Fits this GPU", img.Best.Subtitle);
        Assert.Contains("Apache-2.0", img.Best.Subtitle);
        Assert.Contains("declared tier, not measured", img.Best.Subtitle);
        Assert.StartsWith("Best fit for this PC", img.Best.Reason);
        Assert.DoesNotContain(img.Alternatives, a => a.VariantId == img.Best.VariantId);
        Assert.Equal(MediaHubAction.Download, img.Best.Action);
    }

    [Fact]
    public void Resolver_only_models_are_visible_but_never_downloadable_and_say_why()
    {
        var model = MediaHubPresenter.Build(Catalogue(), "standard");
        MediaHubCard qwen = model.Single(m => m.CapabilityId == "video.generate").Alternatives.Single(a => a.VariantId == "wan2.2-i2v-14b-fp8");
        Assert.Equal(MediaHubAction.None, qwen.Action);
        Assert.Contains("Not runnable by PaRuX yet", qwen.ActionLabel);
        Assert.Contains("Listed only", qwen.Status);
        Assert.Null(MediaHubPresenter.ToDownload(qwen.Entry));
        Assert.All(model.Where(m => m.Best is not null), m => Assert.Equal("supported", m.Best!.Entry.Support));
    }

    [Fact]
    public void Uncensored_lane_lists_only_repository_declared_image_entries_and_says_so_elsewhere()
    {
        var model = MediaHubPresenter.Build(Catalogue(), "uncensored");
        MediaHubCapabilityModel img = model.Single(m => m.CapabilityId == "image.generate");
        Assert.NotNull(img.Best);
        Assert.All(img.Alternatives.Concat(new[] { img.Best! }), c =>
        {
            Assert.Equal("uncensored", c.Entry.Lane);
            Assert.Contains(c.Entry.Evidence, line => line.StartsWith("lane: uncensored (", StringComparison.Ordinal));
        });
        Assert.All(model.Where(m => m.CapabilityId != "image.generate"),
            m => { Assert.Null(m.Best); Assert.Empty(m.Alternatives); Assert.Contains("uncensored", m.EmptyMessage); });
    }

    [Fact]
    public void Installed_and_automatically_used_state_is_shown_and_replaces_the_download_action()
    {
        var model = MediaHubPresenter.Build(Catalogue("z_image_turbo_int8_convrot.safetensors"), "standard");
        MediaHubCapabilityModel img = model.Single(m => m.CapabilityId == "image.generate");
        MediaHubCard int8 = img.Alternatives.Concat(new[] { img.Best! }).Single(c => c.VariantId == "z-image-turbo-int8");
        Assert.Equal(MediaHubAction.Installed, int8.Action);
        Assert.True(int8.UsedAutomatically);
        Assert.Contains("used automatically", int8.ActionLabel);
        Assert.Contains("z_image_turbo_int8_convrot.safetensors", img.AutomaticLine);
    }

    [Fact]
    public void Download_adapter_feeds_the_existing_consent_lane_with_the_exact_repository_file()
    {
        var model = MediaHubPresenter.Build(Catalogue(), "standard");
        RecommendedCheckpoint dl = MediaHubPresenter.ToDownload(model.Single(m => m.CapabilityId == "image.generate").Best!.Entry)!;
        Assert.Equal("Comfy-Org/z_image_turbo", dl.HuggingFaceRepoId);
        Assert.Equal("z_image_turbo_bf16.safetensors", dl.FileName);
        Assert.Equal("image", dl.Type);
        Assert.Equal(12_309_866_400, dl.EstimatedSizeBytes);
        Assert.Matches("^[0-9a-f]{40}$", dl.Revision);
        Assert.Matches("^[0-9a-f]{64}$", dl.Sha256);
        Assert.Equal("Apache-2.0", dl.License);
        // The catalogue pins the commit the size was listed at, so the Hub downloads exactly those bytes.
        Assert.Matches("^https://huggingface.co/Comfy-Org/z_image_turbo/resolve/[0-9a-f]{40}/", dl.DownloadUrl);
        Assert.StartsWith("https://huggingface.co/Comfy-Org/z_image_turbo/resolve/" + dl.Revision + "/", dl.DownloadUrl);
    }

    [Fact]
    public void Shipped_flux_klein_card_without_catalogue_pin_keeps_optional_download_metadata_null()
    {
        IReadOnlyList<MediaHubCapabilityModel> model = MediaHubPresenter.Build(Catalogue(64), "standard");
        MediaHubCard flux = model
            .SelectMany(capability => capability.Best is { } best
                ? capability.Alternatives.Prepend(best)
                : capability.Alternatives)
            .Single(card => card.VariantId == "flux2-klein-base-4b");

        Assert.Equal(MediaHubAction.Download, flux.Action);
        RecommendedCheckpoint download = MediaHubPresenter.ToDownload(flux.Entry)!;

        Assert.Null(download.Revision);
        Assert.Null(download.Sha256);
        Assert.Equal("Apache-2.0", download.License);
    }
}
