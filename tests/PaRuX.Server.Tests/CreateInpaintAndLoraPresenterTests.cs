using System.IO.Compression;
using System.Text.Json;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Presenter and model rules behind Create > Image edit > Mask inpaint, the Create LoRA stack, and the chat phrasing that points at them.</summary>
public sealed class CreateInpaintAndLoraPresenterTests
{
    // ---- mask model ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_mask_grid_keeps_the_source_proportions_and_caps_the_long_side()
    {
        InpaintMaskModel wide = InpaintMaskModel.ForSource(4000, 2000);
        Assert.Equal(512, wide.Width);
        Assert.Equal(256, wide.Height);
        InpaintMaskModel tall = InpaintMaskModel.ForSource(300, 900);
        Assert.Equal(512, tall.Height);
        Assert.Equal(171, tall.Width);
        Assert.Throws<ArgumentOutOfRangeException>(() => InpaintMaskModel.ForSource(0, 100));
    }

    [Fact]
    public void Painting_erasing_and_clearing_change_only_the_expected_cells()
    {
        var mask = new InpaintMaskModel(64, 64);
        Assert.False(mask.HasSelection);

        mask.Stamp(32, 32, 8, erase: false);
        Assert.Equal(InpaintMaskModel.Painted, mask[32, 32]);
        Assert.Equal(0, mask[0, 0]);
        Assert.InRange(mask.PaintedFraction, 0.03, 0.07); // a circle of radius 8 in 64x64 is about 4.9%

        mask.Stamp(32, 32, 3, erase: true);
        Assert.Equal(0, mask[32, 32]);
        Assert.Equal(InpaintMaskModel.Painted, mask[32 + 6, 32]);

        mask.Clear();
        Assert.False(mask.HasSelection);
    }

    [Fact]
    public void A_stroke_is_continuous_and_a_stamp_at_the_edge_is_clipped_not_thrown()
    {
        var mask = new InpaintMaskModel(100, 100);
        mask.StampLine(10, 50, 90, 50, 3, erase: false);
        for (int x = 10; x <= 90; x++) Assert.Equal(InpaintMaskModel.Painted, mask[x, 50]);

        mask.Stamp(-5, -5, 4, false);
        mask.Stamp(500, 500, 4, false);
        mask.Stamp(double.NaN, 3, 4, false); // ignored
    }

    [Fact]
    public void Invert_swaps_repaint_and_keep_and_twice_is_identity()
    {
        var mask = new InpaintMaskModel(32, 32);
        mask.Stamp(8, 8, 4, false);
        double before = mask.PaintedFraction;
        byte inside = mask[8, 8], outside = mask[30, 30];

        mask.Invert();
        Assert.Equal(1.0, mask.PaintedFraction + before, 6);
        Assert.Equal(0, mask[8, 8]);
        Assert.Equal(InpaintMaskModel.Painted, mask[30, 30]);

        mask.Invert();
        Assert.Equal(inside, mask[8, 8]);
        Assert.Equal(outside, mask[30, 30]);
    }

    [Fact]
    public void Brush_radius_scales_with_the_slider_and_never_drops_below_one_cell()
    {
        var mask = new InpaintMaskModel(200, 100);
        Assert.Equal(1.0, mask.RadiusFor(0));
        Assert.Equal(50.0, mask.RadiusFor(100));
        Assert.True(mask.RadiusFor(20) > mask.RadiusFor(10));
    }

    [Fact]
    public void The_png_is_a_valid_grayscale_image_with_the_painted_cells_white()
    {
        var mask = new InpaintMaskModel(16, 8);
        mask.Stamp(4, 4, 2, false);
        byte[] png = mask.ToPng();

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(16, ReadInt(png, 16));
        Assert.Equal(8, ReadInt(png, 20));
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(0, png[25]); // grayscale

        // CRC of IHDR matches, so any PNG reader (ComfyUI's PIL included) accepts the chunk.
        uint crc = Crc(png.AsSpan(12, 4 + 13));
        Assert.Equal(crc, (uint)ReadInt(png, 29));

        // Inflate the IDAT and compare with the model.
        int idatLength = ReadInt(png, 33);
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(png, 37, 4));
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress)) z.CopyTo(inflated);
        byte[] raw = inflated.ToArray();
        Assert.Equal((16 + 1) * 8, raw.Length);
        for (int y = 0; y < 8; y++)
        {
            Assert.Equal(0, raw[y * 17]); // filter none
            for (int x = 0; x < 16; x++) Assert.Equal(mask[x, y], raw[y * 17 + 1 + x]);
        }
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    private static int ReadInt(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }

    // ---- inpaint presenter --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, 0.2, "Choose a source image")]
    [InlineData(true, false, 0.0, "mask is empty")]
    [InlineData(true, false, 1.0, "covers the whole image")]
    [InlineData(true, true, 0.2, "Describe what should appear")]
    public void A_run_is_blocked_with_a_specific_reason(bool hasSource, bool promptEmpty, double fraction, string expected) =>
        Assert.Contains(expected, CreateInpaintPresenter.BlockingReason(hasSource, promptEmpty, fraction));

    [Fact]
    public void A_painted_partial_mask_with_a_prompt_may_run_and_the_coverage_line_says_how_much()
    {
        Assert.Null(CreateInpaintPresenter.BlockingReason(true, false, 0.25));
        Assert.Equal("Repainting 25% of the image.", CreateInpaintPresenter.Coverage(true, 0.25));
        Assert.Equal("Repainting 4.5% of the image.", CreateInpaintPresenter.Coverage(true, 0.045));
        Assert.Contains("Paint over", CreateInpaintPresenter.Coverage(true, 0));
    }

    [Fact]
    public void Automation_ids_are_stable_and_unique()
    {
        string[] ids =
        {
            CreateInpaintPresenter.ModeItem, CreateInpaintPresenter.Panel, CreateInpaintPresenter.Canvas, CreateInpaintPresenter.BrushSlider,
            CreateInpaintPresenter.PaintTool, CreateInpaintPresenter.EraseTool, CreateInpaintPresenter.InvertButton, CreateInpaintPresenter.ClearButton,
            CreateInpaintPresenter.GrowBox, CreateInpaintPresenter.CoverageText, CreateInpaintPresenter.ResultStrip, CreateInpaintPresenter.ResultImage,
            CreateLoraStackPresenter.Panel, CreateLoraStackPresenter.Status, CreateLoraStackPresenter.AddButton, CreateLoraStackPresenter.RefreshButton,
            CreateLoraStackPresenter.SlotCombo(0), CreateLoraStackPresenter.SlotCombo(2), CreateLoraStackPresenter.SlotStrength(1), CreateLoraStackPresenter.SlotRemove(2)
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("Create_", id));
        Assert.Equal("Create_LoraStack_Slot3_Combo", CreateLoraStackPresenter.SlotCombo(2));
    }

    // ---- LoRA stack presenter -----------------------------------------------------------------------------------

    private static readonly InstalledLoraChoice Good = new("good.safetensors", "Z-Image", "compatible");
    private static readonly InstalledLoraChoice Bad = new("sdxl_style.safetensors", "SDXL", "incompatible");
    private static readonly InstalledLoraChoice Unknown = new("mystery.safetensors", "unknown", "unknown");
    private static readonly InstalledLoraChoice[] Installed = { Good, Bad, Unknown };

    [Fact]
    public void Empty_slots_are_dropped_and_the_order_and_rounded_strengths_are_kept()
    {
        var slots = new[] { new LoraStackSlot(null, 0.8), new LoraStackSlot("mystery.safetensors", 0.756), new LoraStackSlot("good.safetensors", 1.0) };
        var selections = CreateLoraStackPresenter.BuildSelections(slots)!;
        Assert.Equal(new[] { "mystery.safetensors", "good.safetensors" }, selections.Select(s => s.Name));
        Assert.Equal(0.76, selections[0].Strength);
        Assert.Null(CreateLoraStackPresenter.BuildSelections(new[] { new LoraStackSlot(null, 1) }));
    }

    [Fact]
    public void An_incompatible_duplicate_missing_or_out_of_range_choice_is_explained_before_the_run()
    {
        Assert.Null(CreateLoraStackPresenter.Problem(new[] { new LoraStackSlot("good.safetensors", 0.8), new LoraStackSlot("mystery.safetensors", 1.2) }, Installed));
        Assert.Contains("will not work with the active model", CreateLoraStackPresenter.Problem(new[] { new LoraStackSlot("sdxl_style.safetensors", 0.8) }, Installed));
        Assert.Contains("selected twice", CreateLoraStackPresenter.Problem(new[] { new LoraStackSlot("good.safetensors", 0.8), new LoraStackSlot("GOOD.safetensors", 0.8) }, Installed));
        Assert.Contains("no longer installed", CreateLoraStackPresenter.Problem(new[] { new LoraStackSlot("gone.safetensors", 0.8) }, Installed));
        Assert.Contains("must be between", CreateLoraStackPresenter.Problem(new[] { new LoraStackSlot("good.safetensors", 3.0) }, Installed));
        Assert.Contains("at most 3", CreateLoraStackPresenter.Problem(Enumerable.Range(0, 4).Select(i => new LoraStackSlot("l" + i, 1)).ToList(), Installed));
    }

    [Fact]
    public void The_summary_reports_use_or_the_first_problem()
    {
        Assert.Equal("No LoRA selected: the model runs as it is.", CreateLoraStackPresenter.Summary(new[] { new LoraStackSlot(null, 1) }, Installed));
        Assert.Equal("1 of 3 LoRA slots in use.", CreateLoraStackPresenter.Summary(new[] { new LoraStackSlot("good.safetensors", 1) }, Installed));
        Assert.Contains("will not work", CreateLoraStackPresenter.Summary(new[] { new LoraStackSlot("sdxl_style.safetensors", 1) }, Installed));
    }

    [Fact]
    public void The_installed_list_response_is_parsed_and_a_bad_body_gives_an_empty_list()
    {
        using JsonDocument doc = JsonDocument.Parse(
            "{\"checkpointFamilyName\":\"Z-Image\",\"maxLoras\":3,\"loras\":["
            + "{\"name\":\"a.safetensors\",\"familyName\":\"Z-Image\",\"compatibility\":\"compatible\",\"baseModel\":\"Z-Image Turbo\"},"
            + "{\"name\":\"b.safetensors\",\"familyName\":\"SDXL\",\"compatibility\":\"incompatible\"},{\"nope\":1}]}");
        var (family, choices) = CreateLoraStackPresenter.ParseInstalled(doc.RootElement);
        Assert.Equal("Z-Image", family);
        Assert.Equal(2, choices.Count);
        Assert.Equal("Z-Image Turbo", choices[0].BaseModel);
        Assert.Contains("matches the active model", CreateLoraStackPresenter.ChoiceLabel(choices[0]));
        Assert.Contains("made for SDXL", CreateLoraStackPresenter.ChoiceLabel(choices[1]));

        using JsonDocument odd = JsonDocument.Parse("[1,2]");
        Assert.Empty(CreateLoraStackPresenter.ParseInstalled(odd.RootElement).Choices);
        Assert.Contains("none", CreateLoraStackPresenter.HubHeader(0, "Z-Image"));
        Assert.Contains("(2)", CreateLoraStackPresenter.HubHeader(2, "Z-Image"));
    }

    // ---- chat phrasing ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("replace the sky in the last image")]
    [InlineData("Replace only the sky in the last image with a sunset")]
    [InlineData("cambia solo il cielo")]
    [InlineData("Cambia solo il cielo nell'ultima immagine")]
    [InlineData("sostituisci il cielo in questa foto")]
    [InlineData("change just the hair in this picture")]
    public void Region_edit_phrasing_in_english_and_italian_routes_to_image_edit_and_gets_the_inpaint_hint(string text)
    {
        Assert.Equal(RouteKind.ImageEdit, RouterService.Classify(text));
        Assert.True(RouterService.IsRegionEditRequest(text));
        Assert.Contains("Mask inpaint", RouterService.RegionEditHint(text));
    }

    [Theory]
    [InlineData("replace the sky in the last image")]
    [InlineData("cambia il cielo")]
    [InlineData("remove the wall")]
    public void With_an_attached_image_a_region_request_is_an_edit(string text) =>
        Assert.Equal(RouteKind.ImageEdit, RouterService.ClassifyWithAttachment(text));

    [Theory]
    [InlineData("What colour is the sky?")]
    [InlineData("Explain why the sea is salty")]
    [InlineData("Qual e il colore del cielo?")]
    public void Ordinary_questions_are_not_edits_and_get_no_hint(string text)
    {
        Assert.NotEqual(RouteKind.ImageEdit, RouterService.Classify(text));
        Assert.Null(RouterService.RegionEditHint(text));
    }

    [Fact]
    public void A_region_noun_without_an_image_or_an_only_marker_is_not_routed_to_image_edit() =>
        Assert.NotEqual(RouteKind.ImageEdit, RouterService.Classify("remove the wall from the config file"));

    [Theory]
    [InlineData("edit this image")]
    [InlineData("rimuovi lo sfondo")]
    public void Whole_image_edits_keep_working_as_before(string text) =>
        Assert.Equal(RouteKind.ImageEdit, RouterService.Classify(text));
}
