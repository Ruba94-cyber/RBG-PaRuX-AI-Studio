using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Model Compare runs one prompt against two locally available GGUF models sequentially because
/// PaRuX owns one physical text-runtime slot. The whole comparison owns an exclusive GPU residency
/// lease: swapping Model A/Model B is a runtime mutation, not shared inference, so an ordinary chat
/// cannot keep streaming on a model that the comparison is about to replace. The lease is released
/// before restoring the user's default profile through the same exclusive transition authority.
/// </summary>
public sealed class ModelCompareService
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly CancellationTokenSource Cancellation = new();
        public required ModelCompareStatus Status { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _runs = new();
    private readonly LocalRuntimeService _runtime;
    private readonly ChatProxyService _chat;
    private readonly RuntimeManagerService _runtimeManager;

    public ModelCompareService(
        LocalRuntimeService runtime,
        ChatProxyService chat,
        RuntimeManagerService runtimeManager)
    {
        _runtime = runtime;
        _chat = chat;
        _runtimeManager = runtimeManager;
    }

    private static string PaRuXDownloadedModelsDirectory => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "models");

    public IReadOnlyList<ModelCompareAvailableModel> ListAvailableModels()
    {
        var results = new List<ModelCompareAvailableModel>();

        if (File.Exists(LocalAiPaths.StandardModel))
            results.Add(new ModelCompareAvailableModel
            {
                Kind = "standard", Label = "Standard (Qwen3.8-27B)",
                ModelPath = LocalAiPaths.StandardModel, SizeBytes = SafeLength(LocalAiPaths.StandardModel)
            });
        if (File.Exists(LocalAiPaths.UncensoredModel))
            results.Add(new ModelCompareAvailableModel
            {
                Kind = "uncensored", Label = "Uncensored (Qwen3.8-27B)",
                ModelPath = LocalAiPaths.UncensoredModel, SizeBytes = SafeLength(LocalAiPaths.UncensoredModel)
            });

        AddGgufFiles(results, LocalAiPaths.ModelsPath);
        AddGgufFiles(results, PaRuXDownloadedModelsDirectory);

        return results.OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddGgufFiles(List<ModelCompareAvailableModel> results, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.gguf",
                new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false });
        }
        catch { return; }

        foreach (string file in files)
        {
            if (results.Any(r => string.Equals(r.ModelPath, file, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add(new ModelCompareAvailableModel
            {
                Kind = "path", Label = Path.GetFileNameWithoutExtension(file),
                ModelPath = file, SizeBytes = SafeLength(file)
            });
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    public ModelCompareStatus Create(ModelCompareRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidDataException("A prompt is required.");

        string pathA = ResolveModelPath(request.ModelA, "Model A");
        string pathB = ResolveModelPath(request.ModelB, "Model B");

        DateTime now = DateTime.UtcNow;
        var entry = new Entry
        {
            Status = new ModelCompareStatus
            {
                Id = Guid.NewGuid().ToString("N"), Status = "queued", Stage = "Queued",
                CreatedUtc = now, UpdatedUtc = now
            }
        };
        _runs[entry.Status.Id] = entry;
        _ = Task.Run(() => RunAsync(entry, request, pathA, pathB));
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

    public IReadOnlyList<ModelCompareStatus> List() => _runs.Values
        .Select(Snapshot).OrderByDescending(x => x.CreatedUtc).ToList();

    public ModelCompareStatus? Get(string id) =>
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

    private async Task RunAsync(Entry entry, ModelCompareRequest request, string pathA, string pathB)
    {
        CancellationToken ct = entry.Cancellation.Token;
        string labelA = string.IsNullOrWhiteSpace(request.ModelA.Label) ? Path.GetFileName(pathA) : request.ModelA.Label;
        string labelB = string.IsNullOrWhiteSpace(request.ModelB.Label) ? Path.GetFileName(pathB) : request.ModelB.Label;

        try
        {
            string responseA;
            string responseB;
            await using (MediaExecutionCoordinator.Lease exclusive = await MediaExecutionCoordinator.Current
                .AcquireAsync("text-exclusive:model-compare", ct).ConfigureAwait(false))
            {
                Update(entry, "loading_a", "Loading Model A…");
                RuntimeLoadResult loadA = await _runtimeManager.StartArbitraryTextModelWithinLeaseAsync(
                    exclusive, pathA, labelA, "Compare: ", adapterPaths: null, role: null,
                    persistRoleAssignment: false, ct: ct).ConfigureAwait(false);
                if (!loadA.Success) throw new InvalidOperationException("Failed to load Model A: " + loadA.Message);

                Update(entry, "generating_a", "Generating Response A…");
                responseA = await _chat.CompleteOnceAsync(request.Prompt, ct);
                lock (entry.Gate)
                {
                    entry.Status.ResponseA = new ModelCompareResponseSide { Label = "Response A", Response = responseA };
                    entry.Status.ModelALabel = labelA;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }

                Update(entry, "loading_b", "Loading Model B…");
                RuntimeLoadResult loadB = await _runtimeManager.StartArbitraryTextModelWithinLeaseAsync(
                    exclusive, pathB, labelB, "Compare: ", adapterPaths: null, role: null,
                    persistRoleAssignment: false, ct: ct).ConfigureAwait(false);
                if (!loadB.Success) throw new InvalidOperationException("Failed to load Model B: " + loadB.Message);

                Update(entry, "generating_b", "Generating Response B…");
                responseB = await _chat.CompleteOnceAsync(request.Prompt, ct);
                lock (entry.Gate)
                {
                    entry.Status.ResponseB = new ModelCompareResponseSide { Label = "Response B", Response = responseB };
                    entry.Status.ModelBLabel = labelB;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }

            Update(entry, "restoring", "Restoring your default model…");
            string? restoreError = await TryRestoreDefaultProfileAsync();
            if (restoreError != null)
                throw new InvalidOperationException("Comparison finished, but PaRuX could not restore the default text model: " + restoreError);

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

    private static ModelCompareStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            ModelCompareStatus s = entry.Status;
            return new ModelCompareStatus
            {
                Id = s.Id, Status = s.Status, Stage = s.Stage, Error = s.Error,
                CreatedUtc = s.CreatedUtc, UpdatedUtc = s.UpdatedUtc,
                ResponseA = s.ResponseA, ResponseB = s.ResponseB,
                ModelALabel = s.ModelALabel, ModelBLabel = s.ModelBLabel
            };
        }
    }
}
