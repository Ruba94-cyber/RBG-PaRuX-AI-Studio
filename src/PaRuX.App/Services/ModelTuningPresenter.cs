using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>What the "Tuning for your PC" status shows right now. Pure view model.</summary>
    public sealed record ModelTuningViewModel(
        bool Visible, string Title, string Detail, bool Busy, bool CanCancel, string? CancelId, string? ProposalText,
        string Severity);

    /// <summary>
    /// Wording and selection for the non-modal "Tuning for your PC" status (Model Hub header, Chat footer, Create status).
    /// No UI and no HTTP, so it is unit-testable. Results are shown only as measured by the server, never invented.
    /// </summary>
    public static class ModelTuningPresenter
    {
        /// <summary>A finished result stays visible this long, then the status disappears by itself.</summary>
        public static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(15);
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public static ModelTuningStatusDto Parse(string json)
        {
            ModelTuningStatusDto? status = JsonSerializer.Deserialize<ModelTuningStatusDto>(json, Json);
            status ??= new ModelTuningStatusDto();
            status.Items ??= new List<ModelTuningItemDto>();
            return status;
        }

        public static string StateLabel(ModelTuningItemDto item)
        {
            switch (item.State)
            {
                case ModelTuningStates.Running: return "Tuning";
                case ModelTuningStates.Queued: return "Queued";
                case ModelTuningStates.Applied: return "Applied";
                case ModelTuningStates.Failed: return "Failed";
                default: return "Skipped";
            }
        }

        /// <summary>One plain sentence: "Applied: +104% tokens/s" / "No faster profile found" / "Waiting for the PC to be idle".</summary>
        public static string ResultText(ModelTuningItemDto item)
        {
            string unit = item.Kind == "media" ? "faster image generation" : "tokens/s";
            switch (item.State)
            {
                case ModelTuningStates.Applied:
                    double gain = item.GainPercent ?? 0;
                    return "Applied: +" + Math.Round(gain, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "% " + unit;
                case ModelTuningStates.Running:
                    return string.IsNullOrWhiteSpace(item.Reason) ? "Measuring the fastest settings on this PC" : item.Reason!;
                case ModelTuningStates.Queued:
                    return string.IsNullOrWhiteSpace(item.Reason) ? "Waiting for the PC to be idle" : item.Reason!;
                case ModelTuningStates.Failed:
                    return string.IsNullOrWhiteSpace(item.Reason) ? "Tuning failed; the default settings stay in use" : item.Reason! + " The default settings stay in use.";
                default:
                    return string.IsNullOrWhiteSpace(item.Reason) ? "Skipped" : item.Reason!;
            }
        }

        public static string ProposalText(ModelTuningProposalDto? proposal)
        {
            if (proposal == null || string.IsNullOrWhiteSpace(proposal.FileName) || proposal.SizeBytes <= 0) return string.Empty;
            double mb = proposal.SizeBytes / 1048576.0;
            return "A draft head would speed this model up (" + proposal.FileName + ", download "
                + Math.Round(mb, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                + " MB). Nothing is downloaded unless you choose it in Model Hub.";
        }

        /// <summary>Most suggestions the InfoBar spells out; the rest stay available through the tuning endpoint.</summary>
        public const int MaximumShownSuggestions = 3;

        /// <summary>
        /// "Next steps: 1. Try the Q4_K_M version of this model - about +45% tokens/s (estimate). 2. ..." for a finished job whose
        /// current settings are already the best measured. Empty when there is nothing to say. Never claims a gain the server did
        /// not label; a downloadable suggestion names the file and size and says it is chosen in Model Hub.
        /// </summary>
        public static string SuggestionsText(IList<ModelTuningSuggestionDto>? suggestions)
        {
            if (suggestions == null || suggestions.Count == 0) return string.Empty;
            var parts = new List<string>();
            int number = 0;
            foreach (ModelTuningSuggestionDto s in suggestions.Where(x => !string.IsNullOrWhiteSpace(x.Title)).Take(MaximumShownSuggestions))
            {
                number++;
                string text = number.ToString(CultureInfo.InvariantCulture) + ". " + s.Title.Trim().TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(s.ExpectedGain)) text += " - " + s.ExpectedGain.Trim().TrimEnd('.');
                if (s.ActionKind == "download_file" && !string.IsNullOrWhiteSpace(s.FileName) && s.SizeBytes > 0)
                    text += " [" + s.FileName + ", "
                        + Math.Round(s.SizeBytes / 1048576.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                        + " MB; choose it in Model Hub]";
                else if (s.ActionKind == "open_runtime_manager") text += " [Runtime Manager]";
                if (s.Kind == "info" && !string.IsNullOrWhiteSpace(s.Detail)) text += ". " + s.Detail.Trim();
                parts.Add(text.TrimEnd('.') + ".");
            }
            return parts.Count == 0 ? string.Empty : "Next steps: " + string.Join(" ", parts) + " Nothing is downloaded unless you choose it.";
        }

        /// <summary>The InfoBar's extra text: the draft-head hint (when no draft suggestion covers it) followed by the suggestions.</summary>
        public static string ExtraText(ModelTuningItemDto item)
        {
            bool draftCovered = item.Suggestions != null && item.Suggestions.Any(x => x.Kind == "draft_model");
            string proposal = draftCovered ? string.Empty : ProposalText(item.Proposal);
            string suggestions = SuggestionsText(item.Suggestions);
            return string.Join(" ", new[] { proposal, suggestions }.Where(x => !string.IsNullOrEmpty(x)));
        }

        /// <summary>
        /// The one item worth showing: a running job first, then a queued one, then the most recent result that is still fresh.
        /// <paramref name="activeOnly"/> (Chat footer / Create status) hides finished results.
        /// </summary>
        public static ModelTuningViewModel Present(ModelTuningStatusDto? status, DateTime nowUtc, bool activeOnly = false, string? kind = null)
        {
            var hidden = new ModelTuningViewModel(false, string.Empty, string.Empty, false, false, null, null, "none");
            if (status?.Items == null || status.Items.Count == 0) return hidden;
            IEnumerable<ModelTuningItemDto> items = status.Items;
            if (kind != null) items = items.Where(i => i.Kind == kind);

            ModelTuningItemDto? pick = items.Where(i => i.State == ModelTuningStates.Running).OrderByDescending(i => i.UpdatedUtc).FirstOrDefault()
                ?? items.Where(i => i.State == ModelTuningStates.Queued).OrderByDescending(i => i.UpdatedUtc).FirstOrDefault();
            if (pick == null && !activeOnly)
                pick = items.Where(i => ModelTuningStates.IsTerminal(i.State) && nowUtc - i.UpdatedUtc <= ResultLifetime)
                    .OrderByDescending(i => i.UpdatedUtc).FirstOrDefault();
            if (pick == null) return hidden;

            bool active = !ModelTuningStates.IsTerminal(pick.State);
            string name = string.IsNullOrWhiteSpace(pick.ModelLabel) ? "model" : pick.ModelLabel;
            string title = "Tuning for your PC: " + name + " - " + StateLabel(pick);
            string severity = pick.State switch
            {
                ModelTuningStates.Applied => "success",
                ModelTuningStates.Failed => "warning",
                _ => "info"
            };
            return new ModelTuningViewModel(true, title, ResultText(pick), pick.State == ModelTuningStates.Running, active, active ? pick.Id : null,
                string.IsNullOrEmpty(ExtraText(pick)) ? null : ExtraText(pick), severity);
        }

        /// <summary>Single line for the Chat footer / Create status while a job is queued or running; empty otherwise.</summary>
        public static string FooterLine(ModelTuningStatusDto? status, DateTime nowUtc, string? kind = null)
        {
            ModelTuningViewModel vm = Present(status, nowUtc, activeOnly: true, kind: kind);
            return vm.Visible ? vm.Title + ". " + vm.Detail : string.Empty;
        }

        public const string SettingsExplanation =
            "After a model finishes downloading (or is first assigned a role), PaRuX measures the fastest settings for it on this PC "
            + "(KV cache, GPU offload, flash attention, threads, context, speculative decoding; for image models, the ComfyUI launch flags and low-VRAM options) "
            + "and keeps them only if they are "
            + "measurably faster and give the same quality. It starts a few seconds after the download when the PC is idle, and stops at "
            + "once if you start generating, turn on Gaming Mode, unplug to battery, or the GPU gets busy. It never downloads anything by itself. When nothing is faster it says how fast the model already runs "
            + "and suggests concrete next steps (a smaller quantization, a draft model, a lower context, another runtime, a distilled or fp8 image model).";
    }
}
