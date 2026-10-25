using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaRuX.Services
{
    public sealed record ModelSuggestionGroup(string CapabilityGroup, string Title, IReadOnlyList<ModelSuggestionCard> Cards, int HiddenCount);

    public sealed record ModelSuggestionLaneView(
        string SelectedLane,
        string LaneNote,
        int StandardCount,
        int UncensoredCount,
        string FreshnessText,
        bool IsStale,
        string HardwareText,
        string Disclaimer,
        IReadOnlyList<ModelSuggestionGroup> Groups,
        string? EmptyText);

    /// <summary>Pure Standard | Uncensored lane presentation of the maintenance report. A model is in the
    /// uncensored lane only when the server recorded a publisher-declared marker; each lane keeps its own
    /// per-capability order (hardware fit, then declared cohort), never popularity as quality.</summary>
    public static class ModelSuggestionLanePresenter
    {
        public const string Standard = "standard";
        public const string Uncensored = "uncensored";
        public const int SimpleModeLimit = 3;
        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

        public const string UncensoredNote =
            "Uncensored models have no content filter. Their lane is shown only because the publisher's name or tags declare it. You decide whether to use one; nothing is downloaded or routed until you review it.";
        public const string StandardNote = "Standard models: no uncensored marker was declared by the publisher.";

        private static readonly string[] GroupOrder = { "chat", "reasoning", "coding", "vision" };

        public static string LaneAutomationId(string lane) =>
            lane == Uncensored ? "ModelHub_Suggestion_Lane_Uncensored" : "ModelHub_Suggestion_Lane_Standard";

        public static string LaneLabel(string lane) => lane == Uncensored ? "Uncensored" : "Standard";

        public static string? NormalizeLane(string? lane) => lane?.Trim().ToLowerInvariant() switch
        {
            "standard" => Standard,
            "uncensored" => Uncensored,
            _ => null
        };

        /// <summary>Saved selection wins; otherwise the lane the user explicitly stated; otherwise Standard.
        /// Uncensored is never the default.</summary>
        public static string ResolveLane(string? saved, string? statedPreference) =>
            NormalizeLane(saved) ?? (NormalizeLane(statedPreference) == Uncensored ? Uncensored : Standard);

        public static string CapabilityGroupOf(string? capability)
        {
            string c = (capability ?? string.Empty).ToLowerInvariant();
            if (c.Contains("reason")) return "reasoning";
            if (c.Contains("cod")) return "coding";
            if (c.Contains("vision")) return "vision";
            return "chat";
        }

        public static string GroupTitle(string group) => group switch
        {
            "reasoning" => "Reasoning",
            "coding" => "Coding",
            "vision" => "Vision",
            _ => "Chat"
        };

        public static string GroupAutomationId(string lane, string group) => "ModelHub_Suggestion_Group_" + LaneLabel(lane) + "_" + GroupTitle(group);

        public static string FreshnessLabel(DateTime? finishedUtc, DateTime nowUtc, string? discoveryOutcome, out bool stale)
        {
            if (finishedUtc is null) { stale = true; return "Never refreshed: suggestions are not available yet."; }
            TimeSpan age = nowUtc - finishedUtc.Value;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            string ageText = Age(age);
            bool failed = !string.IsNullOrWhiteSpace(discoveryOutcome)
                && (discoveryOutcome!.StartsWith("failed", StringComparison.OrdinalIgnoreCase) || discoveryOutcome.Contains(':', StringComparison.Ordinal));
            stale = failed || age >= StaleAfter;
            string text = (age >= StaleAfter ? "Stale: " : "") + "catalogue refreshed " + ageText + " ago.";
            if (failed) text += " The latest refresh failed (for example offline), so the last good catalogue is shown.";
            return char.ToUpperInvariant(text[0]) + text[1..];
        }

        private static string Age(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return "less than a minute";
            if (age.TotalHours < 1) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            if (age.TotalDays < 1) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
            int days = (int)age.TotalDays;
            return days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days");
        }

        public static ModelSuggestionLaneView Present(
            SuggestionFeedStatusDto? status, ISet<string>? deferredKeys, ISet<string>? hiddenKeys,
            string? selectedLane, bool advanced, DateTime nowUtc)
        {
            string lane = NormalizeLane(selectedLane) ?? Standard;
            ModelSuggestionSection all = ModelSuggestionPresenter.Present(status, deferredKeys, hiddenKeys, int.MaxValue);
            string freshness = FreshnessLabel(status?.LastReport?.FinishedUtc, nowUtc, status?.LastReport?.DiscoveryOutcome, out bool stale);
            if (status?.LastReport is null) freshness = status == null ? "Suggestion data unavailable." : freshness;
            int std = all.Cards.Count(c => c.Lane == Standard);
            int unc = all.Cards.Count(c => c.Lane == Uncensored);
            string note = lane == Uncensored ? UncensoredNote : StandardNote;

            var groups = new List<ModelSuggestionGroup>();
            foreach (string g in GroupOrder)
            {
                List<ModelSuggestionCard> inGroup = all.Cards.Where(c => c.Lane == lane && c.CapabilityGroup == g).ToList();
                if (inGroup.Count == 0) continue;
                int shown = advanced ? inGroup.Count : Math.Min(SimpleModeLimit, inGroup.Count);
                groups.Add(new ModelSuggestionGroup(g, GroupTitle(g), inGroup.Take(shown).ToList(), inGroup.Count - shown));
            }
            string? empty = all.EmptyText;
            if (empty == null && groups.Count == 0)
                empty = lane == Uncensored
                    ? "No uncensored-lane suggestions for this PC right now. A model appears here only when its publisher declares it uncensored."
                    : "No standard-lane suggestions for this PC right now.";
            return new ModelSuggestionLaneView(lane, note, std, unc, freshness, stale, all.HardwareText, all.Disclaimer, groups, empty);
        }
    }
}
