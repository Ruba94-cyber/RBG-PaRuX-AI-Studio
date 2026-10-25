using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Media fit estimator, catalogue integrity, selection matrix, Hub view data and maintenance proposals. Hermetic: no GPU, disk or network.</summary>
public sealed class MediaCatalogueTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private static MediaHardware Hw(double? vramGb, double? ramGb) =>
        new(vramGb is null ? null : (long)(vramGb * Gib), ramGb is null ? null : (long)(ramGb * Gib));
    private static readonly Func<string, double?> NoSpeed = _ => null;
    private static ISet<string> Files(params string[] f) => new HashSet<string>(f, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        MediaCatalogueLive.HardwareOverride = null;
        MediaCapabilityRouting.FactsOverride = null;
        MediaCapabilityRouting.CapabilityModeRoot = null;
    }

    // ---- estimator table ----

    private static MediaFitInput Img(string precision, double paramsB, long? listed = null) =>
        new(ModelCapabilityIds.ImageGenerate, precision, paramsB, listed, 0, (long)(0.33 * Gib), 1.0, 1, 0);

    public static IEnumerable<object?[]> EstimatorTable() => new[]
    {
        // vram, ram, precision, paramsB, listed, expected
        new object?[] { 12.0, 32.0, "int8", 6.0, 6_201_001_296L, "fits" },
        new object?[] { 6.0, 32.0, "int8", 6.0, 6_201_001_296L, "offload" },
        new object?[] { 1.0, 64.0, "int8", 6.0, 6_201_001_296L, "too_large" },
        new object?[] { null, 64.0, "int8", 6.0, 6_201_001_296L, "unknown" },
        new object?[] { 6.0, null, "int8", 6.0, 6_201_001_296L, "unknown" },
        new object?[] { 6.0, 8.0, "int8", 6.0, 6_201_001_296L, "too_large" },
        new object?[] { 24.0, 64.0, "bf16", 20.0, null, "offload" },
        new object?[] { 24.0, 64.0, "fp8", 20.0, null, "fits" },
        new object?[] { 16.0, 64.0, "q4_k_m", 20.0, null, "fits" },
        new object?[] { 8.0, 32.0, "no-such-precision", 6.0, null, "unknown" },
    };

    [Theory]
    [MemberData(nameof(EstimatorTable))]
    public void Estimator_classifies_conservatively(double? vram, double? ram, string precision, double paramsB, long? listed, string expected)
    {
        MediaFitResult r = MediaFitEstimator.Estimate(Img(precision, paramsB, listed), Hw(vram, ram));
        Assert.Equal(expected, r.Code);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void Estimator_video_length_raises_the_requirement_and_unknown_never_claims_fit()
    {
        MediaFitInput Vid(int frames) => new(ModelCapabilityIds.VideoGenerate, "fp8", 5.0, null, 0, 0, 0.5, frames, 0);
        Assert.True(MediaFitEstimator.Estimate(Vid(161), Hw(24, 64)).RequiredVramBytes > MediaFitEstimator.Estimate(Vid(41), Hw(24, 64)).RequiredVramBytes);
        Assert.NotEqual(MediaFitClass.Fits, MediaFitEstimator.Estimate(Vid(41), Hw(null, null)).Class);
    }

    [Fact]
    public void Hardware_from_gpu_ignores_software_adapters_and_reports_null_when_absent()
    {
        var gpu = new GpuInventoryStatus { Adapters = { new GpuAdapterStatus { Name = "Basic", IsSoftware = true, DedicatedVideoMemoryBytes = 0 },
            new GpuAdapterStatus { Name = "X", DedicatedVideoMemoryBytes = 8 * Gib } } };
        Assert.Equal(8 * Gib, MediaHardware.From(gpu, 16 * Gib).VramBytes);
        Assert.Null(MediaHardware.From(null, 16 * Gib).VramBytes);
        Assert.Null(MediaHardware.From(gpu, 0).RamBytes);
    }

    // ---- catalogue integrity ----

    [Fact]
    public void Shipped_catalogue_loads_and_marks_only_validated_families_supported()
    {
        MediaCatalogue c = MediaCatalogue.Shipped;
        Assert.Equal(new[] { "flux2-klein", "wan2.2-ti2v-5b", "z-image" },
            c.Families.Where(f => f.IsSupported).Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (string id in new[] { "qwen-image", "qwen-image-edit", "wan2.2-14b", "ltx-video", "ace-step" })
        {
            MediaCatalogueFamily f = c.Families.Single(x => x.Id == id);
            Assert.Equal("resolver_only", f.Support);
            Assert.False(string.IsNullOrWhiteSpace(f.Reason));
        }
        Assert.Contains(c.Families, f => f.Capability == "audio.music");
        Assert.Contains(c.Families, f => f.Capability == "audio.speak");
    }

    [Fact]
    public void Shipped_catalogue_entries_are_complete_unique_and_honest()
    {
        MediaCatalogue c = MediaCatalogue.Shipped;
        var variants = c.Families.SelectMany(f => f.Variants.Select(v => (f, v))).ToList();
        Assert.Equal(variants.Count, variants.Select(x => x.v.Id).Distinct().Count());
        Assert.Equal(variants.Count, variants.Select(x => x.v.FileName.ToLowerInvariant()).Distinct().Count());
        foreach (var (f, v) in variants)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.License));
            Assert.InRange(v.Tier, 1, 5);
            Assert.Equal(v.ListingVerified, v.SizeBytes is not null);
            if (v.Lane == "uncensored") Assert.False(string.IsNullOrWhiteSpace(v.LaneEvidence));
            if (v.DownloadUrl is not null) Assert.StartsWith("https://huggingface.co/", v.DownloadUrl);
            if (!f.IsSupported) Assert.False(v.ListingVerified && v.Bundled);
        }
    }

    [Fact]
    public void Verified_sizes_match_the_existing_recommended_checkpoints()
    {
        MediaCatalogue c = MediaCatalogue.Shipped;
        foreach (RecommendedCheckpoint r in RecommendedCheckpointsService.List)
        {
            MediaCatalogueVariant v = c.Families.SelectMany(f => f.Variants).Single(x => x.FileName == r.FileName);
            Assert.Equal(r.EstimatedSizeBytes, v.SizeBytes);
            Assert.Equal(r.HuggingFaceRepoId, v.Repository);
            Assert.Equal(r.DownloadUrl, v.DownloadUrl);
        }
    }

    private static string Doc(string family) => """
        { "schemaVersion": 1, "catalogueVersion": 1,
          "capabilities": [ { "id": "image.generate", "label": "I", "defaultJob": { "megapixels": 1, "frames": 1, "seconds": 0 } } ],
          "families": [ FAMILY ] }
        """.Replace("FAMILY", family);

    private const string GoodVariant = """{ "id": "v1", "title": "V", "lane": "standard", "precision": "fp8", "repository": null, "path": null, "fileName": "a.safetensors", "sizeBytes": null, "sizeBasis": "derived_from_parameters", "license": "MIT", "tier": 3, "listingVerified": false, "baseCompat": "x" }""";

    [Theory]
    [InlineData("""{ "id": "f", "displayName": "F", "capability": "image.generate", "support": "supported", "registryFamily": "flux1", "parametersBillion": 1, "textEncoderGb": 0, "vaeGb": 0, "variants": [ V ] }""")]
    [InlineData("""{ "id": "f", "displayName": "F", "capability": "image.generate", "support": "resolver_only", "parametersBillion": 1, "textEncoderGb": 0, "vaeGb": 0, "variants": [ V ] }""")]
    [InlineData("""{ "id": "f", "displayName": "F", "capability": "image.generate", "support": "resolver_only", "reason": "r", "parametersBillion": 1, "textEncoderGb": 0, "vaeGb": 0, "variants": [] }""")]
    public void Parse_rejects_supported_without_a_template_missing_reasons_and_empty_families(string family)
    {
        Assert.Throws<InvalidDataException>(() => MediaCatalogue.Parse(Doc(family.Replace("V ]", GoodVariant + " ]")), MediaCompanionRegistry.Shipped));
    }

    [Theory]
    [InlineData("\"lane\": \"standard\"", "\"lane\": \"uncensored\"")]
    [InlineData("\"sizeBytes\": null", "\"sizeBytes\": 5")]
    [InlineData("\"tier\": 3", "\"tier\": 9")]
    [InlineData("\"precision\": \"fp8\"", "\"precision\": \"weird\"")]
    [InlineData("\"fileName\": \"a.safetensors\"", "\"fileName\": \"../a.safetensors\"")]
    public void Parse_rejects_bad_variants(string from, string to)
    {
        string fam = "{ \"id\": \"f\", \"displayName\": \"F\", \"capability\": \"image.generate\", \"support\": \"resolver_only\", \"reason\": \"r\", \"parametersBillion\": 1, \"textEncoderGb\": 0, \"vaeGb\": 0, \"variants\": [ "
            + GoodVariant.Replace(from, to) + " ] }";
        Assert.Throws<InvalidDataException>(() => MediaCatalogue.Parse(Doc(fam), MediaCompanionRegistry.Shipped));
    }

    [Fact]
    public void Parse_accepts_an_uncensored_entry_only_with_repository_lane_evidence()
    {
        string fam = "{ \"id\": \"f\", \"displayName\": \"F\", \"capability\": \"image.generate\", \"support\": \"resolver_only\", \"reason\": \"r\", \"parametersBillion\": 1, \"textEncoderGb\": 0, \"vaeGb\": 0, \"variants\": [ "
            + GoodVariant.Replace("\"lane\": \"standard\"", "\"lane\": \"uncensored\", \"laneEvidence\": \"repo model card declares it uncensored\"") + " ] }";
        MediaCatalogue c = MediaCatalogue.Parse(Doc(fam), MediaCompanionRegistry.Shipped);
        Assert.Equal("uncensored", c.Families[0].Variants[0].Lane);
    }

    // ---- selection matrix ----

    private static MediaSelection Sel(string cap, MediaHardware hw, ISet<string> installed, string lane = "standard",
        Func<string, double?>? speed = null, Func<string, bool>? eligible = null) =>
        MediaSelectionEngine.Select(MediaCatalogue.Shipped, cap, lane, hw, installed, speed ?? NoSpeed, eligible);

    [Fact]
    public void Roomy_pc_recommends_the_highest_declared_tier_that_fits_and_never_a_resolver_only_model()
    {
        MediaSelection s = Sel("image.generate", Hw(24, 64), Files());
        Assert.Equal("z-image-turbo-bf16", s.Recommended!.Variant.Id);
        Assert.True(s.Recommended.Family.IsSupported);
        Assert.Null(s.Automatic);
    }

    [Fact]
    public void A_variant_that_fits_beats_a_higher_tier_that_needs_offloading()
    {
        MediaSelection s = Sel("image.generate", Hw(8, 32), Files());
        Assert.Equal("z-image-turbo-nvfp4", s.Recommended!.Variant.Id);
        Assert.Equal(MediaFitClass.Fits, s.Recommended.Fit.Class);
    }

    [Fact]
    public void Automatic_picks_the_best_installed_eligible_variant()
    {
        ISet<string> both = Files("z_image_turbo_bf16.safetensors", "z_image_turbo_int8_convrot.safetensors");
        Assert.Equal("z_image_turbo_bf16.safetensors", Sel("image.generate", Hw(24, 64), both).Automatic!.Variant.FileName);
        MediaSelection noBf16 = Sel("image.generate", Hw(24, 64), both, eligible: f => !f.Contains("bf16"));
        Assert.Equal("z_image_turbo_int8_convrot.safetensors", noBf16.Automatic!.Variant.FileName);
        Assert.Equal("z_image_turbo_bf16.safetensors", noBf16.Recommended!.Variant.FileName);
    }

    [Fact]
    public void Automatic_never_uses_unknown_files_too_large_models_or_resolver_only_families()
    {
        Assert.Null(Sel("image.generate", Hw(24, 64), Files("my_own_model.safetensors")).Automatic);
        Assert.Null(Sel("image.generate", Hw(3, 8), Files("z_image_turbo_nvfp4.safetensors")).Automatic);
        Assert.Null(Sel("image.generate", Hw(24, 64), Files("qwen_image_fp8_e4m3fn.safetensors")).Automatic);
    }

    [Fact]
    public void Measured_speed_breaks_ties_between_equal_fit_and_tier_and_size_otherwise()
    {
        ISet<string> two = Files("z_image_turbo_int8_convrot.safetensors", "z_image_turbo_nvfp4.safetensors");
        Assert.Equal("z_image_turbo_nvfp4.safetensors", Sel("image.generate", Hw(24, 64), two).Automatic!.Variant.FileName);
        MediaSelection fast = Sel("image.generate", Hw(24, 64), two, speed: f => f.Contains("int8") ? 5 : 10);
        Assert.Equal("z_image_turbo_int8_convrot.safetensors", fast.Automatic!.Variant.FileName);
        Assert.Contains("last measured job 5", fast.Explanation);
    }

    [Fact]
    public void Uncensored_lane_without_installed_entries_falls_back_to_standard_and_says_so()
    {
        MediaSelection s = Sel("image.generate", Hw(24, 64), Files("z_image_turbo_bf16.safetensors"), lane: "uncensored");
        Assert.Null(s.Recommended);
        Assert.Equal("z_image_turbo_bf16.safetensors", s.Automatic!.Variant.FileName);
        Assert.Contains(s.Trace, t => t.StartsWith("no_installed_uncensored", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_hardware_never_claims_a_fit_but_still_allows_a_conservative_choice()
    {
        MediaSelection s = Sel("image.generate", Hw(null, null), Files("z_image_turbo_nvfp4.safetensors"));
        Assert.Equal(MediaFitClass.Unknown, s.Automatic!.Fit.Class);
        Assert.Contains("fit unknown", s.Explanation);
    }

    // ---- Hub view data ----

    [Fact]
    public void View_has_one_recommendation_per_capability_with_reason_fit_size_and_licence()
    {
        MediaCatalogueResponse r = MediaCatalogueView.Build(MediaCatalogue.Shipped, Hw(24, 64), "test", "standard", Files(), NoSpeed);
        Assert.Equal(5, r.Capabilities.Count);
        foreach (MediaCatalogueCapabilityDto cap in r.Capabilities)
            Assert.True(cap.Entries.Count(e => e.IsRecommended) <= 1);
        MediaCatalogueCapabilityDto img = r.Capabilities.Single(c => c.CapabilityId == "image.generate");
        MediaCatalogueEntryDto best = img.Entries.First(e => e.IsRecommended);
        Assert.Equal("z-image-turbo-bf16", best.VariantId);
        Assert.Equal("fits", best.FitClass);
        Assert.Contains("declared, not measured", best.Reason);
        Assert.False(string.IsNullOrEmpty(best.License));
        Assert.True(best.SizeBytes > 0);
        Assert.True(best.Downloadable);
        Assert.Contains(best.Evidence, e => e.StartsWith("tier: declared", StringComparison.Ordinal));
    }

    [Fact]
    public void View_never_offers_download_for_resolver_only_or_bundled_entries_and_marks_the_automatic_one()
    {
        MediaCatalogueResponse r = MediaCatalogueView.Build(MediaCatalogue.Shipped, Hw(24, 64), "test", "standard",
            Files("z_image_turbo_int8_convrot.safetensors"), NoSpeed);
        foreach (MediaCatalogueEntryDto e in r.Capabilities.SelectMany(c => c.Entries))
        {
            if (e.Support != "supported") { Assert.False(e.Downloadable); Assert.False(string.IsNullOrEmpty(e.SupportReason)); }
            if (e.VariantId == "z-image-turbo-nvfp4") Assert.False(e.Downloadable);
        }
        MediaCatalogueEntryDto used = r.Capabilities.Single(c => c.CapabilityId == "image.generate").Entries.Single(e => e.UsedAutomatically);
        Assert.Equal("z_image_turbo_int8_convrot.safetensors", used.FileName);
        Assert.True(used.Installed);
    }

    // ---- maintenance proposals ----

    [Fact]
    public void Maintenance_proposes_only_runnable_verified_better_models_and_never_downloads()
    {
        Func<string, Func<string, double?>> speeds = _ => NoSpeed;
        var none = MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(24, 64), Files(), speeds, new HashSet<string>());
        MaintenanceProposal img = Assert.Single(none, p => p.Capability == "image.generate");
        Assert.Equal("media_candidate", img.Kind);
        Assert.Equal("z-image-turbo-bf16", img.Key);
        Assert.Contains("Review in Model Hub", img.Detail);
        Assert.DoesNotContain(none, p => p.Key.StartsWith("qwen", StringComparison.Ordinal) || p.Key.StartsWith("wan2.2-14b", StringComparison.Ordinal));

        var upgrade = MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(24, 64), Files("z_image_turbo_int8_convrot.safetensors"), speeds, new HashSet<string>());
        Assert.Contains("better than installed", Assert.Single(upgrade, p => p.Capability == "image.generate").Reason);

        Assert.DoesNotContain(MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(24, 64), Files("z_image_turbo_bf16.safetensors"), speeds, new HashSet<string>()),
            p => p.Capability == "image.generate");
        Assert.DoesNotContain(MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(24, 64), Files(), speeds, new HashSet<string> { "z-image-turbo-bf16" }),
            p => p.Capability == "image.generate");
    }

    // ---- Automatic mode wiring ----

    [Fact]
    public void Automatic_capability_mode_routes_to_the_best_installed_catalogue_checkpoint()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-medcat-" + Guid.NewGuid().ToString("N"));
        string prevRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(dir, "data"));
        using IDisposable scope = LocalAiPaths.UseRootForTests(Path.Combine(dir, "root"));
        try
        {
            MediaCapabilityRouting.CapabilityModeRoot = Path.Combine(dir, "modes");
            MediaCatalogueLive.HardwareOverride = Hw(24, 64);
            var good = new MediaRoutingFileFacts("z-image", "Z-Image", true, Array.Empty<string>(), 1000);
            string[] files = { "z_image_turbo_int8_convrot.safetensors", "z_image_turbo_bf16.safetensors" };
            MediaCapabilityRouting.FactsOverride = _ => new MediaRoutingFacts(files, _ => good, null);

            MediaRoutingDecision d = MediaCapabilityRouting.Preview("image.generate");
            Assert.Equal("z_image_turbo_bf16.safetensors", d.ChosenFile);
            Assert.True(d.UsesOverride);
            Assert.Equal(ScopedRouteMode.Automatic, d.Mode);
            Assert.Contains(d.Trace, t => t.StartsWith("automatic_pick:", StringComparison.Ordinal));

            // An ineligible file (companions missing) is skipped, never forced.
            MediaCapabilityRouting.FactsOverride = _ => new MediaRoutingFacts(files,
                f => f.Contains("bf16") ? good with { MissingCompanions = new[] { "vae/ae.safetensors" } } : good, null);
            Assert.Equal("z_image_turbo_int8_convrot.safetensors", MediaCapabilityRouting.Preview("image.generate").ChosenFile);
        }
        finally
        {
            PaRuXDataRoot.UseForTests(prevRoot);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
