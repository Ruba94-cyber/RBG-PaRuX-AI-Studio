using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Reads mail out of the two formats desktop clients actually leave on disk: individual RFC 5322
/// <c>.eml</c> files, and mbox files (Thunderbird's mail store, and what most clients export).
///
/// This does the unglamorous parts on purpose, because skipping them is the difference between
/// "reads mail" and "reads mail correctly":
///
/// <list type="bullet">
/// <item><b>Header unfolding.</b> A long Subject is split across lines with leading whitespace. A
/// naive line-per-header parser silently truncates it.</item>
/// <item><b>Encoded words</b> (RFC 2047). A subject with an accent arrives as
/// <c>=?UTF-8?B?...?=</c>. Without decoding, a search for "prenotazione" never matches the mail
/// that is about exactly that.</item>
/// <item><b>Transfer encodings.</b> quoted-printable and base64 bodies are gibberish undecoded.</item>
/// <item><b>multipart/alternative.</b> The text/plain part is preferred; where only HTML exists the
/// tags are stripped rather than handed to a model as markup.</item>
/// </list>
///
/// It is a reader, not a mail library: no attachments, no signature verification, no threading. What
/// it must never do is write, and it never opens a file except read-only and read-shared, because
/// the client that owns it is usually running.
/// </summary>
internal static class MailMessageReader
{
    private static readonly Regex EncodedWord = new(
        @"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=",
        RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>Reads one .eml file. Returns null when the file is unreadable or not a message.</summary>
    internal static DesktopItem? ReadEmlFile(string sourceId, string path, DesktopReadLimits limits)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > limits.MaxFileBytes) return null;

            using FileStream stream = OpenShared(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ParseMessage(sourceId, path, path, reader.ReadToEnd(), limits);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Splits an mbox into messages and parses each. The separator is a line beginning "From "
    /// at the start of a message; a body line that would look like one is escaped to "&gt;From " by
    /// every writer, which is why the check is anchored and why leading '&gt;' is restored below.
    /// </summary>
    internal static IEnumerable<DesktopItem> ReadMbox(string sourceId, string path, DesktopReadLimits limits)
    {
        List<DesktopItem> items = new();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return items;

            using FileStream stream = OpenShared(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var current = new StringBuilder();
            int index = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("From ", StringComparison.Ordinal))
                {
                    if (current.Length > 0)
                    {
                        DesktopItem? item = ParseMessage(
                            sourceId, path + "#" + index, path, current.ToString(), limits);
                        if (item != null) items.Add(item);
                        if (items.Count >= limits.MaxItems) return items;
                        index++;
                        current.Clear();
                    }
                    continue;
                }

                // ">From " at the start of a body line is an escaped "From " - restore it, or every
                // quoted message in a reply comes back subtly corrupted.
                current.AppendLine(
                    line.StartsWith(">From ", StringComparison.Ordinal) ? line[1..] : line);

                if (current.Length > limits.MaxFileBytes) current.Length = limits.MaxFileBytes;
            }

            if (current.Length > 0)
            {
                DesktopItem? item = ParseMessage(sourceId, path + "#" + index, path, current.ToString(), limits);
                if (item != null) items.Add(item);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return items;
    }

    internal static DesktopItem? ParseMessage(
        string sourceId, string id, string location, string raw, DesktopReadLimits limits)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        (Dictionary<string, string> headers, string body) = SplitHeadersAndBody(raw);
        if (headers.Count == 0) return null;

        string subject = DecodeEncodedWords(Header(headers, "subject"));
        string from = DecodeEncodedWords(Header(headers, "from"));
        string to = DecodeEncodedWords(Header(headers, "to"));
        DateTimeOffset? sent = ParseDate(Header(headers, "date"));

        string text = ExtractText(headers, body, limits);

        // A message with neither a subject nor a body is not worth indexing, and a mailbox has
        // plenty of those (delivery receipts, calendar stubs).
        if (string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(text)) return null;

        string subtitle = string.IsNullOrWhiteSpace(from)
            ? (string.IsNullOrWhiteSpace(to) ? string.Empty : "To " + to)
            : "From " + from + (string.IsNullOrWhiteSpace(to) ? string.Empty : " · To " + to);

        return new DesktopItem(
            sourceId,
            DesktopItem.KindMail,
            id,
            string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
            string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            sent,
            text,
            location);
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Header(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out string? value) ? value.Trim() : string.Empty;

    /// <summary>
    /// Splits at the first blank line and unfolds continuation lines. Unfolding is not optional:
    /// RFC 5322 lets any header wrap onto lines that begin with whitespace, and a long subject
    /// almost always does.
    /// </summary>
    private static (Dictionary<string, string> Headers, string Body) SplitHeadersAndBody(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = raw.Replace("\r\n", "\n").Split('\n');

        string? name = null;
        var value = new StringBuilder();
        int index = 0;

        void Commit()
        {
            if (name == null) return;
            // First occurrence wins: a forged duplicate header appended later must not override the
            // one the client itself displayed.
            if (!headers.ContainsKey(name)) headers[name] = value.ToString();
            name = null;
            value.Clear();
        }

        for (; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length == 0) { index++; break; }

            if (line[0] is ' ' or '\t')
            {
                if (name != null) value.Append(' ').Append(line.Trim());
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                // Not a header at all. An mbox chunk that starts with body text is not a message.
                if (headers.Count == 0 && name == null) return (headers, raw);
                continue;
            }

            Commit();
            name = line[..colon].Trim();
            value.Append(line[(colon + 1)..].Trim());
        }
        Commit();

        string body = index < lines.Length ? string.Join("\n", lines[index..]) : string.Empty;
        return (headers, body);
    }

    /// <summary>
    /// Decodes RFC 2047 encoded words. Adjacent encoded words separated only by whitespace are one
    /// logical run and the whitespace between them is dropped, which is what makes a subject split
    /// mid-word reassemble correctly instead of gaining a space.
    /// </summary>
    internal static string DecodeEncodedWords(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal)) return value;

        var result = new StringBuilder();
        int position = 0;
        bool previousWasEncoded = false;

        foreach (Match match in EncodedWord.Matches(value))
        {
            string between = value[position..match.Index];
            if (!(previousWasEncoded && string.IsNullOrWhiteSpace(between))) result.Append(between);

            result.Append(DecodeEncodedWord(
                match.Groups["charset"].Value, match.Groups["encoding"].Value, match.Groups["text"].Value));

            position = match.Index + match.Length;
            previousWasEncoded = true;
        }

        result.Append(value[position..]);
        return result.ToString();
    }

    private static string DecodeEncodedWord(string charset, string encoding, string text)
    {
        try
        {
            Encoding target = ResolveEncoding(charset);
            if (encoding is "B" or "b") return target.GetString(Convert.FromBase64String(text));

            // Q encoding: '_' is a space, "=XX" is a byte. Decoded to bytes first and then through
            // the charset, because a multi-byte character arrives as several =XX in sequence.
            var bytes = new List<byte>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '_') { bytes.Add((byte)' '); continue; }
                if (text[i] == '=' && i + 2 < text.Length
                    && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    bytes.Add(b);
                    i += 2;
                    continue;
                }
                bytes.Add((byte)text[i]);
            }
            return target.GetString(bytes.ToArray());
        }
        catch (FormatException) { return text; }
        // DecoderFallbackException is an ArgumentException, so this one clause covers both a bad
        // base64 payload and a byte sequence the declared charset cannot represent.
        catch (ArgumentException) { return text; }
    }

    private static Encoding ResolveEncoding(string charset)
    {
        try { return Encoding.GetEncoding(charset); }
        catch (ArgumentException) { return Encoding.UTF8; }
        catch (NotSupportedException) { return Encoding.UTF8; }
    }

    /// <summary>
    /// Produces readable text: the text/plain alternative where one exists, otherwise HTML with its
    /// tags stripped. Handing a model raw HTML wastes its context on markup and reads badly when
    /// quoted back to the user.
    /// </summary>
    private static string ExtractText(Dictionary<string, string> headers, string body, DesktopReadLimits limits)
    {
        string contentType = Header(headers, "content-type");
        string encoding = Header(headers, "content-transfer-encoding");

        if (contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            string? boundary = ExtractBoundary(contentType);
            if (boundary != null)
            {
                string? plain = null;
                string? html = null;
                foreach (string part in SplitParts(body, boundary))
                {
                    (Dictionary<string, string> partHeaders, string partBody) = SplitHeadersAndBody(part);
                    string partType = Header(partHeaders, "content-type");
                    string decoded = DecodeBody(partBody, Header(partHeaders, "content-transfer-encoding"), partType);

                    if (partType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)) plain ??= decoded;
                    else if (partType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) html ??= decoded;
                }
                string chosen = plain ?? (html != null ? StripHtml(html) : string.Empty);
                return Trim(chosen, limits.MaxBodyCharacters);
            }
        }

        string text = DecodeBody(body, encoding, contentType);
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) text = StripHtml(text);
        return Trim(text, limits.MaxBodyCharacters);
    }

    private static string? ExtractBoundary(string contentType)
    {
        Match match = Regex.Match(contentType, @"boundary\s*=\s*""?(?<b>[^"";]+)""?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["b"].Value.Trim() : null;
    }

    private static IEnumerable<string> SplitParts(string body, string boundary)
    {
        string[] parts = body.Split("--" + boundary, StringSplitOptions.None);
        // First chunk is the preamble, last is the epilogue after the closing "--boundary--".
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.StartsWith("--", StringComparison.Ordinal)) break;
            yield return part.TrimStart('\r', '\n');
        }
    }

    private static string DecodeBody(string body, string encoding, string contentType)
    {
        Encoding charset = Encoding.UTF8;
        Match match = Regex.Match(contentType, @"charset\s*=\s*""?(?<c>[^"";\s]+)""?", RegexOptions.IgnoreCase);
        if (match.Success) charset = ResolveEncoding(match.Groups["c"].Value);

        if (encoding.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string cleaned = Regex.Replace(body, @"\s", string.Empty);
                return charset.GetString(Convert.FromBase64String(cleaned));
            }
            catch (FormatException) { return body; }
        }

        if (encoding.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase))
            return DecodeQuotedPrintable(body, charset);

        return body;
    }

    internal static string DecodeQuotedPrintable(string body, Encoding charset)
    {
        var bytes = new List<byte>(body.Length);
        string[] lines = body.Replace("\r\n", "\n").Split('\n');

        for (int line = 0; line < lines.Length; line++)
        {
            string text = lines[line];
            bool softBreak = text.EndsWith('=');
            if (softBreak) text = text[..^1];

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '=' && i + 2 < text.Length
                    && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    bytes.Add(b);
                    i += 2;
                    continue;
                }
                bytes.AddRange(charset.GetBytes(text[i].ToString()));
            }

            // A soft line break means the line was wrapped by the encoder and is not a real newline.
            if (!softBreak && line < lines.Length - 1) bytes.Add((byte)'\n');
        }

        try { return charset.GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return body; }
    }

    internal static string StripHtml(string html)
    {
        string text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        text = HtmlTag.Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Trailing "(CEST)"-style comments are legal and defeat the parser.
        value = Regex.Replace(value, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static string Trim(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
