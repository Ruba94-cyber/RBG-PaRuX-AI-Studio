using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>One generation inside a loaded model session. Everything here was observed; unknown values stay null.</summary>
internal sealed record ArenaGeneration(
    string Text, double? TtftMs, double? PrefillTokensPerSecond, double? DecodeTokensPerSecond, int GeneratedTokens,
    double TotalMs, long? PeakMemoryBytes = null);

internal interface IArenaModelSession : IAsyncDisposable
{
    double LoadMs { get; }
    string Backend { get; }
    string Build { get; }
    string LaunchPlan { get; }
    Task<ArenaGeneration> GenerateAsync(string prompt, int maxTokens, CancellationToken ct);
}

/// <summary>Loads ONE model at a time (callers serialize). Implementations must stop the previous resident model first.</summary>
internal interface IArenaModelRunner
{
    Task<IArenaModelSession> LoadAsync(string modelPath, CancellationToken ct);
    /// <summary>Best-effort: bring back whatever model was resident before the arena run.</summary>
    Task RestoreAsync(CancellationToken ct);
}

internal sealed record ArenaTask(string Id, string Title, string Category, string Prompt, Func<string, ArenaCheckResultDto>? Check);

/// <summary>Built-in tasks. Automatic checks are static rules over the text: model-written code is NEVER executed.</summary>
internal static class ArenaTaskCatalog
{
    internal static readonly IReadOnlyList<ArenaTask> All = new List<ArenaTask>
    {
        new("chat-explain", "Explain simply", "chat",
            "Explain in three short sentences why the sky is blue, for a curious ten-year-old.", null),
        new("chat-email", "Polite reply", "chat",
            "Write a two-sentence polite reply declining a meeting invitation for Friday and proposing Monday instead.", null),
        new("reason-pens", "Simple arithmetic", "reasoning",
            "Pens cost 2 euros for every 3 pens. How many euros do 12 pens cost? Answer with the number only.",
            o => NumberCheck(o, 8m)),
        new("reason-order", "Ordering", "reasoning",
            "Tom is taller than Ann. Ann is taller than Raj. Who is the shortest? Answer with one word.",
            o => ContainsCheck(o, "raj")),
        new("code-fizzbuzz", "FizzBuzz function", "coding",
            "Write a Python function fizzbuzz(n) that returns a list of strings for 1..n using Fizz, Buzz and FizzBuzz. Reply with code only.",
            o => CodeCheck(o, "def fizzbuzz", "fizzbuzz", "fizz", "buzz", "%")),
        new("code-reverse", "Reverse words", "coding",
            "Write a Python function reverse_words(s) that reverses the order of the words in a sentence. Reply with code only.",
            o => CodeCheck(o, "def reverse_words", "split", "join")),
    };

    internal static ArenaTask? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    private static ArenaCheckResultDto Result(bool passed, string detail) =>
        new() { Kind = "automatic-check", Passed = passed, Detail = detail };

    private static ArenaCheckResultDto NumberCheck(string output, decimal expected)
    {
        MatchCollection numbers = Regex.Matches(output ?? string.Empty, @"-?\d+(?:\.\d+)?");
        if (numbers.Count == 0) return Result(false, "No number found in the answer.");
        bool ok = decimal.TryParse(numbers[^1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v) && v == expected;
        return Result(ok, ok ? "Final number matches the expected answer." : "Final number does not match the expected answer.");
    }

    private static ArenaCheckResultDto ContainsCheck(string output, string needle)
    {
        bool ok = (output ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase);
        return Result(ok, ok ? "Expected word present." : "Expected word missing.");
    }

    private static ArenaCheckResultDto CodeCheck(string output, params string[] required)
    {
        string text = output ?? string.Empty;
        string[] missing = required.Where(r => !text.Contains(r, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Result(missing.Length == 0,
            (missing.Length == 0 ? "Static rule passed" : "Static rule failed, missing: " + string.Join(", ", missing))
            + " (code was not executed).");
    }
}

/// <summary>User-judged vs automatic-check evidence derived from stored arena runs. Read-only and labelled; it never promotes a model.</summary>
internal sealed record ArenaEvidenceRow(string ModelFileName, string Category, int UserJudgedWins, int UserJudgedComparisons,
    int AutomaticPasses, int AutomaticChecks)
{
    internal const string Provenance = "measured locally; user-judged and automatic-check counts are separate evidence";
}

internal sealed class ModelArenaFile
{
    public int SchemaVersion { get; set; } = ModelArenaStore.CurrentSchemaVersion;
    public List<ArenaRunDto> Runs { get; set; } = new();
}

/// <summary>Atomic, schema-versioned, bounded arena run history.</summary>
internal sealed class ModelArenaStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumRuns = 30;
    internal const long MaximumFileBytes = 8 * 1024 * 1024;
    internal const string FileName = "model-arena-runs.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    internal ModelArenaStore() : this(ServerState.StateDirectory) { }
    internal ModelArenaStore(string directory) => _path = Path.Combine(directory, FileName);

    internal IReadOnlyList<ArenaRunDto> List()
    {
        lock (Gate) return Load().Runs.OrderByDescending(r => r.StartedUtc).ToList();
    }

    internal void Save(ArenaRunDto run)
    {
        lock (Gate)
        {
            ModelArenaFile file = Load();
            file.Runs.RemoveAll(r => r.Id == run.Id);
            file.Runs.Add(run);
            file.Runs = file.Runs.OrderBy(r => r.StartedUtc).TakeLast(MaximumRuns).ToList();
            file.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private ModelArenaFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new ModelArenaFile();
            var info = new FileInfo(_path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumFileBytes) return new ModelArenaFile();
            ModelArenaFile? f = JsonSerializer.Deserialize<ModelArenaFile>(File.ReadAllText(_path), Json);
            return f is null || f.SchemaVersion != CurrentSchemaVersion ? new ModelArenaFile() : f;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ModelArenaFile();
        }
    }
}

/// <summary>Runs the SAME prompts against 2-3 installed models strictly one after another (one resident model, never concurrent
/// on the GPU), under the shared GPU lane, with Gaming hard-off, cancellation and bounded per-model timeouts.
/// Results are evidence only: nothing here selects, promotes or reassigns a model.
/// <para>Smart Router hook (not wired, by design): <see cref="GetEvidence"/> returns labelled, read-only per-model/category rows.
/// A router change may display them as additional evidence next to its own signals but must keep the labels and must not let
/// user-judged or static-check results override a role assignment.</para></summary>
internal sealed class ModelArenaService
{
    internal const int MinModels = 2, MaxModels = 3, MaxPrompts = 6, MaxPromptChars = 4000, MinTokens = 16, MaxTokens = 1024;
    internal const int MaxOutputChars = 8000;
    internal static readonly TimeSpan DefaultModelTimeout = TimeSpan.FromMinutes(6);

    private readonly IArenaModelRunner _runner;
    private readonly ModelArenaStore _store;
    private readonly LocalBenchmarkHistoryStore _history;
    private readonly Func<string, string?> _resolveModel;
    private readonly Func<bool> _gamingActive;
    private readonly Func<string> _hardwareDigest;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>> _acquireLane;
    private readonly TimeSpan _modelTimeout;
    private readonly object _gate = new();
    private ArenaRunDto? _active;
    private CancellationTokenSource? _activeCts;
    private Task _activeTask = Task.CompletedTask;

    internal ModelArenaService(IArenaModelRunner runner, ModelArenaStore store, LocalBenchmarkHistoryStore history,
        Func<string, string?> resolveModel, Func<bool> gamingActive, Func<string> hardwareDigest,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? acquireLane = null, TimeSpan? modelTimeout = null)
    {
        _runner = runner; _store = store; _history = history; _resolveModel = resolveModel;
        _gamingActive = gamingActive; _hardwareDigest = hardwareDigest;
        _acquireLane = acquireLane ?? (ct => RuntimePerformanceCoordinator.Shared.AcquireAsync(ct));
        _modelTimeout = modelTimeout ?? DefaultModelTimeout;
    }

    internal IReadOnlyList<ArenaTaskDto> Tasks() => ArenaTaskCatalog.All.Select(t => new ArenaTaskDto
    {
        Id = t.Id, Title = t.Title, Category = t.Category, Prompt = t.Prompt, HasAutomaticCheck = t.Check is not null
    }).ToList();

    /// <summary>Throws InvalidDataException for a bad request and InvalidOperationException when busy or Gaming is active.</summary>
    internal ArenaRunDto Start(ArenaStartRequest request)
    {
        List<string> names = (request.ModelFileNames ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count < MinModels || names.Count > MaxModels)
            throw new InvalidDataException($"Pick {MinModels} or {MaxModels} different installed models.");
        var paths = new List<(string Name, string Path)>();
        foreach (string n in names)
        {
            string? p = _resolveModel(n);
            if (p is null) throw new InvalidDataException("Model is not installed: " + n);
            paths.Add((n, p));
        }
        var prompts = new List<ArenaTask>();
        foreach (string id in (request.TaskIds ?? new()).Distinct(StringComparer.Ordinal))
            prompts.Add(ArenaTaskCatalog.Find(id) ?? throw new InvalidDataException("Unknown task: " + id));
        string? custom = request.CustomPrompt?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            if (custom.Length > MaxPromptChars) throw new InvalidDataException("The custom prompt is too long.");
            prompts.Add(new ArenaTask("custom", "Your prompt", "custom", custom, null));
        }
        if (prompts.Count == 0 || prompts.Count > MaxPrompts)
            throw new InvalidDataException($"Choose between 1 and {MaxPrompts} prompts.");
        if (_gamingActive()) throw new InvalidOperationException("Gaming Mode is active; model comparison is blocked.");
        int maxTokens = Math.Clamp(request.MaxTokens <= 0 ? 256 : request.MaxTokens, MinTokens, MaxTokens);

        var run = new ArenaRunDto
        {
            Id = Guid.NewGuid().ToString("N"), Status = "running", Stage = "Queued", StartedUtc = DateTime.UtcNow,
            ModelFileNames = names,
            Prompts = prompts.Select(t => new ArenaPromptResultDto { TaskId = t.Id, Title = t.Title, Prompt = t.Prompt }).ToList()
        };
        lock (_gate)
        {
            if (_active is not null) throw new InvalidOperationException("A model comparison is already running.");
            _active = run;
            _activeCts = new CancellationTokenSource();
            CancellationToken token = _activeCts.Token;
            _activeTask = Task.Run(() => ExecuteAsync(run, paths, prompts, maxTokens, token));
        }
        return Snapshot(run);
    }

    internal bool Cancel(string id)
    {
        lock (_gate)
        {
            if (_active is null || _active.Id != id) return false;
            _activeCts?.Cancel();
            return true;
        }
    }

    internal Task WaitForIdleAsync() { lock (_gate) return _activeTask; }

    internal ArenaRunDto? Get(string id)
    {
        lock (_gate) if (_active is not null && _active.Id == id) return Snapshot(_active);
        return _store.List().FirstOrDefault(r => r.Id == id);
    }

    internal IReadOnlyList<ArenaRunDto> List()
    {
        var all = _store.List().ToList();
        lock (_gate)
            if (_active is not null) { all.RemoveAll(r => r.Id == _active.Id); all.Insert(0, Snapshot(_active)); }
        return all;
    }

    /// <summary>Records (or clears) the user's choice for one prompt. Only finished runs can be judged; the pick must be one of the run's models.</summary>
    internal ArenaRunDto? Judge(string id, ArenaJudgeRequest request)
    {
        ArenaRunDto? run = _store.List().FirstOrDefault(r => r.Id == id);
        if (run is null) return null;
        if (request.PromptIndex < 0 || request.PromptIndex >= run.Prompts.Count) throw new InvalidDataException("Unknown prompt.");
        ArenaPromptResultDto prompt = run.Prompts[request.PromptIndex];
        if (request.ModelFileName is null) { prompt.UserPick = null; prompt.UserPickUtc = null; }
        else
        {
            if (!prompt.Results.Any(r => r.Status == "completed"
                    && string.Equals(r.ModelFileName, request.ModelFileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pick a model that produced an answer for this prompt.");
            prompt.UserPick = prompt.Results.First(r => string.Equals(r.ModelFileName, request.ModelFileName, StringComparison.OrdinalIgnoreCase)).ModelFileName;
            prompt.UserPickUtc = DateTime.UtcNow;
        }
        _store.Save(run);
        return run;
    }

    /// <summary>Documented read-only Smart Router hook. See the class remarks.</summary>
    internal IReadOnlyList<ArenaEvidenceRow> GetEvidence() => BuildEvidence(_store.List());

    internal static IReadOnlyList<ArenaEvidenceRow> BuildEvidence(IEnumerable<ArenaRunDto> runs)
    {
        var rows = new Dictionary<(string, string), int[]>();
        foreach (ArenaRunDto run in runs.Where(r => r.Status == "completed"))
            foreach (ArenaPromptResultDto p in run.Prompts)
            {
                string cat = ArenaTaskCatalog.Find(p.TaskId)?.Category ?? "custom";
                int completed = p.Results.Count(r => r.Status == "completed");
                foreach (ArenaModelResultDto r in p.Results.Where(r => r.Status == "completed"))
                {
                    int[] v = rows.TryGetValue((r.ModelFileName, cat), out int[]? e) ? e : rows[(r.ModelFileName, cat)] = new int[4];
                    if (p.UserPick is not null && completed > 1)
                    {
                        v[1]++;
                        if (string.Equals(p.UserPick, r.ModelFileName, StringComparison.OrdinalIgnoreCase)) v[0]++;
                    }
                    if (r.Check.Kind == "automatic-check" && r.Check.Passed is bool passed) { v[3]++; if (passed) v[2]++; }
                }
            }
        return rows.Where(kv => kv.Value[1] > 0 || kv.Value[3] > 0)
            .Select(kv => new ArenaEvidenceRow(kv.Key.Item1, kv.Key.Item2, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
            .OrderBy(r => r.ModelFileName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Category, StringComparer.Ordinal).ToList();
    }

    private async Task ExecuteAsync(ArenaRunDto run, List<(string Name, string Path)> models, List<ArenaTask> tasks,
        int maxTokens, CancellationToken cancel)
    {
        IAsyncDisposable? lane = null;
        try
        {
            SetStage(run, "Waiting for the GPU lane");
            lane = await _acquireLane(cancel).ConfigureAwait(false);
            using var gaming = new GamingHardOffWatch(_gamingActive, cancel);
            CancellationToken token = gaming.Token;
            string hardware = SafeHardware();
            foreach (var (name, path) in models)
            {
                token.ThrowIfCancellationRequested();
                SetStage(run, "Loading " + name);
                await RunModelAsync(run, name, path, tasks, maxTokens, hardware, token).ConfigureAwait(false);
            }
            Finish(run, "completed", null);
        }
        catch (OperationCanceledException)
        {
            Finish(run, "cancelled", _gamingActive() ? "Gaming Mode started; the comparison was stopped." : "Cancelled.");
        }
        catch (Exception ex)
        {
            Finish(run, "failed", ex.Message);
        }
        finally
        {
            try { await _runner.RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            if (lane is not null) await lane.DisposeAsync().ConfigureAwait(false);
            lock (_gate) { _active = null; _activeCts?.Dispose(); _activeCts = null; }
        }
    }

    private async Task RunModelAsync(ArenaRunDto run, string name, string path, List<ArenaTask> tasks, int maxTokens,
        string hardware, CancellationToken token)
    {
        string? modelId = LocalBenchmarkIdentity.ModelId(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_modelTimeout);
        IArenaModelSession? session = null;
        try
        {
            session = await _runner.LoadAsync(path, timeout.Token).ConfigureAwait(false);
            for (int i = 0; i < tasks.Count; i++)
            {
                SetStage(run, $"{name}: prompt {i + 1} of {tasks.Count}");
                var result = new ArenaModelResultDto { ModelFileName = name, LoadMs = i == 0 ? session.LoadMs : null };
                try
                {
                    ArenaGeneration g = await session.GenerateAsync(tasks[i].Prompt, maxTokens, timeout.Token).ConfigureAwait(false);
                    string output = g.Text.Length > MaxOutputChars ? g.Text[..MaxOutputChars] : g.Text;
                    result.Status = "completed"; result.Output = output; result.TtftMs = g.TtftMs;
                    result.DecodeTokensPerSecond = g.DecodeTokensPerSecond; result.GeneratedTokens = g.GeneratedTokens;
                    result.TotalMs = g.TotalMs;
                    if (tasks[i].Check is { } check) result.Check = check(output);
                    if (modelId is not null)
                        _history.Append(new LocalBenchmarkEntry
                        {
                            HardwareDigest = hardware, ModelId = modelId, ModelLabel = name,
                            RuntimeBackend = session.Backend, RuntimeBuild = session.Build, LaunchPlan = session.LaunchPlan,
                            Workload = "arena:" + tasks[i].Id, TtftMs = g.TtftMs, PrefillTokensPerSecond = g.PrefillTokensPerSecond,
                            DecodeTokensPerSecond = g.DecodeTokensPerSecond, PeakMemoryBytes = g.PeakMemoryBytes,
                            LoadMs = i == 0 ? session.LoadMs : null
                        });
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    result.Status = "timed_out"; result.Error = "The model did not finish within the time limit.";
                    Record(run, i, result);
                    MarkRemaining(run, name, i + 1, tasks.Count, "timed_out", "Skipped after an earlier time-out for this model.");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Status = "failed"; result.Error = ex.Message;
                }
                Record(run, i, result);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            MarkRemaining(run, name, 0, tasks.Count, "timed_out", "The model did not load within the time limit.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkRemaining(run, name, 0, tasks.Count, "failed", ex.Message);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string SafeHardware()
    {
        try { return _hardwareDigest(); } catch (Exception) { return string.Empty; }
    }

    private void Record(ArenaRunDto run, int promptIndex, ArenaModelResultDto result)
    {
        lock (_gate) run.Prompts[promptIndex].Results.Add(result);
    }

    private void MarkRemaining(ArenaRunDto run, string name, int from, int count, string status, string error)
    {
        for (int i = from; i < count; i++)
            Record(run, i, new ArenaModelResultDto { ModelFileName = name, Status = status, Error = error });
    }

    private void SetStage(ArenaRunDto run, string stage)
    {
        lock (_gate) run.Stage = stage;
    }

    private void Finish(ArenaRunDto run, string status, string? error)
    {
        lock (_gate)
        {
            run.Status = status; run.Error = error; run.Stage = status == "completed" ? "Done" : run.Stage;
            run.CompletedUtc = DateTime.UtcNow;
        }
        try { _store.Save(Snapshot(run)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private ArenaRunDto Snapshot(ArenaRunDto run)
    {
        lock (_gate)
            return JsonSerializer.Deserialize<ArenaRunDto>(JsonSerializer.Serialize(run))!;
    }
}

/// <summary>Production runner: starts one model at a time through the shared text runtime and streams a chat completion over loopback.</summary>
internal sealed class LlamaServerArenaRunner : IArenaModelRunner
{
    private readonly LocalAiProcessManager _process;
    private string? _previousModel;
    private bool _captured;

    internal LlamaServerArenaRunner(LocalAiProcessManager process) => _process = process;

    public async Task<IArenaModelSession> LoadAsync(string modelPath, CancellationToken ct)
    {
        if (_process.GamingActive) throw new InvalidOperationException("Gaming Mode is active.");
        if (!_captured)
        {
            _captured = true;
            if (await _process.IsTextHealthyAsync(ct).ConfigureAwait(false))
            {
                string active = _process.GetActiveTextModelPath();
                _previousModel = string.IsNullOrWhiteSpace(active) ? null : active;
            }
        }
        if (await _process.IsMediaHealthyAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Media generation is active; stop it before comparing text models.");
        if (!_process.StopText()) throw new InvalidOperationException("The resident text runtime did not stop.");
        var clock = Stopwatch.StartNew();
        LocalAiProcessManager.RuntimeResult started = await _process.StartTextAsync(ct, modelPath).ConfigureAwait(false);
        if (!started.Success) throw new InvalidOperationException(started.Message);
        double loadMs = clock.Elapsed.TotalMilliseconds;
        bool gpu = await _process.HasHeavyGpuAsync(ct).ConfigureAwait(false);
        string backend = gpu ? "text-cuda" : "text-vulkan";
        string build = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
        return new Session(_process.TextEndpointUrl, loadMs, backend, build, started.Message);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        string? previous = _previousModel;
        _previousModel = null; _captured = false;
        if (previous is null || _process.GamingActive) return;
        _process.StopText();
        await _process.StartTextAsync(ct, previous).ConfigureAwait(false);
    }

    private sealed class Session(string endpoint, double loadMs, string backend, string build, string plan) : IArenaModelSession
    {
        public double LoadMs => loadMs;
        public string Backend => backend;
        public string Build => build;
        public string LaunchPlan => plan.Length > 160 ? plan[..160] : plan;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<ArenaGeneration> GenerateAsync(string prompt, int maxTokens, CancellationToken ct)
        {
            using var client = new HttpClient(ManagedRuntimePeers.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            string payload = JsonSerializer.Serialize(new
            {
                model = "local-coder", messages = new[] { new { role = "user", content = prompt } },
                max_tokens = maxTokens, temperature = 0.2, stream = true, cache_prompt = false
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var clock = Stopwatch.StartNew();
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Runtime answered HTTP " + (int)response.StatusCode + ".");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var text = new StringBuilder();
            double? ttft = null; int chunks = 0; double? firstAt = null;
            double? prefillTps = null, decodeTps = null; int predicted = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data == "[DONE]") break;
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out JsonElement delta)
                    && delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } piece)
                {
                    firstAt ??= clock.Elapsed.TotalMilliseconds;
                    ttft ??= firstAt;
                    text.Append(piece); chunks++;
                }
                if (root.TryGetProperty("timings", out JsonElement t))
                {
                    if (t.TryGetProperty("prompt_per_second", out JsonElement pp) && pp.TryGetDouble(out double ppv) && ppv > 0) prefillTps = ppv;
                    if (t.TryGetProperty("predicted_per_second", out JsonElement dp) && dp.TryGetDouble(out double dpv) && dpv > 0) decodeTps = dpv;
                    if (t.TryGetProperty("predicted_n", out JsonElement pn) && pn.TryGetInt32(out int pnv)) predicted = pnv;
                }
            }
            double total = clock.Elapsed.TotalMilliseconds;
            if (decodeTps is null && chunks > 1 && firstAt is double f && total > f)
                decodeTps = (chunks - 1) / ((total - f) / 1000.0); // measured from chunk arrival, not estimated from text
            return new ArenaGeneration(text.ToString(), ttft, prefillTps, decodeTps, predicted > 0 ? predicted : chunks, total);
        }
    }
}
