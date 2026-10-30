using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// An independent, read-only assertion over a fresh foreground-scoped WinApp UIA
    /// observation. This lets Computer Use test any application's accessible controls:
    /// it must observe the exact selector once and then compare the named property.
    /// Descriptive/metadata text, other windows and duplicated selectors are not proof.
    ///
    /// The expected value must never be printed to logs, task summaries or receipts.
    /// An assertion is a control-level observation, not verification of the whole goal.
    /// </summary>
    internal static class WindowsUiAssertionPolicy
    {
        internal const int MaxExpectedCharacters = 512;
        internal const int MaxTreeCharacters = 8_000;
        private const int MaxDepth = 24;

        internal enum Verdict
        {
            Verified,
            MissingTarget,
            AmbiguousTarget,
            MissingProperty,
            DifferentValue,
            InvalidRequest,
            InvalidObservation
        }

        internal static bool IsValidRequest(string? selector, string? property, string? expected) =>
            WindowsUiActionService.IsSafeSelector(selector)
            && (property is "name" or "value" or "text")
            && expected != null && expected.Length <= MaxExpectedCharacters
            && !expected.Contains('\0');

        internal static Verdict AssertExact(
            string? raw, long expectedWindow, string? selector, string? property, string? expected)
        {
            if (expectedWindow <= 0 || !IsValidRequest(selector, property, expected))
                return Verdict.InvalidRequest;
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxTreeCharacters)
                return Verdict.InvalidObservation;
            try
            {
                using JsonDocument json = JsonDocument.Parse(raw);
                JsonElement root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("windows", out JsonElement windows)
                    || windows.ValueKind != JsonValueKind.Array)
                    return Verdict.InvalidObservation;

                JsonElement matched = default;
                int matches = 0;
                foreach (JsonElement window in windows.EnumerateArray())
                {
                    if (window.ValueKind != JsonValueKind.Object
                        || !window.TryGetProperty("hwnd", out JsonElement hwnd)
                        || !MatchesWindow(hwnd, expectedWindow)
                        || !window.TryGetProperty("elements", out JsonElement elements)
                        || elements.ValueKind != JsonValueKind.Array)
                        continue;

                    if (!FindElement(elements, selector!, 0, ref matched, ref matches))
                        return Verdict.InvalidObservation;
                }
                if (matches == 0) return Verdict.MissingTarget;
                if (matches != 1) return Verdict.AmbiguousTarget;
                if (!matched.TryGetProperty(property!, out JsonElement observed)
                    || observed.ValueKind != JsonValueKind.String)
                    return Verdict.MissingProperty;
                return string.Equals(observed.GetString(), expected, StringComparison.Ordinal)
                    ? Verdict.Verified
                    : Verdict.DifferentValue;
            }
            catch (JsonException)
            {
                return Verdict.InvalidObservation;
            }
        }

        private static bool MatchesWindow(JsonElement handle, long expectedWindow) =>
            (handle.ValueKind == JsonValueKind.Number
                && handle.TryGetInt64(out long number) && number == expectedWindow)
            || (handle.ValueKind == JsonValueKind.String
                && long.TryParse(handle.GetString(), out long textHandle) && textHandle == expectedWindow);

        /// <summary>Malformed nested UIA controls refuse verification. Limit nesting
        /// so a tiny but pathological JSON tree cannot monopolize the UI thread.</summary>
        private static bool FindElement(
            JsonElement controls, string selector, int depth, ref JsonElement matched, ref int matches)
        {
            if (depth > MaxDepth || controls.ValueKind != JsonValueKind.Array)
                return false;
            foreach (JsonElement control in controls.EnumerateArray())
            {
                if (control.ValueKind != JsonValueKind.Object) return false;
                if (control.TryGetProperty("selector", out JsonElement identifier)
                    && identifier.ValueKind == JsonValueKind.String
                    && string.Equals(identifier.GetString(), selector, StringComparison.Ordinal))
                {
                    matches++;
                    matched = control;
                    if (matches > 1) return true;
                }

                if (control.TryGetProperty("children", out JsonElement children)
                    && !FindElement(children, selector, depth + 1, ref matched, ref matches))
                    return false;
            }
            return true;
        }
    }
}
