using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Plain immutable record describing media generation provenance (BIC-13, BIC-15).
    /// Pure: no WinUI dependencies, safe for inclusion in server test suites.
    /// </summary>
    public sealed record MediaProvenanceRecord
    {
        public string? EngineFamily { get; init; }
        public string? Model { get; init; }
        public bool IsLocal { get; init; } = true;
        public string? FallbackReason { get; init; }
        /// <summary>Why the router chose this model for this request (server-recorded). Null for results routed before this was recorded.</summary>
        public string? SelectionReason { get; init; }
        public IReadOnlyDictionary<string, string>? AdditionalDetails { get; init; }

        public MediaProvenanceRecord() { }

        public MediaProvenanceRecord(
            string? engineFamily = null,
            string? model = null,
            bool isLocal = true,
            string? fallbackReason = null,
            IReadOnlyDictionary<string, string>? additionalDetails = null,
            string? EngineFamily = null,
            string? Model = null,
            bool? IsLocal = null,
            string? FallbackReason = null)
        {
            this.EngineFamily = EngineFamily ?? engineFamily;
            this.Model = Model ?? model;
            this.IsLocal = IsLocal ?? isLocal;
            this.FallbackReason = FallbackReason ?? fallbackReason;
            this.AdditionalDetails = additionalDetails;
        }

        public string? ModelFileName
        {
            get => Model;
            init => Model = value;
        }

        public string? ModelName
        {
            get => Model;
            init => Model = value;
        }
    }

    /// <summary>
    /// Formatted presentation for media job provenance containing the summary line and detailed list.
    /// </summary>
    public sealed record MediaProvenancePresentation(
        string Line,
        IReadOnlyList<string> Details,
        bool HasProvenance);

    /// <summary>
    /// Pure presenter/formatter for media generation provenance (BIC-13, BIC-15).
    /// Formats engine family, model file name (basename only), and locality into a concise line and expandable details.
    /// </summary>
    public static class MediaProvenanceFormatter
    {
        public const string ProvenanceTextAutomationId = "Create_Media_Provenance_Text";
        public const string ProvenanceDetailsButtonAutomationId = "Create_Media_Provenance_Details_Button";

        public const string MissingProvenanceNote = "Provenance not recorded for this older result";

        /// <summary>
        /// Strips any path separators, drive letters, and parent directories from a model path,
        /// ensuring only the safe basename is presented.
        /// </summary>
        public static string SanitizeModelName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            string s = raw.Trim();

            // Normalize Windows backslashes to forward slashes
            s = s.Replace('\\', '/');

            // Strip drive letters, e.g. "C:" or "D:"
            int colonIdx = s.IndexOf(':');
            if (colonIdx >= 0)
            {
                s = s.Substring(colonIdx + 1);
            }

            // Strip leading slashes
            s = s.TrimStart('/');

            // Extract the final segment (basename)
            int lastSlash = s.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                s = s.Substring(lastSlash + 1);
            }

            s = s.Trim();
            return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
        }

        /// <summary>
        /// Formats a media provenance record into a single summary line and a list of detail lines.
        /// </summary>
        public static MediaProvenancePresentation Format(MediaProvenanceRecord? record)
        {
            if (record == null || (string.IsNullOrWhiteSpace(record.EngineFamily) && string.IsNullOrWhiteSpace(record.Model)))
            {
                return new MediaProvenancePresentation(
                    Line: MissingProvenanceNote,
                    Details: Array.Empty<string>(),
                    HasProvenance: false);
            }

            string engine = !string.IsNullOrWhiteSpace(record.EngineFamily)
                ? record.EngineFamily.Trim()
                : "Local Media Engine";
            string model = SanitizeModelName(record.Model);

            string baseLine = record.IsLocal
                ? $"Made locally with {engine} ({model})"
                : $"Made remotely with {engine} ({model})";

            string line = baseLine;
            if (!string.IsNullOrWhiteSpace(record.SelectionReason))
                line += $" · Why: {record.SelectionReason.Trim()}";
            if (!string.IsNullOrWhiteSpace(record.FallbackReason))
                line += $" · Fallback: {record.FallbackReason.Trim()}";

            var details = new List<string>
            {
                $"Engine family: {engine}",
                $"Model: {model}",
                $"Stayed on this machine: {(record.IsLocal ? "Yes" : "No")}"
            };

            if (!string.IsNullOrWhiteSpace(record.SelectionReason))
            {
                details.Add($"Why this model: {record.SelectionReason.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(record.FallbackReason))
            {
                details.Add($"Fallback: {record.FallbackReason.Trim()}");
            }

            if (record.AdditionalDetails != null)
            {
                foreach (var kvp in record.AdditionalDetails)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        details.Add($"{kvp.Key.Trim()}: {kvp.Value.Trim()}");
                    }
                }
            }

            return new MediaProvenancePresentation(
                Line: line,
                Details: details.AsReadOnly(),
                HasProvenance: true);
        }

        public static MediaProvenancePresentation Format(
            string? engineFamily,
            string? model,
            bool isLocal = true,
            string? fallbackReason = null,
            IReadOnlyDictionary<string, string>? additionalDetails = null) =>
            Format(new MediaProvenanceRecord(engineFamily, model, isLocal, fallbackReason, additionalDetails));

        public static string FormatLine(MediaProvenanceRecord? record) => Format(record).Line;

        public static IReadOnlyList<string> FormatDetails(MediaProvenanceRecord? record) => Format(record).Details;
    }
}
