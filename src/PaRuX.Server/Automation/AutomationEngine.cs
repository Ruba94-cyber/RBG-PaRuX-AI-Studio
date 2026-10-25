using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Automation;

/// <summary>
/// vNext Phase 14/15 - evaluates triggers and fires actions (directive section 28: TRIGGER ->
/// CONDITIONS -> SKILL/WORKFLOW -> AGENT -> VERIFICATION -> RESULT -> NOTIFICATION). An
/// automation's action creates a real orchestrator task (TaskStore.Create) rather than a second
/// execution path - it inherits Phase 4-7's real dispatch/worker/verifier pipeline for free.
///
/// Seven trigger types are evaluated for real: Schedule (elapsed-interval check), FileCreated
/// (poll-based folder diff), GitCommit (poll-based commit SHA diff), ModelDownloadCompleted,
/// ComponentHealthChanged, TaskCompleted, and CustomCondition. CustomCondition is deliberately a
/// closed declarative language over Core-owned state (task/download counts and component health):
/// no script, shell, regex evaluator or arbitrary expression is executed. It is edge-triggered and
/// fires only on a false -> true transition after the first observation has seeded state. All
/// poll-based triggers use the same seed-don't-replay-history discipline after process restart.
/// </summary>
public sealed class AutomationEngine
{
    // Trigger/action config JSON is authored the same camelCase way the rest of the API
    // serializes (Program.cs sets ConfigureHttpJsonOptions to camelCase) - matching that here so
    // a config string that looks right to a caller actually binds, instead of silently falling
    // back to each config record's default values.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AutomationStore _automations;
    private readonly TaskStore _tasks;
    private readonly ModelDownloadService _downloads;
    private readonly ComponentInventoryService _components;

    // In-process observations intentionally seed rather than replay historical state after a
    // restart. Durable automation definitions remain in SQLite; event edges do not.
    private readonly ConcurrentDictionary<string, HashSet<string>> _seenFiles = new();
    private readonly ConcurrentDictionary<string, string> _lastSeenGitSha = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _seenCompletedDownloads = new();
    private readonly ConcurrentDictionary<string, string> _lastSeenComponentHealth = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _seenCompletedTasks = new();

    private sealed record ConditionObservation(bool Value, DateTime DefinitionUpdatedUtc);
    private readonly ConcurrentDictionary<string, ConditionObservation> _lastConditionState = new();

    // GitCommit and component discovery involve real I/O and RunTickAsync has both a background
    // and a manual caller. Skip an overlapping tick rather than allowing duplicate edge detection.
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public AutomationEngine(AutomationStore automations, TaskStore tasks, ModelDownloadService downloads, ComponentInventoryService components)
    {
        _automations = automations;
        _tasks = tasks;
        _downloads = downloads;
        _components = components;
    }

    /// <summary>vNext Phase 54 (Universal STOP) - while paused no NEW automation-created tasks are
    /// produced. Existing tasks are stopped through their own Core-owned cancellation path.</summary>
    public volatile bool IsPaused;

    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        if (IsPaused) return;
        if (!await _tickGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            RunScheduleTriggers(cancellationToken);
            RunFileCreatedTriggers(cancellationToken);
            await RunGitCommitTriggersAsync(cancellationToken).ConfigureAwait(false);
            RunModelDownloadCompletedTriggers(cancellationToken);
            await RunComponentHealthChangedTriggersAsync(cancellationToken).ConfigureAwait(false);
            RunTaskCompletedTriggers(cancellationToken);
            await RunCustomConditionTriggersAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private void RunScheduleTriggers(CancellationToken cancellationToken)
    {
        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.Schedule))
        {
            ScheduleTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<ScheduleTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null) continue;

            DateTime baseline = automation.LastTriggeredUtc ?? automation.CreatedUtc;
            if (DateTime.UtcNow - baseline < TimeSpan.FromSeconds(config.IntervalSeconds)) continue;

            Fire(automation, fileName: null, folderPath: null, cancellationToken);
        }
    }

    private void RunFileCreatedTriggers(CancellationToken cancellationToken)
    {
        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.FileCreated))
        {
            FileCreatedTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<FileCreatedTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null || string.IsNullOrWhiteSpace(config.FolderPath) || !Directory.Exists(config.FolderPath)) continue;

            string[] currentFiles;
            try { currentFiles = Directory.GetFiles(config.FolderPath, config.Filter); }
            catch (IOException) { continue; }

            bool firstObservation = !_seenFiles.ContainsKey(automation.Id);
            HashSet<string> seen = _seenFiles.GetOrAdd(automation.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (firstObservation)
            {
                foreach (string existing in currentFiles) seen.Add(existing);
                continue;
            }

            foreach (string file in currentFiles)
            {
                if (!seen.Add(file)) continue;
                Fire(automation, Path.GetFileName(file), config.FolderPath, cancellationToken);
            }
        }
    }

    private static readonly Regex GitShaPattern = new("^[0-9a-f]{4,40}$", RegexOptions.Compiled);
    private static bool LooksLikeAFlag(string value) => !string.IsNullOrEmpty(value) && value.StartsWith('-');

    private async Task RunGitCommitTriggersAsync(CancellationToken cancellationToken)
    {
        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.GitCommit))
        {
            GitCommitTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<GitCommitTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null || string.IsNullOrWhiteSpace(config.RepositoryPath) || !Directory.Exists(config.RepositoryPath)) continue;
            if (LooksLikeAFlag(config.RepositoryPath) || LooksLikeAFlag(config.Branch)) continue;

            string refName = string.IsNullOrWhiteSpace(config.Branch) ? "HEAD" : config.Branch;
            (int exit, string sha) = await RunGitAsync(config.RepositoryPath, cancellationToken, "rev-parse", refName).ConfigureAwait(false);
            if (exit != 0 || !GitShaPattern.IsMatch(sha)) continue;

            bool firstObservation = !_lastSeenGitSha.ContainsKey(automation.Id);
            string previousSha = _lastSeenGitSha.GetOrAdd(automation.Id, sha);

            if (firstObservation) continue;
            if (string.Equals(previousSha, sha, StringComparison.Ordinal)) continue;

            _lastSeenGitSha[automation.Id] = sha;

            (int msgExit, string message) = await RunGitAsync(config.RepositoryPath, cancellationToken, "log", "-1", "--format=%s", sha).ConfigureAwait(false);
            Fire(automation, fileName: null, folderPath: null, cancellationToken,
                commitSha: sha, commitMessage: msgExit == 0 ? message : string.Empty, repositoryPath: config.RepositoryPath);
        }
    }

    private void RunModelDownloadCompletedTriggers(CancellationToken cancellationToken)
    {
        List<ModelDownloadStatus>? completed = null;

        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.ModelDownloadCompleted))
        {
            ModelDownloadCompletedTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<ModelDownloadCompletedTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null) continue;

            completed ??= _downloads.List().Where(d => d.Status == "completed").ToList();

            bool firstObservation = !_seenCompletedDownloads.ContainsKey(automation.Id);
            HashSet<string> seen = _seenCompletedDownloads.GetOrAdd(automation.Id, _ => new HashSet<string>(StringComparer.Ordinal));

            if (firstObservation)
            {
                foreach (ModelDownloadStatus download in completed) seen.Add(download.Id);
                continue;
            }

            foreach (ModelDownloadStatus download in completed)
            {
                if (!seen.Add(download.Id)) continue;
                if (!string.IsNullOrEmpty(config.FileNameContains)
                    && download.FileName.IndexOf(config.FileNameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                Fire(automation, fileName: null, folderPath: null, cancellationToken,
                    downloadFileName: download.FileName, downloadLocalPath: download.LocalPath);
            }
        }
    }

    private async Task RunComponentHealthChangedTriggersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ComponentStatus>? components = null;

        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.ComponentHealthChanged))
        {
            ComponentHealthChangedTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<ComponentHealthChangedTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null || string.IsNullOrWhiteSpace(config.ComponentId)) continue;

            components ??= await _components.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            ComponentStatus? component = components.FirstOrDefault(c => c.Id == config.ComponentId);
            if (component == null) continue;

            bool firstObservation = !_lastSeenComponentHealth.ContainsKey(automation.Id);
            string previousHealth = _lastSeenComponentHealth.GetOrAdd(automation.Id, component.Health);

            if (firstObservation) continue;
            if (string.Equals(previousHealth, component.Health, StringComparison.Ordinal)) continue;

            _lastSeenComponentHealth[automation.Id] = component.Health;
            Fire(automation, fileName: null, folderPath: null, cancellationToken,
                componentId: component.Id, componentHealth: component.Health, componentPreviousHealth: previousHealth);
        }
    }

    private void RunTaskCompletedTriggers(CancellationToken cancellationToken)
    {
        List<AgentTaskDto>? terminalTasks = null;

        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.TaskCompleted))
        {
            TaskCompletedTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<TaskCompletedTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null) continue;

            terminalTasks ??= _tasks.List().Where(t => AgentTaskStatus.Terminal.Contains(t.Status)).ToList();

            bool firstObservation = !_seenCompletedTasks.ContainsKey(automation.Id);
            HashSet<string> seen = _seenCompletedTasks.GetOrAdd(automation.Id, _ => new HashSet<string>(StringComparer.Ordinal));

            if (firstObservation)
            {
                foreach (AgentTaskDto task in terminalTasks) seen.Add(task.Id);
                continue;
            }

            foreach (AgentTaskDto task in terminalTasks)
            {
                if (!seen.Add(task.Id)) continue;
                if (task.CreatedByAutomationId == automation.Id) continue;
                if (!string.IsNullOrEmpty(config.Role) && !string.Equals(task.Role, config.Role, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(config.Status) && !string.Equals(task.Status, config.Status, StringComparison.OrdinalIgnoreCase)) continue;

                Fire(automation, fileName: null, folderPath: null, cancellationToken,
                    taskId: task.Id, taskGoal: task.Goal, taskStatus: task.Status, taskRole: task.Role);
            }
        }
    }

    /// <summary>
    /// Evaluates only allow-listed Core observations. The first value is a seed, not an event.
    /// A condition fires exactly when the previous observed value was false and the current one is
    /// true. SetEnabled/RecordRun change UpdatedUtc, so re-enabled/changed definitions are safely
    /// re-seeded instead of replaying an edge that may have occurred while disabled. Task-count
    /// signals exclude tasks created by that same automation, preventing self-sustaining loops.
    /// </summary>
    private async Task RunCustomConditionTriggersAsync(CancellationToken cancellationToken)
    {
        List<AgentTaskDto>? tasks = null;
        IReadOnlyList<ModelDownloadStatus>? downloads = null;
        IReadOnlyList<ComponentStatus>? components = null;

        foreach (AutomationDto automation in _automations.ListEnabledByTriggerType(AutomationTriggerType.CustomCondition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CustomConditionTriggerConfig? config;
            try { config = JsonSerializer.Deserialize<CustomConditionTriggerConfig>(automation.TriggerConfigJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (config == null) continue;

            string signal = (config.Signal ?? string.Empty).Trim();
            string op = (config.Operator ?? string.Empty).Trim();
            string expected = (config.Value ?? string.Empty).Trim();
            if (!AutomationConditionSignal.All.Contains(signal) || !AutomationConditionOperator.All.Contains(op)) continue;

            bool current;
            string observed;

            if (AutomationConditionSignal.Numeric.Contains(signal))
            {
                if (!int.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold)) continue;
                int count;
                switch (signal)
                {
                    case AutomationConditionSignal.FailedTaskCount:
                    case AutomationConditionSignal.RunningTaskCount:
                    case AutomationConditionSignal.CompletedTaskCount:
                        tasks ??= _tasks.List();
                        string wantedStatus = signal switch
                        {
                            AutomationConditionSignal.FailedTaskCount => AgentTaskStatus.Failed,
                            AutomationConditionSignal.RunningTaskCount => AgentTaskStatus.Running,
                            _ => AgentTaskStatus.Completed
                        };
                        count = tasks.Count(task =>
                            task.CreatedByAutomationId != automation.Id
                            && string.Equals(task.Status, wantedStatus, StringComparison.Ordinal)
                            && (string.IsNullOrWhiteSpace(config.Role)
                                || string.Equals(task.Role, config.Role, StringComparison.OrdinalIgnoreCase)));
                        break;

                    case AutomationConditionSignal.FailedDownloadCount:
                    case AutomationConditionSignal.ActiveDownloadCount:
                    case AutomationConditionSignal.CompletedDownloadCount:
                        downloads ??= _downloads.List();
                        count = downloads.Count(download =>
                        {
                            bool stateMatches = signal switch
                            {
                                AutomationConditionSignal.FailedDownloadCount => download.Status == "failed",
                                AutomationConditionSignal.ActiveDownloadCount => download.Status is "queued" or "downloading",
                                _ => download.Status == "completed"
                            };
                            return stateMatches
                                && (string.IsNullOrWhiteSpace(config.FileNameContains)
                                    || download.FileName.Contains(config.FileNameContains, StringComparison.OrdinalIgnoreCase));
                        });
                        break;

                    default:
                        continue;
                }

                observed = count.ToString(CultureInfo.InvariantCulture);
                current = CompareNumeric(count, threshold, op);
            }
            else if (signal == AutomationConditionSignal.ComponentHealth)
            {
                if (string.IsNullOrWhiteSpace(config.ComponentId) || !AutomationConditionOperator.Text.Contains(op)) continue;
                components ??= await _components.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                ComponentStatus? component = components.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, config.ComponentId, StringComparison.OrdinalIgnoreCase));
                if (component == null) continue; // missing component is unknown, never truthy by inequality.

                observed = component.Health;
                bool equal = string.Equals(component.Health, expected, StringComparison.OrdinalIgnoreCase);
                current = op == AutomationConditionOperator.Equal ? equal : !equal;
            }
            else
            {
                continue;
            }

            bool firstObservation = !_lastConditionState.TryGetValue(automation.Id, out ConditionObservation? previous)
                || previous.DefinitionUpdatedUtc != automation.UpdatedUtc;
            _lastConditionState[automation.Id] = new ConditionObservation(current, automation.UpdatedUtc);

            if (firstObservation || previous!.Value || !current) continue;

            Fire(automation, fileName: null, folderPath: null, cancellationToken,
                conditionSignal: signal,
                conditionOperator: op,
                conditionExpected: expected,
                conditionObserved: observed,
                conditionComponentId: config.ComponentId);
        }
    }

    private static bool CompareNumeric(int observed, int expected, string op) => op switch
    {
        AutomationConditionOperator.Equal => observed == expected,
        AutomationConditionOperator.NotEqual => observed != expected,
        AutomationConditionOperator.GreaterThan => observed > expected,
        AutomationConditionOperator.GreaterThanOrEqual => observed >= expected,
        AutomationConditionOperator.LessThan => observed < expected,
        AutomationConditionOperator.LessThanOrEqual => observed <= expected,
        _ => false
    };

    private static async Task<(int exit, string output)> RunGitAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
    {
        if (!PaRuX.Contracts.AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
            return (-1, "git refused: " + untrusted);
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in arguments) info.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = info };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }

            string output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return (process.ExitCode, output.Trim());
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }

    private void Fire(
        AutomationDto automation, string? fileName, string? folderPath, CancellationToken cancellationToken,
        string? commitSha = null, string? commitMessage = null, string? repositoryPath = null,
        string? downloadFileName = null, string? downloadLocalPath = null,
        string? componentId = null, string? componentHealth = null, string? componentPreviousHealth = null,
        string? taskId = null, string? taskGoal = null, string? taskStatus = null, string? taskRole = null,
        string? conditionSignal = null, string? conditionOperator = null, string? conditionExpected = null,
        string? conditionObserved = null, string? conditionComponentId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (automation.ActionType != AutomationActionType.CreateAgentTask)
            {
                _automations.RecordRun(automation.Id, "failed: unknown action type " + automation.ActionType, null);
                return;
            }

            CreateAgentTaskActionConfig? actionConfig = JsonSerializer.Deserialize<CreateAgentTaskActionConfig>(automation.ActionConfigJson, JsonOptions);
            if (actionConfig == null || string.IsNullOrWhiteSpace(actionConfig.GoalTemplate))
            {
                _automations.RecordRun(automation.Id, "failed: invalid action config", null);
                return;
            }

            string goal = actionConfig.GoalTemplate
                .Replace("{fileName}", fileName ?? string.Empty)
                .Replace("{folderPath}", folderPath ?? string.Empty)
                .Replace("{commitSha}", commitSha ?? string.Empty)
                .Replace("{commitMessage}", commitMessage ?? string.Empty)
                .Replace("{repositoryPath}", repositoryPath ?? string.Empty)
                .Replace("{downloadFileName}", downloadFileName ?? string.Empty)
                .Replace("{downloadLocalPath}", downloadLocalPath ?? string.Empty)
                .Replace("{componentId}", componentId ?? string.Empty)
                .Replace("{componentHealth}", componentHealth ?? string.Empty)
                .Replace("{componentPreviousHealth}", componentPreviousHealth ?? string.Empty)
                .Replace("{taskId}", taskId ?? string.Empty)
                .Replace("{taskGoal}", taskGoal ?? string.Empty)
                .Replace("{taskStatus}", taskStatus ?? string.Empty)
                .Replace("{taskRole}", taskRole ?? string.Empty)
                .Replace("{conditionSignal}", conditionSignal ?? string.Empty)
                .Replace("{conditionOperator}", conditionOperator ?? string.Empty)
                .Replace("{conditionExpected}", conditionExpected ?? string.Empty)
                .Replace("{conditionObserved}", conditionObserved ?? string.Empty)
                .Replace("{conditionComponentId}", conditionComponentId ?? string.Empty);

            AgentTaskDto task = _tasks.Create(new CreateAgentTaskRequest
            {
                Goal = goal, Role = actionConfig.Role, CreatedByAutomationId = automation.Id
            });
            _tasks.AppendLog(task.Id, "Created by automation \"" + automation.Name + "\" (trigger: " + automation.TriggerType + ")");
            _automations.RecordRun(automation.Id, "fired: created task " + task.Id, task.Id);
        }
        catch (Exception ex)
        {
            _automations.RecordRun(automation.Id, "failed: " + ex.Message, null);
        }
    }
}
