namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// One thing read out of an app already installed on this PC - a note, a mail message, a calendar
/// event, a bookmark - normalised so the rest of PaRuX does not need to know which app it came from.
///
/// <see cref="Location"/> is always a real path on this machine and is carried all the way to the
/// user: an answer built from their mail has to be able to say which message, or it is just an
/// assertion. Nothing here is ever written back; every reader in this namespace opens files
/// read-only and shared, because these are files a running application owns.
/// </summary>
public sealed record DesktopItem(
    string SourceId,
    string Kind,
    string Id,
    string Title,
    string? Subtitle,
    DateTimeOffset? Timestamp,
    string Body,
    string Location)
{
    public const string KindNote = "note";
    public const string KindMail = "mail";
    public const string KindEvent = "event";
    public const string KindBookmark = "bookmark";
    public const string KindDocument = "document";
}

/// <summary>
/// Hard ceilings every reader obeys. A person's mail store or note vault can be tens of gigabytes,
/// and the failure mode of "read it all" is not a slow answer - it is PaRuX becoming unusable while
/// it walks a decade of archived mail. These are deliberately small enough to keep a read
/// interactive; retrieval narrows further before anything reaches a model.
/// </summary>
public sealed record DesktopReadLimits(
    int MaxItems = 2_000,
    int MaxBodyCharacters = 8_000,
    int MaxFileBytes = 4 * 1024 * 1024,
    int MaxFilesScanned = 20_000)
{
    public static readonly DesktopReadLimits Default = new();
}
