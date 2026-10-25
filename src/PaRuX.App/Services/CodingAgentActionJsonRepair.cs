using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services
{
    public static class CodingAgentActionJsonRepair
    {
        public static bool TryRepair(string? response, out string json, out string error)
        {
            json = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                error = "The model returned no action. Return one JSON object with an action field.";
                return false;
            }

            if (!TryExtractSingleObject(response, out string candidate, out error)) return false;
            if (IsActionObject(candidate, out string candidateError))
            {
                json = candidate;
                return true;
            }
            if (candidateError.StartsWith("Duplicate JSON property", StringComparison.Ordinal)
                || candidateError.StartsWith("The JSON root", StringComparison.Ordinal))
            {
                error = candidateError;
                return false;
            }

            string repairedError = string.Empty;
            if (!TryNormalizeNearJson(candidate, out string repaired)
                || !IsActionObject(repaired, out repairedError))
            {
                error = string.IsNullOrWhiteSpace(repairedError)
                    ? "Could not safely parse one action object. Quote every key and string with double quotes, "
                        + "remove trailing commas, and return exactly one object containing a valid action."
                    : repairedError;
                return false;
            }

            json = repaired;
            return true;
        }

        private static bool TryExtractSingleObject(string response, out string candidate, out string error)
        {
            candidate = string.Empty;
            error = string.Empty;
            int depth = 0;
            int start = -1;
            char quote = '\0';
            bool escaped = false;
            var objects = new List<string>(2);

            for (int index = 0; index < response.Length; index++)
            {
                char current = response[index];
                if (depth == 0)
                {
                    if (current == '}')
                    {
                        error = "Found a closing brace without an action object; return one JSON object.";
                        return false;
                    }
                    if (current == '{')
                    {
                        start = index;
                        depth = 1;
                        quote = '\0';
                        escaped = false;
                    }
                    continue;
                }

                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == quote) quote = '\0';
                    continue;
                }

                if (current is '"' or '\'')
                {
                    quote = current;
                    continue;
                }
                if (current == '{') depth++;
                else if (current == '}' && --depth == 0)
                {
                    objects.Add(response.Substring(start, index - start + 1));
                    if (objects.Count > 1)
                    {
                        error = "Found multiple JSON objects. Return exactly one coding action object.";
                        return false;
                    }
                }
            }

            if (depth != 0)
            {
                error = "The action object is incomplete. Return one complete JSON object.";
                return false;
            }
            if (objects.Count == 0)
            {
                error = "No JSON object was found. Return one object containing an action field.";
                return false;
            }

            candidate = objects[0];
            return true;
        }

        private static bool IsActionObject(string json, out string error)
        {
            error = string.Empty;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "The JSON root must be one object containing an unambiguous action.";
                    return false;
                }
                if (ContainsDuplicatePropertyNames(document.RootElement))
                {
                    error = "Duplicate JSON property names make the action ambiguous. Remove duplicates and return one action object.";
                    return false;
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool ContainsDuplicatePropertyNames(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                // JsonSerializerDefaults.Web binds DTO property names case-insensitively.
                // Reject those collisions before deserialization rather than letting casing
                // choose which action/path/command value wins.
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || ContainsDuplicatePropertyNames(property.Value))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                    if (ContainsDuplicatePropertyNames(item)) return true;
            }
            return false;
        }

        private static bool TryNormalizeNearJson(string candidate, out string json)
        {
            json = string.Empty;
            var output = new StringBuilder(candidate.Length + 16);

            for (int index = 0; index < candidate.Length; index++)
            {
                char current = candidate[index];
                if (current == '"')
                {
                    if (!TryAppendDoubleQuotedString(candidate, ref index, output)) return false;
                    continue;
                }
                if (current == '\'')
                {
                    if (!TryAppendSingleQuotedString(candidate, ref index, output)) return false;
                    continue;
                }
                if (IsIdentifierStart(current) && IsPossiblePropertyKeyPosition(candidate, index))
                {
                    int end = index + 1;
                    while (end < candidate.Length && IsIdentifierPart(candidate[end])) end++;
                    int next = end;
                    while (next < candidate.Length && char.IsWhiteSpace(candidate[next])) next++;
                    if (next < candidate.Length && candidate[next] == ':')
                    {
                        output.Append('"').Append(candidate, index, end - index).Append('"');
                        index = end - 1;
                        continue;
                    }
                }
                if (current == ',')
                {
                    int next = index + 1;
                    while (next < candidate.Length && char.IsWhiteSpace(candidate[next])) next++;
                    if (next < candidate.Length && candidate[next] is '}' or ']') continue;
                }
                output.Append(current);
            }

            json = output.ToString();
            return true;
        }

        private static bool TryAppendDoubleQuotedString(string source, ref int index, StringBuilder output)
        {
            output.Append('"');
            bool escaped = false;
            for (int cursor = index + 1; cursor < source.Length; cursor++)
            {
                char current = source[cursor];
                output.Append(current);
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"')
                {
                    index = cursor;
                    return true;
                }
            }
            return false;
        }

        private static bool TryAppendSingleQuotedString(string source, ref int index, StringBuilder output)
        {
            output.Append('"');
            for (int cursor = index + 1; cursor < source.Length; cursor++)
            {
                char current = source[cursor];
                if (current == '\'')
                {
                    output.Append('"');
                    index = cursor;
                    return true;
                }
                if (current == '\\')
                {
                    if (++cursor >= source.Length) return false;
                    char escaped = source[cursor];
                    if (escaped == '\'') output.Append('\'');
                    else if (escaped == '"') output.Append("\\\"");
                    else output.Append('\\').Append(escaped);
                    continue;
                }
                if (current == '"') output.Append("\\\"");
                else output.Append(current);
            }
            return false;
        }

        private static bool IsPossiblePropertyKeyPosition(string source, int index)
        {
            for (int cursor = index - 1; cursor >= 0; cursor--)
            {
                if (char.IsWhiteSpace(source[cursor])) continue;
                return source[cursor] is '{' or ',';
            }
            return false;
        }

        private static bool IsIdentifierStart(char value) =>
            value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

        private static bool IsIdentifierPart(char value) =>
            IsIdentifierStart(value) || value is >= '0' and <= '9';
    }
}
