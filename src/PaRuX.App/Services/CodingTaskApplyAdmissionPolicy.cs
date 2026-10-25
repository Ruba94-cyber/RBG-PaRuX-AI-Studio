using System;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// A stale chat card or a Completed status alone is never authority to
    /// integrate autonomous code into a user repository. A fresh, Core-owned
    /// task must have an independent PASS and accepted deterministic command
    /// evidence tied to its task identity. This does not replace a same-diff
    /// review or the explicit user confirmation.
    /// </summary>
    public static class CodingTaskApplyAdmissionPolicy
    {
        public static bool CanApply(AgentTaskDto? task, string? expectedTaskId)
        {
            if (task == null || string.IsNullOrWhiteSpace(expectedTaskId)
                || !string.Equals(task.Id, expectedTaskId, StringComparison.Ordinal)
                || !string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(task.Status, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(task.VerificationStatus, "pass", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(task.ResultJson)
                || task.ResultJson.Length > 1_000_000)
                return false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(task.ResultJson);
                JsonElement root = doc.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("goalReported", out JsonElement goal)
                    && goal.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("verifiedByCommand", out JsonElement verified)
                    && verified.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("verificationPending", out JsonElement pending)
                    && pending.ValueKind == JsonValueKind.False
                    && root.TryGetProperty("browserPostconditionPending", out JsonElement browserPending)
                    && browserPending.ValueKind == JsonValueKind.False
                    && root.TryGetProperty("checkpointSessionId", out JsonElement checkpoint)
                    && checkpoint.ValueKind == JsonValueKind.String
                    && string.Equals(checkpoint.GetString(), expectedTaskId, StringComparison.Ordinal)
                    && root.TryGetProperty("verifiedWorkspaceFingerprint", out JsonElement fingerprint)
                    && fingerprint.ValueKind == JsonValueKind.String
                    && IsValidDigest(fingerprint.GetString());
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// The Git-state fingerprint that the verifier accepted for an admissible task. Apply must reproduce exactly
        /// this state, so the verified receipt is bound to the reviewed diff instead of merely being present.
        /// </summary>
        public static string? TryGetVerifiedWorkspaceFingerprint(AgentTaskDto? task, string? expectedTaskId)
        {
            if (!CanApply(task, expectedTaskId)) return null;
            using JsonDocument document = JsonDocument.Parse(task!.ResultJson!);
            return document.RootElement.GetProperty("verifiedWorkspaceFingerprint").GetString();
        }

        private static bool IsValidDigest(string? fingerprint)
        {
            if (fingerprint == null || fingerprint.Length != 64) return false;
            foreach (char ch in fingerprint)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                    return false;
            return true;
        }
    }
}
