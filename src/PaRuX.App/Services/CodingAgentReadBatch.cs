using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>One read-only request inside a <c>batch_read</c> action.</summary>
    public sealed class CodingBatchReadItem
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("pattern")] public string Pattern { get; set; } = string.Empty;
        [JsonPropertyName("regex")] public bool Regex { get; set; }
        [JsonPropertyName("line")] public int? Line { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
        [JsonPropertyName("offset")] public int? Offset { get; set; }
        [JsonPropertyName("length")] public int? Length { get; set; }
    }

    public sealed record CodingBatchReadResult(string Description, string Observation);

    /// <summary>
    /// Batched, independent, read-only tool calls. Reading is not inference: running several reads
    /// in one step removes whole model round-trips (the dominant cost on local hardware) without
    /// assuming any extra inference concurrency. Only side-effect-free actions are allowed, the
    /// batch is bounded in size, concurrency and output, results keep request order, one failing
    /// item never hides the others, and cancellation propagates.
    /// </summary>
    public static class CodingAgentReadBatch
    {
        public const int MaxItems = 6;
        public const int MaxConcurrency = 4;
        public const int MaxTotalObservationChars = 24_000;

        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "read_file", "read_file_range", "read_file_lines", "grep_search", "glob_search", "list_directory"
        };

        public static bool IsBatchable(string? action) =>
            action != null && Allowed.Contains(action.Trim().ToLowerInvariant());

        public static bool TryValidate(IReadOnlyList<CodingBatchReadItem>? items, out string error)
        {
            error = string.Empty;
            if (items == null || items.Count < 2 || items.Count > MaxItems)
            { error = "requests must contain between 2 and " + MaxItems + " read-only items"; return false; }
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (CodingBatchReadItem item in items)
            {
                if (item == null || !IsBatchable(item.Action))
                { error = "requests may only contain read_file, read_file_range, read_file_lines, grep_search, glob_search or list_directory"; return false; }
                string kind = item.Action.Trim().ToLowerInvariant();
                bool hasPath = !string.IsNullOrWhiteSpace(item.Path);
                bool hasPattern = !string.IsNullOrWhiteSpace(item.Pattern);
                if (kind is "read_file" or "read_file_range" or "read_file_lines" && !hasPath)
                { error = kind + " item requires path"; return false; }
                if (kind is "grep_search" or "glob_search" && !hasPattern)
                { error = kind + " item requires pattern"; return false; }
                if (kind == "read_file_lines" && item.Line is null or < 1)
                { error = "read_file_lines item requires line >= 1"; return false; }
                if (item.Count is < 1 or > 100) { error = "item count must be between 1 and 100"; return false; }
                if (item.Offset is < 0 or > 16_777_216) { error = "item offset is outside the schema range"; return false; }
                if (item.Length is < 1 or > 64_000) { error = "item length must be between 1 and 64000"; return false; }
                if ((item.Path?.Length ?? 0) > 4096 || (item.Pattern?.Length ?? 0) > 4096)
                { error = "item path/pattern exceeds 4096 characters"; return false; }
                if (!identities.Add(Key(item)))
                { error = "requests contains a duplicate item"; return false; }
            }
            return true;
        }

        /// <summary>Stable identity used for duplicate detection and the repeat guard.</summary>
        public static string Key(CodingBatchReadItem item) => string.Join("|",
            item.Action.Trim().ToLowerInvariant(), item.Path, item.Pattern, item.Regex, item.Line, item.Count, item.Offset, item.Length);

        /// <summary>
        /// Runs every item with bounded concurrency and returns the results in request order. An item
        /// that throws (other than cancellation) becomes a failed-item result so the rest survive.
        /// </summary>
        public static async Task<IReadOnlyList<CodingBatchReadResult>> ExecuteAsync(
            IReadOnlyList<CodingBatchReadItem> items,
            Func<CodingBatchReadItem, CancellationToken, Task<CodingBatchReadResult>> executeOne,
            CancellationToken ct)
        {
            var results = new CodingBatchReadResult[items.Count];
            using var gate = new SemaphoreSlim(MaxConcurrency);
            IEnumerable<Task> tasks = items.Select(async (item, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // Task.Run: grep/glob/list/range reads do synchronous file IO, so without a thread hop
                    // the "concurrent" items would run one after another.
                    results[index] = await Task.Run(() => executeOne(item, ct), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results[index] = new CodingBatchReadResult(
                        item.Action + " \"" + (string.IsNullOrEmpty(item.Path) ? item.Pattern : item.Path) + "\" (failed)",
                        "(" + ex.GetType().Name + ": " + ex.Message + ")");
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }

        /// <summary>Merges ordered results into one observation within a total character budget.</summary>
        public static string Render(IReadOnlyList<CodingBatchReadResult> results, int maxChars = MaxTotalObservationChars)
        {
            int perItem = Math.Max(400, maxChars / Math.Max(1, results.Count));
            var sb = new StringBuilder();
            for (int i = 0; i < results.Count; i++)
            {
                string body = results[i].Observation ?? string.Empty;
                if (body.Length > perItem)
                {
                    // Single reads end with an "[FULL EVIDENCE artifactId=...]" / "[FULL FILE ...]" marker the
                    // model needs to fetch the rest; never cut it off.
                    int marker = body.LastIndexOf("\n[FULL ", StringComparison.Ordinal);
                    string tail = marker >= 0 ? body[marker..] : string.Empty;
                    int head = Math.Max(200, perItem - tail.Length);
                    if (marker >= 0 && marker < head) head = marker;
                    body = body[..Math.Min(head, body.Length)] + "\n[item output truncated at " + perItem + " chars]" + tail;
                }
                sb.Append("--- [").Append(i + 1).Append('/').Append(results.Count).Append("] ")
                  .Append(results[i].Description).Append('\n').Append(body).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
