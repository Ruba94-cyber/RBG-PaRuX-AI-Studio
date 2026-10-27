using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure phrase detection (English and Italian) for "just cut the subject out": a request that only removes the background and
    /// keeps the picture otherwise as it is. Anything that also puts something new behind the subject ("replace the background with a
    /// beach", "rimuovi lo sfondo e metti il mare") is a creative edit and stays with the diffusion editor.
    /// </summary>
    public static class CutoutPhrasing
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private const string Determiner =
            @"(?:(?:the|this|that|its|my|his|her|their|il|lo|la|l['’]|questo|questa|quello|quella|suo|sua|mio|mia)\s*)*";

        private const string Noun =
            @"(?:(?:image|photo|picture|pic|immagine|foto)['’]?s?\s+)?"
          + @"(?:(?:white|black|plain|solid|blurry|blurred|messy|busy|old|current|original|bianco|bianca|nero|nera|sfocato|attuale|originale)\s+)?"
          + @"(?:background|backdrop|bg|sfondo|fondale|fondo)\b";

        private static readonly Regex RemoveBackgroundRx = new Regex(
            @"\b(?:remove|erase|delete|get\s+rid\s+of|take\s+(?:out|off|away)|strip(?:\s+out)?|knock\s+out|clear|"
          + @"rimuovi|rimuovere|rimuovimi|togli|togliere|toglimi|elimina|eliminare|eliminami|cancella|cancellare|levami|leva|via)\s+"
          + Determiner + Noun
          + @"|\b(?:background|bg)[\s-]*(?:removal|remover|removing)\b"
          + @"|\b(?:remove|rimuovi)[\s-]?bg\b"
          + @"|\b(?:cut|cutting)[\s-]*out\s+(?:the\s+|this\s+|that\s+|my\s+)?(?:subject|person|people|product|object|item|figure|character|animal|dog|cat|logo|it|him|her)\b"
          + @"|\bcut-?out\b"
          + @"|\b(?:ritaglia|ritagliare|ritagliami|isola|isolare|isolami|estrai|estrarre)\s+(?:il\s+|lo\s+|la\s+|l['’]\s*|un[a']?\s+)?"
          + @"(?:soggetto|persona|prodotto|oggetto|figura|personaggio|animale|cane|gatto|logo|immagine|foto)\b"
          + @"|\b(?:make|turn|set|render|rendi|rendere|fai|imposta)\s+" + Determiner + @"(?:background|sfondo|bg)\s+"
          + @"(?:transparent|trasparente)\b"
          + @"|\b(?:senza|without)\s+(?:lo\s+|il\s+|the\s+|a\s+)?(?:sfondo|background)\b(?!\s+(?:music|noise|sound|musica|rumore|audio))", Opts);

        // Something new goes behind the subject (or the picture is changed further): that is an edit, not a cutout.
        private static readonly Regex ReplacementRx = new Regex(
            @"\b(?:replace|replacing|swap|substitute|add|adding|put|place|insert|set\s+it\s+(?:on|against|in)|against|new|different|instead|"
          + @"sostituisci|sostituire|aggiungi|aggiungere|metti|mettere|inserisci|inserire|nuovo|nuova|diverso|diversa|invece|"
          + @"gradient|sfumat\w+|"
          + @"con\s+(?:un|una|uno|il|lo|la|l['’])|with\s+(?:a|an|the)\b)", Opts);

        private const string Colors =
            @"(?:white|black|blue|red|green|yellow|pink|orange|purple|gr[ae]y|colou?r|bianco|bianca|nero|nera|blu|rosso|rossa|verde|giallo|gialla|grigio|grigia|colore)";

        // "...and make it white", "...e rendilo blu": a colour that is asked for, as opposed to "remove the white background".
        private static readonly Regex RequestedColorRx = new Regex(
            @"\b(?:and|then|e|poi|with|con|make|turn|set|rendi\w*|fai|in|on|su)\b\W+(?:\w+\W+){0,3}?" + Colors + @"\b", Opts);

        private static readonly Regex ThenRx = new Regex(@"\b(?:and\s+then|then|afterwards|poi|e\s+poi|dopo)\b", Opts);
        private static readonly Regex FollowUpWorkRx = new Regex(
            @"\b(?:animate|anima|video|upscale|ingrandisci|3d|edit|modifica|generate|genera|crea|create|draw|disegna)\w*", Opts);

        /// <summary>True when the message asks only to remove the background (cut the subject out), so the cutout engine, not a diffusion edit, should run.</summary>
        public static bool IsBackgroundRemoval(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (MediaPhrasing.IsNotARequest(t)) return false;
            Match match = RemoveBackgroundRx.Match(t);
            if (!match.Success) return false;
            // The word "transparent" is itself the request ("make the background transparent"), so it is ignored below.
            string rest = Regex.Replace(t, @"\b(?:transparent|trasparente)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (ReplacementRx.IsMatch(rest)) return false;
            string tail = t[(match.Index + match.Length)..];
            if (RequestedColorRx.IsMatch(tail)) return false;
            if (ThenRx.IsMatch(rest) && FollowUpWorkRx.IsMatch(rest)) return false;
            return true;
        }
    }
}
