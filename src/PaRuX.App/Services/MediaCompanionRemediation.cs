using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class MediaDependencyJobException : Exception
    {
        public MediaDependencyError ErrorDetail { get; }

        public MediaDependencyJobException(MediaDependencyError error, string? context = null)
            : base(MediaCompanionRemediationText.FormatFailure(error, context))
        {
            ErrorDetail = error ?? throw new ArgumentNullException(nameof(error));
        }
    }

    public static class MediaCompanionRemediationText
    {
        public static string? CompanionTypeForJob(string? jobType) => jobType?.Trim().ToLowerInvariant() switch
        {
            "image" or "img2img" => "image",
            "video" => "video",
            "edit" => "edit",
            _ => null
        };

        public static bool CanOfferFetch(MediaDependencyError error, MediaCompanionPlan plan, string expectedType)
        {
            if (error == null || plan == null || error.UpdateRequired
                || !string.Equals(error.Reason, "component_missing", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(error.ExpectedFileName)
                || !string.Equals(plan.Type, CompanionTypeForJob(expectedType), StringComparison.Ordinal)
                || !string.Equals(plan.Support, "supported", StringComparison.Ordinal)
                || !plan.Installable
                || string.IsNullOrWhiteSpace(plan.PlanHash)
                || plan.Missing.Count == 0
                || plan.Missing.Any(item => !item.Fetchable))
                return false;

            return plan.Missing.Any(item =>
                string.Equals(item.FileName, error.ExpectedFileName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(error.ComfyFolder)
                    || string.Equals(item.Folder, error.ComfyFolder, StringComparison.OrdinalIgnoreCase)));
        }

        public static bool ShouldRetryAfterFetch(bool fetchSucceeded, bool retryAlreadyAttempted) =>
            fetchSucceeded && !retryAlreadyAttempted;

        public static MediaCompanionFetchRequest CreateFetchRequest(MediaCompanionPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (string.IsNullOrWhiteSpace(plan.PlanHash))
                throw new InvalidOperationException("A reviewed companion plan hash is required before fetching.");

            return new MediaCompanionFetchRequest
            {
                Type = plan.Type,
                PlanHash = plan.PlanHash,
                Consent = true
            };
        }

        public static string FormatFailure(MediaDependencyError error, string? context = null)
        {
            ArgumentNullException.ThrowIfNull(error);
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(error.Message)) lines.Add(error.Message.Trim());
            if (!string.IsNullOrWhiteSpace(error.Role)) lines.Add("Component: " + error.Role);
            if (!string.IsNullOrWhiteSpace(error.ExpectedFileName)) lines.Add("Required file: " + error.ExpectedFileName);
            if (!string.IsNullOrWhiteSpace(error.ComfyFolder)) lines.Add("ComfyUI folder: models/" + error.ComfyFolder);
            if (!string.IsNullOrWhiteSpace(error.Repository)) lines.Add("Repository: " + error.Repository);
            if (error.SizeBytes.HasValue) lines.Add("Expected size: " + FormatSize(error.SizeBytes));
            if (!string.IsNullOrWhiteSpace(error.PackName)) lines.Add("Custom node pack: " + error.PackName);
            if (!string.IsNullOrWhiteSpace(error.PackUrl)) lines.Add("Pack information: " + error.PackUrl);
            if (error.UpdateRequired) lines.Add("A ComfyUI update is required.");
            if (!string.IsNullOrWhiteSpace(error.NextAction)) lines.Add("Next step: " + error.NextAction.Trim());
            if (!string.IsNullOrWhiteSpace(context)) lines.Add(context.Trim());
            return string.Join(Environment.NewLine, lines);
        }

        public static string FormatPlan(MediaCompanionPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var lines = new List<string>();
            string identity = string.IsNullOrWhiteSpace(plan.FamilyName) ? plan.MainFileName : plan.FamilyName;
            lines.Add("Plan for " + (string.IsNullOrWhiteSpace(identity) ? "the selected model" : identity) + ":");
            lines.Add("Status: " + plan.Support + (plan.Installable ? " (fetchable)" : " (not installable)"));
            if (!string.IsNullOrWhiteSpace(plan.Reason)) lines.Add("Plan note: " + plan.Reason.Trim());

            if (plan.Missing.Count == 0)
            {
                lines.Add("No companion files are currently missing.");
                return string.Join(Environment.NewLine, lines);
            }

            lines.Add("Missing companions (about " + FormatSize(plan.MissingApproxBytes) + " total):");
            foreach (MediaCompanionItem item in plan.Missing)
            {
                lines.Add("- " + item.FileName + " -> ComfyUI/models/" + item.Folder);
                lines.Add("  Role: " + (string.IsNullOrWhiteSpace(item.Role) ? "unspecified" : item.Role));
                lines.Add("  Size: " + FormatSize(item.PinnedSizeBytes ?? item.ApproxSizeBytes));
                lines.Add("  Repository: " + (string.IsNullOrWhiteSpace(item.Repository) ? "not recorded" : item.Repository));
                lines.Add("  License: " + (string.IsNullOrWhiteSpace(item.License) ? "not recorded" : item.License));
                if (!item.Fetchable && !string.IsNullOrWhiteSpace(item.Why)) lines.Add("  Fetch unavailable: " + item.Why.Trim());
            }

            lines.Add("Only the missing files listed above are included in this consent plan.");
            return string.Join(Environment.NewLine, lines);
        }

        public static string BuildConsentMessage(MediaDependencyError error, MediaCompanionPlan plan) =>
            FormatFailure(error) + Environment.NewLine + Environment.NewLine + FormatPlan(plan)
            + Environment.NewLine + Environment.NewLine
            + "Fetching these files requires your consent. After a successful fetch, this media job will be retried once.";

        private static string FormatSize(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value < 0) return "unknown";
            if (bytes.Value >= 1024L * 1024L)
                return (bytes.Value / (1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            if (bytes.Value >= 1024L)
                return (bytes.Value / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            return bytes.Value.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }
}
