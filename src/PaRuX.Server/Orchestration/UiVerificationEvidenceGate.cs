using System;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// A UI-verification model opinion cannot become a verified task unless its
/// desktop worker bound the screenshot to a stable native foreground window
/// and persisted an independently inspectable visual snapshot. UIA is optional
/// observation but an asserted UIA capture must match that same window.
/// This gate verifies evidence metadata shape only: it does not assert an
/// app's functionality, replace real UI tests, or authenticate screenshot
/// bytes independently.
/// </summary>
internal static class UiVerificationEvidenceGate
{
    internal sealed record Decision(bool Allowed, string Reason);

    internal static Decision Evaluate(string? role, string? resultJson)
    {
        if (!string.Equals(role, "ui-verification", StringComparison.OrdinalIgnoreCase))
            return new Decision(true, "Not a desktop UI-verification task.");

        if (string.IsNullOrWhiteSpace(resultJson) || resultJson.Length > 1_000_000)
            return Reject("UI verification has no bounded persisted observation.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject("UI verification evidence must be a typed object.");

            if (!root.TryGetProperty("foregroundWindowStable", out JsonElement stable)
                || stable.ValueKind != JsonValueKind.True)
                return Reject("UI capture has no stable native foreground window identity.");

            if (!root.TryGetProperty("visualSnapshotId", out JsonElement snapshot)
                || snapshot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(snapshot.GetString())
                || !Guid.TryParseExact(snapshot.GetString(), "N", out _))
                return Reject("UI verification has no persisted visual snapshot reference.");

            if (!root.TryGetProperty("response", out JsonElement response)
                || response.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(response.GetString()))
                return Reject("UI verification has no factual screenshot observation.");

            if (!root.TryGetProperty("uiaStatus", out JsonElement uia)
                || uia.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(uia.GetString()))
                return Reject("UI verification has no typed UIA availability status.");

            if (!root.TryGetProperty("evidenceMode", out JsonElement mode)
                || mode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("uiTreeMatchedCapture", out JsonElement matched))
                return Reject("UI verification lacks evidence provenance metadata.");

            bool observed = string.Equals(uia.GetString(), "observed", StringComparison.Ordinal);
            bool valid = observed
                ? matched.ValueKind == JsonValueKind.True
                  && mode.GetString() == "screen-plus-read-only-uia-plus-bounded-predecessors"
                : matched.ValueKind == JsonValueKind.False
                  && mode.GetString() == "screen-plus-bounded-predecessors";

            return valid
                ? new Decision(true, observed
                    ? "Screen and matched UIA evidence are available for independent review."
                    : "A persisted screenshot is available; no UIA functionality is claimed.")
                : Reject("UI screenshot and UIA evidence provenance are inconsistent.");
        }
        catch (JsonException)
        {
            return Reject("UI verification metadata is not valid JSON.");
        }
    }

    /// <summary>
    /// A string ID is insufficient evidence. The server reads the durable
    /// VisualContext V2 record from its own store and binds that record to
    /// the UI task and the originally declared immutable snapshot ID.
    /// The store itself computes SHA-256 over retained PNG bytes.
    /// </summary>
    internal static Decision ValidatePersistedSnapshot(
        string taskId, string? claimedSnapshotId, VisualSnapshotDto? stored)
    {
        if (string.IsNullOrWhiteSpace(taskId)
            || string.IsNullOrWhiteSpace(claimedSnapshotId)
            || stored == null
            || !string.Equals(stored.Id, claimedSnapshotId, StringComparison.Ordinal)
            || !string.Equals(stored.Scope, "task", StringComparison.Ordinal)
            || !string.Equals(stored.ScopeId, taskId, StringComparison.Ordinal)
            || !string.Equals(stored.Source, "ui-verification", StringComparison.Ordinal)
            || !string.Equals(stored.MediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            || stored.ByteLength <= 0
            || stored.Sha256?.Length != 64)
            return Reject("UI screenshot is absent, not retained, or not bound to this task.");

        foreach (char character in stored.Sha256)
            if (!Uri.IsHexDigit(character))
                return Reject("UI screenshot has no valid durable digest.");

        return new Decision(true,
            "Durable task-owned PNG snapshot and SHA-256 metadata are present for independent review.");
    }

    internal static string? ReadSnapshotId(string resultJson)
    {
        using JsonDocument document = JsonDocument.Parse(resultJson);
        return document.RootElement.GetProperty("visualSnapshotId").GetString();
    }

    private static Decision Reject(string reason) => new(false, reason);
}
