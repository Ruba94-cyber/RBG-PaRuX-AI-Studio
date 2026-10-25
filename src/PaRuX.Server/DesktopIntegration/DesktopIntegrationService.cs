using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Lets PaRuX read the apps already on this PC - notes, mail, calendar, bookmarks - and answer from
/// them.
///
/// The rules this class exists to enforce, in the order they matter:
///
/// <list type="number">
/// <item><b>Nothing is connected until the user connects it.</b> Detection finds candidates; it
/// never enables anything. A newly installed app does not silently become readable.</item>
/// <item><b>Read-only, always.</b> No writer in this namespace. Files are opened read-shared because
/// the app that owns them is usually running, and a lock taken by an AI assistant would be a bug in
/// the user's mail client from their point of view.</item>
/// <item><b>Scoped to one path.</b> A connected source reads under exactly the directory or file it
/// was given, verified with the same containment rule as the rest of PaRuX. "Connect my notes" must
/// not become "read my home directory".</item>
/// <item><b>Provenance survives.</b> Every item carries the file it came from, so an answer built on
/// a mail can say which mail.</item>
/// </list>
///
/// Local by construction: every reader parses a file on this machine. Connecting a source adds no
/// network egress of any kind.
/// </summary>
public sealed class DesktopIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly object _gate = new();
    private Dictionary<string, ConnectedSource>? _cache;

    public DesktopIntegrationService() : this(ServerState.StateDirectory) { }

    public DesktopIntegrationService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string StorePath => Path.Combine(_stateDirectory, "desktop-integrations.json");

    /// <summary>Every app PaRuX can read, whether it was found on this machine, and whether the user
    /// has connected it. Detection alone changes nothing.</summary>
    public List<DesktopIntegrationStatus> GetStatuses()
    {
        Dictionary<string, ConnectedSource> connected = Load();
        var results = new List<DesktopIntegrationStatus>();

        foreach (DesktopSourceDefinition definition in DesktopIntegrationCatalog.All)
        {
            connected.TryGetValue(definition.Id, out ConnectedSource? state);
            string? detected = DesktopIntegrationCatalog.DetectPath(definition);

            results.Add(new DesktopIntegrationStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Kind = definition.Kind,
                WhatItReads = definition.WhatItReads,
                Detected = detected != null,
                DetectedPath = detected,
                RequiresExplicitPath = definition.ProbePaths.Count == 0,
                Connected = state != null,
                ConnectedPath = state?.Path,
                LastReadUtc = state?.LastReadUtc,
                LastItemCount = state?.LastItemCount ?? 0,
                LastError = state?.LastError
            });
        }
        return results;
    }

    /// <summary>
    /// Connects one source to one path. The path must exist and must be a directory or file the
    /// reader can actually use; a source that would read nothing is refused here rather than
    /// appearing connected and silently returning no results forever.
    /// </summary>
    public DesktopIntegrationStatus Connect(string id, string? path)
    {
        DesktopSourceDefinition definition = DesktopIntegrationCatalog.Find(id)
            ?? throw new InvalidDataException("Unknown desktop integration \"" + id + "\".");

        string resolved = (path ?? string.Empty).Trim();
        if (resolved.Length == 0)
        {
            resolved = DesktopIntegrationCatalog.DetectPath(definition)
                ?? throw new InvalidDataException(
                    definition.DisplayName + " was not found automatically. Choose the folder or file to read.");
        }

        resolved = Path.GetFullPath(resolved);
        bool isFile = File.Exists(resolved);
        bool isDirectory = Directory.Exists(resolved);
        if (!isFile && !isDirectory)
            throw new InvalidDataException("That path does not exist: " + resolved);

        if (definition.Reader == DesktopSourceReader.ChromiumBookmarks && !isFile)
            throw new InvalidDataException("Point this at the browser's \"Bookmarks\" file, not a folder.");
        if (definition.Reader is DesktopSourceReader.MarkdownVault or DesktopSourceReader.EmlFolder
                or DesktopSourceReader.MboxFolder or DesktopSourceReader.OfficeDocuments && !isDirectory)
            throw new InvalidDataException(definition.DisplayName + " reads a folder, not a single file.");

        lock (_gate)
        {
            Dictionary<string, ConnectedSource> connected = LoadUnsafe();
            connected[definition.Id] = new ConnectedSource { Path = resolved };
            SaveUnsafe(connected);
        }

        return GetStatuses().First(s => s.Id == definition.Id);
    }

    /// <summary>Disconnects a source. Nothing was copied anywhere, so there is nothing to delete
    /// beyond the fact that it was connected.</summary>
    public void Disconnect(string id)
    {
        lock (_gate)
        {
            Dictionary<string, ConnectedSource> connected = LoadUnsafe();
            if (connected.Remove(id)) SaveUnsafe(connected);
        }
    }

    /// <summary>
    /// Reads everything a connected source currently holds, bounded by <paramref name="limits"/>.
    /// Read fresh each time rather than from an index: these files change constantly, and an answer
    /// from a stale copy of someone's inbox is worse than no answer.
    /// </summary>
    public List<DesktopItem> Read(string id, DesktopReadLimits? limits = null)
    {
        limits ??= DesktopReadLimits.Default;
        DesktopSourceDefinition definition = DesktopIntegrationCatalog.Find(id)
            ?? throw new InvalidDataException("Unknown desktop integration \"" + id + "\".");

        ConnectedSource state;
        lock (_gate)
        {
            if (!LoadUnsafe().TryGetValue(definition.Id, out ConnectedSource? found))
                throw new InvalidOperationException(definition.DisplayName + " is not connected.");
            state = found;
        }

        try
        {
            List<DesktopItem> items = ReadFrom(definition, state.Path, limits).Take(limits.MaxItems).ToList();
            Record(definition.Id, items.Count, null);
            return items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Record(definition.Id, 0, ex.Message);
            throw;
        }
    }

    /// <summary>Everything connected, in one list. Used by the model-facing search tool.</summary>
    public List<DesktopItem> ReadAllConnected(string? kind = null, DesktopReadLimits? limits = null)
    {
        limits ??= DesktopReadLimits.Default;
        var all = new List<DesktopItem>();

        foreach (DesktopIntegrationStatus status in GetStatuses())
        {
            if (!status.Connected) continue;
            if (!string.IsNullOrWhiteSpace(kind)
                && !string.Equals(status.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;

            try { all.AddRange(Read(status.Id, limits)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // One unreadable source must not take the others down with it - a locked mail store
                // is a normal Tuesday, not a reason to answer nothing.
            }
            if (all.Count >= limits.MaxItems) break;
        }
        return all;
    }

    private static List<DesktopItem> ReadFrom(
        DesktopSourceDefinition definition, string path, DesktopReadLimits limits)
    {
        switch (definition.Reader)
        {
            case DesktopSourceReader.MarkdownVault:
                return MarkdownNoteReader.Read(definition.Id, path, limits).ToList();

            case DesktopSourceReader.OfficeDocuments:
                return OfficeDocumentReader.Read(definition.Id, path, limits).ToList();

            case DesktopSourceReader.ChromiumBookmarks:
                return BrowserBookmarkReader.Read(definition.Id, path, limits).ToList();

            case DesktopSourceReader.IcsCalendar:
                if (File.Exists(path)) return IcsCalendarReader.Read(definition.Id, path, limits).ToList();
                return EnumerateFiles(path, limits, ".ics")
                    .SelectMany(file => IcsCalendarReader.Read(definition.Id, file, limits))
                    .Take(limits.MaxItems)
                    .ToList();

            case DesktopSourceReader.EmlFolder:
                return EnumerateFiles(path, limits, ".eml")
                    .Select(file => MailMessageReader.ReadEmlFile(definition.Id, file, limits))
                    .Where(item => item != null)
                    .Select(item => item!)
                    .Take(limits.MaxItems)
                    .ToList();

            case DesktopSourceReader.MboxFolder:
                return EnumerateMboxFiles(path, limits)
                    .SelectMany(file => MailMessageReader.ReadMbox(definition.Id, file, limits))
                    .Take(limits.MaxItems)
                    .ToList();

            default:
                return new List<DesktopItem>();
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, DesktopReadLimits limits, string extension)
    {
        int scanned = 0;
        IEnumerator<string> enumerator;
        try { enumerator = Directory.EnumerateFiles(root, "*" + extension, SearchOption.AllDirectories).GetEnumerator(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try { if (!enumerator.MoveNext()) break; current = enumerator.Current; }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { continue; }

                if (++scanned > limits.MaxFilesScanned) break;
                yield return current;
            }
        }
    }

    /// <summary>
    /// Thunderbird stores each folder as an extensionless mbox file beside a same-named .msf index
    /// and a .sbd directory of sub-folders. So "the mail files" are the extensionless ones, and the
    /// index files must be skipped or every folder is read twice - once as mail, once as binary.
    /// </summary>
    private static IEnumerable<string> EnumerateMboxFiles(string root, DesktopReadLimits limits)
    {
        foreach (string file in EnumerateFiles(root, limits, string.Empty))
        {
            string extension = Path.GetExtension(file);
            if (!string.IsNullOrEmpty(extension)
                && !string.Equals(extension, ".mbox", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    private void Record(string id, int itemCount, string? error)
    {
        lock (_gate)
        {
            Dictionary<string, ConnectedSource> connected = LoadUnsafe();
            if (!connected.TryGetValue(id, out ConnectedSource? state)) return;
            state.LastReadUtc = DateTime.UtcNow;
            state.LastItemCount = itemCount;
            state.LastError = error;
            SaveUnsafe(connected);
        }
    }

    private Dictionary<string, ConnectedSource> Load()
    {
        lock (_gate) return new Dictionary<string, ConnectedSource>(LoadUnsafe(), StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ConnectedSource> LoadUnsafe()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(StorePath))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, ConnectedSource>>(
                    File.ReadAllText(StorePath), JsonOptions);
            }
        }
        catch { /* a corrupt file means nothing is connected, which is the safe direction */ }
        return _cache ??= new Dictionary<string, ConnectedSource>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveUnsafe(Dictionary<string, ConnectedSource> connected)
    {
        _cache = connected;
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(connected, JsonOptions));
    }

    private sealed class ConnectedSource
    {
        public string Path { get; set; } = string.Empty;
        public DateTime? LastReadUtc { get; set; }
        public int LastItemCount { get; set; }
        public string? LastError { get; set; }
    }
}
