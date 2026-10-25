using System;
using System.Text.Json;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Server-side preflight for work received from a desktop coding worker.
/// A model verdict is never authoritative over unfinished deterministic tests.
/// This validates the worker's claimed status, not the authenticity of a test
/// run: future receipts must be cryptographically/workspace-bound to close that
/// separate gap.
/// </summary>
internal static class CodingResultVerifierGate
{
    private const int MaximumResultCharacters = 1_000_000;

    internal sealed record Decision(bool Allowed, string Reason);

    internal static Decision Evaluate(string? role, string? resultJson, string? taskId = null)
    {
        if (!string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase))
            return new Decision(true, "Non-coding role follows normal verification policy.");

        if (string.IsNullOrWhiteSpace(resultJson)
            || resultJson.Length > MaximumResultCharacters)
            return Reject("Coding result is absent or too large for trusted verification preflight.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject("Coding result must be a typed object.");

            if (!ReadBool(root, "verificationPending", out bool pending)
                || !ReadBool(root, "verifiedByCommand", out bool verified))
                return Reject("Coding result is missing typed verification state from its worker.");

            if (pending)
                return Reject("Coding changes still require successful deterministic verification.");

            if (!ReadBool(root, "browserPostconditionPending", out bool browserPending))
                return Reject("Coding result lacks an explicit typed browser postcondition status.");
            if (browserPending)
                return Reject("Coding browser interactions have unverified DOM postconditions.");

            if (!ReadBool(root, "goalReported", out bool goalReported) || !goalReported)
                return Reject("Coding agent did not explicitly complete its goal; an exhausted or interrupted attempt cannot pass independent review.");

            // A worker that reports an accepted command should also include a
            // nonempty checkpoint session ID for later evidence/replay review.
            if (verified && (!root.TryGetProperty("checkpointSessionId", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())))
                return Reject("Coding verification claim has no checkpoint identity.");
            if (verified && string.IsNullOrWhiteSpace(taskId))
                return Reject("Coding verification claim is not bound to a Core task.");
            if (verified && !string.Equals(root.GetProperty("checkpointSessionId").GetString(),
                    taskId, StringComparison.Ordinal))
                return Reject("Coding verification checkpoint belongs to another task.");

            if (verified && (!root.TryGetProperty("verifiedWorkspaceFingerprint", out JsonElement fingerprint)
                || fingerprint.ValueKind != JsonValueKind.String
                || !IsSha256(fingerprint.GetString())))
                return Reject("Coding verification lacks a valid Git workspace state fingerprint.");

            return new Decision(true,
                verified
                    ? "Coding worker reports accepted command evidence; independent review is still required."
                    : "Coding worker reports no outstanding verification; independent review is still required.");
        }
        catch (JsonException)
        {
            return Reject("Coding result metadata is not valid JSON.");
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char character in value)
            if (!((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))
                return false;
        return true;
    }

    private static Decision Reject(string reason) => new(false, reason);

    private static bool ReadBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out JsonElement field)) return false;
        if (field.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (field.ValueKind == JsonValueKind.False) return true;
        return false;
    }
}
