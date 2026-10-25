using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// A DevTools Protocol Runtime.evaluate transport success is not proof that
    /// the page interaction worked. Runtime exceptions and the typed {ok:false}
    /// returned by PaRuX's browser_click/browser_type snippets are tool failures.
    /// Pure policy allows offline contract tests without launching a browser.
    /// </summary>
    public static class BrowserDevToolsEvaluationPolicy
    {
        /// <summary>
        /// CDP transport/runtime validity only. A typed DOM assertion or UI
        /// action must inspect its own {ok:false,status/error} envelope to tell
        /// a transient mismatch from a permanent denial. This helper must
        /// NEVER imply that the requested browser action succeeded.
        /// </summary>
        public static bool HasUsableResult(JsonElement response)
        {
            if (response.ValueKind != JsonValueKind.Object
                || response.TryGetProperty("exceptionDetails", out _))
                return false;

            if (!response.TryGetProperty("result", out JsonElement remote)
                || remote.ValueKind != JsonValueKind.Object)
                return false;

            if (remote.TryGetProperty("subtype", out JsonElement subtype)
                && subtype.ValueKind == JsonValueKind.String
                && subtype.GetString() == "error")
                return false;

            return true;
        }

        public static bool IsSuccessful(JsonElement response)
        {
            if (!HasUsableResult(response)) return false;
            JsonElement remote = response.GetProperty("result");

            if (remote.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.False)
                return false;

            // Primitive values, including 'false', are legitimate JavaScript
            // evaluation results. Only the explicit {ok:false} action envelope
            // and CDP exceptions signify tool failure here.
            return true;
        }
    }
}
