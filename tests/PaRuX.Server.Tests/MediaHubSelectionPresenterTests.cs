using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaHubSelectionPresenterTests
{
    [Fact]
    public void Tabs_have_stable_automation_ids_and_default_to_text()
    {
        Assert.Equal(new[] { "ModelHub_Tab_Text", "ModelHub_Tab_Media", "ModelHub_Tab_Downloads" },
            ModelHubTabs.All.Select(ModelHubTabs.AutomationId));
        Assert.Equal(new[] { "Text models", "Media", "Downloads" }, ModelHubTabs.All.Select(ModelHubTabs.Label));
        Assert.Equal(ModelHubTab.Text, ModelHubTabs.Parse(null));
        Assert.Equal(ModelHubTab.Text, ModelHubTabs.Parse("nonsense"));
        Assert.Equal(ModelHubTab.Media, ModelHubTabs.Parse("Media"));
        Assert.Equal(ModelHubTab.Downloads, ModelHubTabs.Parse("Downloads"));
    }

    [Fact]
    public void Engines_cover_comfy_speech_3d_and_music_with_distinct_ids()
    {
        Assert.Equal(new[] { "image", "video", "edit", "speech", "3d", "music" }, MediaEngineHubPresenter.Engines.Select(e => e.Type));
        Assert.Equal("ModelHub_Engine_3d", MediaEngineHubPresenter.AutomationIdFor("3d"));
    }

    private static MediaSetupStatus Status(string state, bool ready, params MediaSetupStepDto[] steps) =>
        new() { State = state, Ready = ready, Message = "msg", Steps = steps.ToList() };

    private static MediaSetupStepDto Step(string title, string state, long bytes, string license) =>
        new() { Id = title, Title = title, State = state, SizeBytes = bytes, License = license };

    private static readonly MediaEngineDefinition Image = MediaEngineHubPresenter.Engines[0];

    [Fact]
    public void Ready_engine_shows_installed_with_size_and_licence_and_no_action()
    {
        var card = MediaEngineHubPresenter.Present(Image, Status("completed", true,
            Step("ComfyUI", "done", 2L * 1024 * 1024 * 1024, "GPL-3.0"), Step("Model", "done", 6L * 1024 * 1024 * 1024, "Apache-2.0")));
        Assert.True(card.Installed);
        Assert.Equal("Installed", card.StatusLine);
        Assert.Equal("Size on disk: 8.0 GB", card.SizeLine);
        Assert.Equal("Licence: GPL-3.0, Apache-2.0", card.LicenseLine);
        Assert.False(card.ButtonEnabled);
        Assert.False(card.ShowProgress);
    }

    [Fact]
    public void Idle_engine_offers_install_with_download_size()
    {
        var card = MediaEngineHubPresenter.Present(Image, Status("idle", false,
            Step("ComfyUI", "done", 1024L * 1024 * 1024, "GPL-3.0"), Step("Model", "pending", 3L * 1024 * 1024 * 1024, "MIT")));
        Assert.False(card.Installed);
        Assert.Equal("Not installed", card.StatusLine);
        Assert.Equal("Install", card.ButtonLabel);
        Assert.True(card.ButtonEnabled);
        Assert.False(card.IsCancel);
        Assert.Equal("Download: 3.0 GB of 4.0 GB", card.SizeLine);
    }

    [Fact]
    public void Running_engine_shows_progress_and_cancel()
    {
        var status = Status("running", false, Step("Model", "pending", 100, "MIT"));
        status.BytesDownloaded = 50; status.TotalBytes = 200;
        var card = MediaEngineHubPresenter.Present(Image, status);
        Assert.Equal("Installing", card.StatusLine);
        Assert.Equal("Cancel", card.ButtonLabel);
        Assert.True(card.IsCancel);
        Assert.True(card.ShowProgress);
        Assert.Equal(0.25, card.Progress, 3);
        Assert.False(card.Indeterminate);
    }

    [Fact]
    public void Running_engine_without_total_is_indeterminate()
    {
        var card = MediaEngineHubPresenter.Present(Image, Status("running", false));
        Assert.True(card.Indeterminate);
        Assert.True(card.ShowProgress);
    }

    [Theory]
    [InlineData("failed", "Try again", true)]
    [InlineData("cancelled", "Resume", true)]
    [InlineData("blocked", "Install", false)]
    [InlineData("waiting", "Install", false)]
    public void Failed_cancelled_blocked_and_waiting_states_are_honest(string state, string label, bool enabled)
    {
        var card = MediaEngineHubPresenter.Present(Image, Status(state, false));
        Assert.False(card.Installed);
        Assert.Equal(label, card.ButtonLabel);
        Assert.Equal(enabled, card.ButtonEnabled);
        Assert.False(card.IsCancel);
    }

    [Fact]
    public void Unavailable_status_is_retryable_not_installed()
    {
        var card = MediaEngineHubPresenter.Unavailable(Image, "boom");
        Assert.False(card.Installed);
        Assert.Equal("boom", card.Detail);
        Assert.True(card.ButtonEnabled);
    }

    private static MediaCatalogueEntryDto Entry(string cap = ModelCapabilityIds.ImageGenerate, string file = "a.safetensors",
        bool installed = true, string support = "supported", bool auto = false) =>
        new() { CapabilityId = cap, FileName = file, Installed = installed, Support = support, UsedAutomatically = auto };

    private static MediaCheckpointStatus Current(string file, bool pinned) =>
        new() { Type = "image", CurrentFile = file, IsOverridden = pinned, IsCheckpointPinned = pinned };

    [Theory]
    [InlineData(ModelCapabilityIds.ImageGenerate, "image")]
    [InlineData(ModelCapabilityIds.VideoGenerate, "video")]
    [InlineData(ModelCapabilityIds.ImageEdit, "edit")]
    [InlineData("chat", null)]
    public void Capability_maps_to_checkpoint_type(string cap, string? type) =>
        Assert.Equal(type, MediaSelectionPresenter.TypeFor(cap));

    [Fact]
    public void Installed_supported_model_can_be_used()
    {
        var m = MediaSelectionPresenter.ForCard(Entry(), Current("other.safetensors", true));
        Assert.True(m.ShowUse);
        Assert.True(m.UseEnabled);
        Assert.Equal("Use this model", m.UseLabel);
        Assert.False(m.IsActive);
    }

    [Fact]
    public void Pinned_model_shows_in_use_and_disables_button()
    {
        var m = MediaSelectionPresenter.ForCard(Entry(), Current("A.safetensors", true));
        Assert.True(m.IsActive);
        Assert.False(m.UseEnabled);
        Assert.Equal("In use", m.UseLabel);
        Assert.Contains("you chose", m.ActiveLine);
    }

    [Fact]
    public void Automatic_pick_is_labelled_but_can_still_be_pinned()
    {
        var m = MediaSelectionPresenter.ForCard(Entry(auto: true), Current("a.safetensors", false));
        Assert.True(m.IsActive);
        Assert.True(m.UseEnabled);
        Assert.Contains("Automatic", m.ActiveLine);
    }

    [Fact]
    public void Same_effective_file_is_active_even_when_catalog_metadata_does_not_mark_it_automatic()
    {
        var m = MediaSelectionPresenter.ForCard(Entry(), Current("a.safetensors", false));
        Assert.True(m.IsActive);
        Assert.True(m.UseEnabled);
        Assert.Equal("Active: picked by Automatic.", m.ActiveLine);
    }

    [Fact]
    public void Automatic_pick_is_not_active_while_another_file_is_pinned()
    {
        var m = MediaSelectionPresenter.ForCard(Entry(auto: true), Current("other.safetensors", true));
        Assert.False(m.IsActive);
        Assert.Equal(string.Empty, m.ActiveLine);
    }

    [Theory]
    [InlineData(false, "supported", "a.safetensors")]
    [InlineData(true, "resolver_only", "a.safetensors")]
    [InlineData(true, "supported", "")]
    public void Not_installed_or_not_runnable_models_cannot_be_used(bool installed, string support, string file)
    {
        var m = MediaSelectionPresenter.ForCard(Entry(installed: installed, support: support, file: file), Current("x", false));
        Assert.False(m.ShowUse);
        Assert.False(m.UseEnabled);
    }

    [Fact]
    public void Unknown_capability_cannot_be_used() =>
        Assert.False(MediaSelectionPresenter.ForCard(Entry(cap: "audio.speech"), null).ShowUse);

    [Fact]
    public void Capability_header_reports_choice_and_automatic_state()
    {
        var pinned = MediaSelectionPresenter.ForCapability(Current("mine.safetensors", true));
        Assert.True(pinned.AutomaticEnabled);
        Assert.Contains("mine.safetensors", pinned.ActiveLine);
        Assert.Contains("your choice", pinned.ActiveLine);

        var auto = MediaSelectionPresenter.ForCapability(Current("best.safetensors", false));
        Assert.False(auto.AutomaticEnabled);
        Assert.Equal("Automatic (active)", auto.AutomaticLabel);

        Assert.Contains("no model installed", MediaSelectionPresenter.ForCapability(Current("", false)).ActiveLine);
        Assert.False(MediaSelectionPresenter.ForCapability(null).AutomaticEnabled);
    }

    [Fact]
    public void Custom_checkpoint_rows_follow_the_download_state()
    {
        Assert.False(CustomCheckpointPresenter.Present("a.safetensors", "image", null).CanInstall);
        var running = CustomCheckpointPresenter.Present("a.safetensors", "image",
            new ModelDownloadStatus { Status = "downloading", BytesDownloaded = 25, TotalBytes = 100 });
        Assert.Contains("25%", running.StatusText);
        Assert.False(running.CanInstall);
        var done = CustomCheckpointPresenter.Present("a.safetensors", "image", new ModelDownloadStatus { Status = "completed" });
        Assert.True(done.CanInstall);
        Assert.False(done.Failed);
    }

    [Fact]
    public void Family_refusal_is_shown_with_its_typed_reason_and_cannot_be_installed()
    {
        var row = CustomCheckpointPresenter.Present("a.safetensors", "video", new ModelDownloadStatus
        {
            Status = "failed", Error = "generic",
            ErrorDetail = new MediaDependencyError { Reason = "checkpoint_family_unsupported", Message = "This file is a SDXL checkpoint.", NextAction = "Choose a Wan checkpoint." }
        });
        Assert.True(row.Failed);
        Assert.False(row.CanInstall);
        Assert.Contains("SDXL", row.StatusText);
        Assert.Contains("Choose a Wan checkpoint.", row.StatusText);
    }

    [Theory]
    [InlineData("Media checkpoint request failed (400): {\"error\":\"Not listed.\"}", "Not listed.")]
    [InlineData("plain failure", "plain failure")]
    [InlineData("failed {not json", "failed {not json")]
    [InlineData("", "Unknown error.")]
    public void Server_error_text_is_extracted(string raw, string expected) =>
        Assert.Equal(expected, CustomCheckpointPresenter.ExtractServerError(raw));

    [Theory]
    [InlineData("owner/name", "model.safetensors", "image", null)]
    [InlineData("owner/name", "sub/dir/model.safetensors", "edit", null)]
    [InlineData("owner", "model.safetensors", "image", "Repository must look like owner/name.")]
    [InlineData("a/b/c", "model.safetensors", "image", "Repository must look like owner/name.")]
    [InlineData("owner/name", "", "image", "Enter the file path inside the repository.")]
    [InlineData("owner/name", "model.gguf", "image", "Only .safetensors checkpoints can be added.")]
    [InlineData("owner/name", "https://x/y.safetensors", "image", "Enter the path inside the repository, not a URL.")]
    [InlineData("owner/name", "m.safetensors", "audio", "Choose image, video or edit.")]
    [InlineData("owner/name", "m.safetensors", null, "Choose image, video or edit.")]
    public void Form_validation(string repo, string path, string? type, string? expected) =>
        Assert.Equal(expected, HuggingFaceCheckpointForm.Validate(repo, path, type));
}
