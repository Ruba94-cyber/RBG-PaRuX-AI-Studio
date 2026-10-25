using System.Net;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Automation;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 14/15 - AutomationStore CRUD and AutomationEngine's real trigger
/// types (Schedule, FileCreated, ModelDownloadCompleted). The directive's own acceptance scenario
/// for automations (section 31: "file appears -> automation fires -> Skill executes -> result
/// generated") maps directly onto the FileCreated tests below - a real temp folder, a real file
/// write, a real automation-engine tick.</summary>
public sealed class AutomationEngineTests : IDisposable
{
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), "parux-automation-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchDir = Path.Combine(Path.GetTempPath(), "parux-automation-watch-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdModelPaths = new();

    private static string ModelsDir => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "models");

    public AutomationEngineTests() => Directory.CreateDirectory(_watchDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true); } catch { }
        try { if (Directory.Exists(_watchDir)) Directory.Delete(_watchDir, recursive: true); } catch { }
        // A completed fake download really lands under the shared, non-injectable ModelsDirectory
        // (and records a .repo-ids.json entry) - same cleanup discipline ModelDownloadServiceTests
        // already established for exactly this reason, applied here since this test class also
        // now drives real ModelDownloadService downloads to completion.
        foreach (string path in _createdModelPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        try
        {
            string repoIdsPath = Path.Combine(ModelsDir, ".repo-ids.json");
            if (File.Exists(repoIdsPath) && _createdModelPaths.Count > 0)
            {
                var repoIds = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(repoIdsPath)) ?? new();
                bool changed = _createdModelPaths.Select(Path.GetFileName).Aggregate(false, (acc, name) => acc | repoIds.Remove(name!));
                if (changed) File.WriteAllText(repoIdsPath, JsonSerializer.Serialize(repoIds));
            }
            foreach (string name in _createdModelPaths.Select(Path.GetFileName).Where(name => name != null).Cast<string>())
                ModelDownloadProvenanceManifestStore.Remove(ModelsDir, name);
        }
        catch { /* best-effort cleanup */ }
    }

    private (AutomationStore automations, TaskStore tasks, AutomationEngine engine) Build(
        ModelDownloadService? downloads = null, ComponentInventoryService? components = null)
    {
        var automations = new AutomationStore(_dbDir);
        var tasks = new TaskStore(_dbDir);
        return (automations, tasks, new AutomationEngine(automations, tasks, downloads ?? new ModelDownloadService(),
            components ?? new ComponentInventoryService(new RuntimeManagerService(new LocalAiProcessManager()))));
    }

    /// <summary>Minimal fake HTTP handler for a real end-to-end ModelDownloadService download -
    /// same shape as ModelDownloadServiceTests' own private FakeHandler, duplicated here rather
    /// than shared across test classes (it's a few lines, and the two test classes have no other
    /// coupling reason to share a base/helper type).</summary>
    private sealed class FakeDownloadHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend(request, cancellationToken);
    }

    /// <summary>A ModelDownloadService wired to a fake handler that answers every HEAD/GET with
    /// the same small fixed body - real download lifecycle, no real network call, matching this
    /// project's own established discipline for this service (see ModelDownloadServiceTests' own
    /// class remarks). Callers can drive multiple real downloads to completion on the SAME
    /// instance via CompleteDownloadAsync below - needed to prove the "seed, don't replay
    /// history" behavior (an already-completed download at automation-creation time vs. one that
    /// completes afterward).</summary>
    private static ModelDownloadService NewFakeDownloadService()
    {
        byte[] content = { 1, 2, 3, 4, 5 };
        var handler = new FakeDownloadHandler
        {
            OnSend = (req, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
                response.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(response);
            }
        };
        return new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
    }

    private async Task<ModelDownloadStatus> CompleteDownloadAsync(ModelDownloadService service, string fileName)
    {
        _createdModelPaths.Add(Path.Combine(ModelsDir, fileName));
        _createdModelPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial");
        _createdModelPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial.source.json");
        _createdModelPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial.source.json.tmp");

        ModelDownloadStatus created = await service.CreateDownloadAsync(new ModelDownloadRequest
        {
            RepoId = "test/repo", FileName = fileName, DownloadUrl = "https://huggingface.co/test/repo/resolve/main/" + fileName
        }, CancellationToken.None);

        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? current = service.Get(created.Id);
            if (current != null && current.Status is "completed" or "failed") return current;
            await Task.Delay(20);
        }
        throw new TimeoutException("Fake download did not complete in time.");
    }

    [Fact]
    public void Create_rejects_an_unknown_trigger_type()
    {
        var (automations, _, _) = Build();
        Assert.Throws<InvalidDataException>(() => automations.Create(new CreateAutomationRequest
        {
            Name = "x", TriggerType = "not_a_real_trigger", TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{}"
        }));
    }

    [Fact]
    public void Create_rejects_an_empty_name()
    {
        var (automations, _, _) = Build();
        Assert.Throws<InvalidDataException>(() => automations.Create(new CreateAutomationRequest
        {
            Name = "  ", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{}"
        }));
    }

    [Fact]
    public void Create_then_get_round_trips_enabled_by_default()
    {
        var (automations, _, _) = Build();
        AutomationDto created = automations.Create(new CreateAutomationRequest
        {
            Name = "Nightly report", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":3600}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"do the thing\"}"
        });
        Assert.True(created.Enabled);
        Assert.Equal("Nightly report", automations.Get(created.Id)!.Name);
    }

    [Fact]
    public void SetEnabled_toggles_and_returns_false_for_an_unknown_id()
    {
        var (automations, _, _) = Build();
        AutomationDto created = automations.Create(new CreateAutomationRequest
        {
            Name = "x", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"g\"}"
        });
        Assert.True(automations.SetEnabled(created.Id, false));
        Assert.False(automations.Get(created.Id)!.Enabled);
        Assert.False(automations.SetEnabled("parux-test-unknown-" + Guid.NewGuid().ToString("N"), true));
    }

    [Fact]
    public async Task A_schedule_automation_fires_once_the_interval_has_elapsed_and_creates_a_real_task()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Immediate", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":0}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Scheduled goal\",\"role\":\"research\"}"
        });

        await engine.RunTickAsync(CancellationToken.None);

        AutomationDto? after = automations.Get(automation.Id);
        Assert.NotNull(after!.LastCreatedTaskId);
        AgentTaskDto? task = tasks.Get(after.LastCreatedTaskId!);
        Assert.NotNull(task);
        Assert.Equal("Scheduled goal", task!.Goal);
        Assert.Equal("research", task.Role);
    }

    [Fact]
    public async Task A_fresh_schedule_automation_does_not_fire_before_its_interval_has_elapsed_since_creation()
    {
        // No injectable clock in AutomationEngine (it uses DateTime.UtcNow directly, matching the
        // rest of this codebase's convention), so this exercises the real "not due yet" path
        // rather than simulating elapsed time: a just-created hourly automation must not fire on
        // the very next tick, which is the actual scenario a background 2s-interval loop hits
        // constantly (every automation gets ticked every 2s; only the ones actually due should fire).
        var (automations, tasks, engine) = Build();
        automations.Create(new CreateAutomationRequest
        {
            Name = "Hourly", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":3600}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"g\"}"
        });

        await engine.RunTickAsync(CancellationToken.None);

        Assert.Empty(tasks.List());
    }

    [Fact]
    public async Task A_schedule_automation_does_not_fire_again_before_the_next_interval_once_it_has_already_fired()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Hourly", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":3600}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"g\"}"
        });
        // Simulate "it already fired a moment ago" directly through the store (no clock to fast-
        // forward), so the next tick's elapsed-since-LastTriggeredUtc is well under the interval.
        automations.RecordRun(automation.Id, "fired: created task seed-task", "seed-task-id");

        await engine.RunTickAsync(CancellationToken.None);

        Assert.Equal("seed-task-id", automations.Get(automation.Id)!.LastCreatedTaskId);
        Assert.Empty(tasks.List());
    }

    [Fact]
    public async Task A_disabled_automation_never_fires()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Off", TriggerType = AutomationTriggerType.Schedule, TriggerConfigJson = "{\"intervalSeconds\":0}",
            ActionType = AutomationActionType.CreateAgentTask, ActionConfigJson = "{\"goalTemplate\":\"should never run\"}"
        });
        automations.SetEnabled(automation.Id, false);

        await engine.RunTickAsync(CancellationToken.None);

        Assert.Null(automations.Get(automation.Id)!.LastCreatedTaskId);
        Assert.Empty(tasks.List());
    }

    /// <summary>The directive's own automation acceptance scenario (section 31): a file appears
    /// in a watched folder, and that alone - no manual trigger - causes a real task to be
    /// created. Pre-existing files at automation-creation time must NOT fire (seed-not-replay).</summary>
    [Fact]
    public async Task A_file_created_automation_fires_only_for_a_file_that_appears_after_the_first_tick()
    {
        var (automations, tasks, engine) = Build();
        File.WriteAllText(Path.Combine(_watchDir, "pre-existing.txt"), "already here");

        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Watch folder", TriggerType = AutomationTriggerType.FileCreated,
            TriggerConfigJson = "{\"folderPath\":\"" + _watchDir.Replace("\\", "\\\\") + "\",\"filter\":\"*\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Process the new file: {fileName}\"}"
        });

        // First tick only seeds the pre-existing file - must not fire for it.
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Null(automations.Get(automation.Id)!.LastCreatedTaskId);
        Assert.Empty(tasks.List());

        // A genuinely new file appearing afterward must fire.
        File.WriteAllText(Path.Combine(_watchDir, "new-report.csv"), "real data");
        await engine.RunTickAsync(CancellationToken.None);

        AutomationDto? after = automations.Get(automation.Id);
        Assert.NotNull(after!.LastCreatedTaskId);
        AgentTaskDto task = tasks.Get(after.LastCreatedTaskId!)!;
        Assert.Equal("Process the new file: new-report.csv", task.Goal);
    }

    [Fact]
    public async Task A_file_created_automation_does_not_refire_for_the_same_file_on_a_later_tick()
    {
        var (automations, tasks, engine) = Build();
        automations.Create(new CreateAutomationRequest
        {
            Name = "Watch folder", TriggerType = AutomationTriggerType.FileCreated,
            TriggerConfigJson = "{\"folderPath\":\"" + _watchDir.Replace("\\", "\\\\") + "\",\"filter\":\"*\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"handle {fileName}\"}"
        });
        await engine.RunTickAsync(CancellationToken.None); // seed (empty folder)

        File.WriteAllText(Path.Combine(_watchDir, "once.txt"), "x");
        await engine.RunTickAsync(CancellationToken.None);
        await engine.RunTickAsync(CancellationToken.None);
        await engine.RunTickAsync(CancellationToken.None);

        Assert.Single(tasks.List());
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = new System.Diagnostics.Process { StartInfo = info };
        process.Start();
        string stdOut = await process.StandardOutput.ReadToEndAsync();
        string stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stdOut}{stdErr}");
    }

    /// <summary>The directive's own GitCommit acceptance shape, mirrored from the FileCreated
    /// test above: a real temp git repository, a real commit, a real automation-engine tick -
    /// not a mocked git client.</summary>
    [Fact]
    public async Task A_git_commit_automation_fires_once_per_new_commit_after_seeding()
    {
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-automation-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);
        try
        {
            await RunGitAsync(repoDir, "init", "-b", "main");
            await RunGitAsync(repoDir, "config", "user.email", "test@parux.local");
            await RunGitAsync(repoDir, "config", "user.name", "PaRuX Test");
            File.WriteAllText(Path.Combine(repoDir, "a.txt"), "one");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "initial commit");

            var (automations, tasks, engine) = Build();
            automations.Create(new CreateAutomationRequest
            {
                Name = "Watch repo", TriggerType = AutomationTriggerType.GitCommit,
                TriggerConfigJson = "{\"repositoryPath\":\"" + repoDir.Replace("\\", "\\\\") + "\"}",
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"Review commit {commitSha}: {commitMessage}\"}"
            });

            // First tick only seeds - the pre-existing "initial commit" must not fire (same
            // "don't replay history" rule as FileCreated on an already-populated folder).
            await engine.RunTickAsync(CancellationToken.None);
            Assert.Empty(tasks.List());

            File.WriteAllText(Path.Combine(repoDir, "b.txt"), "two");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "add feature B");

            await engine.RunTickAsync(CancellationToken.None);

            AgentTaskDto task = Assert.Single(tasks.List());
            Assert.Contains("add feature B", task.Goal);

            // A later tick with no new commit must not refire.
            await engine.RunTickAsync(CancellationToken.None);
            Assert.Single(tasks.List());
        }
        finally
        {
            try { Directory.Delete(repoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Security regression: a `Branch` value shaped like a git flag (e.g.
    /// `--output=...`) must never reach a git invocation as a bare positional argument - `git
    /// rev-parse` echoes unrecognized flags to stdout with exit code 0 instead of erroring, which
    /// would otherwise let that flag-shaped string get reused in a second git call.</summary>
    [Fact]
    public async Task A_git_commit_automation_with_a_flag_shaped_branch_never_fires()
    {
        string repoDir = Path.Combine(Path.GetTempPath(), "parux-automation-git-injection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);
        try
        {
            await RunGitAsync(repoDir, "init", "-b", "main");
            await RunGitAsync(repoDir, "config", "user.email", "test@parux.local");
            await RunGitAsync(repoDir, "config", "user.name", "PaRuX Test");
            File.WriteAllText(Path.Combine(repoDir, "a.txt"), "one");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "initial commit");

            string canaryPath = Path.Combine(Path.GetTempPath(), "parux-injection-canary-" + Guid.NewGuid().ToString("N") + ".txt");
            var (automations, tasks, engine) = Build();
            automations.Create(new CreateAutomationRequest
            {
                Name = "Malicious branch", TriggerType = AutomationTriggerType.GitCommit,
                TriggerConfigJson = JsonSerializer.Serialize(new GitCommitTriggerConfig
                {
                    RepositoryPath = repoDir, Branch = "--output=" + canaryPath
                }),
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"x\"}"
            });

            await engine.RunTickAsync(CancellationToken.None);
            await engine.RunTickAsync(CancellationToken.None);

            Assert.False(File.Exists(canaryPath));
            Assert.Empty(tasks.List());
        }
        finally
        {
            try { Directory.Delete(repoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_git_commit_automation_pointed_at_a_missing_folder_does_not_throw()
    {
        var (automations, tasks, engine) = Build();
        automations.Create(new CreateAutomationRequest
        {
            Name = "Watch missing repo", TriggerType = AutomationTriggerType.GitCommit,
            TriggerConfigJson = "{\"repositoryPath\":\"" + Path.Combine(Path.GetTempPath(), "parux-does-not-exist-" + Guid.NewGuid().ToString("N")).Replace("\\", "\\\\") + "\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"x\"}"
        });

        await engine.RunTickAsync(CancellationToken.None);

        Assert.Empty(tasks.List());
    }

    /// <summary>The directive's "model event" trigger category (row #30's scope-mapping gap),
    /// added 2026-08-27 - connects real Model Hub downloads into the same automation pipeline
    /// FileCreated/GitCommit already use. Same seed-don't-replay-history shape as those two: a
    /// download that completed before the automation existed must not fire retroactively, only
    /// one that completes afterward.</summary>
    [Fact]
    public async Task A_model_download_completed_automation_seeds_existing_downloads_then_fires_only_for_new_ones()
    {
        ModelDownloadService downloads = NewFakeDownloadService();
        string firstFile = "parux-test-" + Guid.NewGuid().ToString("N") + ".gguf";
        ModelDownloadStatus firstStatus = await CompleteDownloadAsync(downloads, firstFile);
        Assert.Equal("completed", firstStatus.Status);

        var (automations, tasks, engine) = Build(downloads);
        automations.Create(new CreateAutomationRequest
        {
            Name = "On model download", TriggerType = AutomationTriggerType.ModelDownloadCompleted,
            TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Benchmark {downloadFileName}\"}"
        });

        // First tick only seeds - the download that completed before this automation existed
        // must not fire (same "don't replay history" rule as FileCreated/GitCommit above).
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Empty(tasks.List());

        string secondFile = "parux-test-" + Guid.NewGuid().ToString("N") + ".gguf";
        await CompleteDownloadAsync(downloads, secondFile);

        await engine.RunTickAsync(CancellationToken.None);

        AgentTaskDto task = Assert.Single(tasks.List());
        Assert.Equal("Benchmark " + secondFile, task.Goal);

        // A later tick with no new completed download must not refire.
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Single(tasks.List());
    }

    [Fact]
    public async Task A_model_download_completed_automation_with_a_file_name_filter_ignores_non_matching_downloads()
    {
        ModelDownloadService downloads = NewFakeDownloadService();
        var (automations, tasks, engine) = Build(downloads);
        automations.Create(new CreateAutomationRequest
        {
            Name = "On matching model download", TriggerType = AutomationTriggerType.ModelDownloadCompleted,
            TriggerConfigJson = JsonSerializer.Serialize(new ModelDownloadCompletedTriggerConfig { FileNameContains = "wanted" }),
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Benchmark {downloadFileName}\"}"
        });
        await engine.RunTickAsync(CancellationToken.None); // seed (nothing completed yet)

        string ignoredFile = "parux-test-skipme-" + Guid.NewGuid().ToString("N") + ".gguf";
        await CompleteDownloadAsync(downloads, ignoredFile);
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Empty(tasks.List());

        string matchingFile = "parux-test-wanted-" + Guid.NewGuid().ToString("N") + ".gguf";
        await CompleteDownloadAsync(downloads, matchingFile);
        await engine.RunTickAsync(CancellationToken.None);

        AgentTaskDto task = Assert.Single(tasks.List());
        Assert.Equal("Benchmark " + matchingFile, task.Goal);
    }

    /// <summary>The directive's "system event" trigger category (row #30's remaining gap),
    /// connecting the Runtime Manager component inventory (added earlier 2026-08-27) into the
    /// same Automation Engine pipeline. Forces a real, deterministic health transition via
    /// LocalAiPaths' own Locate mechanism - pointing a component at a path that does not exist
    /// yet reports real "missing" health, then creating that same file makes DiscoverAsync
    /// re-check and report real "detected" health - rather than depending on this machine's
    /// ambient PATH state (which the test cannot control or predict across environments).</summary>
    [Fact]
    public async Task A_component_health_changed_automation_fires_on_a_real_missing_to_detected_transition()
    {
        const string componentId = "ffmpeg";
        string fakePath = Path.Combine(Path.GetTempPath(), "parux-automation-component-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Locate a path that does not exist YET - DiscoverAsync re-checks File.Exists on
            // every call (documented behavior, not cached), so this is a real, deterministic
            // "missing" health regardless of whether ffmpeg happens to be on this machine's PATH.
            File.WriteAllText(fakePath, "placeholder");
            LocalAiPaths.SetComponentLocation(componentId, fakePath);
            File.Delete(fakePath);

            var (automations, tasks, engine) = Build();
            automations.Create(new CreateAutomationRequest
            {
                Name = "On ffmpeg health change", TriggerType = AutomationTriggerType.ComponentHealthChanged,
                TriggerConfigJson = JsonSerializer.Serialize(new ComponentHealthChangedTriggerConfig { ComponentId = componentId }),
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"{componentId} is now {componentHealth} (was {componentPreviousHealth})\"}"
            });

            // First tick only seeds the real current ("missing") health - must not fire.
            await engine.RunTickAsync(CancellationToken.None);
            Assert.Empty(tasks.List());

            // Real transition: the located path now genuinely exists.
            File.WriteAllText(fakePath, "placeholder");
            await engine.RunTickAsync(CancellationToken.None);

            AgentTaskDto task = Assert.Single(tasks.List());
            Assert.Equal("ffmpeg is now detected (was missing)", task.Goal);

            // A later tick with no further health change must not refire.
            await engine.RunTickAsync(CancellationToken.None);
            Assert.Single(tasks.List());
        }
        finally
        {
            LocalAiPaths.ClearComponentLocation(componentId);
            try { if (File.Exists(fakePath)) File.Delete(fakePath); } catch { }
        }
    }

    [Fact]
    public async Task A_component_health_changed_automation_pointed_at_an_unknown_component_id_does_not_throw()
    {
        var (automations, tasks, engine) = Build();
        automations.Create(new CreateAutomationRequest
        {
            Name = "Watch unknown component", TriggerType = AutomationTriggerType.ComponentHealthChanged,
            TriggerConfigJson = JsonSerializer.Serialize(new ComponentHealthChangedTriggerConfig { ComponentId = "not-a-real-component" }),
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"x\"}"
        });

        await engine.RunTickAsync(CancellationToken.None);
        await engine.RunTickAsync(CancellationToken.None);

        Assert.Empty(tasks.List());
    }

    /// <summary>The directive's "application event" trigger category, added 2026-08-27 -
    /// connects the Agent Orchestrator's own task lifecycle into the same Automation Engine
    /// pipeline. Same seed-don't-replay-history shape as ModelDownloadCompleted.</summary>
    [Fact]
    public async Task A_task_completed_automation_seeds_existing_terminal_tasks_then_fires_only_for_new_ones()
    {
        var (automations, tasks, engine) = Build();
        AgentTaskDto preExisting = tasks.Create(new CreateAgentTaskRequest { Goal = "already done before automation existed" });
        tasks.UpdateStatus(preExisting.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        automations.Create(new CreateAutomationRequest
        {
            Name = "On any task completion", TriggerType = AutomationTriggerType.TaskCompleted,
            TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Follow up on {taskId}: {taskGoal} ({taskStatus})\"}"
        });

        // First tick only seeds - the task that finished before this automation existed must not
        // fire (same "don't replay history" rule as every other poll-based trigger above).
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Single(tasks.List()); // still just the pre-existing one

        AgentTaskDto newTask = tasks.Create(new CreateAgentTaskRequest { Goal = "do the real work" });
        tasks.UpdateStatus(newTask.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });

        await engine.RunTickAsync(CancellationToken.None);

        AgentTaskDto followUp = Assert.Single(tasks.List(), t => t.Id != preExisting.Id && t.Id != newTask.Id);
        Assert.Equal("Follow up on " + newTask.Id + ": do the real work (failed)", followUp.Goal);

        // A later tick with nothing newly terminal must not refire.
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Equal(3, tasks.List().Count);
    }

    /// <summary>The real safeguard this trigger uniquely needs: its own action creates more of
    /// the exact thing it watches (a terminal task), so without excluding a task this automation
    /// itself created, the very first fire would create a second task that - once IT reaches a
    /// terminal state - would fire again, forever. Proves the fix: CreatedByAutomationId
    /// correctly excludes only this automation's own output, not tasks in general.</summary>
    [Fact]
    public async Task A_task_completed_automation_does_not_refire_on_a_task_it_created_itself()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Self-loop risk", TriggerType = AutomationTriggerType.TaskCompleted,
            TriggerConfigJson = "{}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Follow up on {taskId}\"}"
        });
        await engine.RunTickAsync(CancellationToken.None); // seed (nothing terminal yet)

        AgentTaskDto trigger = tasks.Create(new CreateAgentTaskRequest { Goal = "the real trigger" });
        tasks.UpdateStatus(trigger.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        await engine.RunTickAsync(CancellationToken.None);
        AgentTaskDto created = Assert.Single(tasks.List(), t => t.Id != trigger.Id);
        Assert.Equal(automation.Id, created.CreatedByAutomationId);

        // Complete the automation's own output too - if the self-loop guard were missing, this
        // tick would create a THIRD task following up on `created`, and so on forever.
        tasks.UpdateStatus(created.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });
        await engine.RunTickAsync(CancellationToken.None);

        Assert.Equal(2, tasks.List().Count); // still just `trigger` and its one real follow-up
    }

    [Fact]
    public async Task A_task_completed_automation_with_role_and_status_filters_ignores_non_matching_tasks()
    {
        var (automations, tasks, engine) = Build();
        automations.Create(new CreateAutomationRequest
        {
            Name = "On failed coding tasks only", TriggerType = AutomationTriggerType.TaskCompleted,
            TriggerConfigJson = JsonSerializer.Serialize(new TaskCompletedTriggerConfig { Role = "coding", Status = AgentTaskStatus.Failed }),
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Investigate {taskId}\"}"
        });
        await engine.RunTickAsync(CancellationToken.None); // seed

        AgentTaskDto wrongRole = tasks.Create(new CreateAgentTaskRequest { Goal = "x", Role = "research" });
        tasks.UpdateStatus(wrongRole.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });
        AgentTaskDto wrongStatus = tasks.Create(new CreateAgentTaskRequest { Goal = "x", Role = "coding" });
        tasks.UpdateStatus(wrongStatus.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Equal(2, tasks.List().Count); // neither matched - no follow-up created

        AgentTaskDto matching = tasks.Create(new CreateAgentTaskRequest { Goal = "x", Role = "coding" });
        tasks.UpdateStatus(matching.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });
        await engine.RunTickAsync(CancellationToken.None);

        AgentTaskDto followUp = Assert.Single(tasks.List(), t => t.Goal.StartsWith("Investigate"));
        Assert.Equal("Investigate " + matching.Id, followUp.Goal);
    }
}
