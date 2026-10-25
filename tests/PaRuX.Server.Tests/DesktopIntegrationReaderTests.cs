using System.Text;
using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// These readers are the difference between "PaRuX can read your mail" being true and being a demo.
/// Every case below is something a real mail store or note vault contains on any given day, and
/// each one silently corrupts the result if the parser skips it.
/// </summary>
public sealed class DesktopIntegrationReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-desktop-" + Guid.NewGuid().ToString("N"));

    public DesktopIntegrationReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- mail ----

    [Fact]
    public void A_subject_folded_across_lines_is_read_whole()
    {
        // RFC 5322 wraps long headers onto continuation lines. A line-per-header parser keeps only
        // the first fragment, which is exactly the part that carries the least meaning.
        string raw = "From: a@example.com\r\nSubject: Quarterly review notes\r\n and the follow-up actions\r\n\r\nbody\r\n";

        DesktopItem item = Assert.IsType<DesktopItem>(
            MailMessageReader.ParseMessage("m", "id", "loc", raw, DesktopReadLimits.Default));

        Assert.Equal("Quarterly review notes and the follow-up actions", item.Title);
    }

    [Theory]
    // Base64 encoded word: without decoding, a search for the real subject never matches.
    [InlineData("=?UTF-8?B?UHJlbm90YXppb25l?=", "Prenotazione")]
    // Q encoding, where '_' is a space and =XX is a byte.
    [InlineData("=?UTF-8?Q?Conferma_prenotazione?=", "Conferma prenotazione")]
    [InlineData("=?UTF-8?Q?Caff=C3=A8?=", "Caffè")]
    // Adjacent encoded words are one run: the whitespace between them is folding, not a space.
    [InlineData("=?UTF-8?B?UHJl?= =?UTF-8?B?bm90YXppb25l?=", "Prenotazione")]
    // Plain text mixed with an encoded word keeps its real spacing.
    [InlineData("Re: =?UTF-8?B?UHJlbm90YXppb25l?=", "Re: Prenotazione")]
    [InlineData("no encoding here", "no encoding here")]
    public void Encoded_subjects_are_decoded(string encoded, string expected)
    {
        Assert.Equal(expected, MailMessageReader.DecodeEncodedWords(encoded));
    }

    [Fact]
    public void A_quoted_printable_body_is_decoded_and_soft_breaks_are_not_newlines()
    {
        string decoded = MailMessageReader.DecodeQuotedPrintable(
            "Il caff=C3=A8 =C3=A8 pronto e questa riga =\r\ncontinua.", Encoding.UTF8);

        Assert.Equal("Il caffè è pronto e questa riga continua.", decoded);
    }

    [Fact]
    public void A_multipart_message_prefers_the_plain_text_alternative()
    {
        string raw =
            "From: a@example.com\r\n" +
            "Subject: Invoice\r\n" +
            "Content-Type: multipart/alternative; boundary=\"XYZ\"\r\n" +
            "\r\n" +
            "--XYZ\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            "The plain version.\r\n" +
            "--XYZ\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n\r\n" +
            "<html><body><p>The HTML version.</p></body></html>\r\n" +
            "--XYZ--\r\n";

        DesktopItem item = Assert.IsType<DesktopItem>(
            MailMessageReader.ParseMessage("m", "id", "loc", raw, DesktopReadLimits.Default));

        Assert.Contains("The plain version.", item.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_html_only_message_is_stripped_rather_than_handed_over_as_markup()
    {
        string raw =
            "From: a@example.com\r\nSubject: Newsletter\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" +
            "<html><head><style>p{color:red}</style></head><body><p>Hello&nbsp;there</p>" +
            "<script>alert(1)</script></body></html>";

        DesktopItem item = Assert.IsType<DesktopItem>(
            MailMessageReader.ParseMessage("m", "id", "loc", raw, DesktopReadLimits.Default));

        Assert.Contains("Hello", item.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<", item.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("alert(1)", item.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_mbox_is_split_into_messages_and_escaped_From_lines_are_restored()
    {
        string mbox =
            "From bounce@example.com Mon Jan  1 00:00:00 2026\r\n" +
            "From: a@example.com\r\nSubject: First\r\n\r\n" +
            "He wrote:\r\n>From the top of the report\r\n" +
            "\r\n" +
            "From bounce@example.com Tue Jan  2 00:00:00 2026\r\n" +
            "From: b@example.com\r\nSubject: Second\r\n\r\nsecond body\r\n";

        string path = Write("mail/Inbox", mbox);
        List<DesktopItem> items = MailMessageReader.ReadMbox("m", path, DesktopReadLimits.Default).ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("First", items[0].Title);
        Assert.Equal("Second", items[1].Title);
        Assert.Contains("From the top of the report", items[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(">From the top", items[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_with_a_timezone_comment_still_parses()
    {
        // "Wed", because 2 September 2026 is a Wednesday: .NET rejects a date whose day name does
        // not match, and so does every mail client, which is why real mail never carries a wrong one.
        string raw = "From: a@example.com\r\nSubject: S\r\nDate: Wed, 2 Sep 2026 14:05:00 +0200 (CEST)\r\n\r\nb";

        DesktopItem item = Assert.IsType<DesktopItem>(
            MailMessageReader.ParseMessage("m", "id", "loc", raw, DesktopReadLimits.Default));

        Assert.NotNull(item.Timestamp);
        Assert.Equal(2026, item.Timestamp!.Value.Year);
    }

    [Fact]
    public void A_message_with_neither_a_subject_nor_a_body_is_not_indexed()
    {
        Assert.Null(MailMessageReader.ParseMessage(
            "m", "id", "loc", "From: a@example.com\r\nX-Type: receipt\r\n\r\n\r\n", DesktopReadLimits.Default));
    }

    [Fact]
    public void The_sender_and_recipient_are_carried_so_an_answer_can_attribute_a_mail()
    {
        string raw = "From: Ada <ada@example.com>\r\nTo: Bob <bob@example.com>\r\nSubject: S\r\n\r\nbody";

        DesktopItem item = Assert.IsType<DesktopItem>(
            MailMessageReader.ParseMessage("m", "id", "loc", raw, DesktopReadLimits.Default));

        Assert.Contains("ada@example.com", item.Subtitle!, StringComparison.Ordinal);
        Assert.Contains("bob@example.com", item.Subtitle!, StringComparison.Ordinal);
    }

    // ---- notes ----

    [Fact]
    public void A_note_title_comes_from_front_matter_then_a_heading_then_the_file_name()
    {
        Write("vault/a.md", "---\ntitle: From front matter\n---\n# Ignored heading\nbody");
        Write("vault/b.md", "# From heading\n\nbody");
        Write("vault/c.md", "just body text");

        List<DesktopItem> notes = MarkdownNoteReader
            .Read("obsidian", Path.Combine(_root, "vault"), DesktopReadLimits.Default)
            .OrderBy(n => n.Location, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(3, notes.Count);
        Assert.Equal("From front matter", notes[0].Title);
        Assert.Equal("From heading", notes[1].Title);
        Assert.Equal("c", notes[2].Title);
    }

    [Fact]
    public void Front_matter_is_stripped_from_the_body_rather_than_read_as_prose()
    {
        Write("vault/a.md", "---\ntitle: T\ntags: [x]\n---\nThe actual note.");

        DesktopItem note = Assert.Single(
            MarkdownNoteReader.Read("obsidian", Path.Combine(_root, "vault"), DesktopReadLimits.Default));

        Assert.Equal("The actual note.", note.Body);
        Assert.DoesNotContain("tags:", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_apps_own_bookkeeping_folders_are_not_read_back_as_notes()
    {
        Write("vault/real.md", "# Real note");
        Write("vault/.obsidian/workspace.md", "# Not a note");
        Write("vault/.trash/deleted.md", "# Deleted");
        Write("vault/logseq/bak/old.md", "# Backup");

        List<DesktopItem> notes = MarkdownNoteReader
            .Read("obsidian", Path.Combine(_root, "vault"), DesktopReadLimits.Default).ToList();

        Assert.Equal("Real note", Assert.Single(notes).Title);
    }

    // ---- calendar ----

    [Fact]
    public void A_folded_ics_line_is_rejoined_and_escapes_are_undone()
    {
        string ics =
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n" +
            "UID:evt-1\r\n" +
            "SUMMARY:Dentist appointment and a very long title that the\r\n  writer wrapped\r\n" +
            "DTSTART:20260903T090000Z\r\n" +
            "LOCATION:Via Roma 1\\, Milano\r\n" +
            "DESCRIPTION:Bring the referral\\nand the card\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        DesktopItem item = Assert.Single(
            IcsCalendarReader.Parse("cal", "loc", ics, DesktopReadLimits.Default));

        Assert.Equal("Dentist appointment and a very long title that the writer wrapped", item.Title);
        Assert.Equal("Via Roma 1, Milano", item.Subtitle);
        Assert.Contains("Bring the referral\nand the card", item.Body, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero), item.Timestamp);
    }

    [Theory]
    [InlineData("20260903T090000Z", 2026, 9, 3)]
    [InlineData("20260903", 2026, 9, 3)]
    [InlineData("20260903T090000", 2026, 9, 3)]
    public void Ics_dates_parse_in_all_three_shapes_a_calendar_writes(string value, int year, int month, int day)
    {
        DateTimeOffset? maybe = IcsCalendarReader.ParseIcsDate(value);

        Assert.NotNull(maybe);
        DateTimeOffset parsed = maybe!.Value;
        Assert.Equal(year, parsed.Year);
        Assert.Equal(month, parsed.Month);
        Assert.Equal(day, parsed.Day);
    }

    [Fact]
    public void A_non_date_is_not_invented_as_one()
    {
        Assert.Null(IcsCalendarReader.ParseIcsDate("not a date"));
        Assert.Null(IcsCalendarReader.ParseIcsDate(null));
    }

    // ---- bookmarks ----

    [Fact]
    public void Bookmarks_are_read_with_their_folder_and_real_date()
    {
        // 13,000,000,000,000,000 microseconds after 1601-01-01 is December 2012 - the point being
        // that this is not the Unix epoch, which would have put the same number in 1970.
        string json = """
        {"roots":{"bookmark_bar":{"type":"folder","name":"Bookmarks bar","children":[
          {"type":"folder","name":"Work","children":[
            {"type":"url","id":"7","name":"llama.cpp","url":"https://github.com/ggml-org/llama.cpp","date_added":"13000000000000000"}
          ]},
          {"type":"url","id":"8","name":"Docs","url":"https://example.com/docs","date_added":"0"}
        ]}}}
        """;

        List<DesktopItem> items = BrowserBookmarkReader.Parse("edge", "loc", json, DesktopReadLimits.Default);

        Assert.Equal(2, items.Count);
        DesktopItem nested = items.Single(i => i.Title == "llama.cpp");
        Assert.Equal("Work", nested.Subtitle);
        Assert.Equal("https://github.com/ggml-org/llama.cpp", nested.Body);
        Assert.Equal(2012, nested.Timestamp!.Value.Year);

        // A zero timestamp is "unknown", not 1601.
        Assert.Null(items.Single(i => i.Title == "Docs").Timestamp);
    }

    [Fact]
    public void A_half_written_bookmarks_file_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(BrowserBookmarkReader.Parse("edge", "loc", "{\"roots\": {", DesktopReadLimits.Default));
    }
}
