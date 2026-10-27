using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The content-lane setting, the per-request prompt scope, the Automatic wiring and the provenance reason. Hermetic: temp data root, no GPU.</summary>
public sealed class MediaModelRouterWiringTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-lane-" + Guid.NewGuid().ToString("N"));
    private readonly string _prevRoot = PaRuXDataRoot.Path;
    private readonly IDisposable _scope;

    public MediaModelRouterWiringTests()
    {
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _scope = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        MediaCapabilityRouting.CapabilityModeRoot = Path.Combine(_dir, "modes");
        MediaCatalogueLive.HardwareOverride = new MediaHardware(24 * Gib, 64 * Gib);
        var good = new MediaRoutingFileFacts("z-image", "Z-Image", true, Array.Empty<string>(), 1000);
        MediaCapabilityRouting.FactsOverride = _ => new MediaRoutingFacts(new[] { "z_image_turbo_bf16.safetensors" }, _ => good, null);
    }

    public void Dispose()
    {
        MediaCatalogueLive.HardwareOverride = null;
        MediaCapabilityRouting.FactsOverride = null;
        MediaCapabilityRouting.CapabilityModeRoot = null;
        _scope.Dispose();
        PaRuXDataRoot.UseForTests(_prevRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- lane setting ----

    [Fact]
    public void Lane_defaults_to_Standard_and_round_trips_case_insensitively()
    {
        Assert.Equal("Standard", MediaContentLaneStore.Get());
        Assert.Equal("Uncensored", MediaContentLaneStore.Set("uncensored"));
        Assert.Equal("Uncensored", MediaContentLaneStore.Get());
        Assert.Equal("Automatic", MediaContentLaneStore.Set(" AUTOMATIC "));
        Assert.Equal("Automatic", MediaContentLaneStore.Get());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nsfw")]
    [InlineData(null)]
    public void Lane_rejects_unknown_values_and_keeps_the_previous_one(string? value)
    {
        MediaContentLaneStore.Set("Automatic");
        Assert.Throws<InvalidDataException>(() => MediaContentLaneStore.Set(value));
        Assert.Equal("Automatic", MediaContentLaneStore.Get());
    }

    [Fact]
    public void A_damaged_lane_file_reads_as_Standard()
    {
        Directory.CreateDirectory(PaRuXDataRoot.Path);
        File.WriteAllText(Path.Combine(PaRuXDataRoot.Path, "media-content-lane.json"), "{ \"lane\": \"Anything goes\" }");
        Assert.Equal("Standard", MediaContentLaneStore.Get());
    }

    // ---- prompt scope ----

    [Fact]
    public void Routing_context_is_scoped_and_restores_the_previous_prompt()
    {
        Assert.Null(MediaRoutingContext.CurrentPrompt);
        using (MediaRoutingContext.Begin("outer"))
        {
            using (MediaRoutingContext.Begin("inner")) Assert.Equal("inner", MediaRoutingContext.CurrentPrompt);
            Assert.Equal("outer", MediaRoutingContext.CurrentPrompt);
        }
        Assert.Null(MediaRoutingContext.CurrentPrompt);
    }

    // ---- Automatic wiring ----

    [Fact]
    public void Automatic_mode_records_the_router_reason_and_still_picks_the_installed_checkpoint()
    {
        MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
        Assert.Equal("z_image_turbo_bf16.safetensors", d.ChosenFile);
        Assert.True(d.UsesOverride);
        Assert.StartsWith("Automatic: ", d.Explanation);
        Assert.Contains(d.Trace, t => t.StartsWith("lane_preference:Standard", StringComparison.Ordinal));
        Assert.Contains(d.Trace, t => t.StartsWith("automatic_pick:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_prompt_in_scope_reaches_the_router()
    {
        MediaContentLaneStore.Set("Automatic");
        using (MediaRoutingContext.Begin("a realistic portrait photo"))
        {
            MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
            Assert.Contains(d.Trace, t => t.StartsWith("hint:photoreal:", StringComparison.Ordinal));
            Assert.Contains("photoreal", d.Explanation);
        }
        MediaRoutingDecision noPrompt = MediaCapabilityRouting.Preview("image.generate");
        Assert.DoesNotContain(noPrompt.Trace, t => t.StartsWith("hint:", StringComparison.Ordinal));
    }

    [Fact]
    public void Automatic_lane_with_adult_wording_and_no_uncensored_model_uses_Standard_openly()
    {
        MediaContentLaneStore.Set("Automatic");
        using (MediaRoutingContext.Begin("nsfw nude portrait"))
        {
            MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
            Assert.False(d.IsRefusal);
            Assert.Equal("z_image_turbo_bf16.safetensors", d.ChosenFile);
            Assert.Contains("no uncensored", d.Explanation);
            Assert.Contains(d.Trace, t => t == "adult_intent:detected");
        }
    }

    [Fact]
    public void Uncensored_lane_without_an_uncensored_model_refuses_the_job_and_never_uses_Standard()
    {
        MediaContentLaneStore.Set("Uncensored");
        MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
        Assert.True(d.IsRefusal);
        Assert.Equal("no_uncensored_model_installed", d.RefusalCode);
        Assert.Null(d.ChosenFile);
        Assert.False(d.UsesOverride);

        var ex = Assert.Throws<MediaRoutingRefusedException>(() => MediaCheckpointResolution.ResolveForJob("image"));
        Assert.Equal("no_uncensored_model_installed", ex.RefusalCode);
        Assert.Contains("no uncensored", ex.Message);
    }

    [Fact]
    public void An_activated_checkpoint_stays_authoritative_over_the_lane_and_the_router()
    {
        // Pin a file through the same store the Create tab writes, then pick the strictest lane.
        string diffusion = LocalAiPaths.DiffusionModelsPath;
        Directory.CreateDirectory(diffusion);
        string pinned = "z_image_turbo_bf16.safetensors";
        File.WriteAllBytes(Path.Combine(diffusion, pinned), new byte[] { 1 });
        LocalAiPaths.MutateOverrides(Path.Combine(PaRuXDataRoot.Path, "media-checkpoint-overrides.json"), w => { w["image"] = pinned; return true; });
        MediaContentLaneStore.Set("Uncensored");

        MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
        Assert.False(d.IsRefusal);
        Assert.Contains(d.Trace, t => t == "automatic:activated_checkpoint_respected");
    }

    [Fact]
    public void Compatible_files_lists_only_files_whose_family_the_type_can_run()
    {
        var ok = new MediaRoutingFileFacts("z-image", "Z-Image", true, Array.Empty<string>(), 1);
        var bad = ok with { FamilyAllowed = false };
        MediaCapabilityRouting.FactsOverride = _ => new MediaRoutingFacts(new[] { "a.safetensors", "b.safetensors" },
            f => f == "a.safetensors" ? ok : bad, null);
        Assert.Equal(new[] { "a.safetensors" }, MediaCheckpointResolution.CompatibleFiles("image"));
    }

    // ---- provenance ----

    [Fact]
    public void Provenance_carries_the_selection_reason_through_create_failure_and_sanitize()
    {
        MediaJobProvenance created = MediaJobProvenanceFactory.Create("ComfyUI", "id", "z_image_turbo_bf16.safetensors",
            modelSelectionReason: "Z-Image Turbo: strong at photoreal, which your prompt asks for; fits your GPU. Automatic lane stayed Standard.");
        Assert.Contains("strong at photoreal", created.ModelSelectionReason);
        Assert.Equal(created.ModelSelectionReason, MediaJobProvenanceFactory.WithFailure(created, new InvalidOperationException("x")).ModelSelectionReason);
        Assert.Equal(created.ModelSelectionReason, MediaJobProvenanceFactory.Sanitize(created).ModelSelectionReason);
    }

    [Fact]
    public void Provenance_reason_never_carries_a_path()
    {
        MediaJobProvenance p = MediaJobProvenanceFactory.Create("ComfyUI", "id", "m.safetensors", modelSelectionReason: @"chosen from C:\models\m.safetensors");
        Assert.DoesNotContain("models", p.ModelSelectionReason);
        Assert.Null(MediaJobProvenanceFactory.Create("ComfyUI", "id", "m.safetensors").ModelSelectionReason);
    }
}
