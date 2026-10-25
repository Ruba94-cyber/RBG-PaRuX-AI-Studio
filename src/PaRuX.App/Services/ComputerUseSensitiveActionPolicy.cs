using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>Classifies an action before it can reach either Computer Use executor.</summary>
    internal static class ComputerUseSensitiveActionPolicy
    {
        internal enum Disposition
        {
            Allow,
            ConfirmRequired,
            Deny
        }

        internal readonly record struct Decision(Disposition Disposition, string? Reason)
        {
            public bool MayExecute => Disposition == Disposition.Allow;
        }

        private const string SensitiveActionReason =
            "The target may send data, spend money, delete content, or change permissions.";
        private const string PasswordTargetReason =
            "Computer Use does not interact with a password-like target.";
        private const string UnknownActionReason =
            "The action kind is not recognized.";
        private const string TargetUnavailableReason =
            "The action target has no verified UI Automation label or role.";

        /// <summary>
        /// Resolves the semantic label of exactly one observed selector. Model supplied titles
        /// and selectors are not semantic evidence; the returned text comes only from the
        /// foreground-scoped UI Automation tree captured for this action.
        /// </summary>
        internal static string? ResolveObservedTargetLabelOrRole(string? tree, string? selector)
        {
            if (string.IsNullOrWhiteSpace(tree) || tree.Length > 8_000 || string.IsNullOrWhiteSpace(selector))
                return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(tree);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("windows", out JsonElement windows)
                    || windows.ValueKind != JsonValueKind.Array)
                    return null;

                string? match = null;
                int matchCount = 0;
                foreach (JsonElement window in windows.EnumerateArray())
                {
                    if (window.ValueKind != JsonValueKind.Object
                        || !window.TryGetProperty("elements", out JsonElement elements)
                        || elements.ValueKind != JsonValueKind.Array)
                        continue;
                    if (!FindTarget(elements, selector, ref match, ref matchCount))
                        return null;
                }

                return matchCount == 1 ? match : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static Decision Evaluate(
            string? actionKind,
            string? targetLabelOrRole,
            string? typedText,
            string? appName)
        {
            // Typed content can contain private data. It is intentionally not inspected or included in any reason.
            _ = typedText;

            List<string> actionWords = Tokenize(actionKind);
            if (actionWords.Count == 0)
                return new Decision(Disposition.Deny, UnknownActionReason);

            if (HasSensitiveActionLabel(actionWords))
                return new Decision(Disposition.ConfirmRequired, SensitiveActionReason);

            string kind = string.Join("_", actionWords);
            bool writesText = kind is "type" or "ui_set_value";
            if ((CanActivateTarget(kind) && IsPasswordLikeTarget(targetLabelOrRole))
                || (writesText && IsPasswordLikeTarget(appName)))
                return new Decision(Disposition.Deny, PasswordTargetReason);

            if (!IsKnownAction(kind))
                return new Decision(Disposition.Deny, UnknownActionReason);

            if (CanActivateTarget(kind)
                && (HasSensitiveActionLabel(targetLabelOrRole) || HasSensitiveActionLabel(appName)))
                return new Decision(Disposition.ConfirmRequired, SensitiveActionReason);

            if (CanActivateTarget(kind) && string.IsNullOrWhiteSpace(targetLabelOrRole))
                return new Decision(Disposition.Deny, TargetUnavailableReason);

            return new Decision(Disposition.Allow, null);
        }

        internal static string RefusalMessage(Decision decision) => decision.Disposition switch
        {
            Disposition.ConfirmRequired => (decision.Reason ?? SensitiveActionReason)
                + " Per-action confirmation is unavailable, so nothing was executed.",
            Disposition.Deny => (decision.Reason ?? UnknownActionReason) + " Nothing was executed.",
            _ => string.Empty
        };

        private static bool IsKnownAction(string kind) => kind is
            "click" or "double_click" or "right_click" or "move" or "type" or "key" or "scroll"
            or "focus_window" or "wait" or "ui_invoke" or "ui_set_value" or "ui_wait_for" or "done";

        private static bool CanActivateTarget(string kind) => kind is
            "click" or "double_click" or "right_click" or "key" or "type" or "ui_invoke" or "ui_set_value";

        private static bool FindTarget(
            JsonElement elements, string selector, ref string? match, ref int matchCount)
        {
            foreach (JsonElement element in elements.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                if (element.TryGetProperty("selector", out JsonElement selectorValue)
                    && selectorValue.ValueKind == JsonValueKind.String
                    && string.Equals(selectorValue.GetString(), selector, System.StringComparison.Ordinal))
                {
                    matchCount++;
                    if (matchCount != 1)
                        return false;
                    match = BuildSemanticLabel(element);
                    if (match == null)
                        return false;
                }

                if (element.TryGetProperty("children", out JsonElement children)
                    && children.ValueKind == JsonValueKind.Array
                    && !FindTarget(children, selector, ref match, ref matchCount))
                    return false;
            }
            return true;
        }

        private static string? BuildSemanticLabel(JsonElement element)
        {
            var parts = new List<string>(4);
            AddStringProperty(element, "name", parts);
            AddStringProperty(element, "type", parts);
            AddStringProperty(element, "controlType", parts);
            AddStringProperty(element, "role", parts);
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private static void AddStringProperty(JsonElement element, string propertyName, List<string> parts)
        {
            if (element.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text
                && !string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        private static bool IsPasswordLikeTarget(string? value)
        {
            List<string> words = Tokenize(value);
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i] is "password" or "passcode" or "passphrase" or "pin" or "otp"
                    or "credential" or "credentials" or "secret" or "token")
                    return true;

                if (words[i] is "passwort" or "contraseña" or "contrasena" or "senha")
                    return true;

                if (IsPhraseAt(words, i, "mot", "de", "passe")
                    || IsPhraseAt(words, i, "parola", "chiave")
                    || IsPhraseAt(words, i, "parola", "d", "ordine"))
                    return true;

                if (i + 1 < words.Count
                    && (words[i] is "security" or "verification" or "access")
                    && words[i + 1] == "code")
                    return true;

                if (i + 2 < words.Count && words[i] == "one" && words[i + 1] == "time" && words[i + 2] == "code")
                    return true;
            }
            return false;
        }

        private static bool HasSensitiveActionLabel(string? value) => HasSensitiveActionLabel(Tokenize(value));

        private static bool HasSensitiveActionLabel(IReadOnlyList<string> words)
        {
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i] is "send" or "sending" or "resend" or "submit" or "submitting" or "publish" or "post"
                    or "share" or "upload" or "buy" or "purchase" or "purchasing" or "purchases" or "pay" or "paying"
                    or "payment" or "payments" or "checkout" or "subscribe" or "transfer" or "withdraw"
                    or "delete" or "deleting" or "remove" or "removing" or "erase" or "destroy" or "uninstall"
                    or "grant" or "granting" or "revoke" or "revoking" or "allow" or "approve" or "authorize"
                    or "permission" or "permissions")
                    return true;

                if (i + 1 < words.Count
                    && ((words[i] == "confirm" && words[i + 1] is "purchase" or "order" or "payment")
                        || (words[i] == "place" && words[i + 1] == "order")))
                    return true;
            }
            return false;
        }

        private static List<string> Tokenize(string? value)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return words;

            var token = new StringBuilder();
            char previous = '\0';
            foreach (char raw in value)
            {
                // Invisible format characters (zero-width space and joiner, soft hyphen) are dropped rather
                // than treated as separators, so "Sen" + U+200B + "d" reads as "send" as it does on screen.
                if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.Format)
                    continue;

                char character = FoldLookalike(raw);
                if (char.IsLetterOrDigit(character))
                {
                    if (char.IsUpper(character) && token.Length > 0
                        && (char.IsLower(previous) || char.IsDigit(previous)))
                    {
                        words.Add(token.ToString());
                        token.Clear();
                    }
                    token.Append(char.ToLowerInvariant(character));
                }
                else if (token.Length > 0)
                {
                    words.Add(token.ToString());
                    token.Clear();
                }
                previous = character;
            }
            if (token.Length > 0) words.Add(token.ToString());
            return words;
        }

        /// <summary>
        /// Maps fullwidth ASCII and the Cyrillic and Greek capitals and letters that look like Latin
        /// letters onto those Latin letters, so a spoofed label matches the same vocabulary.
        /// </summary>
        private static char FoldLookalike(char character)
        {
            if (character is >= '！' and <= '～')
                return (char)(character - 0xFEE0);
            return Lookalikes.TryGetValue(character, out char latin) ? latin : character;
        }

        private static readonly Dictionary<char, char> Lookalikes = BuildLookalikes(
            "аaеeоoрpсcхxуyіiјjѕsԁdһh" +
            "АAВBЕEКKМMНHОOРPСCТTХXУYІIЈJЅS" +
            "ΑAΒBΕEΖZΗHΙIΚKΜMΝNΟOΡPΤTΥYΧX" +
            "οoαaιiρpκkνv");

        private static Dictionary<char, char> BuildLookalikes(string pairs)
        {
            var map = new Dictionary<char, char>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[pairs[i]] = pairs[i + 1];
            return map;
        }

        private static bool IsPhraseAt(List<string> words, int start, params string[] phrase)
        {
            if (start + phrase.Length > words.Count) return false;
            for (int i = 0; i < phrase.Length; i++)
            {
                if (words[start + i] != phrase[i]) return false;
            }
            return true;
        }
    }
}
