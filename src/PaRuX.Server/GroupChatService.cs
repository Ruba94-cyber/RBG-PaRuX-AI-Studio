using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Group Chat: 2-4 local models take turns replying to one prompt, each seeing every earlier
/// participant's reply this round before its own turn. The full panel and reconciliation hold one
/// exclusive GPU residency lease because changing participants is a runtime mutation, not ordinary
/// shared inference. This prevents a participant load from replacing the model under another chat.
/// The lease is released before the user's default profile is restored through the same exclusive
/// transition authority.
/// </summary>
public sealed class GroupChatService
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly CancellationTokenSource Cancellation = new();
        public required GroupChatStatus Status { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _runs = new();
    private readonly LocalRuntimeService _runtime;
    private readonly ChatProxyService _chat;
    private readonly ModelCompareService _modelLister;
    private readonly RuntimeManagerService _runtimeManager;

    public GroupChatService(
        LocalRuntimeService runtime,
        ChatProxyService chat,
        ModelCompareService modelLister,
        RuntimeManagerService runtimeManager)
    {
        _runtime = runtime;
        _chat = chat;
        _modelLister = modelLister;
        _runtimeManager = runtimeManager;
    }

    public IReadOnlyList<ModelCompareAvailableModel> ListAvailableModels() => _modelLister.ListAvailableModels();

    public GroupChatStatus Create(GroupChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidDataException("A prompt is required.");
        if (request.Participants == null || request.Participants.Count < 2 || request.Participants.Count > 4)
            throw new InvalidDataException("Group chat needs between 2 and 4 participants.");

        var resolved = new List<(string Path, string Label)>();
        for (int i = 0; i < request.Participants.Count; i++)
        {
            ModelCompareCandidate candidate = request.Participants[i];
            string path = ResolveModelPath(candidate, "Participant " + (i + 1));
            string label = string.IsNullOrWhiteSpace(candidate.Label) ? Path.GetFileNameWithoutExtension(path) : candidate.Label;
            resolved.Add((path, label));
        }

        DateTime now = DateTime.UtcNow;
        var entry = new Entry
        {
            Status = new GroupChatStatus { Id = Guid.NewGuid().ToString("N"), Status = "queued", Stage = "Queued", CreatedUtc = now, UpdatedUtc = now }
        };
        _runs[entry.Status.Id] = entry;
        _ = Task.Run(() => RunAsync(entry, request.Prompt, resolved));
        return Snapshot(entry);
    }

    private static string ResolveModelPath(ModelCompareCandidate candidate, string which)
    {
        string kind = (candidate.Kind ?? string.Empty).Trim().ToLowerInvariant();
        string path = kind switch
        {
            "standard" => LocalAiPaths.StandardModel,
            "uncensored" => LocalAiPaths.UncensoredModel,
            "path" => candidate.ModelPath ?? string.Empty,
            _ => throw new InvalidDataException(which + ": Kind must be \"standard\", \"uncensored\", or \"path\".")
        };
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException(which + ": no model path was resolved.");
        if (!File.Exists(path)) throw new InvalidDataException(which + ": file not found: " + path);
        return path;
    }

    public IReadOnlyList<GroupChatStatus> List() => _runs.Values
        .Select(Snapshot).OrderByDescending(x => x.CreatedUtc).ToList();

    public GroupChatStatus? Get(string id) =>
        _runs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool Cancel(string id)
    {
        if (!_runs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
        }
        entry.Cancellation.Cancel();
        return true;
    }

    private async Task<GroupChatVerdict?> TryBuildVerdictAsync(
        string prompt, IReadOnlyList<GroupChatTurn> turns, CancellationToken ct)
    {
        var request = new StringBuilder();
        request.Append("Original question: ").AppendLine(prompt).AppendLine();
        request.AppendLine("Panel replies:");
        foreach (GroupChatTurn turn in turns)
        {
            request.Append("### ").AppendLine(turn.Label);
            request.AppendLine(Truncate(turn.Response, MaxVerdictCharactersPerTurn)).AppendLine();
        }
        request.AppendLine(
            "Reconcile these replies. Reply with ONLY a JSON object of the form "
            + "{\"answer\":\"...\",\"disagreements\":[\"...\"],\"supportedBy\":[\"label\"]}. "
            + "\"answer\" is the best combined answer to the original question. \"disagreements\" "
            + "lists points the panel genuinely did not agree on - leave it empty only if they truly "
            + "converged, and never hide a real conflict. \"supportedBy\" lists the panel labels "
            + "whose replies support the answer.");

        string raw;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(VerdictTimeout);
            raw = await _chat.CompleteOnceAsync(request.ToString(), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        return ParseVerdict(raw, turns);
    }

    internal const int MaxVerdictCharactersPerTurn = 2_000;
    internal static readonly TimeSpan VerdictTimeout = TimeSpan.FromMinutes(3);

    internal static GroupChatVerdict? ParseVerdict(string raw, IReadOnlyList<GroupChatTurn> turns)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(raw[start..(end + 1)]); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        string answer = obj["answer"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (answer.Length == 0) return null;

        var knownLabels = new HashSet<string>(turns.Select(t => t.Label), StringComparer.OrdinalIgnoreCase);
        var verdict = new GroupChatVerdict { Answer = answer };

        if (obj["disagreements"] is JsonArray disagreements)
        {
            foreach (JsonNode? item in disagreements)
            {
                if (item is JsonValue value && value.TryGetValue(out string? text)
                    && !string.IsNullOrWhiteSpace(text))
                    verdict.Disagreements.Add(text.Trim());
            }
        }

        if (obj["supportedBy"] is JsonArray supporters)
        {
            foreach (JsonNode? item in supporters)
            {
                if (item is JsonValue value && value.TryGetValue(out string? label)
                    && !string.IsNullOrWhiteSpace(label) && knownLabels.Contains(label.Trim()))
                    verdict.SupportedBy.Add(label.Trim());
            }
        }
        return verdict;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max] + "…";

    private async Task RunAsync(Entry entry, string prompt, List<(string Path, string Label)> participants)
    {
        CancellationToken ct = entry.Cancellation.Token;
        try
        {
            await using (MediaExecutionCoordinator.Lease exclusive = await MediaExecutionCoordinator.Current
                .AcquireAsync("text-exclusive:group-chat", ct).ConfigureAwait(false))
            {
                foreach ((string path, string label) in participants)
                {
                    Update(entry, "running", "Loading " + label + "…");
                    RuntimeLoadResult load = await _runtimeManager.StartArbitraryTextModelWithinLeaseAsync(
                        exclusive, path, label, "Compare: ", adapterPaths: null, role: null,
                        persistRoleAssignment: false, ct: ct).ConfigureAwait(false);
                    if (!load.Success) throw new InvalidOperationException("Failed to load " + label + ": " + load.Message);

                    Update(entry, "running", label + " is responding…");
                    string turnPrompt = BuildTurnPrompt(prompt, entry);
                    string response = await _chat.CompleteOnceAsync(turnPrompt, ct);
                    lock (entry.Gate)
                    {
                        entry.Status.Turns.Add(new GroupChatTurn { Label = label, Response = response });
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                }

                List<GroupChatTurn> turns;
                lock (entry.Gate) turns = entry.Status.Turns.ToList();
                if (turns.Count > 1)
                {
                    Update(entry, "running", "Reconciling the panel…");
                    GroupChatVerdict? verdict = await TryBuildVerdictAsync(prompt, turns, ct);
                    lock (entry.Gate)
                    {
                        entry.Status.Verdict = verdict;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                    }
                }
            }

            Update(entry, "restoring", "Restoring your default model…");
            string? restoreError = await TryRestoreDefaultProfileAsync();
            if (restoreError != null)
                throw new InvalidOperationException("Group chat finished, but PaRuX could not restore the default text model: " + restoreError);

            lock (entry.Gate)
            {
                entry.Status.Status = "completed";
                entry.Status.Stage = "Completed";
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            string? restoreError = await TryRestoreDefaultProfileAsync();
            lock (entry.Gate)
            {
                entry.Status.Status = "cancelled";
                entry.Status.Stage = restoreError == null ? "Cancelled" : "Cancelled; default model restore failed";
                entry.Status.Error = restoreError == null ? null : "Default text model restore failed: " + restoreError;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            string? restoreError = await TryRestoreDefaultProfileAsync();
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Failed";
                entry.Status.Error = restoreError == null
                    ? ex.Message
                    : ex.Message + " | Default text model restore also failed: " + restoreError;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    private static string BuildTurnPrompt(string prompt, Entry entry)
    {
        List<GroupChatTurn> priorTurns;
        lock (entry.Gate) priorTurns = new List<GroupChatTurn>(entry.Status.Turns);
        if (priorTurns.Count == 0) return prompt;

        var sb = new StringBuilder();
        sb.Append("Group chat - the user asked: ").Append(prompt).Append("\n\n");
        sb.Append("Other participants have already replied:\n\n");
        foreach (GroupChatTurn turn in priorTurns)
            sb.Append('[').Append(turn.Label).Append("]: ").Append(turn.Response).Append("\n\n");
        sb.Append("Now give your own reply. You may agree, disagree, or add something new - do not just repeat what was already said.");
        return sb.ToString();
    }

    private async Task<string?> TryRestoreDefaultProfileAsync()
    {
        try
        {
            LocalRuntimeService.RuntimeResult result = await _runtime
                .RestoreDefaultTextExclusivelyAsync(CancellationToken.None).ConfigureAwait(false);
            return result.Success ? null : result.Message;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static void Update(Entry entry, string status, string stage)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = status;
            entry.Status.Stage = stage;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static GroupChatStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            GroupChatStatus s = entry.Status;
            return new GroupChatStatus
            {
                Id = s.Id, Status = s.Status, Stage = s.Stage, Error = s.Error,
                CreatedUtc = s.CreatedUtc, UpdatedUtc = s.UpdatedUtc,
                Turns = s.Turns.Select(turn => new GroupChatTurn
                {
                    Label = turn.Label,
                    Response = turn.Response
                }).ToList(),
                Verdict = s.Verdict == null ? null : new GroupChatVerdict
                {
                    Answer = s.Verdict.Answer,
                    Disagreements = new List<string>(s.Verdict.Disagreements),
                    SupportedBy = new List<string>(s.Verdict.SupportedBy)
                }
            };
        }
    }
}
