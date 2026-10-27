using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Findings of the independent review of the guided media setup, pinned as tests.</summary>
public sealed class MediaSetupHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-hardening-" + Guid.NewGuid().ToString("N"));

    public MediaSetupHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---- companions: "nothing missing" must be a fact, not an absent size ----

    private static MediaCompanionPlan CompanionPlan(string support, params long?[] missingSizes)
    {
        var plan = new MediaCompanionPlan { Support = support };
        foreach (long? size in missingSizes)
            plan.Missing.Add(new MediaCompanionItem { FileName = "x.safetensors", PinnedSizeBytes = size });
        return plan;
    }

    [Fact]
    public void An_unsupported_or_unidentified_family_is_unknown_not_zero_missing_bytes()
    {
        Assert.Null(MediaSetupFacts.CompanionBytes(CompanionPlan("unidentified")));
        Assert.Null(MediaSetupFacts.CompanionBytes(CompanionPlan("resolver_only")));
    }

    [Fact]
    public void A_supported_family_with_nothing_missing_is_zero_and_a_missing_file_without_a_size_is_still_pending()
    {
        Assert.Equal(0, MediaSetupFacts.CompanionBytes(CompanionPlan("supported")));
        Assert.Equal(1, MediaSetupFacts.CompanionBytes(CompanionPlan("supported", (long?)null)));
        Assert.Equal(300, MediaSetupFacts.CompanionBytes(CompanionPlan("supported", 100L, 200L)));
    }

    // ---- checkpoint placement: never leave a truncated file where a finished one is expected ----

    private string File_(string name, int length)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public void Placement_moves_the_download_into_place_through_a_part_file()
    {
        string source = File_("models/a.safetensors", 100);
        string destination = Path.Combine(_dir, "diffusion_models", "a.safetensors");

        MediaCheckpointPlacement.MoveIntoPlace(source, destination, 100);

        Assert.Equal(100, new FileInfo(destination).Length);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public void Placement_refuses_a_wrong_sized_download_and_leaves_no_file_at_the_destination()
    {
        string source = File_("models/b.safetensors", 50);
        string destination = Path.Combine(_dir, "diffusion_models", "b.safetensors");

        Assert.Throws<IOException>(() => MediaCheckpointPlacement.MoveIntoPlace(source, destination, 100));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public void A_truncated_file_already_at_the_destination_is_replaced_and_a_complete_one_is_kept()
    {
        string truncated = File_("diffusion_models/c.safetensors", 10);
        string good = File_("models/c.safetensors", 100);
        MediaCheckpointPlacement.MoveIntoPlace(good, truncated, 100);
        Assert.Equal(100, new FileInfo(truncated).Length);

        string again = File_("models/c.safetensors", 100);
        MediaCheckpointPlacement.MoveIntoPlace(again, truncated, 100);
        Assert.Equal(100, new FileInfo(truncated).Length);
        Assert.False(File.Exists(again));
    }

    // ---- planner: only checkpoints with a pinned digest are offered ----

    [Fact]
    public void The_proposed_checkpoint_carries_a_pinned_sha256()
    {
        MediaSetupPlan plan = MediaSetupPlanner.Plan(MediaCatalogue.Shipped, new MediaHardware(16L << 30, 64L << 30),
            new MediaSetupFacts(true, true, new HashSet<string>()), _ => null);

        MediaSetupStep checkpoint = plan.Steps.Single(s => s.Id == "image-checkpoint");
        Assert.Matches("^[0-9a-f]{64}$", checkpoint.Sha256 ?? string.Empty);
    }

    // ---- the fresh-model queue must expire so one stuck entry cannot shorten the idle gate forever ----

    [Fact]
    public void A_queued_model_older_than_a_day_is_dropped()
    {
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_dir, "state"));
        string model = File_("m.gguf", 4);
        DateTime now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        var queue = new PostDownloadOptimizationQueue(() => now);
        queue.Enqueue(model);

        Assert.Equal(new[] { model }, new PostDownloadOptimizationQueue(() => now.AddHours(23)).Pending());
        Assert.Empty(new PostDownloadOptimizationQueue(() => now.AddHours(25)).Pending());
    }

    // ---- the Create card while another media setup runs ----

    [Fact]
    public void While_another_setup_runs_the_card_waits_instead_of_offering_a_button_that_would_cancel_it()
    {
        var status = new MediaSetupStatus { State = "waiting", Message = "Another media setup (image) is running; wait for it to finish." };

        MediaSetupCardModel card = MediaSetupPresenter.Present(status, "video generation");

        Assert.True(card.Visible);
        Assert.False(card.ButtonEnabled);
        Assert.Contains("Another media setup (image)", card.Detail);
    }
}
