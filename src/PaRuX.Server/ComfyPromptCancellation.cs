using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// What PaRuX could confirm with ComfyUI after it cancelled, or timed out, a media job. Notice is
/// null only when ComfyUI confirmed that none of the job's prompts is still queued or running.
/// </summary>
internal sealed record ComfyCancelReport(
    bool Idle,
    bool Delivered,
    int PendingDeleteRequests,
    int InterruptRequests,
    string? Notice);

/// <summary>
/// Cancellation that has already been settled against ComfyUI. The media admission lease and the
/// execution gate are released only after this propagates, so the next job cannot start while
/// ComfyUI is still working on the cancelled one.
/// </summary>
internal sealed class ComfyCancellationException : OperationCanceledException
{
    public ComfyCancellationException(ComfyCancelReport report, CancellationToken token)
        : base(report.Notice ?? "Media generation was cancelled.", token)
    {
        Report = report;
    }

    public ComfyCancelReport Report { get; }
}

/// <summary>
/// Stops a PaRuX job's ComfyUI prompts. A queued prompt is removed from /queue. A running prompt is
/// interrupted only when it is the one running. Then the caller waits a bounded time for /queue to
/// show none of the job's prompts. Prompts are matched by prompt id or by the client id PaRuX sent
/// with the submission, which ComfyUI keeps in each queue entry's extra_data, so a prompt whose
/// POST response was lost is still found.
/// </summary>
internal static class ComfyPromptCancellation
{
    internal static readonly TimeSpan DefaultIdleBound = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InterruptRepeatInterval = TimeSpan.FromSeconds(1);

    internal sealed record QueueEntry(string PromptId, string? ClientId);

    internal static async Task<ComfyCancelReport> SettleAsync(
        HttpClient http,
        string baseUrl,
        IReadOnlyCollection<string> clientIds,
        IReadOnlyCollection<string> promptIds,
        TimeSpan idleBound,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var ownClients = new HashSet<string>(clientIds, StringComparer.Ordinal);
        var ownPrompts = new HashSet<string>(promptIds, StringComparer.Ordinal);

        // A fresh bound, not the job token: the caller's token is already signalled when this runs.
        using var bound = new CancellationTokenSource(idleBound);
        int pendingDeletes = 0;
        int interrupts = 0;
        bool delivered = true;
        string? lastFailure = null;
        string? lastQueueFailure = null;
        DateTime lastInterruptUtc = DateTime.MinValue;
        string lastBusy = string.Empty;

        while (true)
        {
            IReadOnlyList<QueueEntry> running;
            IReadOnlyList<QueueEntry> pending;
            try
            {
                (running, pending) = await ReadQueueAsync(http, baseUrl, bound.Token).ConfigureAwait(false);
                lastQueueFailure = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException
                                          or JsonException or OperationCanceledException)
            {
                // Keep the admission gate until ComfyUI answers or this bounded confirmation window expires.
                lastQueueFailure = ex.Message;
                if (bound.IsCancellationRequested) break;
                try
                {
                    await Task.Delay(pollInterval, bound.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var ownPending = pending.Where(IsOwn).ToList();
            var ownRunning = running.Where(IsOwn).ToList();
            if (ownPending.Count == 0 && ownRunning.Count == 0)
            {
                return new ComfyCancelReport(
                    Idle: true,
                    Delivered: delivered,
                    PendingDeleteRequests: pendingDeletes,
                    InterruptRequests: interrupts,
                    Notice: delivered
                        ? null
                        : "ComfyUI reports no queued or running prompt for this job, but a stop request failed: " + lastFailure);
            }

            lastBusy = string.Join(", ", ownRunning.Concat(ownPending).Select(entry => entry.PromptId));
            foreach (QueueEntry entry in ownPending)
            {
                string? error = await PostAsync(http, baseUrl + "/queue",
                    new JsonObject { ["delete"] = new JsonArray(JsonValue.Create(entry.PromptId)) },
                    bound.Token).ConfigureAwait(false);
                if (error == null) pendingDeletes++;
                else { delivered = false; lastFailure = error; }
            }

            // Interrupt only while one of this job's prompts is the running one. Repeat at most once a second
            // because ComfyUI checks its interrupt flag between sampler steps, not instantly.
            if (ownRunning.Count > 0 && DateTime.UtcNow - lastInterruptUtc >= InterruptRepeatInterval)
            {
                lastInterruptUtc = DateTime.UtcNow;
                string? error = await PostAsync(http, baseUrl + "/interrupt", new JsonObject(), bound.Token)
                    .ConfigureAwait(false);
                if (error == null) interrupts++;
                else { delivered = false; lastFailure = error; }
            }

            if (bound.IsCancellationRequested) break;
            try
            {
                await Task.Delay(pollInterval, bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        string message = lastQueueFailure == null
            ? "ComfyUI still reports prompt(s) " + lastBusy + " queued or running after "
                + FormatSeconds(idleBound) + " s, so the GPU may stay busy until ComfyUI finishes them."
            : "PaRuX could not confirm with ComfyUI that the job stopped within " + FormatSeconds(idleBound)
                + " s (last queue check failed: " + lastQueueFailure + "). If ComfyUI is still running a prompt, the GPU may stay busy.";
        if (lastFailure != null) message += " Last stop request: " + lastFailure;
        return new ComfyCancelReport(
            Idle: false,
            Delivered: delivered && lastQueueFailure == null,
            PendingDeleteRequests: pendingDeletes,
            InterruptRequests: interrupts,
            Notice: message);

        bool IsOwn(QueueEntry entry) =>
            ownPrompts.Contains(entry.PromptId) || (entry.ClientId != null && ownClients.Contains(entry.ClientId));
    }

    internal static async Task<(IReadOnlyList<QueueEntry> Running, IReadOnlyList<QueueEntry> Pending)> ReadQueueAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(baseUrl + "/queue", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        JsonNode root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("ComfyUI /queue returned an empty body.");
        return (ParseEntries(root["queue_running"]), ParseEntries(root["queue_pending"]));
    }

    // ComfyUI stores each queue item as [number, prompt_id, prompt, extra_data, outputs_to_execute]
    // and puts the submitting client_id into extra_data.
    internal static IReadOnlyList<QueueEntry> ParseEntries(JsonNode? list)
    {
        if (list is not JsonArray array) throw new InvalidDataException("ComfyUI /queue has no queue list.");
        var entries = new List<QueueEntry>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonArray tuple || tuple.Count < 4)
                throw new InvalidDataException("ComfyUI queue entry has an unexpected shape.");
            string promptId = tuple[1]?.GetValue<string>()
                ?? throw new InvalidDataException("ComfyUI queue entry has no prompt id.");
            string? clientId = tuple[3]?["client_id"]?.GetValue<string>();
            entries.Add(new QueueEntry(promptId, clientId));
        }
        return entries;
    }

    private static async Task<string?> PostAsync(HttpClient http, string url, JsonNode body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : url + " answered HTTP " + (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return url + " failed: " + ex.Message;
        }
    }

    private static string FormatSeconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
