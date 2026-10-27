using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Natural English and Italian phrasings that ask Chat to make an image or a video ("can you make a picture of...", "mi fai una
    /// foto di...", "voglio un video di..."). A request verb followed shortly by a media noun is a request; "how do I draw a chart in
    /// matplotlib", "explain how image models work" and similar are ordinary prompts and never start a generation.
    /// </summary>
    public static class MediaPhrasing
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private const string Verbs =
            @"(?:generate|create|make|produce|render|paint|draw|design|show\s+me|i\s+want|i\s*'?d\s+like|i\s+would\s+like|i\s+need|"
          + @"genera(?:mi)?|crea(?:mi)?|fai(?:mi)?|fammi|mi\s+fai|realizza(?:mi)?|disegna(?:mi)?|dipingi|produci|mostrami|voglio|vorrei|"
          + @"ho\s+bisogno\s+di|puoi\s+(?:creare|fare|generare|disegnare|realizzare))";

        private static readonly Regex ImageNoun = new Regex(
            @"\b(?:images?|pictures?|photos?|photographs?|illustrations?|artworks?|logos?|posters?|wallpapers?|portraits?|icons?|avatars?|banners?|sketch(?:es)?|paintings?|"
          + @"immagin[ei]|fotos?|fotografi[ae]|illustrazion[ei]|loghi|logo|locandin[ae]|manifest[oi]|ritratt[oi]|icon[ae]|dipint[oi]|quadr[oi])\b", Opts);

        private static readonly Regex VideoNoun = new Regex(
            @"\b(?:videos?|clips?|films?|short\s+films?|movies?|animations?|footage|gifs?|"
          + @"filmat[oi]|cortometragg[io]|animazion[ei]|spezzone)\b", Opts);

        private static readonly Regex RequestVerb = new Regex(@"\b" + Verbs + @"\b", Opts);

        // A question about HOW to do something, or a programming context, is not a request to make a picture.
        private static readonly Regex NotARequest = new Regex(
            @"^\s*(?:how|what|why|when|which|explain|describe|summari[sz]e|spiega|come|cos['’]?\s*[eè]|cosa|perch[eé]|quando|qual[ei]|riassumi|descrivi)\b"
          + @"|\b(?:matplotlib|canvas|python|javascript|typescript|html|css|svg|function|script|library|api|pandas|opencv|pillow|codice|funzione|libreria|tutorial|"
          + @"workflow|transcript|trascrizione|recipe|ricetta)\b", Opts);

        // "Paint a sunset over the sea": painting verbs name the picture by their object, so no media noun is needed.
        private static readonly Regex PaintVerb = new Regex(
            @"^\s*(?:(?:please|can\s+you|could\s+you|puoi|per\s+favore)\s+)?(?:paint|dipingi)\s+(?:me\s+|mi\s+)?\w+", Opts);

        public enum Kind { None, Image, Video }

        public static Kind Detect(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || NotARequest.IsMatch(text)) return Kind.None;
            if (PaintVerb.IsMatch(text)) return Kind.Image;
            foreach (Match verb in RequestVerb.Matches(text))
            {
                // The noun must follow the verb closely, in the same sentence.
                int start = verb.Index + verb.Length;
                int end = Math.Min(text.Length, start + 48);
                string window = text.Substring(start, end - start);
                int stop = window.IndexOfAny(new[] { '.', '?', '!', '\n' });
                if (stop >= 0) window = window.Substring(0, stop);
                Match video = VideoNoun.Match(window);
                Match image = ImageNoun.Match(window);
                if (!video.Success && !image.Success) continue;
                if (video.Success && (!image.Success || video.Index <= image.Index)) return Kind.Video;
                return Kind.Image;
            }
            return Kind.None;
        }

        /// <summary>True when the text is a how-to, explanation or programming prompt that merely mentions drawing or images.</summary>
        public static bool IsNotARequest(string? text) => !string.IsNullOrWhiteSpace(text) && NotARequest.IsMatch(text);
    }
}
