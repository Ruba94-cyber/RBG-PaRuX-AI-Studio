using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaCheckpointSelectionPresenterTests
{
    [Fact]
    public void Automatic_router_pick_is_presented_as_automatic_not_as_a_custom_pin()
    {
        var status = new MediaCheckpointStatus
        {
            Type = "image",
            CurrentFile = "z_image_turbo_nvfp4.safetensors",
            IsOverridden = true,
            IsCheckpointPinned = false
        };

        (string activeLine, bool automaticEnabled, string automaticLabel) = MediaSelectionPresenter.ForCapability(status);
        MediaModelPickerState picker = MediaModelPickerPresenter.Build(
            status.CurrentFile, status.IsCheckpointPinned, new[] { status.CurrentFile });

        Assert.Equal("Automatic: z_image_turbo_nvfp4.safetensors.", activeLine);
        Assert.False(automaticEnabled);
        Assert.Equal("Automatic (active)", automaticLabel);
        Assert.Equal(0, picker.SelectedIndex);
        Assert.Equal("PaRuX picks the best installed model for each prompt and tells you why after the result.", picker.Hint);

        var entry = new MediaCatalogueEntryDto
        {
            CapabilityId = "image.generate",
            FileName = status.CurrentFile,
            Support = "supported",
            Installed = true,
            UsedAutomatically = true
        };
        MediaSelectionModel card = MediaSelectionPresenter.ForCard(entry, status);
        Assert.True(card.IsActive);
        Assert.Equal("Active: picked by Automatic.", card.ActiveLine);
        Assert.Equal("Use this model", card.UseLabel);
    }

    [Fact]
    public void Explicit_checkpoint_pin_keeps_the_reset_to_automatic_available()
    {
        var status = new MediaCheckpointStatus
        {
            Type = "image",
            CurrentFile = "zimage_custom.safetensors",
            IsOverridden = true,
            IsCheckpointPinned = true
        };

        (string activeLine, bool automaticEnabled, string automaticLabel) = MediaSelectionPresenter.ForCapability(status);

        Assert.Equal("Active: zimage_custom.safetensors (your choice).", activeLine);
        Assert.True(automaticEnabled);
        Assert.Equal("Automatic", automaticLabel);
    }

    [Fact]
    public void Force_route_without_pin_is_not_presented_as_automatic()
    {
        var status = new MediaCheckpointStatus
        {
            Type = "image", CurrentFile = "forced.safetensors", IsCheckpointPinned = false,
            RoutingMode = "Force", RoutingUsesOverride = true
        };
        (string activeLine, bool automaticEnabled, string automaticLabel) = MediaSelectionPresenter.ForCapability(status);
        Assert.Contains("Force routing", activeLine);
        Assert.False(automaticEnabled);
        Assert.DoesNotContain("Automatic: forced.safetensors", activeLine);
        Assert.Equal("Automatic (active)", automaticLabel);
        Assert.Equal("Force routing", MediaSelectionPresenter.SettingsBadge(status));
        Assert.Equal("The active model is selected by Force routing.", MediaSelectionPresenter.SettingsRoutingHint(status));
    }

    [Fact]
    public void Force_route_shadows_saved_pin_and_offers_to_clear_it()
    {
        var status = new MediaCheckpointStatus
        {
            Type = "image", CurrentFile = "forced.safetensors", IsCheckpointPinned = true,
            RoutingMode = "Force", RoutingUsesOverride = true
        };
        (string activeLine, bool automaticEnabled, string automaticLabel) = MediaSelectionPresenter.ForCapability(status);
        Assert.Contains("selected by Force routing", activeLine);
        Assert.Contains("saved pin is shadowed", activeLine);
        Assert.DoesNotContain("your choice", activeLine);
        Assert.True(automaticEnabled);
        Assert.Equal("Clear pinned model", automaticLabel);

        var entry = new MediaCatalogueEntryDto { CapabilityId = "image.generate", FileName = "forced.safetensors", Support = "supported", Installed = true };
        MediaSelectionModel card = MediaSelectionPresenter.ForCard(entry, status);
        Assert.True(card.IsActive);
        Assert.Contains("Force routing", card.ActiveLine);
        Assert.DoesNotContain("your choice", card.ActiveLine);
        Assert.Equal("Force routing · saved pin shadowed", MediaSelectionPresenter.SummarySource(status));
        Assert.Equal("Force routing", MediaSelectionPresenter.SettingsBadge(status));
        Assert.Equal("The active model is selected by Force routing; a saved checkpoint pin is shadowed.", MediaSelectionPresenter.SettingsRoutingHint(status));
    }
}
