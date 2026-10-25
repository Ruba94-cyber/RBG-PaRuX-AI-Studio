using System.Text.Json;

namespace PaRuX.Server;

// Asks ComfyUI what it still holds for a media job's prompts after a Core restart. A prompt matches by its prompt id,
// or by the client id that ComfyUI keeps with each queue entry (the same rule ComfyPromptCancellation uses), so a
// prompt whose submit response was lost is still found.
internal static class ComfyPromptRecovery
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    internal enum State { Running, Finished, Failed, Gone, Unreachable }

    internal sealed record Probe(State State, string? PromptId, string? OutputPath, string? Detail, string? ClientId = null);

    internal static async Task<Probe> ProbeAsync(HttpClient http, string baseUrl, string outputRoot,
        IReadOnlyCollection<string> clientIds, IReadOnlyCollection<string> promptIds, CancellationToken ct, string? saveNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ProbeTimeout);
        try
        {
            (IReadOnlyList<ComfyPromptCancellation.QueueEntry> running, IReadOnlyList<ComfyPromptCancellation.QueueEntry> pending) =
                await ComfyPromptCancellation.ReadQueueAsync(http, baseUrl, bound.Token).ConfigureAwait(false);
            foreach (ComfyPromptCancellation.QueueEntry entry in running.Concat(pending))
            {
                if (promptIds.Contains(entry.PromptId) || (entry.ClientId != null && clientIds.Contains(entry.ClientId)))
                    return new Probe(State.Running, entry.PromptId, null, null, entry.ClientId);
            }

            // Not queued any more: it either finished (history says so) or ComfyUI no longer knows it. The newest prompt first.
            foreach (string promptId in promptIds.Reverse())
            {
                using JsonDocument? history = await ReadHistoryAsync(http, baseUrl, promptId, bound.Token).ConfigureAwait(false);
                if (history == null || !history.RootElement.TryGetProperty(promptId, out JsonElement entry)) continue;
                if (!entry.TryGetProperty("status", out JsonElement status)) continue;
                if (status.TryGetProperty("status_str", out JsonElement statusText)
                    && string.Equals(statusText.GetString(), "error", StringComparison.Ordinal))
                    return new Probe(State.Failed, promptId, null, "ComfyUI reported an error for this prompt.");
                if (status.TryGetProperty("completed", out JsonElement completed) && completed.GetBoolean())
                {
                    string? output;
                    try
                    {
                        output = entry.TryGetProperty("outputs", out JsonElement outputs)
                            ? ComfySavedOutputValidator.FindSavedMediaPath(outputs, outputRoot, saveNodeId)
                            : null;
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        return new Probe(State.Failed, promptId, null, "ComfyUI reported an invalid image output path.");
                    }
                    return output == null
                        ? new Probe(State.Failed, promptId, null, "ComfyUI finished the prompt without a saved media output.")
                        : new Probe(State.Finished, promptId, output, null);
                }
            }
            return new Probe(State.Gone, null, null, "ComfyUI no longer holds a prompt for this job.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException))
        {
            // Nothing could be confirmed. Report that rather than implying the prompt is gone or finished.
            string reason = bound.IsCancellationRequested ? "ComfyUI did not answer within " + (int)ProbeTimeout.TotalSeconds + " s" : ex.GetType().Name;
            return new Probe(State.Unreachable, null, null, reason);
        }
    }

    private static async Task<JsonDocument?> ReadHistoryAsync(HttpClient http, string baseUrl, string promptId, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(baseUrl + "/history/" + promptId, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("ComfyUI history endpoint returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

}
