using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Parses a Grill Me assistant turn's raw content into selectable options. The server forces
    /// this exact JSON shape via a strict structured-output schema, so parsing is expected to
    /// succeed every time content is complete - this stays defensive (tolerates surrounding prose
    /// or a code fence, mirrors GroupChatService.ParseVerdict's convention) rather than assuming
    /// the wire never disagrees with the schema.
    /// </summary>
    public static class GrillMeTurnParser
    {
        /// <param name="turn">The parsed turn when this returns true. [NotNullWhen(true)] is
        /// what makes that contract real for callers rather than merely conventional: both call
        /// sites use the result immediately inside the `if`, and without the attribute each would
        /// be dereferencing something the compiler believes may be null.</param>
        public static bool TryParse(
            string content,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GrillMeTurn? turn)
        {
            turn = null;
            if (string.IsNullOrWhiteSpace(content)) return false;

            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return false;

            JsonNode? node;
            try { node = JsonNode.Parse(content.Substring(start, end - start + 1)); }
            catch (JsonException) { return false; }
            if (!(node is JsonObject obj)) return false;

            string question = obj["question"] is JsonValue qv && qv.TryGetValue(out string? q) ? q!.Trim() : string.Empty;
            if (question.Length == 0) return false;

            var options = new List<string>();
            if (obj["options"] is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                        options.Add(text.Trim());
                }
            }
            // Fewer than two real choices isn't a multiple-choice question - fall back to
            // rendering it as ordinary prose instead of a picker with nothing meaningful to pick.
            if (options.Count < 2) return false;

            string feedback = obj["feedback"] is JsonValue fv && fv.TryGetValue(out string? f) ? f!.Trim() : string.Empty;
            bool allowOther = obj["allowOther"] is JsonValue av && av.TryGetValue(out bool other) && other;

            turn = new GrillMeTurn { Feedback = feedback, Question = question, Options = options, AllowOther = allowOther };
            return true;
        }
    }
}
