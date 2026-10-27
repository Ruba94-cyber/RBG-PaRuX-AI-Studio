using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The first-run media plan: what a clean PC still needs before Create can generate an image. Hermetic.</summary>
public sealed class MediaSetupPlannerTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static MediaHardware Hw(double vramGb, double ramGb) => new((long)(vramGb * Gib), (long)(ramGb * Gib));
    private static readonly Func<string, double?> NoSpeed = _ => null;

    private static MediaSetupPlan Plan(MediaSetupFacts facts, MediaHardware hw) =>
        MediaSetupPlanner.Plan(MediaCatalogue.Shipped, hw, facts, NoSpeed);

    private static MediaSetupFacts Facts(bool comfy = false, bool assistant = false, params string[] checkpoints) =>
        new(comfy, assistant, new HashSet<string>(checkpoints, StringComparer.OrdinalIgnoreCase), checkpoints.Length > 0 ? 0 : null);

    private static MediaSetupFacts WithMissingCompanions(long bytes) =>
        new(true, true, new HashSet<string>(new[] { "z_image_turbo_bf16.safetensors" }, StringComparer.OrdinalIgnoreCase), bytes);

    [Fact]
    public void Clean_pc_needs_comfyui_the_assistant_model_and_a_checkpoint_that_has_a_real_download_source()
    {
        MediaSetupPlan plan = Plan(Facts(), Hw(16, 64));

        Assert.False(plan.Ready);
        Assert.Equal(new[] { "comfyui", "assistant-model", "image-checkpoint", "image-companions" }, plan.Steps.Select(s => s.Id));
        Assert.All(plan.Steps, s => Assert.Equal(MediaSetupStepState.Pending, s.State));
        MediaSetupStep checkpoint = plan.Steps.Single(s => s.Id == "image-checkpoint");
        // The catalogue's "PaRuX default" nvfp4 file is bundled with no repository: it cannot be fetched on a clean PC.
        Assert.NotEqual("z_image_turbo_nvfp4.safetensors", checkpoint.FileName);
        Assert.False(string.IsNullOrEmpty(checkpoint.DownloadUrl));
        Assert.StartsWith("https://huggingface.co/", checkpoint.DownloadUrl);
        // The bytes are pinned to a commit, not the moving "main" branch, so they match the recorded size.
        Assert.Matches("^[0-9a-f]{40}$", checkpoint.Revision ?? string.Empty);
        Assert.Contains("/resolve/" + checkpoint.Revision + "/", checkpoint.DownloadUrl);
        Assert.True(checkpoint.SizeBytes > 0);
        // Every download step states its licence before anything is downloaded.
        Assert.All(plan.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.License), s.Id));
        Assert.Equal("GPL-3.0", plan.Steps.Single(s => s.Id == "comfyui").License);
    }

    [Fact]
    public void Installed_pieces_are_reported_done_and_the_plan_is_ready_when_all_three_exist()
    {
        MediaSetupPlan plan = Plan(Facts(true, true, "z_image_turbo_bf16.safetensors"), Hw(16, 64));

        Assert.True(plan.Ready);
        Assert.All(plan.Steps, s => Assert.Equal(MediaSetupStepState.Done, s.State));
    }

    [Fact]
    public void Only_the_missing_pieces_stay_pending()
    {
        MediaSetupPlan plan = Plan(Facts(true, false, "z_image_turbo_int8_convrot.safetensors"), Hw(16, 64));

        Assert.False(plan.Ready);
        Assert.Equal(MediaSetupStepState.Done, plan.Steps.Single(s => s.Id == "comfyui").State);
        Assert.Equal(MediaSetupStepState.Pending, plan.Steps.Single(s => s.Id == "assistant-model").State);
        Assert.Equal(MediaSetupStepState.Done, plan.Steps.Single(s => s.Id == "image-checkpoint").State);
    }

    [Fact]
    public void Installed_checkpoint_with_missing_text_encoder_and_vae_is_not_ready_until_they_are_fetched()
    {
        MediaSetupPlan plan = Plan(WithMissingCompanions(8_380_286_436), Hw(16, 64));

        MediaSetupStep companions = plan.Steps.Single(s => s.Id == "image-companions");
        Assert.Equal(MediaSetupStepState.Pending, companions.State);
        Assert.Equal(8_380_286_436, companions.SizeBytes);
        Assert.False(plan.Ready);
    }

    [Fact]
    public void Companions_before_the_checkpoint_exists_are_pending_with_a_catalogue_size_estimate()
    {
        MediaSetupPlan plan = Plan(Facts(true, true), Hw(16, 64));

        MediaSetupStep companions = plan.Steps.Single(s => s.Id == "image-companions");
        Assert.Equal(MediaSetupStepState.Pending, companions.State);
        Assert.True(companions.SizeBytes > 1 * Gib);
    }

    [Theory]
    [InlineData("video", "video.generate", "wan2.2_ti2v_5B_fp16.safetensors")]
    [InlineData("edit", "image.edit", "flux-2-klein-4b.safetensors")]
    public void Video_and_edit_plans_download_their_own_default_checkpoint_with_type_specific_step_ids(string type, string capability, string file)
    {
        MediaSetupPlan plan = MediaSetupPlanner.Plan(MediaCatalogue.Shipped, Hw(24, 64), Facts(), NoSpeed, type);

        Assert.Equal(new[] { "comfyui", "assistant-model", type + "-checkpoint", type + "-companions" }, plan.Steps.Select(s => s.Id));
        Assert.Equal(file, plan.Steps.Single(s => s.Id == type + "-checkpoint").FileName);
        Assert.Equal(capability, MediaSetupPlanner.CapabilityFor(type));
    }

    [Fact]
    public void Speech_plan_is_one_engine_step_that_is_pending_until_both_directions_are_installed()
    {
        MediaSetupPlan missing = MediaSetupPlanner.PlanSpeech(sttInstalled: false, ttsInstalled: false);
        MediaSetupPlan half = MediaSetupPlanner.PlanSpeech(sttInstalled: true, ttsInstalled: false);
        MediaSetupPlan ready = MediaSetupPlanner.PlanSpeech(sttInstalled: true, ttsInstalled: true);

        MediaSetupStep step = Assert.Single(missing.Steps);
        Assert.Equal("speech-engine", step.Id);
        Assert.Equal(MediaSetupStepState.Pending, step.State);
        Assert.True(step.SizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(step.License));
        Assert.False(half.Ready);
        Assert.True(ready.Ready);
        Assert.Equal("speech", MediaSetupService.NormalizeType("speech"));
    }

    [Fact]
    public void Threed_plan_is_one_engine_step_pending_until_installed()
    {
        MediaSetupStep step = Assert.Single(MediaSetupPlanner.PlanThreed(installed: false).Steps);
        Assert.Equal("threed-engine", step.Id);
        Assert.Equal(MediaSetupStepState.Pending, step.State);
        Assert.True(step.SizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(step.License));
        Assert.True(MediaSetupPlanner.PlanThreed(installed: true).Ready);
        Assert.Equal("3d", MediaSetupService.NormalizeType("3d"));
    }

    [Fact]
    public void A_pc_too_small_for_every_checkpoint_is_blocked_with_a_reason_instead_of_downloading_something_unusable()
    {
        MediaSetupPlan plan = Plan(Facts(true, true), Hw(1, 4));

        MediaSetupStep checkpoint = plan.Steps.Single(s => s.Id == "image-checkpoint");
        Assert.Equal(MediaSetupStepState.Blocked, checkpoint.State);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.Detail));
        Assert.False(plan.Ready);
    }
}
