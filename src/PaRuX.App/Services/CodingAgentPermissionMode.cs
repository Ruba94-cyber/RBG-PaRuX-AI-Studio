using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>Permission behavior for one coding run. Bypass preserves the established defaults.</summary>
    public static class CodingAgentPermissionMode
    {
        public const string Bypass = "bypass";
        public const string Ask = "ask";
        public const string Plan = "plan";

        private static readonly HashSet<string> Modes = new(StringComparer.OrdinalIgnoreCase)
        {
            Bypass, Ask, Plan
        };

        public static string Normalize(string? value) =>
            !string.IsNullOrWhiteSpace(value) && Modes.Contains(value.Trim())
                ? value.Trim().ToLowerInvariant()
                : Bypass;
    }

    /// <summary>Fail-closed action classification and exact one-shot shell approval checks.</summary>
    public static class CodingAgentPermissionPolicy
    {
        public const string ApproveExactCommandOnce = "Approve this exact command once";
        public const string ApproveExactFileChangeOnce = "Approve this exact file change once";
        public const string DeclineCommand = "Decline";
        // Core's durable question contract is capped at 2,000 characters; leave room for the
        // approval prompt and task framing so the full proposed mutation remains visible.
        public const int MaximumFileApprovalPreviewCharacters = 1_800;

        private static readonly HashSet<string> ReadOnlyActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "update_plan", "list_directory", "read_file", "read_file_range", "read_file_lines",
            "grep_search", "glob_search", "list_artifacts", "read_artifact", "search_artifact",
            "git_status", "git_diff", "git_log", "web_search", "browser_dom", "browser_console",
            "browser_network", "browser_assert", "browser_screenshot", "ask_user", "collect_children",
            "batch_read", "done"
        };

        private static readonly HashSet<string> WorkspaceMutationActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "replace_range", "write_file", "create_file", "delete_file", "rename_file"
        };

        public static bool IsAllowedInPlanMode(string? action) =>
            !string.IsNullOrWhiteSpace(action) && ReadOnlyActions.Contains(action.Trim());

        public static bool IsAllowedInPlanMode(string? action, string? command) =>
            string.Equals(action?.Trim(), "run_command", StringComparison.OrdinalIgnoreCase)
                ? CodingCommandPreflight.IsReadOnlyPlanCommand(command)
                : IsAllowedInPlanMode(action);

        public static bool IsRefusedInPlanMode(string? mode, string? action, string? command = null) =>
            CodingAgentPermissionMode.Normalize(mode) == CodingAgentPermissionMode.Plan
            && !IsAllowedInPlanMode(action, command);

        public static bool IsWorkspaceMutationAction(string? action) =>
            !string.IsNullOrWhiteSpace(action) && WorkspaceMutationActions.Contains(action.Trim());

        public static bool IsExactCommandApproval(string? approvedCommand, string? proposedCommand) =>
            !string.IsNullOrEmpty(approvedCommand)
            && !string.IsNullOrEmpty(proposedCommand)
            && string.Equals(approvedCommand, proposedCommand, StringComparison.Ordinal);

        public static bool IsApprovedAnswer(string? answer) =>
            string.Equals(answer, ApproveExactCommandOnce, StringComparison.Ordinal);

        public static string FileMutationApprovalKey(
            string? action, string? path, string? newPath, string? content, string? oldText, string? newText)
        {
            string canonical = JsonSerializer.Serialize(new[]
            {
                action ?? string.Empty, path ?? string.Empty, newPath ?? string.Empty,
                content ?? string.Empty, oldText ?? string.Empty, newText ?? string.Empty
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        public static string? FileMutationApprovalPreview(
            string? action, string? path, string? newPath, string? content, string? oldText, string? newText)
        {
            var preview = new StringBuilder()
                .Append("Action: ").Append(action ?? string.Empty)
                .Append("\nPath: ").Append(path ?? string.Empty);
            if (!string.IsNullOrEmpty(newPath)) preview.Append("\nNew path: ").Append(newPath);
            if (string.Equals(action, "write_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "create_file", StringComparison.OrdinalIgnoreCase))
                preview.Append("\nComplete content:\n").Append(content ?? string.Empty);
            else if (string.Equals(action, "replace_range", StringComparison.OrdinalIgnoreCase))
                preview.Append("\nExact old text:\n").Append(oldText ?? string.Empty)
                    .Append("\nExact replacement text:\n").Append(newText ?? string.Empty);
            if (preview.Length > MaximumFileApprovalPreviewCharacters) return null;
            return preview.ToString();
        }
    }

    /// <summary>Persists one exact command approval through the existing durable question exchange.</summary>
    public static class CodingAgentCommandApproval
    {
        public static void Stage(CodingAgentCheckpoint checkpoint, string questionId, string command)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.PendingCommandApprovalQuestionId = questionId ?? string.Empty;
            checkpoint.PendingCommandApprovalText = command ?? string.Empty;
            checkpoint.ApprovedCommandText = string.Empty;
        }

        public static bool ConsumeAnswer(
            CodingAgentCheckpoint checkpoint, string? questionId, string? answer)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (string.IsNullOrWhiteSpace(checkpoint.PendingCommandApprovalQuestionId)
                || !string.Equals(checkpoint.PendingCommandApprovalQuestionId, questionId, StringComparison.Ordinal))
                return false;

            bool approved = CodingAgentPermissionPolicy.IsApprovedAnswer(answer);
            checkpoint.ApprovedCommandText = approved ? checkpoint.PendingCommandApprovalText : string.Empty;
            checkpoint.PendingCommandApprovalQuestionId = string.Empty;
            checkpoint.PendingCommandApprovalText = string.Empty;
            return true;
        }

        public static void ConsumeApproval(CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.ApprovedCommandText = string.Empty;
        }
    }

    /// <summary>Persists exact, single-use file mutation approvals through Core user questions.</summary>
    public static class CodingAgentFileApproval
    {
        public static void Stage(CodingAgentCheckpoint checkpoint, string questionId, string approvalKey)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.PendingFileApprovalQuestionId = questionId ?? string.Empty;
            checkpoint.PendingFileApprovalKey = approvalKey ?? string.Empty;
            checkpoint.ApprovedFileApprovalKey = string.Empty;
        }

        public static bool ConsumeAnswer(CodingAgentCheckpoint checkpoint, string? questionId, string? answer)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (string.IsNullOrWhiteSpace(checkpoint.PendingFileApprovalQuestionId)
                || !string.Equals(checkpoint.PendingFileApprovalQuestionId, questionId, StringComparison.Ordinal))
                return false;

            checkpoint.ApprovedFileApprovalKey = string.Equals(
                answer, CodingAgentPermissionPolicy.ApproveExactFileChangeOnce, StringComparison.Ordinal)
                    ? checkpoint.PendingFileApprovalKey
                    : string.Empty;
            checkpoint.PendingFileApprovalQuestionId = string.Empty;
            checkpoint.PendingFileApprovalKey = string.Empty;
            return true;
        }

        public static bool IsApproved(CodingAgentCheckpoint checkpoint, string approvalKey) =>
            !string.IsNullOrEmpty(approvalKey)
            && string.Equals(checkpoint.ApprovedFileApprovalKey, approvalKey, StringComparison.Ordinal);

        public static void ConsumeApproval(CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.ApprovedFileApprovalKey = string.Empty;
        }
    }
}
