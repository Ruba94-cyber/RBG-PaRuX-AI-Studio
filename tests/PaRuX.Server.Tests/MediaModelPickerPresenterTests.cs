using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class MediaModelPickerPresenterTests
    {
        [Fact]
        public void Automatic_is_first_and_selected_when_nothing_is_pinned()
        {
            var state = MediaModelPickerPresenter.Build("z_image_turbo_bf16.safetensors", false, new[] { "b.safetensors", "A.safetensors" });
            Assert.Equal(MediaModelPickerPresenter.AutomaticLabel, state.Items[0].Label);
            Assert.Null(state.Items[0].FileName);
            Assert.Equal(0, state.SelectedIndex);
            Assert.Equal(new[] { "A.safetensors", "b.safetensors" }, state.Items.Skip(1).Select(i => i.FileName));
            Assert.Contains("best installed model", state.Hint);
        }

        [Fact]
        public void A_pinned_compatible_file_is_selected_and_the_hint_says_it_is_pinned()
        {
            var state = MediaModelPickerPresenter.Build("b.safetensors", true, new[] { "a.safetensors", "b.safetensors" });
            Assert.Equal("b.safetensors", state.Items[state.SelectedIndex].FileName);
            Assert.Contains("Pinned to b.safetensors", state.Hint);
        }

        [Fact]
        public void A_pinned_file_that_is_not_compatible_stays_visible_and_is_labelled()
        {
            var state = MediaModelPickerPresenter.Build("old.safetensors", true, new[] { "a.safetensors" });
            var item = state.Items[state.SelectedIndex];
            Assert.Equal("old.safetensors", item.FileName);
            Assert.EndsWith("(not compatible)", item.Label);
        }

        [Fact]
        public void Force_route_does_not_show_the_routed_file_as_a_saved_pin()
        {
            var state = MediaModelPickerPresenter.Build("forced-b.safetensors", true,
                new[] { "pinned-a.safetensors", "forced-b.safetensors" }, routingUsesOverride: true, routingMode: "Force", hasShadowedPin: true, pinnedFile: "pinned-a.safetensors");
            Assert.NotEqual(0, state.SelectedIndex);
            Assert.Equal("pinned-a.safetensors", state.Items[state.SelectedIndex].FileName);
            Assert.Contains("saved pin, shadowed", state.Items[state.SelectedIndex].Label);
            Assert.Contains("Selected by Force routing", state.Hint);
            Assert.Contains("shadowed", state.Hint);
            Assert.DoesNotContain("Every request uses it", state.Hint);
            MediaModelPickerItem pin = Assert.Single(state.Items, item => item.FileName == "pinned-a.safetensors");
            Assert.Contains("saved pin, shadowed", pin.Label);
            Assert.Equal("pinned-a.safetensors", MediaModelPickerPresenter.ActionFor(pin).FileName);
            Assert.True(MediaModelPickerPresenter.ActionFor(state.Items[0]).ClearOverride);
        }

        [Fact]
        public void With_no_compatible_model_only_Automatic_is_listed_and_the_hint_says_what_to_do()
        {
            var state = MediaModelPickerPresenter.Build("", false, null);
            Assert.Single(state.Items);
            Assert.Contains("No compatible model", state.Hint);
        }

        [Fact]
        public void Selecting_Automatic_clears_the_override_and_selecting_a_file_pins_it()
        {
            Assert.True(MediaModelPickerPresenter.ActionFor(new MediaModelPickerItem(MediaModelPickerPresenter.AutomaticLabel, null)).ClearOverride);
            Assert.True(MediaModelPickerPresenter.ActionFor(null).ClearOverride);
            var pin = MediaModelPickerPresenter.ActionFor(new MediaModelPickerItem("a.safetensors", "a.safetensors"));
            Assert.False(pin.ClearOverride);
            Assert.Equal("a.safetensors", pin.FileName);
        }

        [Theory]
        [InlineData("uncensored", "Uncensored")]
        [InlineData(" AUTOMATIC ", "Automatic")]
        [InlineData("standard", "Standard")]
        [InlineData("anything else", "Standard")]
        [InlineData(null, "Standard")]
        public void Lane_normalisation_falls_back_to_the_safe_default(string? input, string expected) =>
            Assert.Equal(expected, MediaModelPickerPresenter.NormalizeLane(input));

        [Fact]
        public void Every_lane_has_a_distinct_hint_and_the_uncensored_one_promises_no_silent_fallback()
        {
            var hints = MediaModelPickerPresenter.Lanes.Select(MediaModelPickerPresenter.LaneHint).ToList();
            Assert.Equal(3, hints.Distinct().Count());
            Assert.Contains("never falls back to Standard", MediaModelPickerPresenter.LaneHint("Uncensored"));
        }

        [Theory]
        [InlineData("Automatic (recommended)", 60, "Automatic (recommended)")]
        [InlineData("  model-Q4_K_M.gguf  ", 60, "model-Q4_K_M.gguf")]
        [InlineData(null, 60, "Unknown model")]
        public void Compact_model_display_preserves_short_names(string? input, int limit, string expected)
        {
            Assert.Equal(expected, MediaModelPickerPresenter.CompactDisplayLabel(input, limit));
        }

        [Fact]
        public void Compact_model_display_preserves_family_and_quantization_suffix()
        {
            string full = "qwen-super-long-experimental-image-model-with-many-details-and-settings-Q4_K_M.gguf";
            string display = MediaModelPickerPresenter.CompactDisplayLabel(full, 48);
            Assert.True(display.Length <= 48);
            Assert.StartsWith("qwen-", display);
            Assert.EndsWith("Q4_K_M.gguf", display);
            Assert.Contains("…", display);
            Assert.Equal(full, new MediaModelPickerItem(display, full).FileName);
        }

        [Fact]
        public void Provenance_line_and_details_show_the_model_and_the_routers_reason()
        {
            var presentation = MediaProvenanceFormatter.Format(new MediaProvenanceRecord
            {
                EngineFamily = "ComfyUI", Model = "z_image_turbo_bf16.safetensors", SelectionReason = "strong at photoreal; fits your GPU"
            });
            Assert.Contains("(z_image_turbo_bf16.safetensors)", presentation.Line);
            Assert.Contains("Why: strong at photoreal; fits your GPU", presentation.Line);
            Assert.Contains("Why this model: strong at photoreal; fits your GPU", presentation.Details);
        }

        [Fact]
        public void Provenance_without_a_reason_is_unchanged()
        {
            var presentation = MediaProvenanceFormatter.Format(new MediaProvenanceRecord { EngineFamily = "ComfyUI", Model = "m.safetensors" });
            Assert.DoesNotContain("Why", presentation.Line);
            Assert.DoesNotContain(presentation.Details, d => d.StartsWith("Why"));
        }
    }
}
