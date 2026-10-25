using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    public sealed class CodingReviewFileItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ChangeKind { get; set; } = "Modified";
        public string Summary { get; set; } = string.Empty;
        public bool IsAccepted { get; set; } = true;

        public string AutomationIdSafePath =>
            RelativePath.Replace('\\', '_').Replace('/', '_').Replace('.', '_').Replace(' ', '_').Replace(':', '_');
    }

    /// <summary>
    /// Pure review model representing staged/changed files in a coding task worktree.
    /// Supports per-file accept and reject, diffstat parsing, and formatted apply confirmation copy.
    /// </summary>
    public sealed class CodingTaskReviewModel
    {
        public List<CodingReviewFileItem> Files { get; } = new();

        public int TotalFiles => Files.Count;
        public int AcceptedCount => Files.Count(f => f.IsAccepted);
        public int RejectedCount => Files.Count(f => !f.IsAccepted);
        public bool HasAcceptedFiles => AcceptedCount > 0;
        public bool AllAccepted => TotalFiles > 0 && AcceptedCount == TotalFiles;
        public bool NoneAccepted => TotalFiles > 0 && AcceptedCount == 0;

        public IReadOnlyList<string> GetAcceptedPaths() =>
            Files.Where(f => f.IsAccepted).Select(f => f.RelativePath).ToList();

        public IReadOnlyList<string> GetRejectedPaths() =>
            Files.Where(f => !f.IsAccepted).Select(f => f.RelativePath).ToList();

        public bool SetFileAccepted(string relativePath, bool accepted)
        {
            CodingReviewFileItem? file = Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (file == null) return false;
            file.IsAccepted = accepted;
            return true;
        }

        public bool ToggleFile(string relativePath)
        {
            CodingReviewFileItem? file = Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (file == null) return false;
            file.IsAccepted = !file.IsAccepted;
            return true;
        }

        public void AcceptAll()
        {
            foreach (CodingReviewFileItem f in Files) f.IsAccepted = true;
        }

        public void RejectAll()
        {
            foreach (CodingReviewFileItem f in Files) f.IsAccepted = false;
        }

        public string FormatReviewStatus()
        {
            if (TotalFiles == 0) return "No changes found to review.";
            if (AllAccepted) return $"All {TotalFiles} file(s) accepted for apply.";
            if (NoneAccepted) return $"All {TotalFiles} file(s) rejected. Select at least one file to apply.";
            return $"{AcceptedCount} of {TotalFiles} file(s) accepted ({RejectedCount} rejected).";
        }

        public string FormatConfirmationSummary(string targetName, bool multiMode, bool isShadow)
        {
            int accepted = AcceptedCount;
            int rejected = RejectedCount;
            string targetDesc = multiMode
                ? "across all repositories"
                : isShadow
                    ? $"to folder '{targetName}'"
                    : $"to repository '{targetName}'";

            string header = accepted == TotalFiles
                ? $"PaRuX will apply all {accepted} reviewed file(s) {targetDesc} as uncommitted local modifications. No git commit or push will be executed."
                : $"PaRuX will apply {accepted} accepted file(s) {targetDesc} as uncommitted local modifications ({rejected} rejected file(s) skipped). No git commit or push will be executed.";

            IReadOnlyList<string> acceptedList = GetAcceptedPaths();
            IReadOnlyList<string> rejectedList = GetRejectedPaths();

            string body = string.Empty;
            if (acceptedList.Count > 0)
            {
                body += "\n\nAccepted files:\n" + string.Join("\n", acceptedList.Select(p => "+ " + p));
            }
            if (rejectedList.Count > 0)
            {
                body += "\n\nRejected files (will not be modified):\n" + string.Join("\n", rejectedList.Select(p => "- " + p));
            }

            return header + body;
        }

        public static CodingTaskReviewModel Parse(string? reviewText)
        {
            var model = new CodingTaskReviewModel();
            if (string.IsNullOrWhiteSpace(reviewText))
                return model;

            string[] lines = reviewText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                // 1. Untracked line format: "- created-by-agent.txt" or "* file.txt"
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    string file = line[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(file)
                        && !file.StartsWith("Review their", StringComparison.OrdinalIgnoreCase)
                        && seen.Add(file))
                    {
                        model.Files.Add(new CodingReviewFileItem
                        {
                            RelativePath = file,
                            ChangeKind = "Added",
                            Summary = "New file",
                            IsAccepted = true
                        });
                        continue;
                    }
                }

                // 2. Git diff --stat line format: "path/to/file.ext | 12 +++---" or "file.ext | Bin 0 -> 123 bytes"
                int pipeIdx = line.IndexOf('|');
                if (pipeIdx > 0)
                {
                    string filePath = line[..pipeIdx].Trim();
                    string statSummary = line[(pipeIdx + 1)..].Trim();
                    // Filter out header / summary lines like "2 files changed, ..."
                    if (!string.IsNullOrWhiteSpace(filePath)
                        && !filePath.Contains("files changed", StringComparison.OrdinalIgnoreCase)
                        && !filePath.Contains("file changed", StringComparison.OrdinalIgnoreCase)
                        && seen.Add(filePath))
                    {
                        string kind = statSummary.Contains("Bin", StringComparison.OrdinalIgnoreCase)
                            ? "Binary"
                            : statSummary.Contains('-') && !statSummary.Contains('+')
                                ? "Deleted"
                                : statSummary.Contains('+') && !statSummary.Contains('-')
                                    ? "Added"
                                    : "Modified";

                        model.Files.Add(new CodingReviewFileItem
                        {
                            RelativePath = filePath,
                            ChangeKind = kind,
                            Summary = statSummary,
                            IsAccepted = true
                        });
                        continue;
                    }
                }

                // 3. Git porcelain format: "M path/to/file" or "A path" or "D path" or "?? path"
                if (line.Length > 3 && (line[1] == ' ' || line[2] == ' '))
                {
                    char code = line[0];
                    string codeStr = line[..2].Trim();
                    string rest = line[2..].Trim();
                    if ((code == 'M' || code == 'A' || code == 'D' || codeStr == "??" || codeStr == "R")
                        && !string.IsNullOrWhiteSpace(rest)
                        && seen.Add(rest))
                    {
                        string kind = code switch
                        {
                            'A' => "Added",
                            'D' => "Deleted",
                            'R' => "Renamed",
                            _ => codeStr == "??" ? "Added" : "Modified"
                        };

                        model.Files.Add(new CodingReviewFileItem
                        {
                            RelativePath = rest,
                            ChangeKind = kind,
                            Summary = kind,
                            IsAccepted = true
                        });
                        continue;
                    }
                }
            }

            return model;
        }
    }
}
