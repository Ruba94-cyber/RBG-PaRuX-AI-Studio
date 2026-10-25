using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// A passing build/test command can certify only the workspace state
    /// observed immediately after its completion. An indirect command edit,
    /// external file modification, repair or resume invalidates that receipt.
    /// This is a deterministic Git-worktree-state guard, NOT cryptographic
    /// authentication of the command or a substitute for independent review.
    /// </summary>
    public static class CodingWorkspaceVerificationFingerprint
    {
        private const int MaximumDiffCharacters = 4_000_000;
        private const int MaximumRepositories = 16;

        internal static string? FingerprintDiff(string? diff)
        {
            // Match the stable notice phrases rather than the wrapper punctuation: the producer
            // currently wraps them as "(new file <path> ...)", and a wrapper change must not let a
            // truncated or linked-file diff be fingerprinted as if it were complete.
            if (diff == null || diff.Length > MaximumDiffCharacters
                || diff.Contains("more new file(s) not shown", StringComparison.Ordinal)
                || diff.Contains("exceeds the diff budget", StringComparison.Ordinal)
                || diff.Contains("omitted: link or junction", StringComparison.Ordinal))
                return null;

            // Empty tracked diff is a valid clean state. Its digest still
            // changes once someone edits a tracked file or adds a visible file.
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(diff))).ToLowerInvariant();
        }

        public static async Task<string?> CaptureAsync(string workspace, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
                return null;
            try
            {
                string root = Path.GetFullPath(workspace);
                if (!CodingWorkspaceGitLayoutPolicy.TrySelect(
                    root, MaximumRepositories,
                    out IReadOnlyList<(string Alias, string Root)> repositories))
                    return null;

                var pieces = new List<string>(repositories.Count);
                foreach ((string alias, string path) in repositories
                    .OrderBy(x => x.Alias, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    // Fail closed if a git error would otherwise be converted
                    // into an empty diff (indistinguishable from clean).
                    (int statusCode, _) = await CodingAgentTools.RunGitAsync(
                        path, ct, includeStderr: false,
                        "status", "--porcelain=v1").ConfigureAwait(false);
                    if (statusCode != 0) return null;
                    (int headExit, string head) = await CodingAgentTools.RunGitAsync(
                        path, ct, includeStderr: false,
                        "rev-parse", "HEAD").ConfigureAwait(false);
                    // A clean diff after a new commit must NOT reuse an earlier
                    // test receipt bound to another revision of source code.
                    head = head.Trim();
                    if (headExit != 0 || head.Length is not (40 or 64)
                        || !head.All(Uri.IsHexDigit)) return null;

                    // WorkingTreeDiffWithUntrackedAsync is also used for
                    // bounded human display and historically converts a Git
                    // subprocess failure into an empty string. A verification
                    // fingerprint must not confuse that with a clean worktree:
                    // independently check BOTH tracked diff and untracked
                    // enumeration command exit codes before hashing.
                    (int trackedExit, _) = await CodingAgentTools.RunGitAsync(
                        path, ct, includeStderr: false,
                        "diff", "--quiet", "--no-ext-diff", "HEAD", "--")
                        .ConfigureAwait(false);
                    if (trackedExit is not (0 or 1)) return null;
                    (int untrackedExit, _) = await CodingAgentTools.RunGitAsync(
                        path, ct, includeStderr: false,
                        "ls-files", "--others", "--exclude-standard", "-z")
                        .ConfigureAwait(false);
                    if (untrackedExit != 0) return null;

                    string diff = await CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(
                        path, ct).ConfigureAwait(false);
                    string? fingerprint = FingerprintDiff(diff);
                    if (fingerprint == null) return null;
                    pieces.Add(alias + ":" + head.ToLowerInvariant()
                        + ":" + fingerprint);
                }

                if (pieces.Count == 0) return null;
                return Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join("\n", pieces))))
                    .ToLowerInvariant();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                return null;
            }
        }
    }
}
