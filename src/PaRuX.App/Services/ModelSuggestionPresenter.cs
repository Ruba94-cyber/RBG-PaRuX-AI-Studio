using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaRuX.Services
{
    public sealed record ModelSuggestionCard(
        string Key,
        string Repo,
        string Title,
        string SizeText,
        string FitText,
        IReadOnlyList<string> CohortChips,
        string CapabilityText,
        string LicenceText,
        string ReasonText,
        string ProvenanceText,
        bool CanReviewAndDownload,
        string? RuntimeNoticeText,
        bool CanDefer,
        bool CanIgnore,
        string Lane = "standard",
        string? LaneEvidenceText = null,
        string CapabilityGroup = "chat");

    public sealed record ModelSuggestionSection(
        string Title,
        string Disclaimer,
        string HardwareText,
        IReadOnlyList<ModelSuggestionCard> Cards,
        string? EmptyText);

    /// <summary>Pure presentation of the maintenance report's candidate proposals. Everything shown is
    /// publisher-declared or derived from this PC's hardware; nothing is validated or recommended.</summary>
    public static class ModelSuggestionPresenter
    {
        public const string SectionAutomationId = "ModelHub_Suggestion_Section";
        public const string EmptyAutomationId = "ModelHub_Suggestion_Empty_Text";
        public const string HardwareAutomationId = "ModelHub_Suggestion_Hardware_Text";
        public const string KindNewCandidate = "new_candidate";
        public const string KindNeedsRuntime = "candidate_needs_runtime_update";
        public const int MaximumCards = 8;

        public static string CardAutomationId(string key, string part) => "ModelHub_Suggestion_" + part + "_" + key;

        public static string FormatFit(string? fitClass) => fitClass switch
        {
            "FitsOnGpu" => "Fits GPU",
            "SplitWithRam" => "GPU/RAM split",
            "CpuOnly" => "CPU-only",
            _ => "Fit unknown"
        };

        private static int FitRank(string? fitClass) => fitClass switch
        {
            "FitsOnGpu" => 0, "SplitWithRam" => 1, "CpuOnly" => 2, _ => 3
        };

        public static string? CohortLabel(string? cohort) => cohort?.Trim().ToLowerInvariant() switch
        {
            "popular" => "Popular",
            "new" => "New",
            "recently_updated" or "updated" => "Recently updated",
            _ => null
        };

        public static IReadOnlyList<string> CohortChips(IEnumerable<string>? cohorts) =>
            (cohorts ?? Array.Empty<string>()).Select(CohortLabel).Where(l => l != null)
                .Select(l => l!).Distinct(StringComparer.Ordinal).ToList();

        public static string FormatSize(long? bytes)
        {
            if (bytes is not > 0) return "size not declared";
            double gb = bytes.Value / 1073741824.0;
            return gb >= 1
                ? "~" + gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : "~" + (bytes.Value / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string FormatCapability(string? capability)
        {
            if (string.IsNullOrWhiteSpace(capability)) return "Capability unknown";
            string text = capability.Trim().Replace('_', ' ').Replace('-', ' ');
            return char.ToUpperInvariant(text[0]) + text[1..];
        }

        public static string FormatHardware(SuggestionFeedFingerprintDto? fingerprint)
        {
            if (fingerprint == null) return "Hardware profile not available yet.";
            var parts = new List<string>();
            foreach (SuggestionFeedAdapterDto gpu in fingerprint.Adapters.Take(2))
            {
                string name = string.IsNullOrWhiteSpace(gpu.Name) ? "GPU" : gpu.Name.Trim();
                parts.Add(gpu.DedicatedVramBytes > 0 ? name + " (" + Gb(gpu.DedicatedVramBytes) + " VRAM)" : name);
            }
            if (fingerprint.Adapters.Count == 0) parts.Add("no dedicated GPU detected");
            if (fingerprint.SystemRamBytes > 0) parts.Add(Gb(fingerprint.SystemRamBytes) + " RAM");
            return "Based on this PC: " + string.Join(" · ", parts) + ".";
        }

        private static string Gb(long bytes) =>
            (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

        public static ModelSuggestionSection Present(
            SuggestionFeedStatusDto? status, ISet<string>? deferredKeys, ISet<string>? hiddenKeys = null,
            int maximumCards = MaximumCards)
        {
            const string title = "SUGGESTED FOR THIS PC · CANDIDATES, NOT VALIDATED";
            const string disclaimer = "Chosen from public model feeds by how well they fit this PC. Publisher metadata is unverified; nothing is downloaded, validated or routed until you review it.";
            string hardware = FormatHardware(status?.Fingerprint);
            SuggestionFeedReportDto? report = status?.LastReport;
            if (report == null)
                return new ModelSuggestionSection(title, disclaimer, hardware, Array.Empty<ModelSuggestionCard>(),
                    status == null
                        ? "Suggestions are unavailable: PaRuX Core did not return a maintenance report. Nothing was assumed."
                        : "No suggestions yet. PaRuX has not completed a hardware check; none is shown until it does.");

            string provenance = "Maintenance check " + report.FinishedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + " UTC (" + (string.IsNullOrWhiteSpace(report.Trigger) ? "unknown trigger" : report.Trigger) + ")";
            List<ModelSuggestionCard> cards = (report.Proposals ?? new List<SuggestionFeedProposalDto>())
                .Where(p => p.Kind is KindNewCandidate or KindNeedsRuntime
                    && !string.IsNullOrWhiteSpace(p.Repo) && !string.IsNullOrWhiteSpace(p.Key))
                .Where(p => deferredKeys?.Contains(p.Key) != true && hiddenKeys?.Contains(p.Key) != true)
                .OrderBy(p => FitRank(p.FitClass))
                .ThenBy(p => (p.Cohorts ?? new List<string>()).Any(c => c is "new" or "recently_updated") ? 0 : 1)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(maximumCards)
                .Select(p => BuildCard(p, provenance))
                .ToList();
            string? empty = cards.Count > 0 ? null : EmptyText(report, (deferredKeys?.Count ?? 0) > 0);
            return new ModelSuggestionSection(title, disclaimer, hardware, cards, empty);
        }

        private static string EmptyText(SuggestionFeedReportDto report, bool someDeferred) => report.Outcome switch
        {
            "skipped_gaming" => "No suggestions yet: the last check was skipped because Gaming mode was active.",
            "skipped_disabled" => "No suggestions yet: automatic maintenance is turned off.",
            "busy" => "No suggestions yet: the last check was busy and will be retried.",
            "error" => "No suggestions yet: the last check failed. Nothing was assumed.",
            _ => someDeferred
                ? "No more suggestions right now. Deferred ones return after PaRuX restarts."
                : "No suggestions yet. The last check found no public model that beats what is installed on this PC."
        };

        private static ModelSuggestionCard BuildCard(SuggestionFeedProposalDto p, string provenance)
        {
            bool needsRuntime = p.Kind == KindNeedsRuntime;
            string repo = p.Repo!.Trim();
            return new ModelSuggestionCard(
                p.Key.Trim(),
                repo,
                repo,
                FormatSize(p.SizeBytes) + " · quantization chosen at review",
                "On this PC: " + FormatFit(p.FitClass),
                CohortChips(p.Cohorts),
                FormatCapability(p.Capability),
                string.IsNullOrWhiteSpace(p.License) ? "Licence not declared" : "Publisher-declared licence: " + p.License.Trim() + " (unverified)",
                string.IsNullOrWhiteSpace(p.Reason) ? p.Detail ?? string.Empty : p.Reason!,
                provenance,
                CanReviewAndDownload: !needsRuntime,
                RuntimeNoticeText: needsRuntime
                    ? "Needs a newer runtime before it can run. Update the runtime in Runtime Manager first; no download is offered."
                    : null,
                CanDefer: true,
                CanIgnore: true,
                Lane: ModelSuggestionLanePresenter.NormalizeLane(p.Lane) ?? ModelSuggestionLanePresenter.Standard,
                LaneEvidenceText: ModelSuggestionLanePresenter.NormalizeLane(p.Lane) == ModelSuggestionLanePresenter.Uncensored
                    ? "Uncensored lane because the publisher declares it (" + (string.IsNullOrWhiteSpace(p.LaneEvidence) ? "marker not recorded" : p.LaneEvidence!.Trim()) + ")."
                    : null,
                CapabilityGroup: ModelSuggestionLanePresenter.CapabilityGroupOf(p.Capability));
        }
    }

    public enum ModelCohortFilter { All, Popular, New, Updated }

    /// <summary>Pure cohort filter for the Candidates list (discovery provenance only, never quality).</summary>
    public static class ModelCohortFilterPresenter
    {
        public static string AutomationId(ModelCohortFilter filter) => "ModelHub_CohortFilter_" + filter;

        public static string Label(ModelCohortFilter filter) => filter switch
        {
            ModelCohortFilter.Popular => "Popular",
            ModelCohortFilter.New => "New",
            ModelCohortFilter.Updated => "Updated",
            _ => "All"
        };

        public static bool Matches(ModelCohortFilter filter, IEnumerable<string>? cohorts)
        {
            if (filter == ModelCohortFilter.All) return true;
            string wanted = filter switch
            {
                ModelCohortFilter.Popular => "popular",
                ModelCohortFilter.New => "new",
                _ => "recently_updated"
            };
            return (cohorts ?? Array.Empty<string>()).Any(c =>
                string.Equals(c?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                || (filter == ModelCohortFilter.Updated && string.Equals(c?.Trim(), "updated", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>"Discovery feed: Popular, New" or an honest note when no feed label was recorded.</summary>
        public static string CohortLine(IEnumerable<string>? cohorts)
        {
            IReadOnlyList<string> chips = ModelSuggestionPresenter.CohortChips(cohorts);
            return chips.Count == 0 ? "Discovery feed: not recorded" : "Discovery feed: " + string.Join(", ", chips);
        }
    }

    /// <summary>Category filter for Search results; handles every category the server emits, including
    /// audio, abliterated and uncensored-roleplay, tolerant of case, spaces and underscores.</summary>
    public static class ModelCategoryFilter
    {
        public const string AllCategories = "All categories";

        public static string Normalize(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

        public static bool Matches(string? selected, string? category) =>
            string.IsNullOrWhiteSpace(selected)
            || string.Equals(selected.Trim(), AllCategories, StringComparison.OrdinalIgnoreCase)
            || Normalize(selected) == Normalize(category);
    }
}
