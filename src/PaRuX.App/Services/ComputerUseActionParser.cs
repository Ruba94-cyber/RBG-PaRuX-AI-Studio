using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaRuX.Services
{
    /// <summary>One action the Computer Use agent asked PaRuX to perform on the real desktop.</summary>
    internal sealed class ComputerUseAction
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("x")] public int? X { get; set; }
        [JsonPropertyName("y")] public int? Y { get; set; }
        [JsonPropertyName("button")] public string? Button { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("combo")] public string? Combo { get; set; }
        [JsonPropertyName("lines")] public int? Lines { get; set; }
        [JsonPropertyName("selector")] public string? Selector { get; set; }
        [JsonPropertyName("property")] public string? Property { get; set; }
        [JsonPropertyName("expected")] public string? Expected { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("ms")] public int? Ms { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }

    /// <summary>
    /// The virtual-desktop rectangle, in physical pixels, that pixel actions must land in. It is read from the same
    /// capture the model was shown. An empty rectangle contains nothing, so an unreadable screen fails closed.
    /// </summary>
    internal readonly record struct ScreenBounds(int Left, int Top, int Width, int Height)
    {
        internal bool Contains(int x, int y) =>
            Width > 0 && Height > 0
            && x >= Left && (long)x < (long)Left + Width
            && y >= Top && (long)y < (long)Top + Height;
    }

    /// <summary>
    /// The Computer Use action grammar and its parser, deliberately separated from the executor so
    /// it carries no dependency on the desktop automation, chat or server-bootstrap services. That
    /// keeps the part where a malformed or unsupported action decides whether PaRuX touches the
    /// user's machine directly unit-testable, without linking real-machine state into a test run.
    /// </summary>
    internal static class ComputerUseActionParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The action grammar as a JSON Schema, so the model is CONSTRAINED to emit a valid action
        /// rather than merely asked to. Before this, the loop relied on prompt discipline plus a
        /// free-text brace scan; a weaker local model that wrapped its reply in prose or emitted
        /// reasoning containing braces produced an unparseable action and the run stopped at step
        /// one. Core drops this hint when the active provider cannot prove JSON-Schema constrained
        /// generation, and <see cref="TryParse"/> stays the fallback.
        /// </summary>
        internal const string ActionJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["action"],
          "properties": {
            "action": {
              "type": "string",
              "enum": ["click","double_click","right_click","move","type","key","scroll","focus_window","wait","ui_invoke","ui_set_value","ui_wait_for","ui_assert","done"]
            },
            "x": { "type": "integer" },
            "y": { "type": "integer" },
            "button": { "type": "string", "enum": ["left","right","middle"] },
            "text": { "type": "string" },
            "combo": { "type": "string" },
            "lines": { "type": "integer" },
            "selector": { "type": "string" },
            "property": { "type": "string", "enum": ["name","value","text"] },
            "expected": { "type": "string", "maxLength": 512 },
            "title": { "type": "string" },
            "ms": { "type": "integer" },
            "summary": { "type": "string" }
          }
        }
        """;

        /// <summary>
        /// The canonical spelling of an action name the grammar defines, or null for any other name. Surrounding whitespace and
        /// letter case are ignored, as the gates always ignored them; no other variation is accepted. Every stage reads the name
        /// this returns (see <see cref="TryCanonicalize"/>), so no two stages can match one reply by different rules.
        /// </summary>
        internal static string? CanonicalAction(string? action)
        {
            string name = (action ?? string.Empty).Trim().ToLowerInvariant();
            return name is "click" or "double_click" or "right_click" or "move" or "type" or "key" or "scroll"
                or "focus_window" or "wait" or "ui_invoke" or "ui_set_value" or "ui_wait_for" or "ui_assert" or "done"
                ? name
                : null;
        }

        /// <summary>
        /// Writes the canonical name into the action and returns true. For a name the grammar does not define it returns false and
        /// leaves the action untouched, so the refusal can quote what the model wrote. The loop calls this once per reply.
        /// </summary>
        internal static bool TryCanonicalize(ComputerUseAction action)
        {
            string? name = CanonicalAction(action.Action);
            if (name == null) return false;
            action.Action = name;
            return true;
        }

        /// <summary>The actions the executor can really perform. "done" is terminal and handled by the loop before the executor is
        /// reached, so it is deliberately not listed here. A name the executor does not implement used to reach its default case and
        /// do nothing while the step was logged and counted as done - acting on an assumption of success over an action that never ran.</summary>
        internal static bool IsExecutable(string? action) => CanonicalAction(action) is { } name && name != "done";

        internal static bool IsDone(string? action) =>
            string.Equals((action ?? string.Empty).Trim(), "done", StringComparison.OrdinalIgnoreCase);

        /// <summary>Longest text a single "type" action may enter. A model that emits a larger payload
        /// is refused before any keystroke is sent, instead of being typed character by character into
        /// whatever window currently has focus.</summary>
        internal const int MaxTypeTextLength = 2_000;

        /// <summary>Actions that deliver mouse input at a screen position, so they must name one.</summary>
        internal static bool IsPointerAction(string? action) =>
            (action ?? string.Empty).Trim().ToLowerInvariant() is "click" or "double_click" or "right_click" or "move" or "scroll";

        /// <summary>
        /// The mouse button a click names. A missing name means left. Any other name is refused: the old code sent
        /// every unrecognized name as a left click, so a "middle" request activated links with the wrong button.
        /// </summary>
        internal static bool TryNormalizeButton(string? button, out string name)
        {
            name = string.IsNullOrWhiteSpace(button) ? "left" : button.Trim().ToLowerInvariant();
            return name is "left" or "right" or "middle";
        }

        /// <summary>
        /// Null when the action may run; otherwise the reason it must not. A pointer action must name a position
        /// inside the screen that was captured. The old code defaulted a missing x or y to 0, which clicked the
        /// top-left pixel of the primary monitor and still passed the foreground pin. Reasons never echo the payload,
        /// so a refusal cannot leak typed content or coordinates into the task log or the chat.
        /// </summary>
        internal static string? ValidateExecutable(ComputerUseAction action, ScreenBounds screen)
        {
            // A name the grammar does not define is refused here too, so no caller can run one by skipping the loop's gate.
            string? name = CanonicalAction(action.Action);
            if (name == null)
                return "The action is not one PaRuX can perform. Nothing was sent.";
            string kind = name;
            if (kind == "ui_assert" && !WindowsUiAssertionPolicy.IsValidRequest(
                    action.Selector, action.Property, action.Expected))
                return "The UI assertion requires a valid observed selector, property and bounded expected value. Nothing was verified.";
            if (kind == "type")
            {
                int length = action.Text?.Length ?? 0;
                return length <= MaxTypeTextLength
                    ? null
                    : "The typed text is " + length + " characters, above the " + MaxTypeTextLength
                      + "-character limit. Nothing was typed.";
            }
            if (IsPointerAction(kind))
            {
                string label = kind.Replace('_', ' ');
                if (!action.X.HasValue || !action.Y.HasValue)
                    return "The " + label + " names no x/y position on the screen. Nothing was sent.";
                if (!screen.Contains(action.X.Value, action.Y.Value))
                    return "The " + label + " position is outside the captured screen. Nothing was sent.";
            }
            // An empty title matches every window, so a focus_window with no title would focus whichever window is listed first.
            if (kind == "focus_window" && string.IsNullOrWhiteSpace(action.Title))
                return "The focus_window action names no window title, so nothing was focused.";
            if ((kind == "click" || kind == "double_click") && !TryNormalizeButton(action.Button, out _))
                return "The mouse button is not one PaRuX supports (left, right or middle). Nothing was sent.";
            return null;
        }

        /// <summary>
        /// Fallback parser for providers that cannot honor the JSON Schema. Scans for the first
        /// BALANCED object rather than first-brace-to-last-brace: with reasoning enabled a reply
        /// can contain several brace-bearing fragments, and the old span joined the start of one to
        /// the end of another, turning two individually valid objects into invalid JSON. String
        /// literals are tracked so a brace inside a "text" value cannot end the object early.
        /// </summary>
        internal static ComputerUseAction? TryParse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
            {
                int depth = 0;
                bool inString = false, escaped = false;
                for (int index = start; index < text.Length; index++)
                {
                    char c = text[index];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') { inString = true; continue; }
                    if (c == '{') depth++;
                    else if (c == '}' && --depth == 0)
                    {
                        string candidate = text.Substring(start, index - start + 1);
                        ComputerUseAction? parsed;
                        try { parsed = JsonSerializer.Deserialize<ComputerUseAction>(candidate, JsonOptions); }
                        catch (JsonException) { parsed = null; }
                        // An object carrying no action (a stray reasoning or tool fragment) is not
                        // the answer; keep scanning rather than reporting a parse failure.
                        if (!string.IsNullOrWhiteSpace(parsed?.Action)) return parsed;
                        break;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Null when a focus_window action may report success. A title that matches no open window, or a window Windows would not
        /// bring forward, is not a success: nothing was focused, so the run must stop rather than act on whatever window is in front.
        /// </summary>
        internal static string? FocusRefusal(bool windowFound, bool windowFocused) =>
            !windowFound ? "No open window matches that title, so nothing was focused."
            : windowFocused ? null
            : "Windows did not bring the window to the front, so nothing was focused.";

        internal static string Describe(ComputerUseAction a) => (a.Action ?? "?").Trim().ToLowerInvariant() switch
        {
            "click" => "click (" + a.X + "," + a.Y + ")" + (string.Equals(a.Button, "right", StringComparison.OrdinalIgnoreCase) ? " right button" : string.Empty),
            "double_click" => "double-click (" + a.X + "," + a.Y + ")",
            "right_click" => "right-click (" + a.X + "," + a.Y + ")",
            "move" => "move mouse to (" + a.X + "," + a.Y + ")",
            // Only the length is ever logged. The text may be a password or other secret typed into a
            // window the user is looking at, and this string reaches the task log and visual notes.
            "type" => "type (text: " + (a.Text?.Length ?? 0) + " chars, redacted)",
            "key" => "press " + a.Combo,
            "scroll" => "scroll " + a.Lines + " lines at (" + a.X + "," + a.Y + ")",
            "focus_window" => "focus window \"" + a.Title + "\"",
            "wait" => "wait " + a.Ms + " ms",
            "ui_invoke" => "invoke UIA control " + Truncate(a.Selector),
            "ui_set_value" => "set UIA value on " + Truncate(a.Selector),
            "ui_wait_for" => "wait for UIA control " + Truncate(a.Selector),
            "ui_assert" => "verify UIA " + Truncate(a.Selector) + "." + Truncate(a.Property) + " equals expected value (redacted)",
            "done" => "done — " + Truncate(a.Summary),
            _ => "unsupported action \"" + Truncate(a.Action) + "\""
        };

        internal static string Truncate(string? s) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 80 ? s : s.Substring(0, 80) + "…");

        /// <summary>
        /// Identity of an action for detecting a model that repeats itself. It is built from every
        /// field, not from <see cref="Describe"/>, because the description is redacted and truncated:
        /// two different texts of equal length must not look like one repeated action. It is compared
        /// in memory only and is never logged or persisted.
        /// </summary>
        internal static string RepeatKey(ComputerUseAction a) => string.Join("\u001f",
            (a.Action ?? string.Empty).Trim().ToLowerInvariant(),
            a.X, a.Y, a.Button, a.Text, a.Combo, a.Lines, a.Selector, a.Title, a.Ms, a.Summary,
            a.Property, a.Expected);
    }
}
