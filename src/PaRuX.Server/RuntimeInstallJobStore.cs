using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The on-disk record of runtime install jobs, so a job's state survives a Core restart. Each save replaces the whole list through a
/// temporary file, so a crash mid-write leaves the previous list in place. A job whose stage had not finished when Core stopped is
/// reported as interrupted: nothing is resumed or activated automatically, and there is no running install left to cancel.
/// </summary>
internal static class RuntimeInstallJobStore
{
    internal const string InterruptedStage = "interrupted";
    internal const string InterruptedMessage =
        "Core stopped before this install finished. Resume this job to retry; the archive will be verified before installation.";

    private static readonly string[] TerminalStages = ["completed", "failed", "cancelled"];

    /// <summary>Reads the saved jobs. A missing file is an empty list. A damaged file is reported and treated as empty, so it cannot keep
    /// Core from starting, and the next save replaces it.</summary>
    internal static IReadOnlyList<RuntimeInstallStatus> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<RuntimeInstallStatus>>(File.ReadAllText(path)) ?? [])
                .Where(job => job is not null && !string.IsNullOrEmpty(job.Id))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine("Runtime install jobs could not be read from " + Path.GetFileName(path) + ": " + ex.Message);
            return [];
        }
    }

    internal static void Save(string path, IReadOnlyList<RuntimeInstallStatus> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(jobs));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Marks a job that had not reached a final stage as interrupted. Returns true when the job changed.</summary>
    internal static bool MarkInterruptedIfUnfinished(RuntimeInstallStatus job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (TerminalStages.Contains(job.Stage, StringComparer.Ordinal)) return false;
        job.Stage = InterruptedStage;
        job.Error = InterruptedMessage;
        job.UpdatedUtc = DateTime.UtcNow;
        return true;
    }
}
