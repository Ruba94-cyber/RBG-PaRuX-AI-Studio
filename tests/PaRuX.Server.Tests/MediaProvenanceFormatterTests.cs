using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class MediaProvenanceFormatterTests
    {
        [Fact]
        public void Format_when_record_is_null_returns_missing_provenance_note_and_empty_details()
        {
            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(null);

            Assert.False(result.HasProvenance);
            Assert.Equal("Provenance not recorded for this older result", result.Line);
            Assert.Empty(result.Details);
            Assert.Equal("Provenance not recorded for this older result", MediaProvenanceFormatter.FormatLine(null));
            Assert.Empty(MediaProvenanceFormatter.FormatDetails(null));
        }

        [Fact]
        public void Format_when_engine_and_model_are_empty_returns_missing_provenance_note()
        {
            var record = new MediaProvenanceRecord
            {
                EngineFamily = "   ",
                Model = string.Empty,
                IsLocal = true
            };

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.False(result.HasProvenance);
            Assert.Equal("Provenance not recorded for this older result", result.Line);
            Assert.Empty(result.Details);
        }

        [Fact]
        public void Format_when_record_is_present_returns_expected_single_line_and_details()
        {
            var record = new MediaProvenanceRecord(
                engineFamily: "ComfyUI",
                model: "flux1-schnell.safetensors",
                isLocal: true);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.True(result.HasProvenance);
            Assert.Equal("Made locally with ComfyUI (flux1-schnell.safetensors)", result.Line);

            Assert.Contains(result.Details, d => d.Equals("Engine family: ComfyUI", StringComparison.Ordinal));
            Assert.Contains(result.Details, d => d.Equals("Model: flux1-schnell.safetensors", StringComparison.Ordinal));
            Assert.Contains(result.Details, d => d.Equals("Stayed on this machine: Yes", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Details, d => d.StartsWith("Fallback:", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_when_fallback_reason_is_present_includes_fallback_in_plain_words()
        {
            var record = new MediaProvenanceRecord(
                engineFamily: "ComfyUI",
                model: "sd_xl_turbo.safetensors",
                isLocal: true,
                fallbackReason: "low VRAM fallback to CPU");

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.True(result.HasProvenance);
            Assert.Contains("Made locally with ComfyUI (sd_xl_turbo.safetensors)", result.Line, StringComparison.Ordinal);
            Assert.Contains("Fallback: low VRAM fallback to CPU", result.Line, StringComparison.Ordinal);

            Assert.Contains(result.Details, d => d.Equals("Fallback: low VRAM fallback to CPU", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_when_model_is_windows_path_strips_separators_and_drive_letter()
        {
            string windowsPath = @"D:\fixtures\models\checkpoints\sdxl_base.safetensors";
            var record = new MediaProvenanceRecord("SD.Next", windowsPath, isLocal: true);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.Equal("Made locally with SD.Next (sdxl_base.safetensors)", result.Line);
            Assert.DoesNotContain(@"D:", result.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(@"fixtures", result.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\", result.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(@"/", result.Line, StringComparison.Ordinal);

            Assert.Contains(result.Details, d => d.Equals("Model: sdxl_base.safetensors", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Details, d => d.Contains(@"fixtures", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_when_model_is_posix_path_strips_separators_and_shows_basename_only()
        {
            string posixPath = "/opt/models/stable-diffusion/v1-5-pruned.safetensors";
            var record = new MediaProvenanceRecord("Stable Diffusion", posixPath, isLocal: true);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.Equal("Made locally with Stable Diffusion (v1-5-pruned.safetensors)", result.Line);
            Assert.DoesNotContain("/opt", result.Line, StringComparison.Ordinal);
            Assert.DoesNotContain("stable-diffusion", result.Line, StringComparison.Ordinal);

            Assert.Contains(result.Details, d => d.Equals("Model: v1-5-pruned.safetensors", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Details, d => d.Contains("/opt", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_when_model_is_unc_path_strips_separators_and_shows_basename_only()
        {
            string uncPath = @"\\fileserver\models\flux-dev.safetensors";
            var record = new MediaProvenanceRecord("ComfyUI", uncPath, isLocal: true);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.Equal("Made locally with ComfyUI (flux-dev.safetensors)", result.Line);
            Assert.DoesNotContain("fileserver", result.Line, StringComparison.Ordinal);
            Assert.Contains(result.Details, d => d.Equals("Model: flux-dev.safetensors", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_when_remote_indicates_work_did_not_stay_on_this_machine()
        {
            var record = new MediaProvenanceRecord("CloudEndpoint", "remote-model-v2", isLocal: false);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.Equal("Made remotely with CloudEndpoint (remote-model-v2)", result.Line);
            Assert.Contains(result.Details, d => d.Equals("Stayed on this machine: No", StringComparison.Ordinal));
        }

        [Fact]
        public void Format_preserves_additional_details_in_detail_list()
        {
            var extra = new Dictionary<string, string>
            {
                ["Steps"] = "30",
                ["Seed"] = "1337"
            };

            var record = new MediaProvenanceRecord("ComfyUI", "model.safetensors", isLocal: true, additionalDetails: extra);

            MediaProvenancePresentation result = MediaProvenanceFormatter.Format(record);

            Assert.Contains(result.Details, d => d.Equals("Steps: 30", StringComparison.Ordinal));
            Assert.Contains(result.Details, d => d.Equals("Seed: 1337", StringComparison.Ordinal));
        }

        [Fact]
        public void AutomationIds_match_contract_constants()
        {
            Assert.Equal("Create_Media_Provenance_Text", MediaProvenanceFormatter.ProvenanceTextAutomationId);
            Assert.Equal("Create_Media_Provenance_Details_Button", MediaProvenanceFormatter.ProvenanceDetailsButtonAutomationId);
        }
    }
}
