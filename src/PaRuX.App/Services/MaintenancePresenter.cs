using System;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Pure parsing and wording for the "Automatic checks" Settings section and the runtime suggestion card.
    /// No UI, no HTTP: everything here is unit-testable.</summary>
    public static class MaintenancePresenter
    {
        public const int MinimumIntervalHours = 1;
        public const int MaximumIntervalHours = 720;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public static int ClampHours(double hours) =>
            double.IsNaN(hours) ? 24 : (int)Math.Clamp(Math.Round(hours), MinimumIntervalHours, MaximumIntervalHours);

        public static MaintenanceSettingsDto ParseSettings(string json)
        {
            MaintenanceSettingsDto settings = Deserialize<MaintenanceSettingsDto>(json);
            settings.IntervalHours = ClampHours(settings.IntervalHours);
            return settings;
        }

        public static MaintenanceStatusDto ParseStatus(string json)
        {
            MaintenanceStatusDto status = Deserialize<MaintenanceStatusDto>(json);
            status.Settings ??= new MaintenanceSettingsDto();
            status.Settings.IntervalHours = ClampHours(status.Settings.IntervalHours);
            return status;
        }

        public static MaintenanceLastReportDto ParseReport(string json) => Deserialize<MaintenanceLastReportDto>(json);

        public static RuntimeSuggestionsResponse ParseSuggestions(string json)
        {
            RuntimeSuggestionsResponse response = Deserialize<RuntimeSuggestionsResponse>(json);
            response.Suggestions ??= new();
            return response;
        }

        public static object BuildSettingsBody(bool enabled, double intervalHours, bool? keepRuntimesUpdated = null, bool? autoOptimizeMedia = null) =>
            (keepRuntimesUpdated, autoOptimizeMedia) switch
            {
                (null, null) => new { enabled, intervalHours = ClampHours(intervalHours) },
                (bool k, null) => new { enabled, intervalHours = ClampHours(intervalHours), keepRuntimesUpdated = k },
                (null, bool a) => new { enabled, intervalHours = ClampHours(intervalHours), autoOptimizeMedia = a },
                (bool k, bool a) => (object)new { enabled, intervalHours = ClampHours(intervalHours), keepRuntimesUpdated = k, autoOptimizeMedia = a }
            };

        public static MediaOptimizationStatusDto ParseMediaOptimization(string json)
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            System.Text.Json.JsonElement status = root.TryGetProperty("status", out var s1) ? s1 : root;
            return Deserialize<MediaOptimizationStatusDto>(status.GetRawText());
        }

        /// <summary>One honest line for the media-engine optimisation state (never a performance claim without a local measurement).</summary>
        public static string SummarizeMediaOptimization(MediaOptimizationStatusDto? s) => s?.Phase switch
        {
            "inspecting" => "Optimizing the media engine: reading this PC...",
            "racing" => $"Optimizing the media engine: measuring option {s.CandidateIndex} of {s.CandidateCount}. {s.Message}",
            "done" or "cancelled" => s.Message ?? string.Empty,
            _ => "The media engine uses its default settings until you optimize it for this PC."
        };

        public static RuntimeUpdateRunDto ParseRuntimeUpdateRun(string json) => Deserialize<RuntimeUpdateRunDto>(json);
        public static RuntimeUpdateSummaryDto ParseRuntimeUpdate(string json) => Deserialize<RuntimeUpdateSummaryDto>(json);

        /// <summary>One honest line for the runtime auto-update setting and its last result.</summary>
        public static string SummarizeRuntimeUpdate(MaintenanceStatusDto status, DateTime nowUtc)
        {
            if (!status.Settings.KeepRuntimesUpdated) return "Automatic runtime updates are off. PaRuX-managed runtimes change only when you update them.";
            RuntimeUpdateSummaryDto? u = status.RuntimeUpdate;
            if (u is null || string.IsNullOrWhiteSpace(u.LastResultLine) || u.LastResultUtc is not { } at)
                return "Runtime updates: no automatic update has run yet.";
            return "Last runtime update " + DescribeAge(nowUtc - at.ToUniversalTime()) + ": " + u.LastResultLine;
        }

        /// <summary>One honest line about the last automatic run.</summary>
        public static string SummarizeLastRun(MaintenanceStatusDto status, DateTime nowUtc)
        {
            MaintenanceLastReportDto? report = status.LastReport;
            if (status.Running) return "A check is running now.";
            if (report is null || report.FinishedUtc == default)
                return status.Settings.Enabled ? "No automatic check has run yet." : "Automatic checks are off.";
            string when = DescribeAge(nowUtc - report.FinishedUtc.ToUniversalTime());
            int suggestions = report.Proposals?.Count ?? 0;
            string outcome = report.Outcome switch
            {
                "ok" => suggestions > 0 ? suggestions + " suggestion(s) recorded - nothing was changed." : "finished.",
                "no_change" => "nothing new.",
                "skipped_gaming" => "skipped because Gaming Mode is on.",
                "skipped_disabled" => "skipped because automatic checks are off.",
                "busy" => "another check was already running.",
                "error" => "finished with errors" + (report.Errors is { Count: > 0 } ? ": " + report.Errors[0] : "."),
                _ => report.Outcome
            };
            return "Last check " + when + " (" + report.Trigger + "): " + outcome;
        }

        private static string DescribeAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + " min ago";
            if (age.TotalDays < 1) return (int)age.TotalHours + " h ago";
            return (int)age.TotalDays + " d ago";
        }

        /// <summary>Label of the primary suggestion action, or null when the suggestion is informational only.</summary>
        public static string? PrimaryActionLabel(RuntimeBackendSuggestion s) =>
            !s.Informational && s.Action == "install_managed" ? "Install with PaRuX…" : null;

        public static string EvidenceLabel(RuntimeBackendSuggestion s) =>
            s.EvidenceLevel == "measured_locally" ? "Measured on this PC" : "Unmeasured - reasoned from your hardware";

        private static T Deserialize<T>(string json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Empty maintenance response.");
            try { return JsonSerializer.Deserialize<T>(json, Json) ?? new T(); }
            catch (JsonException ex) { throw new FormatException("Unreadable maintenance response.", ex); }
        }
    }
}
