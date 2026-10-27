using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The "Tuning for your PC" status wording and selection. Pure: no UI, no HTTP.</summary>
public sealed class ModelTuningPresenterTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static ModelTuningItemDto Item(string state, string label = "Qwen3.8-27B", string kind = "text", double? gain = null,
        string? reason = null, int ageSeconds = 5) => new()
    {
        Id = kind + label, Kind = kind, ModelLabel = label, State = state, GainPercent = gain, Reason = reason,
        UpdatedUtc = Now.AddSeconds(-ageSeconds)
    };

    private static ModelTuningStatusDto Status(params ModelTuningItemDto[] items) => new() { Items = items.ToList() };

    [Fact]
    public void An_applied_text_result_reads_as_tokens_per_second_gain()
    {
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(Item("applied", gain: 104.2)), Now);
        Assert.True(vm.Visible);
        Assert.Equal("Applied: +104% tokens/s", vm.Detail);
        Assert.Contains("Qwen3.8-27B", vm.Title);
        Assert.False(vm.CanCancel);
        Assert.Equal("success", vm.Severity);
    }

    [Fact]
    public void A_skipped_run_says_no_faster_profile_was_found()
    {
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(Item("skipped", reason: "No faster profile found")), Now);
        Assert.Equal("No faster profile found", vm.Detail);
    }

    [Fact]
    public void A_running_job_is_shown_first_with_cancel_and_busy()
    {
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(
            Item("applied", "old", gain: 10, ageSeconds: 1), Item("queued", "next", ageSeconds: 2), Item("running", "now", ageSeconds: 3)), Now);
        Assert.Contains("now", vm.Title);
        Assert.True(vm.Busy);
        Assert.True(vm.CanCancel);
        Assert.Equal("textnow", vm.CancelId);
    }

    [Fact]
    public void A_media_result_names_image_generation()
    {
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(Item("applied", "z_image_turbo", "media", 22)), Now);
        Assert.Equal("Applied: +22% faster image generation", vm.Detail);
    }

    [Fact]
    public void A_failed_run_keeps_the_defaults_and_warns()
    {
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(Item("failed", reason: "Tuning failed: server did not start")), Now);
        Assert.Equal("warning", vm.Severity);
        Assert.Contains("server did not start", vm.Detail);
        Assert.Contains("default settings stay in use", vm.Detail);
    }

    [Fact]
    public void Old_results_disappear_and_the_footer_shows_only_active_jobs()
    {
        ModelTuningStatusDto old = Status(Item("applied", gain: 50, ageSeconds: (int)ModelTuningPresenter.ResultLifetime.TotalSeconds + 60));
        Assert.False(ModelTuningPresenter.Present(old, Now).Visible);

        ModelTuningStatusDto fresh = Status(Item("applied", gain: 50));
        Assert.True(ModelTuningPresenter.Present(fresh, Now).Visible);
        Assert.Equal(string.Empty, ModelTuningPresenter.FooterLine(fresh, Now));
        Assert.Contains("Queued", ModelTuningPresenter.FooterLine(Status(Item("queued", reason: "Waiting for the PC to be idle")), Now));
    }

    [Fact]
    public void The_media_line_ignores_text_jobs_and_vice_versa()
    {
        ModelTuningStatusDto both = Status(Item("running", "text-model"), Item("running", "ckpt", "media"));
        Assert.Contains("ckpt", ModelTuningPresenter.FooterLine(both, Now, "media"));
        Assert.Contains("text-model", ModelTuningPresenter.FooterLine(both, Now, "text"));
        Assert.Equal(string.Empty, ModelTuningPresenter.FooterLine(Status(Item("running", "text-model")), Now, "media"));
    }

    [Fact]
    public void A_draft_head_proposal_states_the_size_and_that_nothing_is_downloaded_automatically()
    {
        ModelTuningItemDto item = Item("applied", gain: 12);
        item.Proposal = new ModelTuningProposalDto { RepoId = "o/r", FileName = "mtp-Qwen3-8B-Q4_0.gguf", SizeBytes = 420L * 1024 * 1024 };
        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(item), Now);
        Assert.Contains("420 MB", vm.ProposalText);
        Assert.Contains("Nothing is downloaded unless you choose", vm.ProposalText);
        Assert.Equal(string.Empty, ModelTuningPresenter.ProposalText(new ModelTuningProposalDto { FileName = "x.gguf", SizeBytes = 0 }));
    }

    [Fact]
    public void An_already_optimal_result_shows_what_was_measured_and_the_concrete_next_steps_in_the_infobar_text()
    {
        ModelTuningItemDto item = Item("skipped", reason: "Already optimal for this PC (measured 51.2 tok/s)");
        item.Suggestions = new List<ModelTuningSuggestionDto>
        {
            new() { Kind = "smaller_quant", Title = "Try the Q4_K_M version of this model", ExpectedGain = "about +60% tokens/s from the smaller file (estimate)",
                ActionKind = "download_file", FileName = "Qwen3-8B-Q4_K_M.gguf", SizeBytes = 5L << 30 },
            new() { Kind = "switch_backend", Title = "Use the CUDA runtime build", ExpectedGain = "often faster (estimate)", ActionKind = "open_runtime_manager" },
            new() { Kind = "lower_context", Title = "Lower the context window to 16,384 tokens", ExpectedGain = "frees 1.0 GiB (estimate)", ActionKind = "info" },
            new() { Kind = "info", Title = "A fourth suggestion is not spelled out in the InfoBar", ActionKind = "info" }
        };

        ModelTuningViewModel vm = ModelTuningPresenter.Present(Status(item), Now);

        Assert.Equal("Already optimal for this PC (measured 51.2 tok/s)", vm.Detail);
        Assert.StartsWith("Next steps: 1. Try the Q4_K_M version of this model - about +60% tokens/s from the smaller file (estimate)", vm.ProposalText);
        Assert.Contains("[Qwen3-8B-Q4_K_M.gguf, 5120 MB; choose it in Model Hub]", vm.ProposalText);
        Assert.Contains("2. Use the CUDA runtime build - often faster (estimate) [Runtime Manager]", vm.ProposalText);
        Assert.Contains("3. Lower the context window", vm.ProposalText);
        Assert.DoesNotContain("fourth", vm.ProposalText);
        Assert.EndsWith("Nothing is downloaded unless you choose it.", vm.ProposalText);
    }

    [Fact]
    public void Suggestions_survive_the_server_json_and_the_draft_head_hint_is_not_repeated_when_a_draft_suggestion_exists()
    {
        ModelTuningStatusDto parsed = ModelTuningPresenter.Parse(
            "{\"items\":[{\"id\":\"a\",\"kind\":\"text\",\"modelLabel\":\"m\",\"state\":\"skipped\",\"reason\":\"Already optimal for this PC (measured 40.0 tok/s)\","
            + "\"proposal\":{\"kind\":\"draft_head\",\"repoId\":\"o/r\",\"fileName\":\"d.gguf\",\"sizeBytes\":314572800},"
            + "\"suggestions\":[{\"kind\":\"draft_model\",\"title\":\"Add a speculative-decoding draft head\",\"expectedGain\":\"typically 1.3x to 2x (estimate)\","
            + "\"actionKind\":\"download_file\",\"repoId\":\"o/r\",\"fileName\":\"d.gguf\",\"sizeBytes\":314572800}],\"updatedUtc\":\"2026-10-27T12:00:00Z\"}],\"active\":false}");
        ModelTuningViewModel vm = ModelTuningPresenter.Present(parsed, Now);
        Assert.Contains("d.gguf, 300 MB", vm.ProposalText);
        Assert.DoesNotContain("A draft head would speed this model up", vm.ProposalText);
    }

    [Fact]
    public void A_result_without_suggestions_or_proposal_has_no_extra_text()
    {
        Assert.Null(ModelTuningPresenter.Present(Status(Item("skipped", reason: "Cancelled by you")), Now).ProposalText);
    }

    [Fact]
    public void The_server_json_parses_and_the_settings_explanation_names_the_safety_gates()
    {
        ModelTuningStatusDto parsed = ModelTuningPresenter.Parse(
            "{\"items\":[{\"id\":\"a\",\"kind\":\"text\",\"modelLabel\":\"m\",\"state\":\"applied\",\"gainPercent\":104.0,\"updatedUtc\":\"2026-10-27T12:00:00Z\"}],\"active\":false}");
        Assert.Equal(104.0, Assert.Single(parsed.Items).GainPercent);
        Assert.Contains("Gaming Mode", ModelTuningPresenter.SettingsExplanation);
        Assert.Contains("never downloads", ModelTuningPresenter.SettingsExplanation);
        Assert.Empty(ModelTuningPresenter.Parse("null").Items);
    }
}
