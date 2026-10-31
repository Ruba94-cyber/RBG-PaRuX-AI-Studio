using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Human-facing presentation of an executor-reported GUI receipt.
    /// Reads only a fixed, schema-validated numeric/enum subset of task JSON.
    /// Neither model-authored strings nor native app content reach the UI line.
    /// The server's real task status remains the only completion state.
    /// </summary>
    internal static class ComputerUseGuiReceiptPresenter
    {
        private const int MaxResultCharacters = 32_768;

        internal static string? BuildLine(
            string? role, string? resultJson,
            string? taskStatus = null, string? verificationStatus = null)
        {
            if (!string.Equals(role, "computer-use", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(resultJson)
                || resultJson.Length > MaxResultCharacters)
                return null;

            try
            {
                using JsonDocument parsed = JsonDocument.Parse(resultJson);
                JsonElement root = parsed.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("guiRunReceipt", out JsonElement receipt)
                    || receipt.ValueKind != JsonValueKind.Object
                    || !TryInt(receipt, "schemaVersion", 1, 1, out _)
                    || !TryInt(receipt, "actionsAttempted", 0, 100, out int attempts)
                    || !TryInt(receipt, "actionsDelivered", 0, 100, out int delivered)
                    || !TryInt(receipt, "uiAssertionsPassed", 0, 100, out int passed)
                    || !TryInt(receipt, "uiAssertionsFailed", 0, 100, out int failed)
                    || !TryInt(receipt, "finalReadbackChecks", 0, 100, out int checks)
                    || !TryInt(receipt, "finalReadbackFailures", 0, 100, out int failures)
                    || !TryBool(receipt, "readyForIndependentVerifier", out bool ready)
                    || !TryBool(receipt, "guiTestRequired", out bool guiTest)
                    || !TryBool(receipt, "finalControlReadbackVerified", out bool finalReadback)
                    || !TryString(receipt, "outcome", out string? outcome)
                    || !TryString(receipt, "assurance", out string? assurance)
                    || assurance != "executor-reported-not-independently-attested"
                    || delivered > attempts || failures > checks
                    || (ready && outcome != "ready_for_independent_verifier")
                    || (!ready && outcome == "ready_for_independent_verifier")
                    || (ready && guiTest && (!finalReadback || passed == 0 || checks == 0)))
                    return null;

                // Older v1 receipts may lack this additive field. Newly
                // emitted receipts show the number of DISTINCT controls, not
                // just how many times ui_assert was invoked.
                int finalControls = 0;
                bool hasFinalControls = receipt.TryGetProperty("finalDistinctControlsVerified", out _);
                if (hasFinalControls && (!TryInt(receipt, "finalDistinctControlsVerified",
                        0, 8, out finalControls)
                    || finalControls > passed
                    || (ready && guiTest && finalControls == 0)
                    || (!guiTest && finalControls != 0)))
                    return null;

                int finalWindows = 0;
                bool hasFinalWindows = receipt.TryGetProperty("finalDistinctWindowsVerified", out _);
                if (hasFinalWindows && (!TryInt(receipt, "finalDistinctWindowsVerified",
                        0, 8, out finalWindows)
                    || finalWindows > passed
                    || (ready && guiTest && finalWindows == 0)
                    || (!guiTest && finalWindows != 0)
                    || (hasFinalControls && finalWindows > finalControls)))
                    return null;

                string status = outcome switch
                {
                    "ready_for_independent_verifier" =>
                        "GUI evidence captured; independent goal verification required",
                    "unverified" => "GUI result unverified",
                    "incomplete" => "GUI task incomplete",
                    "cancelled" => "GUI task cancelled",
                    "not_started" => "GUI task not started",
                    _ => string.Empty
                };
                if (status.Length == 0) return null;
                // The receipt alone never says PASS. A later PASS may be
                // shown only when supplied by the orchestrator's separate,
                // authoritative terminal status and verifier result.
                if (ready && string.Equals(taskStatus, "completed",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(verificationStatus, "pass",
                        StringComparison.OrdinalIgnoreCase))
                    status = "GUI evidence recorded; goal verifier reports PASS";
                else if (ready && string.Equals(taskStatus, "failed",
                        StringComparison.OrdinalIgnoreCase))
                    status = "GUI evidence recorded; goal verification did not pass";
                if (!guiTest)
                    status = ready ? "Computer Use reported completion; goal verification required"
                        : "Computer Use did not complete";

                return status + " · UIA " + passed + " passed, " + failed + " failed"
                    + (hasFinalControls && guiTest
                        ? " · distinct controls " + finalControls : string.Empty)
                    + (hasFinalWindows && guiTest
                        ? " · distinct windows " + finalWindows : string.Empty)
                    + " · final checks " + checks + " (" + failures + " failed)"
                    + " · delivered actions " + delivered + "/" + attempts;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryInt(JsonElement value, string key, int min, int max, out int result)
        {
            result = 0;
            return value.TryGetProperty(key, out JsonElement field)
                && field.ValueKind == JsonValueKind.Number
                && field.TryGetInt32(out result) && result >= min && result <= max;
        }

        private static bool TryBool(JsonElement value, string key, out bool result)
        {
            result = false;
            if (!value.TryGetProperty(key, out JsonElement field)
                || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            result = field.GetBoolean();
            return true;
        }

        private static bool TryString(JsonElement value, string key, out string? result)
        {
            result = null;
            if (!value.TryGetProperty(key, out JsonElement field)
                || field.ValueKind != JsonValueKind.String)
                return false;
            result = field.GetString();
            return result != null && result.Length <= 96;
        }
    }
}
