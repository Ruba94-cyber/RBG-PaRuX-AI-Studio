using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

// parux - command-line surface over the exact same PaRuX Core used by Windows and Android.
// There is no second inference/agent implementation here. Agent/project commands create, inspect,
// stop, resume and open the same durable TaskStore/worktrees/checkpoints the desktop workers own.

string bootstrapPath = Path.Combine(PaRuXDataRoot.Path, "server", "bootstrap.json");
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
TimeSpan pollTimeout = TimeSpan.FromMinutes(30);
TimeSpan pollInterval = TimeSpan.FromSeconds(1);
// Exit code for "paused, waiting for a user answer": distinct from 1 (failure) so scripts can answer and resume.
const int AgentExitWaitingForAnswer = 2;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
switch (command)
{
    case "serve": Environment.ExitCode = await PaRuX.Cli.CliServerLauncher.RunAsync(cts.Token); break;
    case "status": await RunStatusAsync(cts.Token); break;
    case "models": await RunModelsAsync(cts.Token); break;
    case "chat": await RunChatAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "download": await RunDownloadAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "benchmark": await RunBenchmarkAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "eval": await RunEvalAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "train": await RunTrainAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "agent": await RunAgentCommandAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "project": await RunProjectCommandAsync(args.Skip(1).ToArray(), cts.Token); break;
    case "help": case "--help": case "-h": PrintHelp(); break;
    default:
        Console.Error.WriteLine($"Unknown command \"{command}\".");
        PrintHelp();
        Environment.ExitCode = 1;
        break;
}
return;

ServerBootstrap? LoadBootstrap()
{
    if (!File.Exists(bootstrapPath)) return null;
    try
    {
        using var stream = new FileStream(bootstrapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return JsonSerializer.Deserialize<ServerBootstrap>(stream, jsonOptions);
    }
    catch (Exception ex) when (ex is IOException or JsonException) { return null; }
}

ServerBootstrap? RequireRunningServer()
{
    ServerBootstrap? bootstrap = LoadBootstrap();
    if (bootstrap == null)
    {
        Console.Error.WriteLine("PaRuX Core isn't running. Run `parux serve` or start RBG PaRuX AI Studio.");
        return null;
    }
    if (string.IsNullOrWhiteSpace(bootstrap.Token)
        || !PaRuXControlPlaneHttpClient.TryParseLocalEndpoint(bootstrap.Endpoint, out _))
    {
        Console.Error.WriteLine("PaRuX Core bootstrap does not contain a valid local endpoint. Start PaRuX again.");
        return null;
    }
    try { _ = Process.GetProcessById(bootstrap.ProcessId); }
    catch (ArgumentException)
    {
        Console.Error.WriteLine("PaRuX Core bootstrap is stale. Run `parux serve` or start the desktop app again.");
        return null;
    }
    return bootstrap;
}

HttpClient CreateClient(ServerBootstrap bootstrap)
{
    var client = PaRuXControlPlaneHttpClient.CreateNoRedirectClient(TimeSpan.FromMinutes(5));
    client.BaseAddress = new Uri(bootstrap.Endpoint);
    client.DefaultRequestHeaders.Add("X-PaRuX-Token", bootstrap.Token);
    return client;
}

async Task<T?> SendJsonAsync<T>(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct)
{
    using var request = new HttpRequestMessage(method, path);
    if (body != null) request.Content = JsonContent.Create(body, options: jsonOptions);
    using HttpResponseMessage response = await client.SendAsync(request, ct);
    string text = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"Core request failed ({(int)response.StatusCode}): {text}");
    if (typeof(T) == typeof(NoContent)) return (T)(object)new NoContent();
    if (string.IsNullOrWhiteSpace(text)) return default;
    return JsonSerializer.Deserialize<T>(text, jsonOptions);
}

async Task RunStatusAsync(CancellationToken ct)
{
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v1/health", ct);
        Console.WriteLine($"Endpoint: {bootstrap.Endpoint}");
        Console.WriteLine($"Process:  {bootstrap.ProcessId} (started {bootstrap.StartedUtc:u})");
        Console.WriteLine($"Health:   {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunModelsAsync(CancellationToken ct)
{
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        using HttpResponseMessage response = await client.GetAsync("/v1/models", ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using JsonDocument doc = JsonDocument.Parse(body);
        foreach (JsonElement model in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = model.GetProperty("id").GetString() ?? "?";
            string real = model.TryGetProperty("real_model", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
            Console.WriteLine($"{id,-20} {real}");
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunChatAsync(string[] rest, CancellationToken ct)
{
    string model = "parux-main";
    var parts = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--model" && i + 1 < rest.Length) model = rest[++i];
        else parts.Add(rest[i]);
    }
    string message = string.Join(' ', parts);
    if (string.IsNullOrWhiteSpace(message)) { Fail("Usage: parux chat <message> [--model <name>]"); return; }

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        var payload = new { model, stream = false, messages = new[] { new { role = "user", content = message } } };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using JsonDocument doc = JsonDocument.Parse(body);
        Console.WriteLine(doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunDownloadAsync(string[] rest, CancellationToken ct)
{
    long sizeBytes = 0;
    var positional = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--size" && i + 1 < rest.Length) long.TryParse(rest[++i], out sizeBytes);
        else positional.Add(rest[i]);
    }
    if (positional.Count < 3) { Fail("Usage: parux download <repoId> <fileName> <downloadUrl> [--size <bytes>]"); return; }

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    string? downloadId = null;
    try
    {
        ModelDownloadStatus? status = await SendJsonAsync<ModelDownloadStatus>(client, HttpMethod.Post,
            "/api/v1/models/downloads",
            new ModelDownloadRequest { RepoId = positional[0], FileName = positional[1], DownloadUrl = positional[2], SizeBytes = sizeBytes }, ct);
        if (status == null) throw new InvalidOperationException("Core returned an empty download response.");
        downloadId = status.Id;
        Console.WriteLine($"Download started: {downloadId} ({status.FileName})");
        status = await PollAsync(
            () => client.GetFromJsonAsync<ModelDownloadStatus>($"/api/v1/models/downloads/{downloadId}", jsonOptions, ct),
            value => value.Status is "completed" or "failed" or "cancelled",
            value => value.TotalBytes > 0
                ? $"  {value.Status}: {value.BytesDownloaded / 1024 / 1024} / {value.TotalBytes / 1024 / 1024} MB"
                : $"  {value.Status}: {value.BytesDownloaded / 1024 / 1024} MB", ct);
        if (status == null) { Fail("Download polling timed out; Core may still be running it."); return; }
        if (status.Status != "completed") { Fail("Download " + status.Status + ": " + status.Error); return; }
        Console.WriteLine($"Completed: {status.LocalPath} (verification: {status.VerificationStatus ?? "unavailable"})");
    }
    catch (OperationCanceledException)
    {
        if (!string.IsNullOrWhiteSpace(downloadId))
        {
            try { await client.PostAsync($"/api/v1/models/downloads/{downloadId}/cancel", null, CancellationToken.None); }
            catch { }
        }
        Console.Error.WriteLine("Cancelled; PaRuX Core was asked to stop the download.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunBenchmarkAsync(string[] rest, CancellationToken ct)
{
    string label = string.Empty;
    var positional = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--label" && i + 1 < rest.Length) label = rest[++i];
        else positional.Add(rest[i]);
    }
    if (positional.Count == 0) { Fail("Usage: parux benchmark <modelPath> [--label <name>]"); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    string? benchmarkId = null;
    try
    {
        ModelBenchmarkRun? run = await SendJsonAsync<ModelBenchmarkRun>(client, HttpMethod.Post,
            "/api/v1/models/benchmarks", new ModelBenchmarkRequest { ModelPath = positional[0], Label = label }, ct);
        if (run == null) throw new InvalidOperationException("Core returned an empty benchmark response.");
        benchmarkId = run.Id;
        Console.WriteLine($"Benchmark started: {benchmarkId} ({run.ModelPath})");
        run = await PollAsync(
            () => client.GetFromJsonAsync<ModelBenchmarkRun>($"/api/v1/models/benchmarks/{benchmarkId}", jsonOptions, ct),
            value => value.Status is "completed" or "failed" or "cancelled",
            value => $"  {value.Status}: {value.Stage}", ct);
        if (run == null) { Fail("Benchmark polling timed out."); return; }
        if (run.Status != "completed") { Fail("Benchmark " + run.Status + ": " + run.Error); return; }
        Console.WriteLine($"Completed: {run.TokensPerSecond:0.0} tok/s ({run.GeneratedTokens} tokens, {run.Backend ?? "unknown backend"})");
    }
    catch (OperationCanceledException)
    {
        if (!string.IsNullOrWhiteSpace(benchmarkId))
        {
            try { await client.PostAsync($"/api/v1/models/benchmarks/{benchmarkId}/cancel", null, CancellationToken.None); }
            catch { }
        }
        Console.Error.WriteLine("Cancelled; PaRuX Core was asked to stop the benchmark.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunEvalAsync(string[] rest, CancellationToken ct)
{
    string? role = null;
    var positional = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--role" && i + 1 < rest.Length) role = rest[++i];
        else positional.Add(rest[i]);
    }
    if (positional.Count == 0) { Fail("Usage: parux eval <datasetId> [--role <name>]"); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        EvalRunDto? run = await SendJsonAsync<EvalRunDto>(client, HttpMethod.Post,
            $"/api/v1/eval/datasets/{positional[0]}/runs", new StartEvalRunRequest { Role = role }, ct);
        if (run == null) throw new InvalidOperationException("Core returned an empty eval response.");
        string runId = run.Id;
        Console.WriteLine($"Eval started: {runId}");
        run = await PollAsync(
            () => client.GetFromJsonAsync<EvalRunDto>($"/api/v1/eval/runs/{runId}", jsonOptions, ct),
            value => value.Status is "completed" or "error",
            value => $"  {value.Status}: {value.PassCount} pass, {value.FailCount} fail, {value.ErrorCount} error", ct);
        if (run == null) { Fail("Eval polling timed out; the run may still be active."); return; }
        if (run.Status == "error") Fail("Eval finished with execution errors.");
        else Console.WriteLine($"Completed: {run.PassCount} pass, {run.FailCount} fail, {run.InconclusiveCount} inconclusive, {run.ErrorCount} error");
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Stopped watching only; Eval Lab has no destructive implicit cancel on Ctrl+C.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task RunTrainAsync(string[] rest, CancellationToken ct)
{
    string baseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";
    string method = TrainingMethod.Sft;
    int loraRank = 16, maxSteps = 60, maxSequenceLength = 2048, grpoGenerations = 4;
    double learningRate = 0.0002, dpoBeta = 0.1;
    string grpoRewardMode = GrpoRewardMode.ExactReference;
    string textField = "text", imageField = "image", audioField = "audio";
    bool planOnly = false;
    var positional = new List<string>();

    for (int i = 0; i < rest.Length; i++)
    {
        string option = rest[i];
        string Next(string name)
        {
            if (i + 1 >= rest.Length) throw new InvalidDataException(name + " requires a value.");
            return rest[++i];
        }
        switch (option)
        {
            case "--base-model": baseModel = Next(option); break;
            case "--method": method = Next(option); break;
            case "--lora-rank": loraRank = ParseInt(Next(option), option); break;
            case "--max-steps": maxSteps = ParseInt(Next(option), option); break;
            case "--lr": learningRate = ParseDouble(Next(option), option); break;
            case "--dpo-beta": dpoBeta = ParseDouble(Next(option), option); break;
            case "--max-sequence": maxSequenceLength = ParseInt(Next(option), option); break;
            case "--grpo-reward": grpoRewardMode = Next(option); break;
            case "--grpo-generations": grpoGenerations = ParseInt(Next(option), option); break;
            case "--text-field": textField = Next(option); break;
            case "--image-field": imageField = Next(option); break;
            case "--audio-field": audioField = Next(option); break;
            case "--plan-only": planOnly = true; break;
            default:
                if (option.StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidDataException("Unknown train option: " + option);
                positional.Add(option);
                break;
        }
    }

    if (positional.Count != 1)
    {
        Fail("Usage: parux train <trainJsonlPath> [--method sft|dpo|continued-pretraining|grpo|vision-sft|tts-sft] [options]");
        return;
    }

    try
    {
        method = TrainingMethod.Normalize(method);
        grpoRewardMode = GrpoRewardMode.Normalize(grpoRewardMode);
    }
    catch (InvalidDataException ex) { Fail(ex.Message); return; }

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    string? jobId = null;
    try
    {
        var planRequest = new TrainingPlanRequest
        {
            TrainJsonlPath = positional[0],
            BaseModel = baseModel,
            Method = method,
            LoraRank = loraRank,
            MaxSteps = maxSteps,
            LearningRate = learningRate,
            MaxSequenceLength = maxSequenceLength,
            GrpoRewardMode = grpoRewardMode
        };
        TrainingPlanResult? plan = await SendJsonAsync<TrainingPlanResult>(client, HttpMethod.Post,
            "/api/v1/training/plan", planRequest, ct);
        if (plan == null) throw new InvalidOperationException("Core returned an empty training preflight response.");
        PrintTrainingPlan(plan);
        if (!plan.CanStart)
        {
            Fail("Training was not started because Core preflight found blocking issues.");
            return;
        }
        if (planOnly)
        {
            Console.WriteLine("Plan-only requested; no GPU work was started.");
            return;
        }

        TrainingJobStatus? job = await SendJsonAsync<TrainingJobStatus>(client, HttpMethod.Post,
            "/api/v1/training/jobs", new TrainingJobRequest
            {
                TrainJsonlPath = positional[0],
                BaseModel = baseModel,
                Method = method,
                LoraRank = loraRank,
                MaxSteps = maxSteps,
                LearningRate = learningRate,
                DpoBeta = dpoBeta,
                MaxSequenceLength = maxSequenceLength,
                GrpoRewardMode = grpoRewardMode,
                GrpoGenerations = grpoGenerations,
                TextField = textField,
                ImageField = imageField,
                AudioField = audioField
            }, ct);
        if (job == null) throw new InvalidOperationException("Core returned an empty training response.");
        jobId = job.Id;
        Console.WriteLine($"Training started: {jobId} ({job.Method}, {baseModel})");
        job = await PollAsync(
            () => client.GetFromJsonAsync<TrainingJobStatus>($"/api/v1/training/jobs/{jobId}", jsonOptions, ct),
            value => value.Status is "completed" or "failed" or "cancelled",
            value => value.TotalSteps > 0
                ? $"  {value.Status}: {value.Stage} ({value.CurrentStep}/{value.TotalSteps})"
                : $"  {value.Status}: {value.Stage}", ct);
        if (job == null) { Fail("Training polling timed out; Core may still be running it."); return; }
        if (job.Status != "completed") { Fail("Training " + job.Status + ": " + job.Error); return; }
        Console.WriteLine("Completed: " + job.AdapterPath);
    }
    catch (OperationCanceledException)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            try { await client.PostAsync($"/api/v1/training/jobs/{jobId}/cancel", null, CancellationToken.None); }
            catch { }
        }
        Console.Error.WriteLine(jobId == null
            ? "Cancelled during preflight; no training job was started."
            : "Cancelled; PaRuX Core was asked to stop the training job.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

void PrintTrainingPlan(TrainingPlanResult plan)
{
    Console.WriteLine((plan.CanStart ? "Preflight: READY" : "Preflight: BLOCKED") + " · " + plan.DatasetSummary);
    if (plan.EstimatedGib > 0)
        Console.WriteLine($"Memory:    ~{plan.EstimatedGib:0.#} GiB required / {plan.AvailableGib:0.#} GiB available");
    if (plan.RecommendedMaxSteps > 0)
        Console.WriteLine($"Suggested: rank {plan.RecommendedLoraRank}, {plan.RecommendedMaxSteps} steps, lr {plan.RecommendedLearningRate:G}"
            + (plan.RecommendedMaxSequenceLength > 0 ? $", max-sequence {plan.RecommendedMaxSequenceLength}" : string.Empty));
    foreach (TrainingPlanFinding finding in plan.Findings.OrderBy(value => value.Severity == "Blocking" ? 0 : 1))
        Console.WriteLine($"  {(finding.Severity == "Blocking" ? "BLOCK" : "WARN ")} {finding.Field}: {finding.Message}");
}

int ParseInt(string value, string option)
{
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        throw new InvalidDataException(option + " expects an integer.");
    return result;
}

double ParseDouble(string value, string option)
{
    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
        || !double.IsFinite(result))
        throw new InvalidDataException(option + " expects a finite number using '.' as decimal separator.");
    return result;
}

async Task RunAgentCommandAsync(string[] rest, CancellationToken ct)
{
    string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : string.Empty;
    if (sub is "list" or "ls") { await AgentListAsync(ct); return; }
    if (sub is "show" or "status") { await AgentShowAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub is "logs" or "log") { await AgentLogsAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub is "stop" or "cancel") { await AgentStopAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "resume") { await AgentResumeAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "open") { await AgentOpenAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub is "questions" or "question") { await AgentQuestionsAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "answer") { await AgentAnswerAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "run") rest = rest.Skip(1).ToArray();
    await AgentRunAsync(rest, ct);
}

async Task AgentRunAsync(string[] rest, CancellationToken ct)
{
    string? role = null;
    string? workspace = null;
    string permissionProfile = CodingTaskPermissionProfile.Offline;
    int maxRepair = 2;
    bool detach = false;
    var repositorySpecs = new List<string>();
    var goalParts = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--role" when i + 1 < rest.Length: role = rest[++i]; break;
            case "--workspace" when i + 1 < rest.Length: workspace = rest[++i]; break;
            case "--repo" when i + 1 < rest.Length: repositorySpecs.Add(rest[++i]); break;
            case "--permissions" when i + 1 < rest.Length: permissionProfile = rest[++i]; break;
            case "--max-repair" when i + 1 < rest.Length: int.TryParse(rest[++i], out maxRepair); break;
            case "--detach": detach = true; break;
            default: goalParts.Add(rest[i]); break;
        }
    }
    string goal = string.Join(' ', goalParts).Trim();
    if (goal.Length == 0)
    {
        Fail("Usage: parux agent run <goal> [--role <name>] [--workspace <repo>] [--repo <alias>=<repo>]... [--permissions offline|package-fetch|trusted-restore] [--max-repair N] [--detach]");
        return;
    }
    if (!CodingTaskPermissionProfile.IsKnown(permissionProfile))
    { Fail("Unknown --permissions value. Use offline, package-fetch or trusted-restore."); return; }
    permissionProfile = CodingTaskPermissionProfile.Normalize(permissionProfile);
    maxRepair = Math.Clamp(maxRepair, 0, 3);

    if (!string.IsNullOrWhiteSpace(workspace) && !Directory.Exists(workspace))
    { Fail("--workspace must be an existing local directory."); return; }
    if (!string.IsNullOrWhiteSpace(workspace)
        && !string.IsNullOrWhiteSpace(role)
        && !string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase))
    { Fail("--workspace is a coding-task authority; it cannot be combined with a non-coding --role."); return; }
    if (!string.Equals(permissionProfile, CodingTaskPermissionProfile.Offline, StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(workspace)
        && !string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase))
    { Fail("A non-offline --permissions profile requires an explicit coding task (--workspace or --role coding)."); return; }
    if (repositorySpecs.Count > 0 && string.IsNullOrWhiteSpace(workspace))
    { Fail("--repo requires --workspace because additional repositories are part of one atomic coding authority set."); return; }
    if (repositorySpecs.Count > 7)
    { Fail("At most 7 --repo entries are allowed in addition to the primary --workspace."); return; }

    var additionalRepositories = new List<AddAgentRepositoryAuthorityRequest>();
    foreach (string spec in repositorySpecs)
    {
        int separator = spec.IndexOf('=');
        if (separator <= 0 || separator == spec.Length - 1)
        { Fail("Invalid --repo value '" + spec + "'. Use --repo <alias>=<repo>."); return; }
        string alias = spec[..separator].Trim();
        string rawPath = spec[(separator + 1)..].Trim();
        if (alias.Length == 0 || rawPath.Length == 0)
        { Fail("Invalid --repo value '" + spec + "'. Alias and path are required."); return; }
        string fullPath;
        try { fullPath = Path.GetFullPath(rawPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { Fail("Invalid --repo path for alias '" + alias + "'."); return; }
        if (!Directory.Exists(fullPath))
        { Fail("--repo path for alias '" + alias + "' must be an existing local directory."); return; }
        additionalRepositories.Add(new AddAgentRepositoryAuthorityRequest { Alias = alias, WorkspaceRoot = fullPath });
    }

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        AgentTaskDto? task;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            task = await SendJsonAsync<AgentTaskDto>(client, HttpMethod.Post,
                "/api/v1/orchestrator/coding-tasks", new CreateDesktopCodingTaskRequest
                {
                    Goal = goal,
                    WorkspaceRoot = Path.GetFullPath(workspace),
                    AdditionalRepositories = additionalRepositories,
                    CodingPermissionProfile = permissionProfile,
                    MaxRepairAttempts = maxRepair
                }, ct);
        }
        else
        {
            bool explicitCoding = string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase);
            task = await SendJsonAsync<AgentTaskDto>(client, HttpMethod.Post,
                "/api/v1/orchestrator/tasks", new CreateAgentTaskRequest
                {
                    Goal = goal,
                    Role = role,
                    GrantedCapabilities = explicitCoding
                        ? CodingTaskPermissionProfile.RootCapabilitiesFor(permissionProfile).ToList()
                        : new List<string>(),
                    MaxRepairAttempts = maxRepair
                }, ct);
        }
        if (task == null) throw new InvalidOperationException("Core returned an empty agent response.");

        await SendJsonAsync<JsonElement>(client, HttpMethod.Post, "/api/v1/orchestrator/schedule", null, ct);
        Console.WriteLine($"Agent task: {task.Id} · role {task.Role ?? "auto"}"
            + (string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase)
                ? " · permissions " + CodingTaskPermissionProfile.FromCapabilities(task.GrantedCapabilities)
                : string.Empty)
            + (!string.IsNullOrWhiteSpace(workspace) ? " · repos " + (1 + additionalRepositories.Count) : string.Empty));
        if (detach) return;
        string taskId = task.Id;
        (AgentTaskDto? watched, AgentUserQuestionDto? question) = await WatchAgentTaskAsync(client, taskId, ct);
        if (question != null)
        {
            // The durable task is paused, not failed: say so at once instead of polling for the whole 30 minutes.
            Console.WriteLine("Waiting for your answer (question " + question.Id + "): " + question.Question);
            if (question.Options.Count > 0) Console.WriteLine("Options: " + string.Join(" | ", question.Options));
            Console.WriteLine("Answer with: parux agent answer " + taskId + " " + question.Id + " <answer>");
            Environment.ExitCode = AgentExitWaitingForAnswer;
            return;
        }
        if (watched == null) { Fail("Agent polling timed out; task remains durable in Core."); return; }
        await PrintTaskAsync(client, watched, includeResult: true, ct);
        if (watched.Status != AgentTaskStatus.Completed) Environment.ExitCode = 1;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Stopped watching only. The durable task was NOT cancelled; use `parux agent stop <id>` explicitly.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

// Watches a run until it is terminal or paused on a user question. PollAsync cannot do this on its own: its
// predicate is synchronous, and a waiting task reveals its question only through the user-question endpoint.
async Task<(AgentTaskDto? Current, AgentUserQuestionDto? Question)> WatchAgentTaskAsync(HttpClient client, string taskId, CancellationToken ct)
{
    DateTime deadline = DateTime.UtcNow + pollTimeout;
    while (true)
    {
        ct.ThrowIfCancellationRequested();
        AgentTaskDto? current = await client.GetFromJsonAsync<AgentTaskDto>($"/api/v1/orchestrator/tasks/{taskId}", jsonOptions, ct);
        if (current == null) return (null, null);
        Console.WriteLine(DescribeTask(current));
        AgentUserQuestionDto? pending = null;
        if (string.Equals(current.Status, AgentTaskStatus.Waiting, StringComparison.Ordinal))
        {
            AgentUserQuestionStateDto? state = await SendJsonAsync<AgentUserQuestionStateDto>(client, HttpMethod.Get,
                $"/api/v1/orchestrator/tasks/{Uri.EscapeDataString(taskId)}/user-question", null, ct);
            pending = state?.PendingQuestion;
        }
        switch (AgentRunWaitPolicy.Decide(current.Status, pending != null, DateTime.UtcNow >= deadline))
        {
            case AgentRunWaitDecision.Finished: return (current, null);
            case AgentRunWaitDecision.WaitingForUserAnswer: return (current, pending);
            case AgentRunWaitDecision.TimedOut: return (null, null);
        }
        await Task.Delay(pollInterval, ct);
    }
}

async Task AgentQuestionsAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent questions <taskId>"); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        AgentUserQuestionStateDto? state = await SendJsonAsync<AgentUserQuestionStateDto>(client, HttpMethod.Get,
            $"/api/v1/orchestrator/tasks/{Uri.EscapeDataString(rest[0])}/user-question", null, ct);
        AgentUserQuestionDto? pending = state?.PendingQuestion;
        if (pending == null) { Console.WriteLine("No pending question for task " + rest[0] + "."); return; }
        Console.WriteLine("Question " + pending.Id + " (asked " + pending.AskedUtc.ToString("u") + "): " + pending.Question);
        if (pending.Options.Count > 0) Console.WriteLine("Options: " + string.Join(" | ", pending.Options));
        Console.WriteLine("Answer with: parux agent answer " + rest[0] + " " + pending.Id + " <answer>");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentAnswerAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length < 3) { Fail("Usage: parux agent answer <taskId> <questionId> <answer...>"); return; }
    string answer = string.Join(' ', rest.Skip(2)).Trim();
    if (answer.Length == 0) { Fail("The answer text is empty."); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        AgentUserQuestionStateDto? state = await SendJsonAsync<AgentUserQuestionStateDto>(client, HttpMethod.Post,
            $"/api/v1/orchestrator/tasks/{Uri.EscapeDataString(rest[0])}/user-question/answer",
            new AnswerAgentUserQuestionRequest { QuestionId = rest[1], Answer = answer }, ct);
        Console.WriteLine(state?.PendingQuestion == null
            ? "Answer recorded. The task resumes from its durable checkpoint."
            : "Answer recorded; another question is pending: " + state.PendingQuestion.Question);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentListAsync(CancellationToken ct)
{
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        AgentTaskListResponse? response = await SendJsonAsync<AgentTaskListResponse>(client, HttpMethod.Get,
            "/api/v1/orchestrator/tasks", null, ct);
        foreach (AgentTaskDto task in response?.Tasks ?? new List<AgentTaskDto>())
            Console.WriteLine($"{task.Id}  {task.Status,-10} {task.Role ?? "auto",-15} {Trim(task.Goal, 72)}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentShowAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent show <taskId>"); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    AgentTaskDto? task = await GetTaskAsync(client, rest[0], ct);
    if (task != null) await PrintTaskAsync(client, task, includeResult: true, ct);
}

async Task AgentLogsAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent logs <taskId> [--follow]"); return; }
    string id = rest[0];
    bool follow = rest.Contains("--follow", StringComparer.OrdinalIgnoreCase);
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    try
    {
        while (true)
        {
            AgentTaskLogListResponse? logs = await SendJsonAsync<AgentTaskLogListResponse>(client, HttpMethod.Get,
                $"/api/v1/orchestrator/tasks/{id}/logs", null, ct);
            foreach (AgentTaskLogEntryDto entry in logs?.Logs ?? new List<AgentTaskLogEntryDto>())
                if (seen.Add(entry.Id)) Console.WriteLine($"{entry.TimestampUtc:O}  {entry.Message}");
            if (!follow) return;
            AgentTaskDto? task = await client.GetFromJsonAsync<AgentTaskDto>($"/api/v1/orchestrator/tasks/{id}", jsonOptions, ct);
            if (task == null || AgentTaskStatus.Terminal.Contains(task.Status)) return;
            await Task.Delay(pollInterval, ct);
        }
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Stopped following logs; task continues."); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentStopAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent stop <taskId>"); return; }
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        await SendJsonAsync<NoContent>(client, HttpMethod.Post, $"/api/v1/orchestrator/tasks/{rest[0]}/cancel", null, ct);
        Console.WriteLine("Cancellation requested for " + rest[0] + ".");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentResumeAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent resume <taskId>"); return; }
    string id = rest[0];
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        AgentTaskDto? task = await SendJsonAsync<AgentTaskDto>(client, HttpMethod.Post,
            $"/api/v1/orchestrator/tasks/{id}/resume",
            new ResumeDesktopAgentTaskRequest { Source = "PaRuX CLI" }, ct);
        if (task == null) throw new InvalidOperationException("Core returned an empty resume response.");
        await SendJsonAsync<JsonElement>(client, HttpMethod.Post, "/api/v1/orchestrator/schedule", null, ct);
        Console.WriteLine("Resumed durable task " + id + " through the authoritative Core resume policy. "
            + "State=" + task.Status + "; the same worker/checkpoint will reclaim it.");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task AgentOpenAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux agent open <taskId> [--editor code|cursor]"); return; }
    string editor = "code";
    for (int i = 1; i < rest.Length; i++)
        if (rest[i] == "--editor" && i + 1 < rest.Length) editor = rest[++i];

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    AgentTaskDto? task = await GetTaskAsync(client, rest[0], ct);
    if (task == null) return;
    AgentDesktopWorktreeDto? desktop = await GetDesktopWorktreeAsync(client, task.Id, ct);
    string? worktree = desktop?.WorktreePath;
    if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
    { Fail("This task has no reviewable local worktree yet."); return; }
    try
    {
        var start = new ProcessStartInfo { FileName = editor, UseShellExecute = true };
        start.ArgumentList.Add(worktree);
        Process.Start(start);
        Console.WriteLine($"Opened the PaRuX task worktree in {editor}. Task/checkpoint remains owned by Core.");
    }
    catch (Exception ex) { Fail("Could not launch editor: " + ex.Message); }
}

async Task<AgentTaskDto?> GetTaskAsync(HttpClient client, string id, CancellationToken ct)
{
    try { return await SendJsonAsync<AgentTaskDto>(client, HttpMethod.Get, $"/api/v1/orchestrator/tasks/{id}", null, ct); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; return null; }
}

async Task<AgentDesktopWorktreeDto?> GetDesktopWorktreeAsync(HttpClient client, string id, CancellationToken ct)
{
    try
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/orchestrator/tasks/{id}/desktop-worktree", ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Core worktree request failed ({(int)response.StatusCode}): {text}");
        return JsonSerializer.Deserialize<AgentDesktopWorktreeDto>(text, jsonOptions);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; return null; }
}

async Task RunProjectCommandAsync(string[] rest, CancellationToken ct)
{
    string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "list";
    if (sub is "list" or "ls") { await ProjectListAsync(ct); return; }
    if (sub == "create") { await ProjectCreateAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "show") { await ProjectShowAsync(rest.Skip(1).ToArray(), ct); return; }
    if (sub == "watch") { await ProjectWatchAsync(rest.Skip(1).ToArray(), ct); return; }
    Fail("Usage: parux project create|list|show|watch ...");
}

async Task<ProjectListResponse?> GetProjectsAsync(CancellationToken ct)
{
    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return null; }
    using HttpClient client = CreateClient(bootstrap);
    return await SendJsonAsync<ProjectListResponse>(client, HttpMethod.Get, "/api/v1/projects", null, ct);
}

async Task ProjectListAsync(CancellationToken ct)
{
    try
    {
        ProjectListResponse? response = await GetProjectsAsync(ct);
        foreach (ProjectDto project in response?.Projects ?? new List<ProjectDto>())
            Console.WriteLine($"{project.Id}  tasks={project.TaskIds.Count,-3} updated={project.UpdatedUtc:u}  {Trim(project.Goal, 80)}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task ProjectCreateAsync(string[] rest, CancellationToken ct)
{
    string permissionProfile = CodingTaskPermissionProfile.Offline;
    var goalParts = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--permissions" && i + 1 < rest.Length) permissionProfile = rest[++i];
        else goalParts.Add(rest[i]);
    }
    string goal = string.Join(' ', goalParts).Trim();
    if (goal.Length == 0)
    { Fail("Usage: parux project create <goal> [--permissions offline|package-fetch|trusted-restore]"); return; }
    if (!CodingTaskPermissionProfile.IsKnown(permissionProfile))
    { Fail("Unknown --permissions value. Use offline, package-fetch or trusted-restore."); return; }
    permissionProfile = CodingTaskPermissionProfile.Normalize(permissionProfile);

    ServerBootstrap? bootstrap = RequireRunningServer();
    if (bootstrap == null) { Environment.ExitCode = 1; return; }
    using HttpClient client = CreateClient(bootstrap);
    try
    {
        ProjectDto? project = await SendJsonAsync<ProjectDto>(client, HttpMethod.Post, "/api/v1/projects/desktop",
            new CreateDesktopProjectRequest
            {
                Goal = goal,
                CodingPermissionProfile = permissionProfile
            }, ct);
        if (project == null) throw new InvalidOperationException("Core returned an empty project response.");
        PrintProject(project);
        Console.WriteLine("Coding permissions for new project coding tasks: " + permissionProfile + ".");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task ProjectShowAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux project show <projectId>"); return; }
    try
    {
        ProjectDto? project = (await GetProjectsAsync(ct))?.Projects.FirstOrDefault(value => value.Id == rest[0]);
        if (project == null) { Fail("Project not found: " + rest[0]); return; }
        PrintProject(project);
        ServerBootstrap? bootstrap = RequireRunningServer();
        if (bootstrap == null) { Environment.ExitCode = 1; return; }
        using HttpClient client = CreateClient(bootstrap);
        foreach (string taskId in project.TaskIds)
        {
            AgentTaskDto? task = await GetTaskAsync(client, taskId, ct);
            if (task != null) Console.WriteLine("  " + DescribeTask(task));
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task ProjectWatchAsync(string[] rest, CancellationToken ct)
{
    if (rest.Length == 0) { Fail("Usage: parux project watch <projectId>"); return; }
    string id = rest[0];
    try
    {
        while (true)
        {
            ProjectDto? project = (await GetProjectsAsync(ct))?.Projects.FirstOrDefault(value => value.Id == id);
            if (project == null) { Fail("Project not found: " + id); return; }
            ServerBootstrap? bootstrap = RequireRunningServer();
            if (bootstrap == null) { Environment.ExitCode = 1; return; }
            using HttpClient client = CreateClient(bootstrap);
            AgentTaskListResponse? all = await SendJsonAsync<AgentTaskListResponse>(client, HttpMethod.Get,
                "/api/v1/orchestrator/tasks", null, ct);
            List<AgentTaskDto> tasks = (all?.Tasks ?? new List<AgentTaskDto>())
                .Where(task => project.TaskIds.Contains(task.Id, StringComparer.Ordinal)).ToList();
            Console.WriteLine($"{DateTime.Now:T}  project {id}: " + string.Join(" · ", tasks.Select(task => task.Role + "=" + task.Status)));
            if (tasks.Count == project.TaskIds.Count && tasks.All(task => AgentTaskStatus.Terminal.Contains(task.Status)))
            {
                if (tasks.Any(task => task.Status != AgentTaskStatus.Completed)) Environment.ExitCode = 1;
                return;
            }
            await Task.Delay(pollInterval, ct);
        }
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Stopped watching only; project tasks remain durable."); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

async Task<T?> PollAsync<T>(Func<Task<T?>> fetch, Func<T, bool> isDone, Func<T, string> describe, CancellationToken ct) where T : class
{
    DateTime deadline = DateTime.UtcNow + pollTimeout;
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        T? current = await fetch();
        if (current == null) return null;
        Console.WriteLine(describe(current));
        if (isDone(current)) return current;
        await Task.Delay(pollInterval, ct);
    }
    return null;
}

string DescribeTask(AgentTaskDto task) =>
    $"{task.Id} {task.Status} {task.Role ?? "auto"} {task.ProgressPercent}% · {task.Stage ?? "-"}";

async Task PrintTaskAsync(HttpClient client, AgentTaskDto task, bool includeResult, CancellationToken ct)
{
    Console.WriteLine(DescribeTask(task));
    Console.WriteLine("Goal:       " + task.Goal);
    Console.WriteLine("Created:    " + task.CreatedUtc.ToString("u"));
    Console.WriteLine("Updated:    " + task.UpdatedUtc.ToString("u"));
    if (string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase))
    {
        AgentDesktopWorktreeDto? worktree = await GetDesktopWorktreeAsync(client, task.Id, ct);
        Console.WriteLine("Worktree:   " + (worktree?.WorktreePath ?? "-"));
        Console.WriteLine("Permissions:" + " " + CodingTaskPermissionProfile.FromCapabilities(task.GrantedCapabilities));
    }
    Console.WriteLine("Capabilities: " + (task.GrantedCapabilities.Count == 0 ? "-" : string.Join(", ", task.GrantedCapabilities)));
    Console.WriteLine("Verify:     " + (task.VerificationStatus ?? "-"));
    Console.WriteLine($"Repair:     {task.RepairAttemptsUsed}/{task.MaxRepairAttempts}");
    if (!string.IsNullOrWhiteSpace(task.Error)) Console.WriteLine("Error:      " + task.Error);
    if (includeResult && !string.IsNullOrWhiteSpace(task.ResultJson)) Console.WriteLine("Result:     " + task.ResultJson);
}

void PrintProject(ProjectDto project)
{
    Console.WriteLine($"Project: {project.Id}");
    Console.WriteLine("Goal:    " + project.Goal);
    Console.WriteLine($"Tasks:   {project.TaskIds.Count} ({string.Join(", ", project.TaskIds)})");
    Console.WriteLine("Updated: " + project.UpdatedUtc.ToString("u"));
}

void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.ExitCode = 1;
}

string Trim(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty
    : value.Length <= max ? value : value[..(max - 1)] + "…";

void PrintHelp()
{
    Console.WriteLine("""
parux - RBG PaRuX AI Studio CLI (same Core, agent state and Virtual Context as desktop/Android)

Core / inference:
  parux serve
  parux status
  parux models
  parux chat <message> [--model <name>]
  parux download <repoId> <fileName> <downloadUrl> [--size <bytes>]
  parux benchmark <modelPath> [--label <name>]
  parux eval <datasetId> [--role <name>]
  parux train <trainJsonlPath>
              [--method sft|dpo|continued-pretraining|grpo|vision-sft|tts-sft]
              [--base-model <hfId>] [--lora-rank N] [--max-steps N] [--lr RATE]
              [--max-sequence N] [--dpo-beta BETA]
              [--grpo-reward exact-reference|contains-reference|xml-format|json-format]
              [--grpo-generations 2..16] [--text-field NAME] [--image-field NAME]
              [--audio-field NAME] [--plan-only]

Training always runs Core preflight first. --plan-only prints compatibility, memory evidence,
recommendations and blocking/warning findings without starting GPU work. All six methods execute
through the same PaRuX-managed uv/Python Training Lab; no system-Python fallback is used.

Agent command center:
  parux agent run <goal> [--role <name>] [--workspace <primaryRepo>]
                  [--repo <alias>=<repo>]... [--permissions offline|package-fetch|trusted-restore]
                  [--max-repair N] [--detach]
  parux agent <goal>                         Backward-compatible alias for agent run
  parux agent list
  parux agent show <taskId>
  parux agent logs <taskId> [--follow]
  parux agent stop <taskId>
  parux agent resume <taskId>                Core-authoritative resume of preserved failed/inconclusive state
  parux agent open <taskId> [--editor code|cursor]
  parux agent questions <taskId>             Show the question a paused task is waiting for
  parux agent answer <taskId> <questionId> <text>   Answer it; the task resumes from its checkpoint
  (`agent run` exits 2 when the task pauses for an answer, and prints the question id.)

Projects / multi-agent DAGs:
  parux project create <goal> [--permissions offline|package-fetch|trusted-restore]
  parux project list
  parux project show <projectId>
  parux project watch <projectId>

Coding agents are PaRuX-native. --workspace creates the coding task through the same Core boundary
used by Chat and the VS Code extension, including explicit repository authority and bounded
self-delegation. Repeat --repo alias=path to authorize up to seven additional repositories; Core
persists the complete topology atomically before the worker may freeze it. Permission profiles
affect only PaRuX-controlled package restore; arbitrary run_command remains network-disabled and
the global Internet switch remains authoritative. Worktree paths are exposed only to authenticated
loopback desktop tools, never through the generic task DTO seen by paired devices.

Ctrl+C on agent/log/project watch commands detaches from observation; it does not silently cancel
durable work. Ctrl+C on download/benchmark/training explicitly asks Core to cancel that job.
Virtual Context is a Core capability, so CLI/desktop/mobile continuations share the same >128k/1M+
logical-history behavior.
""");
}

sealed class NoContent { }
