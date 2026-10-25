using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable recovery metadata for PaRuX-managed Training Lab jobs. Recovery is deliberately strict:
/// an interrupted job may continue only when the dataset, verified runtime and a complete Trainer
/// checkpoint still match the state that created it. Otherwise callers must surface a failed
/// recovery rather than silently restarting expensive training from step zero.
/// </summary>
internal static class TrainingJobDurability
{
    internal const int SchemaVersion = 1;
    private const string JournalDirectoryName = "job-state";
    private const string RunsDirectoryName = "runs";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal sealed class Journal
    {
        public int SchemaVersion { get; set; } = TrainingJobDurability.SchemaVersion;
        public required TrainingJobRequest Request { get; set; }
        public required TrainingJobStatus Status { get; set; }
        public int MaxWallClockMinutes { get; set; }
        public required string DatasetSha256 { get; set; }
        public required string RuntimeFingerprint { get; set; }
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    }

    internal sealed record LoadResult(string Id, Journal? Value, string? Error);

    internal static string ComputeFileSha256(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string ComputeRuntimeFingerprint(TrainingRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string canonical = string.Join('\n', new[]
        {
            "training-runtime-v1",
            status.Manifest?.ResolvedPackagesSha256 ?? string.Empty,
            status.Probe?.PythonVersion ?? status.Manifest?.PythonVersion ?? string.Empty,
            status.Probe?.UnslothVersion ?? status.Manifest?.UnslothVersion ?? string.Empty,
            status.Probe?.TorchVersion ?? status.Manifest?.TorchVersion ?? string.Empty,
            status.Probe?.Backend ?? status.Manifest?.Backend ?? string.Empty
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static void Save(string runtimeRoot, Journal journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJobId(journal.Status.Id);
        journal.SchemaVersion = SchemaVersion;
        journal.SavedUtc = DateTime.UtcNow;

        string directory = Path.Combine(runtimeRoot, JournalDirectoryName);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, journal.Status.Id + ".json");
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonSerializer.Serialize(journal, JsonOptions);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    internal static IReadOnlyList<LoadResult> LoadAll(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        string directory = Path.Combine(runtimeRoot, JournalDirectoryName);
        if (!Directory.Exists(directory)) return Array.Empty<LoadResult>();

        var results = new List<LoadResult>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            try
            {
                ValidateJobId(id);
                Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file), JsonOptions);
                if (journal == null) throw new InvalidDataException("Training job journal is empty.");
                if (journal.SchemaVersion != SchemaVersion)
                    throw new InvalidDataException("Unsupported Training Lab journal schema " + journal.SchemaVersion + ".");
                if (!string.Equals(journal.Status.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Training job journal id does not match its file name.");
                if (journal.Request == null || journal.Status == null)
                    throw new InvalidDataException("Training job journal is missing request/status data.");
                if (journal.MaxWallClockMinutes is < 1 or > 1_440)
                    throw new InvalidDataException("Training job journal has an invalid wall-clock budget.");
                if (!IsSha256(journal.DatasetSha256) || !IsSha256(journal.RuntimeFingerprint))
                    throw new InvalidDataException("Training job journal has an invalid recovery fingerprint.");
                results.Add(new LoadResult(id, journal, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or ArgumentException)
            {
                results.Add(new LoadResult(id, null, ex.Message));
            }
        }
        return results;
    }

    internal static string OutputDirectory(string runtimeRoot, string jobId)
    {
        ValidateJobId(jobId);
        return Path.Combine(runtimeRoot, RunsDirectoryName, jobId);
    }

    internal static string? FindLatestCompleteCheckpoint(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string outputFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (!Directory.Exists(outputFull)) return null;

        var candidates = new List<(int Step, string Path)>();
        foreach (string candidate in Directory.EnumerateDirectories(outputFull, "checkpoint-*", SearchOption.TopDirectoryOnly))
        {
            string candidateFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!string.Equals(Path.GetDirectoryName(candidateFull), outputFull, PathComparison)) continue;
            var info = new DirectoryInfo(candidateFull);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            string name = info.Name;
            if (!name.StartsWith("checkpoint-", StringComparison.Ordinal)
                || !int.TryParse(name["checkpoint-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int step)
                || step < 1)
                continue;
            if (!CheckpointIsComplete(candidateFull, step)) continue;
            candidates.Add((step, candidateFull));
        }

        return candidates
            .OrderByDescending(item => item.Step)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static bool CheckpointIsComplete(string checkpoint, int expectedStep)
    {
        string trainerState = Path.Combine(checkpoint, "trainer_state.json");
        string optimizer = Path.Combine(checkpoint, "optimizer.pt");
        string scheduler = Path.Combine(checkpoint, "scheduler.pt");
        if (!File.Exists(trainerState) || !File.Exists(optimizer) || !File.Exists(scheduler)) return false;

        bool hasWeights = File.Exists(Path.Combine(checkpoint, "adapter_model.safetensors"))
            || File.Exists(Path.Combine(checkpoint, "adapter_model.bin"))
            || File.Exists(Path.Combine(checkpoint, "model.safetensors"))
            || File.Exists(Path.Combine(checkpoint, "pytorch_model.bin"));
        if (!hasWeights) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(trainerState));
            return document.RootElement.TryGetProperty("global_step", out JsonElement globalStep)
                && globalStep.TryGetInt32(out int value)
                && value == expectedStep;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void ValidateJobId(string? id)
    {
        if (id?.Length != 32 || id.Any(ch => !char.IsAsciiHexDigit(ch)))
            throw new InvalidDataException("Training job id is not a GUID-N identifier.");
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
