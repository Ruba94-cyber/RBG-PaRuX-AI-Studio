using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// A typed UI-action response is NOT arbitrary browser-evaluation evidence.
    /// Only a recognized {ok:true} contract permits an action-delivered claim;
    /// neither typed content, page text, selectors, exception messages nor
    /// model-supplied error descriptions are reflected to the agent.
    /// Delivery is not proof that the user's desired postcondition holds.
    /// </summary>
    public static class BrowserDomActionResultPolicy
    {
        public sealed record Decision(bool Success, string Observation, string? Error);

        public static Decision Classify(JsonElement cdp)
        {
            const string fallback = "Browser UI action failed; use a fresh DOM inspection to diagnose it.";
            if (!BrowserDevToolsEvaluationPolicy.HasUsableResult(cdp)
                || !cdp.TryGetProperty("result", out JsonElement result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("ok", out JsonElement ok))
                return new Decision(false, string.Empty, fallback);

            if (ok.ValueKind == JsonValueKind.True)
                return new Decision(true,
                    "Browser UI action delivered. Inspect the actual DOM/UI before claiming the intended result.",
                    null);

            if (ok.ValueKind != JsonValueKind.False)
                return new Decision(false, string.Empty, fallback);

            if (value.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
            {
                string? reason = error.GetString();
                if (reason is "selector not found"
                    or "target disabled or inert"
                    or "target disabled, inert or read-only"
                    or "sensitive field requires user-owned input"
                    or "unsupported input type"
                    or "input value setter unavailable"
                    or "textarea value setter unavailable"
                    or "select option unavailable"
                    or "target is not an editable form control"
                    or "browser rejected input value"
                    or "page did not retain input value")
                    return new Decision(false, string.Empty, "Browser UI action denied: " + reason + ".");
            }

            return new Decision(false, string.Empty, fallback);
        }
    }
}
