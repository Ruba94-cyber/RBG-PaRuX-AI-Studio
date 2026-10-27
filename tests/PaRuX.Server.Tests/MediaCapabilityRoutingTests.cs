using System.Buffers.Binary;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Media capability overrides (image.generate / image.edit / video.generate) change the checkpoint decision.</summary>
public sealed class MediaCapabilityRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-medcap-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _rootScope;

    public MediaCapabilityRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
        Directory.CreateDirectory(LocalAiPaths.ConfigDir);
        File.WriteAllText(LocalAiPaths.ImageWorkflowTemplate,
            "{\"prompt\":{\"1\":{\"class_type\":\"UNETLoader\",\"inputs\":{\"unet_name\":\"default.safetensors\"}}}}");
        MediaCapabilityRouting.CapabilityModeRoot = Path.Combine(_dir, "modes");
    }

    public void Dispose()
    {
        MediaCapabilityRouting.FactsOverride = null;
        MediaCapabilityRouting.CapabilityModeRoot = null;
        _rootScope.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MediaRoutingFileFacts Good() => new("z-image", "Z-Image", true, Array.Empty<string>(), 1000);

    private void Facts(Dictionary<string, MediaRoutingFileFacts> files, long? budget = null) =>
        MediaCapabilityRouting.FactsOverride = _ => new MediaRoutingFacts(files.Keys.ToList(), f => files[f], budget);

    private void SetMode(string capability, string mode, string? model) =>
        CapabilityRoutingPreferenceStore.SetCapabilityMode(capability, mode, model, MediaCapabilityRouting.CapabilityModeRoot);

    private static void WriteZImageHeader(string path)
    {
        string[] names = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };
        string json = "{" + string.Join(",", names.Select((name, i) => "\"" + name + "\":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}")) + "}";
        byte[] header = Encoding.UTF8.GetBytes(json);
        byte[] bytes = new byte[8 + header.Length + names.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Automatic_keeps_the_template_default_and_has_no_routing_refusal()
    {
        MediaCheckpointEffective e = MediaCheckpointResolution.ResolveForJob("image");
        Assert.Equal("default.safetensors", e.File);
        Assert.False(e.IsOverridden);
        Assert.False(e.IsCheckpointPinned);
        Assert.False(e.Routing!.IsRefusal);
    }

    [Fact]
    public void Automatic_router_pick_is_effective_but_not_an_explicit_checkpoint_pin()
    {
        Facts(new() { ["z_image_turbo_nvfp4.safetensors"] = Good() });

        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve("image");

        Assert.Equal("z_image_turbo_nvfp4.safetensors", effective.File);
        Assert.True(effective.IsOverridden); // The graph must load the routed file.
        Assert.True(effective.Routing!.UsesOverride);
        Assert.False(effective.IsCheckpointPinned); // The user did not pin it in the Model picker.
    }

    [Fact]
    public void Force_uses_the_forced_checkpoint_for_the_job_and_for_status()
    {
        Facts(new() { ["forced.safetensors"] = Good() });
        SetMode("image.generate", "force", "forced.safetensors");

        MediaCheckpointEffective e = MediaCheckpointResolution.ResolveForJob("image");

        Assert.Equal("forced.safetensors", e.File);
        Assert.True(e.IsOverridden);
        Assert.Equal("forced.safetensors", MediaCheckpointResolution.Resolve("image").File);
        Assert.Equal("forced.safetensors", MediaCheckpointResolution.FileNameForProvenance("image"));
    }

    [Fact]
    public void Force_route_preserves_the_different_saved_pin_for_the_picker_to_clear_or_reselect()
    {
        WriteZImageHeader(Path.Combine(LocalAiPaths.DiffusionModelsPath, "pinned-a.safetensors"));
        LocalAiPaths.SetMediaCheckpointOverride("image", "pinned-a.safetensors");
        Facts(new() { ["forced-b.safetensors"] = Good() });
        SetMode("image.generate", "force", "forced-b.safetensors");

        MediaCheckpointEffective e = MediaCheckpointResolution.Resolve("image");

        Assert.Equal("forced-b.safetensors", e.File);
        Assert.True(e.IsCheckpointPinned);
        Assert.Equal("pinned-a.safetensors", e.PinnedFile);
        Assert.True(e.Routing!.UsesOverride);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_family")]
    [InlineData("companion_files_missing")]
    [InlineData("hardware_unfit")]
    [InlineData("runtime_too_old")]
    public void Force_refuses_with_a_typed_code_and_never_substitutes(string code)
    {
        MediaRoutingFileFacts facts = code switch
        {
            "wrong_family" => Good() with { FamilyAllowed = false },
            "companion_files_missing" => Good() with { MissingCompanions = new[] { "vae/ae.safetensors" } },
            "hardware_unfit" => Good() with { SizeBytes = 10_000 },
            "runtime_too_old" => Good() with { RuntimeTooOld = true },
            _ => Good()
        };
        var files = new Dictionary<string, MediaRoutingFileFacts> { ["other.safetensors"] = Good() };
        if (code != "missing") files["forced.safetensors"] = facts;
        Facts(files, budget: 5_000);
        SetMode("image.generate", "force", "forced.safetensors");

        var ex = Assert.Throws<MediaRoutingRefusedException>(() => MediaCheckpointResolution.ResolveForJob("image"));

        Assert.Equal(code, ex.RefusalCode);
        Assert.Contains("No other checkpoint was substituted", ex.Message);
        Assert.Contains("force_refused:" + code + ":no_substitution", ex.Trace);
        // Status stays readable and shows the refusal without throwing.
        MediaCheckpointEffective status = MediaCheckpointResolution.Resolve("image");
        Assert.Equal(code, status.Routing!.RefusalCode);
        Assert.Equal("default.safetensors", status.File);
    }

    [Fact]
    public void Prefer_falls_back_to_automatic_with_a_recorded_trace_when_unavailable()
    {
        Facts(new() { ["x.safetensors"] = Good() with { FamilyAllowed = false } });
        SetMode("image.generate", "prefer", "x.safetensors");

        MediaCheckpointEffective e = MediaCheckpointResolution.ResolveForJob("image");

        Assert.Equal("default.safetensors", e.File);
        Assert.False(e.IsOverridden);
        Assert.Contains("prefer_unavailable:wrong_family", e.Routing!.Trace);
        Assert.Contains("fallback:automatic", e.Routing.Trace);
        Assert.Contains("unavailable", MediaCapabilityRouting.LastFor("image.generate")!.Value.Decision.Explanation);
    }

    [Fact]
    public void Prefer_uses_the_preferred_checkpoint_when_eligible()
    {
        Facts(new() { ["p.safetensors"] = Good() });
        SetMode("video.generate", "prefer", "p.safetensors");
        Assert.Equal("p.safetensors", MediaCapabilityRouting.Preview("video.generate").ChosenFile);
    }

    [Fact]
    public void Capabilities_are_independent_and_edit_maps_to_image_edit()
    {
        Facts(new() { ["e.safetensors"] = Good() });
        SetMode("image.edit", "force", "e.safetensors");
        Assert.Equal("image.edit", MediaCapabilityRouting.CapabilityFor("edit"));
        Assert.True(MediaCapabilityRouting.Preview("image.edit").UsesOverride);
        Assert.False(MediaCapabilityRouting.Preview("image.generate").UsesOverride);
    }

    [Fact]
    public void Corrupt_settings_fail_closed_for_a_job()
    {
        Directory.CreateDirectory(MediaCapabilityRouting.CapabilityModeRoot!);
        File.WriteAllText(Path.Combine(MediaCapabilityRouting.CapabilityModeRoot!, "capability-mode-overrides.json"), "{not json");
        var ex = Assert.Throws<MediaRoutingRefusedException>(() => MediaCheckpointResolution.ResolveForJob("image"));
        Assert.Equal("routing_settings_corrupt", ex.RefusalCode);
    }

    [Fact]
    public void Status_reports_state_trace_and_candidates()
    {
        Facts(new() { ["a.safetensors"] = Good() });
        SetMode("image.generate", "force", "gone.safetensors");

        CapabilityRoutingStatusDto dto = MediaCapabilityRouting.BuildStatus("image.generate",
            CapabilityRoutingPreferenceStore.GetCapabilityMode("image.generate", MediaCapabilityRouting.CapabilityModeRoot));

        Assert.Equal("refused", dto.State);
        Assert.Equal("missing", dto.RefusalCode);
        Assert.NotEmpty(dto.Trace);
        Assert.Equal("live", MediaCapabilityRouting.BuildStatus("coding", new CapabilityModeOverride { Capability = "coding" }).State);
    }
}
