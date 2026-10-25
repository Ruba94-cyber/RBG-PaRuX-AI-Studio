using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed record PromptEnhancementResult(
        string Original, string Enhanced, bool UsedFallback, string? FallbackReason)
    {
        public bool Changed => !string.Equals(Original.Trim(), Enhanced.Trim(), StringComparison.Ordinal);

        /// <summary>Provenance details keeping BOTH prompts, for the media job record.</summary>
        public IReadOnlyDictionary<string, string> ToProvenanceDetails(bool userAccepted)
        {
            var d = new Dictionary<string, string>
            {
                ["Original prompt"] = Original,
                ["Prompt enhancement"] = UsedFallback ? "not applied (" + FallbackReason + ")" : (userAccepted ? "applied after review" : "offered, not used")
            };
            if (!UsedFallback) d["Enhanced prompt"] = Enhanced;
            return d;
        }
    }

    /// <summary>
    /// Optional, opt-in rewrite of an image/video/music prompt by the active LOCAL text model. Pure prompt building and
    /// validation here; the model call is injected so the policy (timeout, fallback, consent) is unit-testable.
    /// Never replaces the user's text on its own: callers show <see cref="PromptEnhancementResult.Enhanced"/> for review.
    /// </summary>
    public static class MediaPromptEnhancer
    {
        public const int MaxInputChars = 2_000;
        public const int MaxOutputChars = 1_200;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        public static string BuildInstruction(string kind, string prompt)
        {
            string noun = kind switch { "video" => "video-generation", "music" or "audio" => "music-generation", _ => "image-generation" };
            string extra = kind switch
            {
                "video" => " Include subject, motion, camera movement and mood.",
                "music" or "audio" => " Include genre, tempo, instruments and mood.",
                _ => " Include subject, setting, style and lighting."
            };
            string bounded = prompt.Length > MaxInputChars ? prompt.Substring(0, MaxInputChars) : prompt;
            return "Rewrite the user's request as one improved " + noun + " prompt." + extra
                + " Keep the user's intent and keep the SAME LANGUAGE as the request; do not add unrelated subjects."
                + " Output ONLY the prompt text: no preamble, no quotes, no explanation.\n\nRequest: " + bounded;
        }

        private static readonly Regex Preamble = new(
            @"^\s*(?:sure|certainly|of course|here(?:'s| is| are)[^:\n]*|improved prompt|enhanced prompt|rewritten prompt|prompt)\s*(?:[:\-]|[!.]\s)\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Strips meta text (preambles, code fences, wrapping quotes), caps length. Returns null when nothing usable remains.</summary>
        public static string? Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = raw.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                text = Regex.Replace(text, @"^```[a-zA-Z]*\s*", string.Empty);
                int end = text.IndexOf("```", StringComparison.Ordinal);
                if (end >= 0) text = text.Substring(0, end);
            }
            for (int i = 0; i < 3; i++)
            {
                string stripped = Preamble.Replace(text, string.Empty, 1);
                if (stripped == text) break;
                text = stripped;
            }
            text = text.Trim().Trim('"', '\'', '`', '“', '”').Trim();
            if (text.Length == 0) return null;
            if (text.Length > MaxOutputChars)
            {
                text = text.Substring(0, MaxOutputChars);
                int cut = text.LastIndexOfAny(new[] { ' ', ',', '.' });
                if (cut > MaxOutputChars / 2) text = text.Substring(0, cut);
                text = text.TrimEnd(' ', ',');
            }
            return text;
        }

        /// <summary>True when the rewrite plausibly kept the request's language (script heuristic: non-Latin input must stay non-Latin).</summary>
        public static bool PreservesLanguageScript(string original, string enhanced)
        {
            static double NonLatinRatio(string s)
            {
                int letters = 0, nonLatin = 0;
                foreach (char c in s)
                {
                    if (!char.IsLetter(c)) continue;
                    letters++;
                    if (c > 0x024F) nonLatin++;
                }
                return letters == 0 ? 0 : (double)nonLatin / letters;
            }
            bool origNonLatin = NonLatinRatio(original) > 0.5;
            bool enhNonLatin = NonLatinRatio(enhanced) > 0.5;
            return origNonLatin == enhNonLatin;
        }

        /// <param name="complete">Runs the active local text model once.</param>
        /// <param name="localOnly">True when only local inference is allowed.</param>
        /// <param name="modelIsLocal">True when the model that would answer is local.</param>
        /// <param name="cloudConsent">Explicit user consent for a non-local model.</param>
        public static async Task<PromptEnhancementResult> EnhanceAsync(
            string kind, string prompt,
            Func<string, CancellationToken, Task<string>> complete,
            bool localOnly, bool modelIsLocal, bool cloudConsent,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return new PromptEnhancementResult(prompt ?? string.Empty, prompt ?? string.Empty, true, "empty prompt");
            if (!modelIsLocal && (localOnly || !cloudConsent))
                return new PromptEnhancementResult(prompt, prompt, true, "no local model; cloud not permitted");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                string raw = await complete(BuildInstruction(kind, prompt), cts.Token).ConfigureAwait(false);
                string? clean = Sanitize(raw);
                if (clean == null) return new PromptEnhancementResult(prompt, prompt, true, "empty model response");
                if (!PreservesLanguageScript(prompt, clean)) return new PromptEnhancementResult(prompt, prompt, true, "language not preserved");
                return new PromptEnhancementResult(prompt, clean, false, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new PromptEnhancementResult(prompt, prompt, true, "timed out");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new PromptEnhancementResult(prompt, prompt, true, "model error: " + ex.GetType().Name);
            }
        }
    }
}
