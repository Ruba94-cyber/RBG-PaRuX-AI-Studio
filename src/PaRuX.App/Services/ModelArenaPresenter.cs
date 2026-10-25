using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Pure wording and validation for the "Compare models" section. No UI, no HTTP.</summary>
    public static class ModelArenaPresenter
    {
        public const int MinModels = 2;
        public const int MaxModels = 3;
        public const string EvidenceNote =
            "Measured on this PC. Your pick and the automatic check are stored as separate evidence; neither changes which model PaRuX uses.";

        public static string TaskAutomationId(string taskId) => "Arena_Task_" + taskId;
        public static string ModelAutomationId(int index) => "Arena_Model_" + index.ToString(CultureInfo.InvariantCulture);
        public static string JudgeAutomationId(int promptIndex, int modelIndex) =>
            "Arena_Judge_" + promptIndex.ToString(CultureInfo.InvariantCulture) + "_" + modelIndex.ToString(CultureInfo.InvariantCulture);

        public static (bool CanStart, string Message) ValidateStart(IReadOnlyCollection<string> models, int taskCount, string? customPrompt, bool busy)
        {
            if (busy) return (false, "A comparison is already running.");
            int distinct = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct < MinModels) return (false, "Pick at least two different installed models.");
            if (distinct > MaxModels) return (false, "Compare at most three models at a time.");
            if (taskCount == 0 && string.IsNullOrWhiteSpace(customPrompt)) return (false, "Choose at least one task or type your own prompt.");
            return (true, "Models run one after another, never at the same time. Nothing is changed in your routing.");
        }

        public static string FormatTiming(ArenaModelResultDto r)
        {
            if (r.Status != "completed")
                return StatusText(r);
            var parts = new List<string>
            {
                "first token " + Ms(r.TtftMs),
                r.DecodeTokensPerSecond is double t ? t.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s" : "speed not reported",
                r.GeneratedTokens.ToString(CultureInfo.InvariantCulture) + " tokens"
            };
            if (r.LoadMs is double l) parts.Add("load " + Ms(l));
            return string.Join(" - ", parts);
        }

        public static string StatusText(ArenaModelResultDto r) => r.Status switch
        {
            "completed" => "Answered",
            "timed_out" => "Timed out" + Suffix(r.Error),
            "failed" => "Failed" + Suffix(r.Error),
            "cancelled" => "Cancelled",
            _ => "Skipped" + Suffix(r.Error)
        };

        public static string CheckText(ArenaCheckResultDto check) =>
            check.Kind != "automatic-check" || check.Passed is null
                ? string.Empty
                : "Automatic check: " + (check.Passed == true ? "passed" : "not passed") + " - " + check.Detail;

        public static string PickText(ArenaPromptResultDto prompt) =>
            prompt.UserPick is null ? "No pick yet (optional)." : "Your pick (user-judged): " + prompt.UserPick;

        public static string RunStatusText(ArenaRunDto run) => run.Status switch
        {
            "running" => run.Stage,
            "completed" => "Finished. Pick the better answer for each prompt if you like.",
            "cancelled" => "Stopped" + Suffix(run.Error),
            _ => "Comparison failed" + Suffix(run.Error)
        };

        private static string Ms(double? ms) => ms is double v ? (v >= 1000 ? (v / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : v.ToString("0", CultureInfo.InvariantCulture) + " ms") : "n/a";
        private static string Suffix(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : ": " + s;
    }
}
