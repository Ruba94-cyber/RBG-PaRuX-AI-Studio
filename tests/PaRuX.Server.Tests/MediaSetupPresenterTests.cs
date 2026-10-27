using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>What the Create "Set up image generation" card shows for each server state. Pure.</summary>
public sealed class MediaSetupPresenterTests
{
    private static MediaSetupStatus Status(string state, bool ready = false, string message = "", string? error = null,
        long done = 0, long total = 0, params (string Id, string Title, string State, long Size, string License)[] steps) => new()
    {
        State = state, Ready = ready, Message = message, Error = error, BytesDownloaded = done, TotalBytes = total,
        Steps = steps.Select(s => new MediaSetupStepDto { Id = s.Id, Title = s.Title, State = s.State, SizeBytes = s.Size, License = s.License }).ToList()
    };

    [Fact]
    public void Ready_hides_the_card()
    {
        Assert.False(MediaSetupPresenter.Present(Status("completed", ready: true)).Visible);
    }

    [Fact]
    public void Idle_lists_what_is_needed_with_sizes_and_offers_the_setup_button()
    {
        MediaSetupCardModel card = MediaSetupPresenter.Present(Status("idle", steps: new[]
        {
            ("comfyui", "Image engine (ComfyUI)", "pending", 2_018_444_114L, "GPL-3.0"),
            ("assistant-model", "Prompt assistant model", "done", 0L, "Apache-2.0"),
            ("image-checkpoint", "Image model", "pending", 12_309_866_400L, "Apache-2.0")
        }));

        Assert.True(card.Visible);
        Assert.Equal("Set up image generation", card.ButtonLabel);
        Assert.True(card.ButtonEnabled);
        Assert.Contains("Image engine (ComfyUI)", card.Detail);
        Assert.Contains("Image model", card.Detail);
        Assert.DoesNotContain("Prompt assistant model", card.Detail);
        Assert.Contains("13.3 GB", card.Detail); // total of pending downloads, 1024-based
        Assert.Contains("Licences: GPL-3.0 (Image engine (ComfyUI)), Apache-2.0 (Image model)", card.Detail);
    }

    [Fact]
    public void Running_shows_progress_and_a_cancel_button()
    {
        MediaSetupCardModel card = MediaSetupPresenter.Present(Status("running", message: "Downloading Image model.", done: 500, total: 1000));

        Assert.True(card.ShowProgress);
        Assert.Equal(0.5, card.Progress, 3);
        Assert.Equal("Cancel setup", card.ButtonLabel);
        Assert.Contains("Downloading Image model.", card.Detail);
    }

    [Fact]
    public void Running_without_a_known_total_shows_indeterminate_progress()
    {
        MediaSetupCardModel card = MediaSetupPresenter.Present(Status("running", message: "Installing ComfyUI (extracting)."));

        Assert.True(card.ShowProgress);
        Assert.True(card.Indeterminate);
    }

    [Fact]
    public void Failure_names_the_reason_and_offers_retry()
    {
        MediaSetupCardModel card = MediaSetupPresenter.Present(Status("failed", message: "Image model could not be downloaded", error: "HTTP 503 from huggingface.co"));

        Assert.Equal("Try again", card.ButtonLabel);
        Assert.Contains("HTTP 503 from huggingface.co", card.Detail);
        Assert.Contains("stopped", card.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelled_offers_resume_and_blocked_has_no_button_action()
    {
        Assert.Equal("Resume setup", MediaSetupPresenter.Present(Status("cancelled", message: "cancelled")).ButtonLabel);

        MediaSetupCardModel blocked = MediaSetupPresenter.Present(Status("blocked", message: "No supported image model fits this PC."));
        Assert.False(blocked.ButtonEnabled);
        Assert.Contains("No supported image model fits this PC.", blocked.Detail);
    }
}
