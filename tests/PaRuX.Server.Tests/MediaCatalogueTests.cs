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
        Assert.Equal(new[] { "chroma", "flux1-schnell", "flux2-klein", "qwen-image", "qwen-image-edit", "realvisxl-v5", "sdxl", "sdxl-anime", "wan2.2-ti2v-5b", "z-image" },
            c.Families.Where(f => f.IsSupported).Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        foreach (string id in new[] { "qwen-image-gguf", "wan2.2-14b", "ltx-video", "ace-step" })
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
    public void Qwen_image_is_pinned_to_one_commit_with_sizes_and_digests_read_from_the_repository_listing()
    {
        MediaCatalogueFamily family = MediaCatalogue.Shipped.Families.Single(f => f.Id == "qwen-image");
        RegistryFamily registry = MediaCompanionRegistry.Shipped.FindFamily(family.RegistryFamily!)!;
        MediaCatalogueVariant v = Assert.Single(family.Variants);

        Assert.True(family.IsSupported);
        Assert.True(v.ListingVerified);
        Assert.Equal("exact_listing", v.SizeBasis);
        Assert.Equal(20_430_635_136, v.SizeBytes);
        Assert.Equal("98763a127701eb6fb59096f7742cb3aa7d64ed510b9f4e882d8351f8176e3ce3", v.Sha256);
        Assert.NotNull(v.Revision);
        Assert.NotNull(v.DownloadUrl);
        // The checkpoint and both companions come from the same commit of the same repository, so they are a matching set.
        Assert.All(registry.Companions, c => { Assert.True(c.Source.IsPinned); Assert.Equal(v.Revision, c.Source.Revision); Assert.Equal(v.Repository, c.Source.Repository); });
        AssertEncoderAndVaeSizesMatch(family, registry);

        // Qwen-Image-Edit is the same architecture in its own repository: pinned to its own commit, sharing the companions.
        MediaCatalogueFamily edit = MediaCatalogue.Shipped.Families.Single(f => f.Id == "qwen-image-edit");
        MediaCatalogueVariant e = Assert.Single(edit.Variants);
        Assert.True(edit.IsSupported);
        Assert.Equal("qwen-image", edit.RegistryFamily);
        Assert.Equal("image.edit", edit.Capability);
        Assert.True(e.ListingVerified);
        Assert.Equal("Comfy-Org/Qwen-Image-Edit_ComfyUI", e.Repository);
        Assert.Equal(20_430_635_136, e.SizeBytes);
        Assert.Equal("393c6743d1de2e9031b5197027b36116f2096958ccc0223526d34e1860266021", e.Sha256);
        Assert.Equal("f68ace85e60b4a02a323e394253731947657b7d2", e.Revision);
        AssertEncoderAndVaeSizesMatch(edit, registry);
    }

    private static void AssertEncoderAndVaeSizesMatch(MediaCatalogueFamily family, RegistryFamily registry)
    {
        // The fit estimator's text-encoder and VAE sizes are the pinned companion files' sizes in GiB, rounded up (conservative).
        double GiB(string role) => registry.Companions.Single(c => c.Role == role).Source.SizeBytes!.Value / (1024.0 * 1024 * 1024);
        Assert.InRange(family.TextEncoderGb - GiB("text_encoder"), 0.0, 0.1);
        Assert.InRange(family.VaeGb - GiB("vae"), 0.0, 0.05);
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
            // The catalogue pins the commit; the recommended list still names the moving "main" branch for the same file.
            Assert.Equal(r.DownloadUrl, v.DownloadUrl!.Replace("/resolve/" + v.Revision + "/", "/resolve/main/"));
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

    [Fact]
    public void Parse_rejects_a_supported_family_whose_registry_family_does_not_run_its_capability()
    {
        // z-image runs image and img2img only: offering it under image.edit would list a download the edit workflow then refuses.
        string doc = """
            { "schemaVersion": 1, "catalogueVersion": 1,
              "capabilities": [ { "id": "image.edit", "label": "E", "defaultJob": { "megapixels": 1, "frames": 1, "seconds": 0 } } ],
              "families": [ { "id": "f", "displayName": "F", "capability": "image.edit", "support": "supported", "registryFamily": "z-image",
                              "parametersBillion": 1, "textEncoderGb": 0, "vaeGb": 0, "variants": [ V ] } ] }
            """.Replace("V ]", GoodVariant + " ]");

        var error = Assert.Throws<InvalidDataException>(() => MediaCatalogue.Parse(doc, MediaCompanionRegistry.Shipped));
        Assert.Contains("is not a pipeline", error.Message);
        // The same family under its own capability is accepted.
        MediaCatalogue.Parse(Doc("{ \"id\": \"f\", \"displayName\": \"F\", \"capability\": \"image.generate\", \"support\": \"supported\", \"registryFamily\": \"z-image\", \"parametersBillion\": 1, \"textEncoderGb\": 0, \"vaeGb\": 0, \"variants\": [ "
            + GoodVariant + " ] }"), MediaCompanionRegistry.Shipped);
    }

    [Fact]
    public void Every_shipped_supported_family_maps_to_a_registry_family_that_runs_its_capability()
    {
        foreach (MediaCatalogueFamily family in MediaCatalogue.Shipped.Families.Where(f => f.IsSupported))
        {
            RegistryFamily registry = MediaCompanionRegistry.Shipped.FindFamily(family.RegistryFamily!)!;
            Assert.Contains(MediaCatalogue.PipelineOf(family.Capability)!, registry.Pipelines);
        }
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
        // A 24 GB card holds Qwen-Image fp8 (declared tier 5) entirely, so it outranks the tier-4 Z-Image bf16 there.
        MediaSelection s = Sel("image.generate", Hw(24, 64), Files());
        Assert.Equal("qwen-image-fp8", s.Recommended!.Variant.Id);
        Assert.Equal(MediaFitClass.Fits, s.Recommended.Fit.Class);
        Assert.True(s.Recommended.Family.IsSupported);
        Assert.Null(s.Automatic);
    }

    [Fact]
    public void On_a_16_gb_card_qwen_image_needs_offloading_so_the_z_image_that_fits_is_recommended()
    {
        MediaSelection s = Sel("image.generate", Hw(16, 64), Files());
        Assert.Equal("z-image-turbo-bf16", s.Recommended!.Variant.Id);
        Assert.Equal(MediaFitClass.Fits, s.Recommended.Fit.Class);
        MediaEvaluatedVariant qwen = s.Ranked.Single(v => v.Variant.Id == "qwen-image-fp8");
        Assert.Equal(MediaFitClass.Offload, qwen.Fit.Class);
        Assert.True(qwen.Selectable);
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
        Assert.Equal("z_image_turbo_bf16.safetensors", Sel("image.generate", Hw(16, 64), both).Automatic!.Variant.FileName);
        MediaSelection noBf16 = Sel("image.generate", Hw(16, 64), both, eligible: f => !f.Contains("bf16"));
        Assert.Equal("z_image_turbo_int8_convrot.safetensors", noBf16.Automatic!.Variant.FileName);
        Assert.Equal("z_image_turbo_bf16.safetensors", noBf16.Recommended!.Variant.FileName);
    }

    [Fact]
    public void Automatic_never_uses_unknown_files_too_large_models_or_resolver_only_families()
    {
        Assert.Null(Sel("image.generate", Hw(24, 64), Files("my_own_model.safetensors")).Automatic);
        Assert.Null(Sel("image.generate", Hw(3, 8), Files("z_image_turbo_nvfp4.safetensors")).Automatic);
        // The GGUF Qwen-Image build stays resolver_only (PaRuX has no validated GGUF loader graph), so it is never picked.
        Assert.Null(Sel("image.generate", Hw(24, 64), Files("Qwen_Image-Q4_K_M.gguf")).Automatic);
        // The supported fp8 build is picked once installed and runnable.
        Assert.Equal("qwen_image_fp8_e4m3fn.safetensors",
            Sel("image.generate", Hw(24, 64), Files("qwen_image_fp8_e4m3fn.safetensors")).Automatic!.Variant.FileName);
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
        // Uncensored entries exist (each backed by laneEvidence), so one is recommended for download; none is installed,
        // so Automatic still uses the installed standard model.
        Assert.Equal("uncensored", s.Recommended!.Variant.Lane);
        Assert.False(string.IsNullOrWhiteSpace(s.Recommended.Variant.LaneEvidence));
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
        MediaCatalogueResponse r = MediaCatalogueView.Build(MediaCatalogue.Shipped, Hw(16, 64), "test", "standard", Files(), NoSpeed);
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
    public void View_never_offers_download_for_resolver_only_entries_offers_every_supported_default_and_marks_the_automatic_one()
    {
        MediaCatalogueResponse r = MediaCatalogueView.Build(MediaCatalogue.Shipped, Hw(24, 64), "test", "standard",
            Files("z_image_turbo_int8_convrot.safetensors"), NoSpeed);
        foreach (MediaCatalogueEntryDto e in r.Capabilities.SelectMany(c => c.Entries))
        {
            if (e.Support != "supported") { Assert.False(e.Downloadable); Assert.False(string.IsNullOrEmpty(e.SupportReason)); }
            // The PaRuX defaults used to be "bundled" with no source, so a clean PC could never fetch them.
            if (e.VariantId is "z-image-turbo-nvfp4" or "flux2-klein-4b" or "wan2.2-ti2v-5b-fp16") Assert.True(e.Downloadable, e.VariantId);
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
        var none = MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(16, 64), Files(), speeds, new HashSet<string>());
        MaintenanceProposal img = Assert.Single(none, p => p.Capability == "image.generate");
        Assert.Equal("media_candidate", img.Kind);
        Assert.Equal("z-image-turbo-bf16", img.Key);
        Assert.Contains("Review in Model Hub", img.Detail);
        Assert.DoesNotContain(none, p => p.Key.StartsWith("qwen", StringComparison.Ordinal) || p.Key.StartsWith("wan2.2-14b", StringComparison.Ordinal));

        var upgrade = MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(16, 64), Files("z_image_turbo_int8_convrot.safetensors"), speeds, new HashSet<string>());
        Assert.Contains("better than installed", Assert.Single(upgrade, p => p.Capability == "image.generate").Reason);

        Assert.DoesNotContain(MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(16, 64), Files("z_image_turbo_bf16.safetensors"), speeds, new HashSet<string>()),
            p => p.Capability == "image.generate");
        Assert.DoesNotContain(MediaMaintenanceProposals.Propose(MediaCatalogue.Shipped, Hw(16, 64), Files(), speeds, new HashSet<string> { "z-image-turbo-bf16" }),
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
