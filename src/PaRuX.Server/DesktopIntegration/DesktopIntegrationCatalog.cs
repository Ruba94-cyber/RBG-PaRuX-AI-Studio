namespace PaRuX.Server.DesktopIntegration;

/// <summary>How PaRuX reads one kind of desktop app.</summary>
internal enum DesktopSourceReader
{
    /// <summary>A folder of Markdown/plain-text notes.</summary>
    MarkdownVault,
    /// <summary>A folder of RFC 5322 .eml files.</summary>
    EmlFolder,
    /// <summary>A Thunderbird-style mail directory holding mbox files.</summary>
    MboxFolder,
    /// <summary>A single .ics file, or a folder of them.</summary>
    IcsCalendar,
    /// <summary>A Chromium-family "Bookmarks" JSON file.</summary>
    ChromiumBookmarks,
    /// <summary>A folder of Word, Excel, PowerPoint, PDF and RTF documents.</summary>
    OfficeDocuments
}

/// <summary>
/// One connectable app, and where it keeps the files PaRuX would read.
///
/// <see cref="ProbePaths"/> is how "is this installed?" is answered - by looking for the data, not by
/// reading a registry key or a package manifest. Someone who moved their Obsidian vault to another
/// drive still has Obsidian; someone with the app installed and no vault has nothing to read. The
/// data is the thing that matters, so the data is what is probed, and any path the user gives
/// explicitly overrides the guess.
/// </summary>
internal sealed record DesktopSourceDefinition(
    string Id,
    string DisplayName,
    string Kind,
    DesktopSourceReader Reader,
    IReadOnlyList<string> ProbePaths,
    string WhatItReads);

/// <summary>
/// The apps PaRuX knows how to read, and why this list is short.
///
/// Every entry here reads an <em>open format the app does not own</em>: Markdown files, RFC 5322
/// messages, iCalendar, a JSON bookmark tree. That is a deliberate boundary rather than a limit of
/// effort. Reading a proprietary store - a locked .pst, a live places.sqlite, a Notion cache - means
/// either shipping a reverse-engineered parser that breaks on the next update, or holding a lock on
/// a file the user's own app needs. Neither belongs in a product whose pitch is that it does not
/// break your machine.
///
/// Anything not listed is still reachable: every source accepts an explicit path, so exporting to
/// .eml, .ics or Markdown connects any app at all.
/// </summary>
internal static class DesktopIntegrationCatalog
{
    internal const string KindNotes = "notes";
    internal const string KindMail = "mail";
    internal const string KindCalendar = "calendar";
    internal const string KindBookmarks = "bookmarks";
    internal const string KindDocuments = "documents";

    internal static IReadOnlyList<DesktopSourceDefinition> All { get; } = new List<DesktopSourceDefinition>
    {
        new("obsidian", "Obsidian", KindNotes, DesktopSourceReader.MarkdownVault,
            new[]
            {
                Home("Documents", "Obsidian"),
                Home("Obsidian"),
                Home("Documents", "Obsidian Vault")
            },
            "Markdown notes in your vault, including sub-folders. Obsidian's own .obsidian settings folder is skipped."),

        new("logseq", "Logseq", KindNotes, DesktopSourceReader.MarkdownVault,
            new[] { Home("Logseq"), Home("Documents", "Logseq") },
            "Markdown pages and journals in your graph. The bak folder is skipped."),

        new("notes-folder", "Notes folder", KindNotes, DesktopSourceReader.MarkdownVault,
            Array.Empty<string>(),
            "Any folder of Markdown or text notes you point PaRuX at - Zettlr, Joplin exports, iA Writer, or just a folder."),

        new("office-documents", "Word, Excel and PowerPoint", KindDocuments, DesktopSourceReader.OfficeDocuments,
            new[] { Documents() },
            "Text from .docx, .xlsx and .pptx files in the folder you choose, plus PDF and RTF. Read-only: PaRuX never edits a document in place."),

        new("onedrive-documents", "OneDrive documents", KindDocuments, DesktopSourceReader.OfficeDocuments,
            new[] { Home("OneDrive", "Documents"), Home("OneDrive") },
            "The same document types, from your local OneDrive folder. Only files already synced to this PC are read - PaRuX never talks to OneDrive itself."),

        new("thunderbird", "Thunderbird", KindMail, DesktopSourceReader.MboxFolder,
            new[]
            {
                AppData("Thunderbird", "Profiles"),
                Home(".thunderbird")
            },
            "Mail stored in your Thunderbird profile. Read-only: PaRuX never marks, moves or deletes a message."),

        new("mail-export", "Mail export folder", KindMail, DesktopSourceReader.EmlFolder,
            Array.Empty<string>(),
            "A folder of .eml files exported from any mail client, including Outlook and Apple Mail."),

        new("calendar-ics", "Calendar (.ics)", KindCalendar, DesktopSourceReader.IcsCalendar,
            Array.Empty<string>(),
            "An .ics file or a folder of them, exported or subscribed from any calendar app."),

        new("edge-bookmarks", "Microsoft Edge bookmarks", KindBookmarks, DesktopSourceReader.ChromiumBookmarks,
            new[] { LocalAppData("Microsoft", "Edge", "User Data", "Default", "Bookmarks") },
            "Saved bookmarks and their folders. Browsing history is deliberately not read."),

        new("chrome-bookmarks", "Google Chrome bookmarks", KindBookmarks, DesktopSourceReader.ChromiumBookmarks,
            new[]
            {
                LocalAppData("Google", "Chrome", "User Data", "Default", "Bookmarks"),
                Home(".config", "google-chrome", "Default", "Bookmarks")
            },
            "Saved bookmarks and their folders. Browsing history is deliberately not read.")
    };

    internal static DesktopSourceDefinition? Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first probe path that exists, or null. Detection never guesses at a path that is
    /// not there - "detected" has to mean "there is something to read".</summary>
    internal static string? DetectPath(DesktopSourceDefinition definition)
    {
        foreach (string candidate in definition.ProbePaths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string Documents() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static string Home(params string[] parts) =>
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), parts);

    private static string AppData(params string[] parts) =>
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), parts);

    private static string LocalAppData(params string[] parts) =>
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), parts);

    private static string Combine(string root, string[] parts) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(new[] { root }.Concat(parts).ToArray());
}
