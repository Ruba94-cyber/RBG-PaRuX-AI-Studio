using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Builds and audits a human-review packet for media outputs. Visual/typographic/identity/edit/video
/// quality cannot be certified by CLIP or by the model that produced the output, so this class only
/// packages outputs, prompts, seeds and settings with content hashes and a blank score sheet. It has
/// no API that writes scores or marks a packet reviewed: the criterion stays PENDING_OWNER_REVIEW
/// until the owner fills the sheet and names themselves, and <see cref="Evaluate"/> reports only
/// Pending/Complete/Invalid from what is on disk.
/// </summary>
public static class MediaReviewPacket
{
    public const int SchemaVersion = 1;
    public const string StatusPending = "PENDING_OWNER_REVIEW";
    public const string StatusReviewed = "REVIEWED_BY_OWNER";
    public static readonly string[] Tasks =
        { "text-render", "edit-fidelity", "identity", "workflow-recovery", "video-temporal", "general" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ReviewSheetHeader =
        { "id", "task", "output_file", "prompt_adherence_1to5", "visual_quality_1to5", "task_specific_1to5", "pass_yes_no", "notes" };

    public sealed record Item(
        string Id, string Task, string Prompt, long Seed, int Width, int Height, int Steps,
        string Engine, string ModelId, string ModelRevision, string OutputPath, string? InputPath = null,
        string? Settings = null);

    public sealed class ManifestItem
    {
        public string Id { get; set; } = "";
        public string Task { get; set; } = "";
        public string Prompt { get; set; } = "";
        public long Seed { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Steps { get; set; }
        public string Engine { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string ModelRevision { get; set; } = "";
        public string? Settings { get; set; }
        public string OutputFile { get; set; } = "";
        public string OutputSha256 { get; set; } = "";
        public long OutputBytes { get; set; }
        public string? InputFile { get; set; }
        public string? InputSha256 { get; set; }
        public long? InputBytes { get; set; }
    }

    public sealed class Manifest
    {
        public int SchemaVersion { get; set; } = MediaReviewPacket.SchemaVersion;
        public string Status { get; set; } = StatusPending;
        public string? Reviewer { get; set; }
        public DateTime? ReviewedUtc { get; set; }
        public string Note { get; set; } =
            "Quality judgement is human-owned; automated metrics and the generating model cannot certify it.";
        public List<ManifestItem> Items { get; set; } = new();
    }

    public enum Outcome { Pending, Complete, Invalid }

    public sealed record Evaluation(Outcome Outcome, IReadOnlyList<string> Problems);

    public static Manifest Build(string packetDir, IReadOnlyList<Item> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packetDir);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new InvalidDataException("A review packet needs at least one item.");
        if (items.Any(item => item is null)) throw new InvalidDataException("Review packet items cannot be null.");

        string target = Path.GetFullPath(packetDir);
        if (File.Exists(target)) throw new InvalidDataException("Review packet destination is a file.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Refusing to overwrite a nonempty review packet.");

        var prepared = new List<(Item Item, string OutputFile, string? InputFile)>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Item item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Review item id is not a valid file-name token: " + item.Id);
            if (!ids.Add(item.Id)) throw new InvalidDataException("Review item ids must be unique.");
            if (!Tasks.Contains(item.Task, StringComparer.Ordinal))
                throw new InvalidDataException("Unknown review task '" + item.Task + "'.");
            if (string.IsNullOrWhiteSpace(item.OutputPath) || !File.Exists(item.OutputPath))
                throw new FileNotFoundException("Review output is missing.", item.OutputPath);

            string outFile = item.Id + Path.GetExtension(item.OutputPath).ToLowerInvariant();
            ValidateGeneratedFileName(outFile);
            AddDestination("outputs/" + outFile, destinationNames);
            string? inFile = null;
            if (!string.IsNullOrEmpty(item.InputPath))
            {
                if (!File.Exists(item.InputPath))
                    throw new FileNotFoundException("Review input is missing.", item.InputPath);
                inFile = item.Id + ".input" + Path.GetExtension(item.InputPath).ToLowerInvariant();
                ValidateGeneratedFileName(inFile);
                AddDestination("outputs/" + inFile, destinationNames);
            }
            prepared.Add((item, outFile, inFile));
        }

        string parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Review packet destination has no parent directory.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(target) + ".building-" + Guid.NewGuid().ToString("N"));
        try
        {
            string outputs = Path.Combine(staging, "outputs");
            Directory.CreateDirectory(outputs);
            var manifest = new Manifest();
            foreach ((Item item, string outFile, string? inFile) in prepared)
            {
                string outDest = Path.Combine(outputs, outFile);
                File.Copy(item.OutputPath, outDest, overwrite: false);
                var entry = new ManifestItem
                {
                    Id = item.Id, Task = item.Task, Prompt = item.Prompt, Seed = item.Seed,
                    Width = item.Width, Height = item.Height, Steps = item.Steps, Engine = item.Engine,
                    ModelId = item.ModelId, ModelRevision = item.ModelRevision, Settings = item.Settings,
                    OutputFile = "outputs/" + outFile, OutputSha256 = Sha256(outDest),
                    OutputBytes = new FileInfo(outDest).Length
                };
                if (inFile != null)
                {
                    string inDest = Path.Combine(outputs, inFile);
                    File.Copy(item.InputPath!, inDest, overwrite: false);
                    entry.InputFile = "outputs/" + inFile;
                    entry.InputSha256 = Sha256(inDest);
                    entry.InputBytes = new FileInfo(inDest).Length;
                }
                manifest.Items.Add(entry);
            }

            File.WriteAllText(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
            var sheet = new StringBuilder(string.Join(',', ReviewSheetHeader)).Append("\r\n");
            foreach (ManifestItem m in manifest.Items)
                sheet.Append(Csv(m.Id)).Append(',').Append(Csv(m.Task)).Append(',').Append(Csv(m.OutputFile))
                    .Append(",,,,,\r\n");
            File.WriteAllText(Path.Combine(staging, "review-sheet.csv"), sheet.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(staging, "REVIEW.md"),
                "# Media human-review packet\r\n\r\nStatus: **" + StatusPending + "**\r\n\r\n"
                + "Open each file under `outputs/`, fill `review-sheet.csv` (scores 1-5, pass yes/no), then set `status` to `"
                + StatusReviewed + "` plus `reviewer` and `reviewedUtc` in `manifest.json`. "
                + "Nobody else (including the generating model) may fill it.\r\n\r\n"
                + "Task checks: text-render = legible and correct text; edit-fidelity = requested change made and the rest preserved; "
                + "identity = same subject; workflow-recovery = result after interrupt/resume equals uninterrupted; "
                + "video-temporal = coherent motion, no flicker.\r\n",
                new UTF8Encoding(false));

            if (Directory.Exists(target))
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                    throw new IOException("Refusing to overwrite a nonempty review packet.");
                Directory.Delete(target, recursive: false);
            }
            Directory.Move(staging, target);
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>Audits a packet on disk. Pending until the owner has signed AND scored every item.</summary>
    public static Evaluation Evaluate(string packetDir)
    {
        var problems = new List<string>();
        Manifest? manifest;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packetDir);
            manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(Path.Combine(packetDir, "manifest.json")), Json);
        }
        catch (Exception ex) when (IsExpectedPacketFailure(ex))
        {
            return new Evaluation(Outcome.Invalid, new[] { "manifest unreadable: " + ex.Message });
        }
        if (manifest is null || manifest.SchemaVersion != SchemaVersion || manifest.Items is null || manifest.Items.Count == 0)
            return new Evaluation(Outcome.Invalid, new[] { "manifest missing, wrong schema or empty" });

        try
        {
            string root = Path.GetFullPath(packetDir);
            var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestItem? item in manifest.Items)
            {
                if (item is null) { problems.Add("manifest contains a null item"); continue; }
                if (string.IsNullOrWhiteSpace(item.Id) || !itemIds.Add(item.Id))
                    problems.Add("manifest item id is missing or duplicated");
                AuditContent(root, item.Id, item.OutputFile, item.OutputSha256, item.OutputBytes, "output", referencedFiles, problems);
                bool hasInput = item.InputFile is not null || item.InputSha256 is not null || item.InputBytes.HasValue;
                if (hasInput)
                {
                    if (item.InputFile is null || item.InputSha256 is null || !item.InputBytes.HasValue)
                        problems.Add(item.Id + ": input metadata is incomplete");
                    else
                        AuditContent(root, item.Id, item.InputFile, item.InputSha256, item.InputBytes.Value, "input", referencedFiles, problems);
                }
            }

            Dictionary<string, string[]> rows = ReadSheet(packetDir, manifest, problems);
            bool anyScore = rows.Values.Any(r => r.Skip(3).Take(4).Any(c => c.Length > 0));
            bool signed = !string.IsNullOrWhiteSpace(manifest.Reviewer) && manifest.ReviewedUtc.HasValue;
            bool claimsReviewed = string.Equals(manifest.Status, StatusReviewed, StringComparison.Ordinal);

            if (!claimsReviewed)
            {
                if (anyScore) problems.Add("scores present but status is not " + StatusReviewed);
                if (signed) problems.Add("reviewer set but status is not " + StatusReviewed);
                return new Evaluation(problems.Count == 0 ? Outcome.Pending : Outcome.Invalid, problems);
            }

            if (!signed) problems.Add("status reviewed without reviewer and reviewedUtc");
            foreach (ManifestItem? nullableItem in manifest.Items)
            {
                if (nullableItem is null) continue;
                ManifestItem item = nullableItem;
                if (!rows.TryGetValue(item.Id, out string[]? row)) { problems.Add(item.Id + ": no sheet row"); continue; }
                for (int c = 3; c <= 5; c++)
                    if (!int.TryParse(row[c], out int score) || score is < 1 or > 5)
                        problems.Add(item.Id + ": score column " + c + " must be 1-5");
                if (!row[6].Equals("yes", StringComparison.OrdinalIgnoreCase)
                    && !row[6].Equals("no", StringComparison.OrdinalIgnoreCase))
                    problems.Add(item.Id + ": pass must be yes or no");
            }
            return new Evaluation(problems.Count == 0 ? Outcome.Complete : Outcome.Invalid, problems);
        }
        catch (Exception ex) when (IsExpectedPacketFailure(ex))
        {
            problems.Add("packet content unreadable: " + ex.Message);
            return new Evaluation(Outcome.Invalid, problems);
        }
    }

    private static bool IsExpectedPacketFailure(Exception ex) =>
        ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static void AuditContent(string root, string id, string? relativePath, string? expectedHash, long expectedBytes,
        string kind, HashSet<string> referencedFiles, List<string> problems)
    {
        if (!IsSafePacketPath(relativePath))
        {
            problems.Add(id + ": " + kind + " path is unsafe");
            return;
        }

        string normalized = relativePath!;
        if (!referencedFiles.Add(normalized)) problems.Add(id + ": duplicate packet content path");
        string path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(id + ": " + kind + " path escapes packet");
            return;
        }
        string outputsDirectory = Path.Combine(root, "outputs");
        if (IsReparsePoint(root) || IsReparsePoint(outputsDirectory) || IsReparsePoint(path))
        {
            problems.Add(id + ": " + kind + " path uses a filesystem reparse point");
            return;
        }
        if (!File.Exists(path)) { problems.Add(id + ": " + kind + " missing"); return; }
        var info = new FileInfo(path);
        if (info.Length != expectedBytes) problems.Add(id + ": " + kind + " byte count changed after packaging");
        if (expectedHash is null || expectedHash.Length != 64 ||
            !string.Equals(Sha256(path), expectedHash, StringComparison.OrdinalIgnoreCase))
            problems.Add(id + ": " + kind + " changed after packaging");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafePacketPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.Contains('\\') || Path.IsPathRooted(path)) return false;
        string[] parts = path.Split('/');
        return parts.Length == 2 && parts[0] == "outputs" && parts[1].Length > 0
            && parts[1] != "." && parts[1] != ".." && !parts[1].Contains(':');
    }

    private static void ValidateGeneratedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\'))
            throw new InvalidDataException("Generated review file name is unsafe: " + fileName);
    }

    private static void AddDestination(string relativePath, HashSet<string> destinations)
    {
        if (!destinations.Add(relativePath))
            throw new InvalidDataException("Review packet file-name collision: " + relativePath);
    }

    private static Dictionary<string, string[]> ReadSheet(string packetDir, Manifest manifest, List<string> problems)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(packetDir, "review-sheet.csv");
        if (!File.Exists(path)) { problems.Add("review sheet missing"); return result; }
        if (!TryParseCsv(File.ReadAllText(path), out List<string[]> records))
        {
            problems.Add("review sheet contains malformed CSV quoting or record endings");
            return result;
        }

        if (records.Count == 0 || !records[0].SequenceEqual(ReviewSheetHeader, StringComparer.Ordinal))
        {
            problems.Add("review sheet header is invalid");
            return result;
        }

        var expected = manifest.Items.Where(item => item is not null)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        if (records.Count - 1 != expected.Count) problems.Add("review sheet row count does not match manifest");
        foreach (string[] cells in records.Skip(1))
        {
            if (cells.Length != ReviewSheetHeader.Length)
            {
                problems.Add("review sheet row must contain exactly eight columns");
                continue;
            }
            string id = cells[0];
            if (string.IsNullOrWhiteSpace(id)) { problems.Add("review sheet row has no item id"); continue; }
            if (!result.TryAdd(id, cells)) { problems.Add("duplicate review sheet row: " + id); continue; }
            if (!expected.TryGetValue(id, out ManifestItem? item))
            {
                problems.Add("review sheet row has unknown item id: " + id);
                continue;
            }
            if (!string.Equals(cells[1], item.Task, StringComparison.Ordinal) ||
                !string.Equals(cells[2], item.OutputFile, StringComparison.Ordinal))
                problems.Add(id + ": review sheet task or output does not match manifest");
        }
        return result;
    }

    private static bool TryParseCsv(string text, out List<string[]> records)
    {
        records = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool quoteClosed = false;
        bool hasRecordContent = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else { inQuotes = false; quoteClosed = true; }
                }
                else cell.Append(ch);
            }
            else if (quoteClosed)
            {
                if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); quoteClosed = false; hasRecordContent = true; }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) return false;
                    if (ch == '\r') i++;
                    cells.Add(cell.ToString()); cell.Clear();
                    records.Add(cells.ToArray()); cells.Clear(); quoteClosed = false; hasRecordContent = false;
                }
                else return false;
            }
            else if (ch == '"')
            {
                if (cell.Length != 0) return false;
                inQuotes = true;
                hasRecordContent = true;
            }
            else if (ch == ',')
            {
                cells.Add(cell.ToString()); cell.Clear(); hasRecordContent = true;
            }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) return false;
                if (ch == '\r') i++;
                cells.Add(cell.ToString()); cell.Clear();
                records.Add(cells.ToArray()); cells.Clear(); hasRecordContent = false;
            }
            else { cell.Append(ch); hasRecordContent = true; }
        }
        if (inQuotes) return false;
        if (hasRecordContent || cells.Count > 0 || cell.Length > 0 || quoteClosed)
        {
            cells.Add(cell.ToString());
            records.Add(cells.ToArray());
        }
        return true;
    }

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
