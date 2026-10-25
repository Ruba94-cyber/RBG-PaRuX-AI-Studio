using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Model Hub media section view model: best first, lanes, honest actions. Built from real catalogue view data.</summary>
public sealed class MediaHubPresenterTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static MediaCatalogueResponse Catalogue(params string[] installed) =>
        MediaCatalogueView.Build(MediaCatalogue.Shipped, new MediaHardware(24 * Gib, 64 * Gib), "test PC", "standard",
            new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase), _ => null);

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
        MediaHubCard qwen = model.Single(m => m.CapabilityId == "image.edit").Alternatives.Single(a => a.VariantId == "qwen-image-edit-fp8");
        Assert.Equal(MediaHubAction.None, qwen.Action);
        Assert.Contains("Not runnable by PaRuX yet", qwen.ActionLabel);
        Assert.Contains("Listed only", qwen.Status);
        Assert.Null(MediaHubPresenter.ToDownload(qwen.Entry));
        Assert.All(model.Where(m => m.Best is not null), m => Assert.Equal("supported", m.Best!.Entry.Support));
    }

    [Fact]
    public void Uncensored_lane_is_empty_until_a_repository_declared_entry_exists_and_says_so()
    {
        var model = MediaHubPresenter.Build(Catalogue(), "uncensored");
        Assert.All(model, m => { Assert.Null(m.Best); Assert.Empty(m.Alternatives); Assert.Contains("uncensored", m.EmptyMessage); });
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
        Assert.StartsWith("https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/", dl.DownloadUrl);
    }
}
