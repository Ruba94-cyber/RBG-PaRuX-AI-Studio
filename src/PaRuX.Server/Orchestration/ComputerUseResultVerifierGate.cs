using System;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Core-owned preflight for desktop Computer Use results. It prevents a model
/// verifier from upgrading missing, stale or contradictory GUI worker metadata
/// into a PASS. This checks a bounded worker-produced receipt, NOT independent
/// attestation of UIA calls or proof of the entire user goal.
/// </summary>
internal static class ComputerUseResultVerifierGate
{
    private const int MaxResultCharacters = 128_000;
    internal sealed record Decision(bool Allowed, string Reason);

    internal static Decision Evaluate(string? role, string? originalGoal, string? resultJson)
    {
        if (!string.Equals(role, "computer-use", StringComparison.OrdinalIgnoreCase))
            return new Decision(true, "Not a Computer Use task.");
        if (string.IsNullOrWhiteSpace(resultJson) || resultJson.Length > MaxResultCharacters)
            return Reject("Computer Use result receipt is missing or unbounded.");

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(resultJson);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !ReadBool(root, "goalReported", out bool goalReported) || !goalReported
                || !ReadBool(root, "guiTestProofRequired", out bool workerRequired)
                || !ReadBool(root, "guiTestProofSatisfied", out bool workerSatisfied)
                || !ReadBool(root, "guiFinalReadbackVerified", out bool workerReadback)
                || !ReadInt(root, "uiAssertionsPassed", 0, 100, out int workerAssertions)
                || !ReadInt(root, "stepsTaken", 0, 15, out int workerSteps)
                || !root.TryGetProperty("guiRunReceipt", out JsonElement receipt)
                || receipt.ValueKind != JsonValueKind.Object)
                return Reject("Computer Use result lacks the typed completion and GUI receipt.");

            bool required = ComputerUseGuiIntentPolicy.RequiresAssertion(originalGoal);
            if (!ReadInt(receipt, "schemaVersion", 1, 1, out _)
                || !ReadString(receipt, "outcome", "ready_for_independent_verifier")
                || !ReadString(receipt, "reasonCode", "handoff_only")
                || !ReadString(receipt, "assurance", "executor-reported-not-independently-attested")
                || !ReadBool(receipt, "guiTestRequired", out bool receiptRequired)
                || !ReadBool(receipt, "goalReported", out bool receiptGoal) || !receiptGoal
                || !ReadBool(receipt, "readyForIndependentVerifier", out bool ready) || !ready
                || !ReadBool(receipt, "finalControlReadbackVerified", out bool receiptReadback)
                || !ReadInt(receipt, "actionsAttempted", 0, 100, out int attempted)
                || !ReadInt(receipt, "actionsDelivered", 0, 100, out int delivered)
                || !ReadInt(receipt, "uiAssertionsPassed", 0, 100, out int passed)
                || !ReadInt(receipt, "uiAssertionsFailed", 0, 100, out _)
                || !ReadInt(receipt, "finalReadbackChecks", 0, 100, out int checks)
                || !ReadInt(receipt, "finalReadbackFailures", 0, 100, out int failures)
                || !ReadInt(receipt, "stepsTaken", 0, 15, out int receiptSteps))
                return Reject("Computer Use receipt is missing required, valid typed evidence.");

            if (receiptRequired != required || workerRequired != required)
                return Reject("GUI testing requirement differs from the original user goal.");
            if (delivered > attempted || attempted > workerSteps || failures > checks
                || passed > attempted || checks > attempted
                || receiptSteps != workerSteps || workerAssertions != passed
                || receiptReadback != workerReadback || attempted == 0)
                return Reject("Computer Use receipt counters or native readback flags disagree.");

            if (required && (!workerSatisfied || !workerReadback
                || passed == 0 || checks == 0))
                return Reject("GUI test has no consistent fresh exact-control verification.");
            if (!required && (workerSatisfied || workerReadback))
                return Reject("Ordinary Computer Use result falsely claims GUI test evidence.");

            return new Decision(true,
                "Bounded Computer Use execution receipt admitted for independent goal verification; no PASS implied.");
        }
        catch (JsonException)
        {
            return Reject("Computer Use result receipt is not valid JSON.");
        }
    }

    private static Decision Reject(string reason) => new(false, reason);

    private static bool ReadString(JsonElement item, string name, string expected) =>
        item.TryGetProperty(name, out JsonElement field)
        && field.ValueKind == JsonValueKind.String
        && string.Equals(field.GetString(), expected, StringComparison.Ordinal);

    private static bool ReadBool(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out JsonElement field)
            || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = field.GetBoolean();
        return true;
    }

    private static bool ReadInt(JsonElement item, string name, int min, int max, out int result)
    {
        result = 0;
        return item.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt32(out result)
            && result >= min && result <= max;
    }
}
