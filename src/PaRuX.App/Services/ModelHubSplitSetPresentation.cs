using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Testable presentation data for repository quantisation choices and incomplete local sets.</summary>
    public static class ModelHubSplitSetPresentation
    {
        public sealed record QuantChoiceOption(ModelQuantChoice Choice, string Label, bool CanDownload);

        public sealed record IncompleteSetRow(
            string Title,
            string Note,
            string RemoveFileName,
            bool CanResume,
            string? RepoId,
            string? Quantization,
            string SetName,
            string? Directory,
            int PartCount);

        public static IReadOnlyList<QuantChoiceOption> BuildQuantChoiceOptions(IEnumerable<ModelQuantChoice> choices) =>
            choices
                .Where(choice => !string.IsNullOrWhiteSpace(choice.SetName))
                .OrderBy(choice => choice.Quantization, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.SetName, StringComparer.OrdinalIgnoreCase)
                .Select(choice =>
                {
                    string shape = choice.IsSplit
                        ? choice.PartCount + (choice.PartCount == 1 ? " part" : " parts")
                        : "single file";
                    string incomplete = choice.Complete
                        ? string.Empty
                        : " · incomplete (" + choice.FilePaths.Count + "/" + choice.PartCount + " parts listed)";
                    string directory = string.IsNullOrEmpty(choice.Directory) ? "repository root" : choice.Directory + "/";
                    return new QuantChoiceOption(choice,
                        choice.Quantization + " · " + directory + choice.SetName + " · " + shape + incomplete,
                        choice.Complete);
                })
                .ToList();

        /// <summary>
        /// First-run Model Hub handoff: prefer the recommended quantization when its set
        /// is complete. Never preselect a broken multipart set; downloads still require
        /// explicit review of size, source revision, and license.
        /// </summary>
        public static int PreferredQuantChoiceIndex(
            IReadOnlyList<QuantChoiceOption> choices, string? quantizationHint)
        {
            ArgumentNullException.ThrowIfNull(choices);
            int firstDownloadable = -1;
            string? hint = quantizationHint?.Trim();
            for (int i = 0; i < choices.Count; i++)
            {
                QuantChoiceOption option = choices[i];
                if (!option.CanDownload) continue;
                if (firstDownloadable < 0) firstDownloadable = i;
                if (!string.IsNullOrWhiteSpace(hint)
                    && string.Equals(option.Choice.Quantization?.Trim(), hint, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return firstDownloadable;
        }

        /// <summary>
        /// Resolve a curated recommendation to the exact repository and one complete GGUF
        /// quantization set. Never fall back to a different search result or a single piece of
        /// a split model; the caller still performs the normal review and license confirmation.
        /// </summary>
        public sealed record RecommendedSetOption(string RepoId, ModelQuantChoice Choice);

        public static RecommendedSetOption? PickRecommendedSet(
            IEnumerable<ModelSearchResult> searchResults, string expectedRepoId, string? quantizationHint)
        {
            ArgumentNullException.ThrowIfNull(searchResults);
            if (string.IsNullOrWhiteSpace(expectedRepoId)) return null;

            ModelSearchResult? repository = searchResults.FirstOrDefault(candidate =>
                string.Equals(candidate.RepoId, expectedRepoId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (repository == null) return null;
            IReadOnlyList<QuantChoiceOption> choices = BuildQuantChoiceOptions(repository.QuantChoices);
            int selected = PreferredQuantChoiceIndex(choices, quantizationHint);
            return selected < 0 ? null : new RecommendedSetOption(repository.RepoId, choices[selected].Choice);
        }

        public static IncompleteSetRow? BuildIncompleteSetRow(ManagedModelInfo model)
        {
            ManagedModelSplitInfo? split = model.SplitSet;
            string? note = split == null ? null : ManagedSplitSetNotes.Describe(split);
            if (split == null || note == null) return null;

            string? repoId = string.IsNullOrWhiteSpace(model.RepoId) ? null : model.RepoId.Trim();
            string quantization = model.Quantization?.Trim() ?? string.Empty;
            string? sourceDirectory = GetSourceDirectory(model.Provenance?.RepoFilePath);
            bool canResume = repoId != null
                && !string.IsNullOrWhiteSpace(split.SetName)
                && sourceDirectory != null;
            string? resumeQuantization = string.IsNullOrWhiteSpace(quantization)
                || string.Equals(quantization, "unknown", StringComparison.OrdinalIgnoreCase)
                ? null
                : quantization;

            return new IncompleteSetRow(
                split.SetName,
                note,
                split.PartFileNames.FirstOrDefault() ?? model.FileName,
                canResume,
                repoId,
                resumeQuantization,
                split.SetName,
                sourceDirectory,
                split.PartCount);
        }

        private static string? GetSourceDirectory(string? repoFilePath)
        {
            if (string.IsNullOrWhiteSpace(repoFilePath)) return null;
            string normalized = repoFilePath.Replace('\\', '/').Trim('/');
            int slash = normalized.LastIndexOf('/');
            return slash < 0 ? string.Empty : normalized[..slash];
        }
    }
}
