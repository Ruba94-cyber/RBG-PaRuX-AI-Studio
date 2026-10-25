using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace PaRuX.Server;

/// <summary>
/// Dataset Hub's curation half: schema detection, basic
/// quality checks (empty fields, exact-match duplicates), and a deterministic train/validation/
/// test split. Supports CSV, JSON (array of objects), JSON Lines, and - since 2026-08-27 - Apache
/// Parquet, the most common real-world Hugging Face dataset format.
///
/// Dependency decision (directive: evaluate maintained candidates, check license/supply-chain,
/// prefer minimal mature dependencies, document the decision): added `Parquet.Net` 6.1.0 - MIT
/// licensed, fully-managed (no native binary, so no cross-platform/Android-adjacent ABI concerns),
/// actively maintained (github.com/aloneguid/parquet-dotnet), and its own dependency tree is itself
/// small and reputable (Microsoft's own CommunityToolkit.HighPerformance/RecyclableMemoryStream
/// plus well-known compression libraries). `dotnet restore`'s own NuGet vulnerability advisory
/// check ran clean at add-time. Chosen over ParquetSharp (a native-binary wrapper around Apache
/// Arrow C++ - a heavier, platform-specific dependency for a single-project reader) since a pure
/// C# implementation matches this codebase's existing "no unnecessary native dependency" posture.
///
/// Parquet input is read-only here (inspect + split) - SplitAsync always normalizes a Parquet
/// source's train/validation/test output to JSON Lines (the same normalization CSV-vs-JSON/JSONL
/// input already gets), rather than also implementing a Parquet writer; the real, common need this
/// closes is "I downloaded a Parquet HF dataset, now curate/split it for training," and every
/// existing Training Lab/Eval Lab consumer already expects JSON Lines or CSV, never Parquet.
/// </summary>
public sealed class DatasetCurationService
{
    // A sane bound so a multi-GB file doesn't get fully materialized in memory - large enough for
    // any real curation/training-prep workflow this feature targets, small enough to stay honest
    // about what "inspect" actually means (a bounded sample-or-full read, disclosed via RowCount).
    private const int MaxRowsToInspect = 200_000;

    public async Task<DatasetCurationReport> InspectAsync(string sourcePath, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            return new DatasetCurationReport { SourcePath = sourcePath, DetectedFormat = "unsupported", Error = "File not found." };

        string format = DetectFormat(sourcePath);
        if (format == "unsupported")
            return new DatasetCurationReport
            {
                SourcePath = sourcePath,
                DetectedFormat = format,
                Error = "Unsupported format - only .csv, .json, .jsonl, and .parquet are supported.",
            };

        try
        {
            List<Dictionary<string, string?>> rows = await ReadRowsAsync(sourcePath, format, ct);
            List<string> columns = rows.SelectMany(r => r.Keys).Distinct().ToList();
            int emptyFields = rows.Sum(r => r.Values.Count(v => string.IsNullOrWhiteSpace(v)));
            int duplicates = CountDuplicates(rows);
            return new DatasetCurationReport
            {
                SourcePath = sourcePath,
                DetectedFormat = format,
                Columns = columns,
                RowCount = rows.Count,
                EmptyFieldCount = emptyFields,
                DuplicateRowCount = duplicates,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatasetCurationReport { SourcePath = sourcePath, DetectedFormat = format, Error = "Could not parse file: " + ex.Message };
        }
    }

    /// <summary>"Use for Eval": maps an already-downloaded/curated dataset's rows to Eval Lab cases
    /// via caller-supplied column names (never guessed - see ImportDatasetAsEvalCasesRequest). Pure
    /// mapping only; the caller (Program.cs) owns actually creating the cases via EvalStore, since
    /// this service has no EvalStore dependency and should not gain one just for this one bridge.</summary>
    public async Task<(List<CreateEvalCaseRequest> Cases, int RowsSkipped)> MapToEvalCasesAsync(
        string sourcePath, string promptColumn, string expectedBehaviorColumn, string? systemPromptColumn, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Dataset file not found.", sourcePath);
        if (string.IsNullOrWhiteSpace(promptColumn) || string.IsNullOrWhiteSpace(expectedBehaviorColumn))
            throw new InvalidDataException("PromptColumn and ExpectedBehaviorColumn are required - PaRuX never guesses which column means what, since dataset schemas vary (prompt/completion, instruction/output, question/answer, ...).");

        string format = DetectFormat(sourcePath);
        if (format == "unsupported")
            throw new InvalidDataException("Unsupported format - only .csv, .json, .jsonl, and .parquet are supported.");

        List<Dictionary<string, string?>> rows = await ReadRowsAsync(sourcePath, format, ct);
        if (rows.Count == 0) return (new List<CreateEvalCaseRequest>(), 0);

        List<string> columns = rows.SelectMany(r => r.Keys).Distinct().ToList();
        if (!columns.Contains(promptColumn))
            throw new InvalidDataException($"Column \"{promptColumn}\" not found. Available columns: {string.Join(", ", columns)}");
        if (!columns.Contains(expectedBehaviorColumn))
            throw new InvalidDataException($"Column \"{expectedBehaviorColumn}\" not found. Available columns: {string.Join(", ", columns)}");
        if (systemPromptColumn != null && !columns.Contains(systemPromptColumn))
            throw new InvalidDataException($"Column \"{systemPromptColumn}\" not found. Available columns: {string.Join(", ", columns)}");

        var cases = new List<CreateEvalCaseRequest>();
        int skipped = 0;
        foreach (Dictionary<string, string?> row in rows)
        {
            string? prompt = row.GetValueOrDefault(promptColumn);
            string? expected = row.GetValueOrDefault(expectedBehaviorColumn);
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(expected)) { skipped++; continue; }

            cases.Add(new CreateEvalCaseRequest
            {
                Prompt = prompt,
                ExpectedBehavior = expected,
                SystemPrompt = systemPromptColumn != null ? row.GetValueOrDefault(systemPromptColumn) : null,
            });
        }
        return (cases, skipped);
    }

    public async Task<DatasetSplitResult> SplitAsync(DatasetSplitRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException("Dataset file not found.", request.SourcePath);
        if (request.TrainFraction <= 0 || request.TrainFraction >= 1)
            throw new InvalidDataException("TrainFraction must be between 0 and 1 (exclusive).");
        if (request.ValidationFraction < 0 || request.TrainFraction + request.ValidationFraction >= 1)
            throw new InvalidDataException("TrainFraction + ValidationFraction must be less than 1 - the remainder becomes the test split.");

        string format = DetectFormat(request.SourcePath);
        if (format == "unsupported")
            throw new InvalidDataException("Unsupported format for splitting - only .csv, .json, .jsonl, and .parquet are supported.");

        List<Dictionary<string, string?>> rows = await ReadRowsAsync(request.SourcePath, format, ct);

        int duplicatesRemoved = 0;
        if (request.DeduplicateBeforeSplit)
        {
            var seen = new HashSet<string>();
            var deduped = new List<Dictionary<string, string?>>(rows.Count);
            foreach (Dictionary<string, string?> row in rows)
            {
                if (seen.Add(RowKey(row))) deduped.Add(row);
            }
            duplicatesRemoved = rows.Count - deduped.Count;
            rows = deduped;
        }

        // Deterministic shuffle (seeded RNG) rather than "keep original order then slice" - avoids
        // handing every downstream consumer the same systematic ordering bias (e.g. a source file
        // sorted by label), while staying exactly reproducible for the same seed and input.
        var rng = new Random(request.Seed);
        List<Dictionary<string, string?>> shuffled = rows.OrderBy(_ => rng.Next()).ToList();

        int trainCount = Math.Min(shuffled.Count, (int)Math.Round(shuffled.Count * request.TrainFraction));
        int validationCount = Math.Min(shuffled.Count - trainCount, (int)Math.Round(shuffled.Count * request.ValidationFraction));
        int testCount = shuffled.Count - trainCount - validationCount;

        List<Dictionary<string, string?>> trainRows = shuffled.Take(trainCount).ToList();
        List<Dictionary<string, string?>> validationRows = shuffled.Skip(trainCount).Take(validationCount).ToList();
        List<Dictionary<string, string?>> testRows = shuffled.Skip(trainCount + validationCount).ToList();

        string directory = Path.GetDirectoryName(request.SourcePath) is { Length: > 0 } dir ? dir : DatasetDownloadService.DatasetsDirectory;
        string baseName = Path.GetFileNameWithoutExtension(request.SourcePath);
        // Normalize JSON/JSON-Lines input to .jsonl output (one record per line - the common
        // training-pipeline convention); CSV input stays CSV so the header/column shape is obvious.
        string extension = format == "csv" ? ".csv" : ".jsonl";

        string trainPath = Path.Combine(directory, baseName + ".train" + extension);
        string validationPath = Path.Combine(directory, baseName + ".validation" + extension);
        string testPath = Path.Combine(directory, baseName + ".test" + extension);

        await WriteRowsAsync(trainPath, trainRows, format, ct);
        await WriteRowsAsync(validationPath, validationRows, format, ct);
        await WriteRowsAsync(testPath, testRows, format, ct);

        return new DatasetSplitResult
        {
            TrainPath = trainPath,
            ValidationPath = validationPath,
            TestPath = testPath,
            TrainCount = trainRows.Count,
            ValidationCount = validationRows.Count,
            TestCount = testRows.Count,
            DuplicatesRemoved = duplicatesRemoved,
        };
    }

    internal static string DetectFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => "csv",
        ".json" => "json",
        ".jsonl" or ".ndjson" => "jsonl",
        ".parquet" => "parquet",
        _ => "unsupported",
    };

    private static Task<List<Dictionary<string, string?>>> ReadRowsAsync(string path, string format, CancellationToken ct) => format switch
    {
        "csv" => ReadCsvAsync(path, ct),
        "json" => ReadJsonArrayAsync(path, ct),
        "jsonl" => ReadJsonLinesAsync(path, ct),
        "parquet" => ReadParquetAsync(path, ct),
        _ => throw new InvalidDataException("Unsupported format."),
    };

    /// <summary>Reads every row group's every declared data field (nested/repeated/group fields
    /// are flattened to their raw .ToString() representation via the same "good enough for
    /// curation, not a full schema-preserving transform" convention FlattenObject already applies
    /// to nested JSON) into the same string-keyed row shape CSV/JSON/JSONL already produce, so
    /// every downstream consumer (quality checks, splitting, JSON Lines output) needs no
    /// Parquet-specific code of its own.</summary>
    private static async Task<List<Dictionary<string, string?>>> ReadParquetAsync(string path, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, string?>>();
        await using Stream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: ct);
        DataField[] fields = reader.Schema.GetDataFields();

        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            ct.ThrowIfCancellationRequested();
            using ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(groupIndex);
            int rowCountInGroup = checked((int)groupReader.RowCount);
            var columns = new List<(string Name, Array Data)>();
            foreach (DataField field in fields)
                columns.Add((field.Name, await ReadColumnAsync(groupReader, field, rowCountInGroup, ct)));

            for (int r = 0; r < rowCountInGroup; r++)
            {
                var row = new Dictionary<string, string?>();
                foreach ((string name, Array data) in columns)
                    row[name] = FormatCellValue(data.GetValue(r));
                rows.Add(row);
                if (rows.Count >= MaxRowsToInspect) return rows;
            }
        }
        return rows;
    }

    /// <summary>Parquet.Net 6.x's real column-read API is generic and type-aware
    /// (<c>ReadAsync&lt;T&gt;(field, Memory&lt;T&gt;, ...)</c> for a non-nullable column,
    /// <c>Memory&lt;T?&gt;</c> for a nullable one) rather than the single dynamically-typed
    /// "give me a column" call an earlier draft assumed - confirmed by inspecting the actual
    /// installed assembly's public API via reflection (no live Parquet.Net documentation was
    /// reachable from this session's network-restricted container), not guessed. Dispatches to the
    /// correctly-typed buffer for every primitive family a real Hugging Face Parquet dataset
    /// plausibly uses; an unrecognized column type surfaces as a clear per-file parse error
    /// (InspectAsync/SplitAsync's existing "Could not parse file" wrapping) rather than silently
    /// producing wrong values.</summary>
    private static Task<Array> ReadColumnAsync(ParquetRowGroupReader groupReader, DataField field, int rowCount, CancellationToken ct)
    {
        Type clrType = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
        // Parquet.Net's own DataField<string>.ClrType reports ReadOnlyMemory<char> internally
        // (confirmed by reflecting the real installed assembly, not assumed - no live Parquet.Net
        // documentation was reachable from this session's network-restricted container) even
        // though the actual read/write API for a string column is Memory<string?>/
        // IReadOnlyCollection<string>, unrelated to that internal representation.
        if (clrType == typeof(string) || clrType == typeof(ReadOnlyMemory<char>))
            return ReadStringColumnAsync(groupReader, field, rowCount, ct);
        if (clrType == typeof(byte[])) return ReadByteArrayColumnAsync(groupReader, field, rowCount, ct);
        if (clrType == typeof(bool)) return ReadValueColumnAsync<bool>(groupReader, field, rowCount, ct);
        if (clrType == typeof(byte)) return ReadValueColumnAsync<byte>(groupReader, field, rowCount, ct);
        if (clrType == typeof(sbyte)) return ReadValueColumnAsync<sbyte>(groupReader, field, rowCount, ct);
        if (clrType == typeof(short)) return ReadValueColumnAsync<short>(groupReader, field, rowCount, ct);
        if (clrType == typeof(ushort)) return ReadValueColumnAsync<ushort>(groupReader, field, rowCount, ct);
        if (clrType == typeof(int)) return ReadValueColumnAsync<int>(groupReader, field, rowCount, ct);
        if (clrType == typeof(uint)) return ReadValueColumnAsync<uint>(groupReader, field, rowCount, ct);
        if (clrType == typeof(long)) return ReadValueColumnAsync<long>(groupReader, field, rowCount, ct);
        if (clrType == typeof(ulong)) return ReadValueColumnAsync<ulong>(groupReader, field, rowCount, ct);
        if (clrType == typeof(float)) return ReadValueColumnAsync<float>(groupReader, field, rowCount, ct);
        if (clrType == typeof(double)) return ReadValueColumnAsync<double>(groupReader, field, rowCount, ct);
        if (clrType == typeof(decimal)) return ReadValueColumnAsync<decimal>(groupReader, field, rowCount, ct);
        if (clrType == typeof(DateTime)) return ReadValueColumnAsync<DateTime>(groupReader, field, rowCount, ct);
        if (clrType == typeof(TimeSpan)) return ReadValueColumnAsync<TimeSpan>(groupReader, field, rowCount, ct);
        if (clrType == typeof(Guid)) return ReadValueColumnAsync<Guid>(groupReader, field, rowCount, ct);
        throw new InvalidDataException($"Column \"{field.Name}\" has an unsupported Parquet type ({clrType.Name}).");
    }

    private static async Task<Array> ReadStringColumnAsync(ParquetRowGroupReader groupReader, DataField field, int rowCount, CancellationToken ct)
    {
        var buffer = new string?[rowCount];
        await groupReader.ReadAsync(field, buffer.AsMemory(), null, ct);
        return buffer;
    }

    private static async Task<Array> ReadByteArrayColumnAsync(ParquetRowGroupReader groupReader, DataField field, int rowCount, CancellationToken ct)
    {
        var buffer = new byte[]?[rowCount];
        await groupReader.ReadAsync(field, buffer.AsMemory(), null, ct);
        return buffer;
    }

    private static async Task<Array> ReadValueColumnAsync<T>(ParquetRowGroupReader groupReader, DataField field, int rowCount, CancellationToken ct)
        where T : struct
    {
        if (field.IsNullable)
        {
            var buffer = new T?[rowCount];
            await groupReader.ReadAsync<T>(field, buffer.AsMemory(), null, ct);
            return buffer;
        }
        else
        {
            var buffer = new T[rowCount];
            await groupReader.ReadAsync<T>(field, buffer.AsMemory(), null, ct);
            return buffer;
        }
    }

    /// <summary>Binary columns render as a bounded byte count rather than raw bytes/base64 - a
    /// curation report is meant to be skimmed by a person, and dumping arbitrary binary payloads
    /// as text would be neither readable nor useful; downstream training/eval consumers of a
    /// Parquet-sourced dataset overwhelmingly care about text/label columns, not embedded blobs.</summary>
    private static string? FormatCellValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => $"<{bytes.Length} bytes>",
        DateTime dt => dt.ToString("O"),
        _ => value.ToString(),
    };

    private static async Task<List<Dictionary<string, string?>>> ReadCsvAsync(string path, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, string?>>();
        using var reader = new StreamReader(path);
        List<string>? header = null;
        List<string>? record;
        while ((record = await ReadCsvRecordAsync(reader, ct)) != null)
        {
            if (header == null) { header = record; continue; }
            if (record.Count == 1 && record[0].Length == 0) continue; // trailing blank line
            var row = new Dictionary<string, string?>();
            for (int i = 0; i < header.Count; i++) row[header[i]] = i < record.Count ? record[i] : null;
            rows.Add(row);
            if (rows.Count >= MaxRowsToInspect) break;
        }
        return rows;
    }

    /// <summary>Minimal RFC4180-ish CSV record reader: handles quoted fields, embedded commas,
    /// escaped quotes ("" inside a quoted field), and embedded newlines inside quoted fields.
    /// Not a full spec implementation, but correct for the well-formed CSV files a real dataset
    /// download produces - not a naive Split(',') that would break on the first quoted field.</summary>
    private static async Task<List<string>?> ReadCsvRecordAsync(StreamReader reader, CancellationToken ct)
    {
        if (reader.EndOfStream) return null;
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool sawAnyChar = false;
        int ci;
        while ((ci = reader.Read()) != -1)
        {
            ct.ThrowIfCancellationRequested();
            char c = (char)ci;
            sawAnyChar = true;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* wait for \n, or a bare \r as a line end is rare enough to ignore here */ }
            else if (c == '\n') { fields.Add(field.ToString()); return fields; }
            else field.Append(c);
            await Task.Yield();
        }
        if (sawAnyChar) fields.Add(field.ToString());
        return fields.Count > 0 ? fields : null;
    }

    private static async Task<List<Dictionary<string, string?>>> ReadJsonArrayAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected a JSON array of objects at the top level.");

        var rows = new List<Dictionary<string, string?>>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            rows.Add(FlattenObject(item));
            if (rows.Count >= MaxRowsToInspect) break;
        }
        return rows;
    }

    private static async Task<List<Dictionary<string, string?>>> ReadJsonLinesAsync(string path, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, string?>>();
        using var reader = new StreamReader(path);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            rows.Add(FlattenObject(doc.RootElement));
            if (rows.Count >= MaxRowsToInspect) break;
        }
        return rows;
    }

    private static Dictionary<string, string?> FlattenObject(JsonElement element)
    {
        var row = new Dictionary<string, string?>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            row["value"] = element.ToString();
            return row;
        }
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            row[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => prop.Value.GetRawText(),
            };
        }
        return row;
    }

    private static async Task WriteRowsAsync(string path, List<Dictionary<string, string?>> rows, string format, CancellationToken ct)
    {
        if (format == "csv")
        {
            await using var writer = new StreamWriter(path);
            List<string> columns = rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>();
            await writer.WriteLineAsync(string.Join(',', columns.Select(EscapeCsvField)));
            foreach (Dictionary<string, string?> row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(string.Join(',', columns.Select(c => EscapeCsvField(row.GetValueOrDefault(c)))));
            }
        }
        else
        {
            await using var writer = new StreamWriter(path);
            foreach (Dictionary<string, string?> row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(row));
            }
        }
    }

    private static string EscapeCsvField(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static int CountDuplicates(List<Dictionary<string, string?>> rows)
    {
        var seen = new HashSet<string>();
        int duplicates = 0;
        foreach (Dictionary<string, string?> row in rows)
        {
            if (!seen.Add(RowKey(row))) duplicates++;
        }
        return duplicates;
    }

    private static string RowKey(Dictionary<string, string?> row) => string.Join(
        '', row.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value ?? string.Empty)));
}
