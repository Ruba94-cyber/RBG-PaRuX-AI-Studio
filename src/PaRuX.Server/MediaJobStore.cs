using System.Globalization;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

// Durable intent and status of media jobs, so a Core restart can show what was interrupted and reattach to
// ComfyUI prompts that are still running. Generated media and source images never reach this file; video jobs
// retain only their bounded clip plan and output file references.
internal sealed class MediaJobStore
{
    internal const int MaximumRecords = 64;
    internal const int MaximumVideoClips = 128;
    internal const string FileName = "media-jobs.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;
    private bool _loadUnavailable;

    public MediaJobStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _filePath = Path.Combine(directory, FileName);
    }

    public string FilePath => _filePath;

    // Reads the saved records for recovery. A file that cannot be parsed is moved aside for diagnosis rather than
    // trusted, and recovery starts empty instead of guessing at what was running. IO failures remain unavailable so
    // recovery cannot save an empty in-memory set over records that were not read.
    public IReadOnlyList<MediaJobRecord> Load()
    {
        lock (_gate)
        {
            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (FileNotFoundException)
            {
                _loadUnavailable = false;
                return Array.Empty<MediaJobRecord>();
            }
            catch (DirectoryNotFoundException)
            {
                _loadUnavailable = false;
                return Array.Empty<MediaJobRecord>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadUnavailable = true;
                throw new MediaJobStoreUnavailableException("The media job file could not be read; saving is disabled until a successful load.", ex);
            }

            MediaJobFile? file;
            try
            {
                file = JsonSerializer.Deserialize<MediaJobFile>(json, Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                try
                {
                    File.Move(_filePath, Path.Combine(_directory, FileName + ".damaged-" + stamp + ".bak"), overwrite: true);
                }
                catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
                {
                    _loadUnavailable = true;
                    throw new MediaJobStoreUnavailableException("The damaged media job file could not be moved aside; saving is disabled until a successful load.", moveException);
                }

                _loadUnavailable = false;
                return Array.Empty<MediaJobRecord>();
            }

            _loadUnavailable = false;
            return file?.Jobs ?? new List<MediaJobRecord>();
        }
    }

    // Writes the newest MaximumRecords records. The snapshot is taken under the store lock, so an older snapshot can never
    // replace a newer one. The file is replaced in one step, so a crash mid-write keeps the previous complete file.
    public void Save(Func<IEnumerable<MediaJobRecord>> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_loadUnavailable)
                throw new MediaJobStoreUnavailableException("The media job store cannot be saved until a load succeeds.");

            List<MediaJobRecord> bounded = snapshot()
                .OrderByDescending(record => record.UpdatedUtc)
                .Take(MaximumRecords)
                .ToList();
            Directory.CreateDirectory(_directory);
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new MediaJobFile { Jobs = bounded }, Options));
            File.Move(temp, _filePath, overwrite: true);
        }
    }
}

internal sealed class MediaJobStoreUnavailableException : IOException
{
    public MediaJobStoreUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

internal sealed class MediaJobFile
{
    public int Version { get; set; } = 1;
    public List<MediaJobRecord> Jobs { get; set; } = new();
}

// Intent and status of one media job. The source image and any generated media are deliberately absent.
internal sealed class MediaJobRecord
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? AssetFilePath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? CustomWorkflowId { get; set; }
    public string? SaveNodeId { get; set; }
    public bool HasSourceImage { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
    public int Fps { get; set; }
    public int Steps { get; set; }
    public long Seed { get; set; }
    public List<long> VideoClipSeeds { get; set; } = new();
    // ComfyUI's client id is recorded before a submission is posted; the prompt id is recorded once ComfyUI returns it.
    public List<string> ClientIds { get; set; } = new();
    public List<string> PromptIds { get; set; } = new();
    public string? VideoRunId { get; set; }
    public string? VideoPrompt { get; set; }
    public int VideoRequestedFrames { get; set; }
    public string? VideoFinalPath { get; set; }
    public List<MediaVideoClipRecord> VideoClips { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MediaJobProvenance? Provenance { get; set; }
}

internal sealed class MediaVideoClipRecord
{
    public int FrameCount { get; set; }
    public string? ClientId { get; set; }
    public string? PromptId { get; set; }
    public string? OutputPath { get; set; }
}
