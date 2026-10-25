using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// WinApp CLI returning exit code zero means command delivery, not successful
    /// verification. Its --json envelope is checked independently, especially
    /// wait-for, whose authoritative evidence is {found:true,timedOut:false}.
    /// Does not infer that an invoked control achieved the user's desired goal.
    /// </summary>
    public static class WindowsUiActionResultPolicy
    {
        public static bool IsDelivered(string? action, string? response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (root.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind != JsonValueKind.Null) return false;
                if (root.TryGetProperty("success", out JsonElement success)
                    && success.ValueKind == JsonValueKind.False) return false;
                if (root.TryGetProperty("ok", out JsonElement ok)
                    && ok.ValueKind == JsonValueKind.False) return false;

                if (action == "ui_wait_for")
                    return root.TryGetProperty("found", out JsonElement found)
                        && found.ValueKind == JsonValueKind.True
                        && (!root.TryGetProperty("timedOut", out JsonElement timedOut)
                            || timedOut.ValueKind == JsonValueKind.False);
                return (action is "ui_invoke" or "ui_set_value") && HasPositiveDeliveryEvidence(root);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// The positive fields that show an invoke or set-value was performed: <c>success</c> set to true, or a
        /// <c>performedAction</c> naming the pattern that ran. Merely lacking an error is not evidence. Before this,
        /// an empty object with exit code zero was reported as a delivered action (CU-12).
        /// </summary>
        private static bool HasPositiveDeliveryEvidence(JsonElement root) =>
            (root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("performedAction", out JsonElement performed)
                && performed.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(performed.GetString()));
    }
}
