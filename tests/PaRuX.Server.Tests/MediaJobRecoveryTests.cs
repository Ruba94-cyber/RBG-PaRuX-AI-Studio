using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

// Restart recovery for media jobs: the job file, reattaching to a ComfyUI prompt that is still running, collecting one that
// finished, failing the rest honestly, and never sending a ComfyUI submission that PaRuX could not record first.
// Uses the loopback FakeComfyUi from ComfyCancelPropagationTests. Every file lives under a temp root.
public sealed class MediaJobRecoveryTests : IDisposable
{
    private static readonly byte[] FinishedImage = Encoding.ASCII.GetBytes("recovered-image-bytes");
    private const string InjectedRecoveryErrorPath = @"D:\fixtures\media\recovered.png";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-media-recovery-" + Guid.NewGuid().ToString("N"));

    public MediaJobRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string StateDir => Path.Combine(_root, "state");
    private string OutputRoot => Path.Combine(_root, "output");
    private string TemplatePath => Path.Combine(_root, "template.json");

    [Fact]
    public void The_job_file_keeps_only_the_newest_records_and_is_replaced_in_one_step()
    {
        var store = new MediaJobStore(StateDir);
        DateTime start = DateTime.UtcNow.AddHours(-1);
        List<MediaJobRecord> records = Enumerable.Range(0, MediaJobStore.MaximumRecords + 10)
            .Select(i => Record("job-" + i, "completed", start.AddSeconds(i)))
            .ToList();

        store.Save(() => records);

        IReadOnlyList<MediaJobRecord> loaded = store.Load();
        Assert.Equal(MediaJobStore.MaximumRecords, loaded.Count);
        Assert.DoesNotContain(loaded, record => record.Id == "job-0");
        Assert.Contains(loaded, record => record.Id == "job-" + (MediaJobStore.MaximumRecords + 9));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void An_unreadable_job_file_is_moved_aside_and_recovery_starts_empty()
    {
        Directory.CreateDirectory(StateDir);
        string path = Path.Combine(StateDir, MediaJobStore.FileName);
        File.WriteAllText(path, "{ not json");
        var store = new MediaJobStore(StateDir);

        Assert.Empty(store.Load());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(StateDir, MediaJobStore.FileName + ".damaged-*.bak"));
    }

    [Fact]
    public async Task A_job_with_no_comfyui_prompt_is_failed_with_its_last_stage_and_finished_jobs_are_not_restored()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[]
        {
            Record("job-running", "running", DateTime.UtcNow),
            Record("job-done", "completed", DateTime.UtcNow.AddMinutes(-5))
        });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = Assert.Single(jobs.List());
        Assert.Equal("job-running", status.Id);
        Assert.Equal("failed", status.Status);
        Assert.Equal("Interrupted by restart", status.Stage);
        Assert.Contains("last stage: Generating image", status.Error);
        Assert.Equal("failed", Assert.Single(store.Load()).Status);
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("img2img")]
    public async Task A_running_single_image_prompt_is_reattached_and_its_output_is_collected_when_it_finishes(string type)
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-reattach");
        // ComfyUI saves the image when the prompt finishes; it is already on disk so the collection cannot race the finish.
        WriteFakeOutput(promptId);
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-reattach", promptId, type) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(30));
        Assert.Equal("running", jobs.Get("job-a")!.Status);

        comfy.CompleteAll();
        MediaJobStatus done = await WaitForTerminalAsync(jobs, "job-a");

        Assert.Equal("completed", done.Status);
        Assert.Equal("fake-" + promptId + ".png", done.Asset!.FileName);
        Assert.Equal(FinishedImage, Convert.FromBase64String(done.Asset.Base64Data));
        Assert.Equal(0, comfy.CountRequests("POST /queue"));
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
    }

    [Fact]
    public async Task An_unreachable_reattach_yields_the_execution_gate_and_can_resume_when_comfyui_returns()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-reattach-yield");
        WriteFakeOutput(promptId);
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-reattach-yield", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        WriteTemplate();
        MediaJobService jobs = NewService(
            store,
            runner,
            async (_, _, ct) =>
            {
                string output = await runner.SubmitAndWaitAsync(
                    TemplatePath, _ => { }, "11", TimeSpan.FromMinutes(1), ct);
                return new MediaAsset
                {
                    FileName = Path.GetFileName(output),
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(new byte[] { 2 })
                };
            },
            TimeSpan.FromMilliseconds(20));

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(15));
        Assert.Equal("running", jobs.Get("job-a")!.Status);

        int queueProbesBeforeFailure = comfy.CountRequests("GET /queue");
        comfy.FailQueueReads = true;
        await WaitForRequestCountAsync(comfy, "GET /queue", queueProbesBeforeFailure + 1, TimeSpan.FromSeconds(2));
        MediaJobStatus second = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "second" });
        await WaitForRequestCountAsync(comfy, "POST /prompt", 2, TimeSpan.FromSeconds(2));

        Assert.Equal("running", jobs.Get("job-a")!.Status);
        Assert.Equal("running", Assert.Single(store.Load().Where(record => record.Id == "job-a")).Status);

        comfy.FailQueueReads = false;
        comfy.CompleteAll();
        MediaJobStatus recovered = await WaitForTerminalAsync(jobs, "job-a");
        MediaJobStatus generated = await WaitForTerminalAsync(jobs, second.Id);

        Assert.Equal("completed", recovered.Status);
        Assert.Equal(FinishedImage, Convert.FromBase64String(recovered.Asset!.Base64Data));
        Assert.Equal("completed", generated.Status);
    }

    [Fact]
    public async Task A_prompt_that_finished_while_paritx_was_down_is_collected_during_recovery()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-finished");
        WriteFakeOutput(promptId);
        comfy.CompleteAll();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-finished", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-a")!;
        Assert.Equal("completed", status.Status);
        Assert.Equal(FinishedImage, Convert.FromBase64String(status.Asset!.Base64Data));
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
    }

    [Fact]
    public async Task An_oversized_recovered_image_is_not_read_and_keeps_its_path_reference()
    {
        const long assumedRecoveryLimitBytes = 64L * 1024L * 1024L;
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-large-recovery");
        string outputPath = WriteSparseFakeOutput(promptId, assumedRecoveryLimitBytes + 1);
        comfy.CompleteAll();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-large", "running", DateTime.UtcNow, "parux-client-large-recovery", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        int readCount = 0;
        MediaJobService jobs = NewService(store, runner, recoveredOutputReader: _ =>
        {
            readCount++;
            return FinishedImage;
        });

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-large")!;
        Assert.Equal(0, readCount);
        Assert.Equal(assumedRecoveryLimitBytes + 1, new FileInfo(outputPath).Length);
        Assert.Equal("failed", status.Status);
        Assert.Equal("output_too_large_for_recovery", status.ErrorCode);
        Assert.NotNull(status.Asset);
        Assert.Empty(status.Asset.Base64Data);
        Assert.Equal(outputPath, ReadStringProperty(status.Asset, "FilePath"));
        Assert.Equal(outputPath, ReadStringProperty(Assert.Single(store.Load()), "AssetFilePath"));

        int queueReadsBeforeNextRestart = comfy.CountRequests("GET /queue");
        MediaJobService nextRestart = NewService(store, runner);
        await nextRestart.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));
        MediaJobStatus? persistedStatus = nextRestart.Get("job-large");
        Assert.NotNull(persistedStatus);
        Assert.Equal("output_too_large_for_recovery", persistedStatus!.ErrorCode);
        Assert.Equal(outputPath, ReadStringProperty(persistedStatus.Asset, "FilePath"));
        Assert.Equal(queueReadsBeforeNextRestart, comfy.CountRequests("GET /queue"));
    }

    [Fact]
    public async Task A_recovered_image_at_exactly_the_recovery_limit_is_still_collected()
    {
        const long recoveryLimitBytes = 64L * 1024L * 1024L;
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-limit-recovery");
        WriteSparseFakeOutput(promptId, recoveryLimitBytes);
        comfy.CompleteAll();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-limit", "running", DateTime.UtcNow, "parux-client-limit-recovery", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        int readCount = 0;
        MediaJobService jobs = NewService(store, runner, recoveredOutputReader: _ =>
        {
            readCount++;
            return FinishedImage;
        });

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-limit")!;
        Assert.Equal(1, readCount);
        Assert.Equal("completed", status.Status);
        Assert.Null(status.ErrorCode);
        Assert.Equal(FinishedImage, Convert.FromBase64String(status.Asset!.Base64Data));
    }

    [Fact]
    public async Task An_unreachable_comfy_probe_reports_its_exception_type_not_its_transport_message()
    {
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-unreachable", "running", DateTime.UtcNow, "parux-client-unreachable", "prompt-unreachable") });
        ComfyWorkflowRunner runner = NewRunner("http://127.0.0.1:1");
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-unreachable")!;
        Assert.Equal("failed", status.Status);
        Assert.Contains("could not be checked after the restart", status.Error);
        Assert.DoesNotContain("127.0.0.1", status.Error);
        Assert.DoesNotContain("refused", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recovered_video_is_referenced_by_path_without_reading_the_file()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-video-finished");
        string outputPath = WriteFakeOutput(promptId);
        comfy.CompleteAll();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-video", "running", DateTime.UtcNow, "parux-client-video-finished", promptId, type: "video") });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        int readCount = 0;
        MediaJobService jobs = NewService(store, runner, recoveredOutputReader: _ =>
        {
            readCount++;
            return FinishedImage;
        });

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-video")!;
        Assert.Equal(0, readCount);
        Assert.Equal("completed", status.Status);
        Assert.NotNull(status.Asset);
        Assert.Empty(status.Asset.Base64Data);
        Assert.Equal(outputPath, ReadStringProperty(status.Asset, "FilePath"));
        Assert.Equal(outputPath, ReadStringProperty(Assert.Single(store.Load()), "AssetFilePath"));

        int queueReadsBeforeNextRestart = comfy.CountRequests("GET /queue");
        MediaJobService nextRestart = NewService(store, runner);
        await nextRestart.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));
        MediaJobStatus? persistedStatus = nextRestart.Get("job-video");
        Assert.NotNull(persistedStatus);
        Assert.Equal("completed", persistedStatus!.Status);
        Assert.Equal(outputPath, ReadStringProperty(persistedStatus.Asset, "FilePath"));
        Assert.Equal(queueReadsBeforeNextRestart, comfy.CountRequests("GET /queue"));
    }

    [Fact]
    public async Task Recovery_collection_failure_uses_a_typed_status_and_logs_raw_exception_locally()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-recovery-error");
        WriteFakeOutput(promptId);
        comfy.CompleteAll();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-recovery-error", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        var logger = new CapturingLogger<MediaJobService>();
        var injectedException = new IOException("Could not read " + InjectedRecoveryErrorPath);
        MediaJobService jobs = NewService(store, runner,
            recoveredOutputReader: _ => throw injectedException,
            logger: logger);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-a")!;
        Assert.Equal("failed", status.Status);
        Assert.Equal("The finished media output could not be collected after restart.", status.Error);
        Assert.Equal("media_recovery_output_failed", status.ErrorCode);
        Assert.Same(injectedException, Assert.Single(logger.Exceptions));
        Assert.Contains(InjectedRecoveryErrorPath, injectedException.Message);
        Assert.DoesNotContain(InjectedRecoveryErrorPath, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(InjectedRecoveryErrorPath, string.Join(Environment.NewLine, logger.Messages));
        Assert.DoesNotContain(InjectedRecoveryErrorPath, File.ReadAllText(store.FilePath));
        Assert.Equal("media_recovery_output_failed", Assert.Single(store.Load()).ErrorCode);
    }

    [Fact]
    public async Task Reattached_collection_failure_uses_a_typed_status_and_logs_raw_exception_locally()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-reattach-error");
        WriteFakeOutput(promptId);
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-reattach-error", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        var logger = new CapturingLogger<MediaJobService>();
        var injectedException = new IOException("Could not read " + InjectedRecoveryErrorPath);
        MediaJobService jobs = NewService(store, runner,
            reattachPollInterval: TimeSpan.FromMilliseconds(10),
            recoveredOutputReader: _ => throw injectedException,
            logger: logger);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.Equal("running", jobs.Get("job-a")!.Status);

        comfy.CompleteAll();
        MediaJobStatus status = await WaitForTerminalAsync(jobs, "job-a");

        Assert.Equal("failed", status.Status);
        Assert.Equal("The reattached media job could not be collected after restart.", status.Error);
        Assert.Equal("media_recovery_failed", status.ErrorCode);
        Assert.Same(injectedException, Assert.Single(logger.Exceptions));
        Assert.Contains(InjectedRecoveryErrorPath, injectedException.Message);
        Assert.DoesNotContain(InjectedRecoveryErrorPath, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(InjectedRecoveryErrorPath, string.Join(Environment.NewLine, logger.Messages));
        Assert.DoesNotContain(InjectedRecoveryErrorPath, File.ReadAllText(store.FilePath));
        Assert.Equal("media_recovery_failed", Assert.Single(store.Load()).ErrorCode);
    }

    [Fact]
    public async Task A_running_prompt_that_cannot_be_collected_after_a_restart_is_stopped_and_the_job_is_failed()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-video");
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-v", "running", DateTime.UtcNow, "parux-client-video", promptId, type: "video") });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-v")!;
        Assert.Equal("failed", status.Status);
        Assert.Contains("It was stopped.", status.Error);
        Assert.Equal(1, comfy.CountRequests("POST /interrupt"));
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task An_output_path_that_leaves_the_output_root_is_refused()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-escape");
        // ComfyUI reports a saved image under a folder outside the output root.
        comfy.OutputSubfolder = "..\\..\\outside";
        comfy.CompleteAll();
        using var http = new HttpClient();

        ComfyPromptRecovery.Probe probe = await ComfyPromptRecovery.ProbeAsync(
            http, comfy.BaseUrl, OutputRoot, new[] { "parux-client-escape" }, new[] { promptId }, CancellationToken.None);

        Assert.Equal(ComfyPromptRecovery.State.Failed, probe.State);
        Assert.Null(probe.OutputPath);
    }

    [Fact]
    public async Task A_non_successful_history_response_is_unreachable()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        comfy.FailHistoryReads = true;
        using var http = new HttpClient();

        ComfyPromptRecovery.Probe probe = await ComfyPromptRecovery.ProbeAsync(
            http, comfy.BaseUrl, OutputRoot, Array.Empty<string>(), new[] { "prompt-500" }, CancellationToken.None);

        Assert.Equal(ComfyPromptRecovery.State.Unreachable, probe.State);
    }

    [Fact]
    public async Task Completed_history_without_outputs_is_a_failed_prompt_instead_of_crashing_recovery()
    {
        using var http = new HttpClient(new MissingOutputsHistoryHandler());

        ComfyPromptRecovery.Probe probe = await ComfyPromptRecovery.ProbeAsync(
            http, "http://comfy.test", OutputRoot, Array.Empty<string>(), new[] { "prompt-no-output" }, CancellationToken.None);

        Assert.Equal(ComfyPromptRecovery.State.Failed, probe.State);
        Assert.Null(probe.OutputPath);
    }

    private sealed class MissingOutputsHistoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.RequestUri!.AbsolutePath == "/queue"
                ? "{\"queue_running\":[],\"queue_pending\":[]}"
                : "{\"prompt-no-output\":{\"status\":{\"completed\":true,\"status_str\":\"success\"}}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task A_bad_recovered_output_path_fails_only_its_job_and_recovery_continues()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        string badPromptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-bad-output-path");
        string goodPromptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-good-output-path");
        WriteFakeOutput(badPromptId);
        WriteFakeOutput(goodPromptId);
        comfy.CompleteAll();
        comfy.OutputSubfolderForPrompt = id => id == badPromptId ? "\0" : string.Empty;

        var store = new MediaJobStore(StateDir);
        DateTime now = DateTime.UtcNow;
        store.Save(() => new[]
        {
            Record("job-bad-output-path", "running", now, "parux-client-bad-output-path", badPromptId),
            Record("job-good-output-path", "running", now.AddSeconds(1), "parux-client-good-output-path", goodPromptId)
        });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        Assert.Equal("failed", jobs.Get("job-bad-output-path")!.Status);
        MediaJobStatus good = jobs.Get("job-good-output-path")!;
        Assert.Equal("completed", good.Status);
        Assert.Equal(FinishedImage, Convert.FromBase64String(good.Asset!.Base64Data));
        Assert.Equal(1, comfy.CountRequests("GET /history/" + badPromptId));
        Assert.Equal(1, comfy.CountRequests("GET /history/" + goodPromptId));
    }

    [Fact]
    public async Task A_successful_empty_history_response_stays_gone()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var http = new HttpClient();

        ComfyPromptRecovery.Probe probe = await ComfyPromptRecovery.ProbeAsync(
            http, comfy.BaseUrl, OutputRoot, Array.Empty<string>(), new[] { "prompt-missing" }, CancellationToken.None);

        Assert.Equal(ComfyPromptRecovery.State.Gone, probe.State);
    }

    [Fact]
    public async Task When_comfyui_cannot_be_checked_after_a_restart_the_job_says_so_instead_of_guessing()
    {
        var comfy = await FakeComfyUi.StartAsync();
        string baseUrl = comfy.BaseUrl;
        await comfy.DisposeAsync();
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-unknown", "prompt-unknown") });
        ComfyWorkflowRunner runner = NewRunner(baseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-a")!;
        Assert.Equal("failed", status.Status);
        Assert.Contains("could not be checked after the restart", status.Error);
    }

    [Fact]
    public async Task A_submission_that_cannot_be_recorded_is_never_sent_to_comfyui()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        WriteTemplate();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        ComfyWorkflowRunner.SubmissionObserver.Value = (_, promptId, _) =>
        {
            if (promptId == null) throw new IOException("The job file cannot be written.");
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => runner.SubmitAndWaitAsync(
                TemplatePath, _ => { }, "11", TimeSpan.FromSeconds(30), CancellationToken.None));
        }
        finally
        {
            ComfyWorkflowRunner.SubmissionObserver.Value = null;
        }

        Assert.Equal(0, comfy.CountRequests("POST /prompt"));
    }

    [Fact]
    public async Task The_submission_observer_records_the_client_id_before_the_post_and_the_prompt_id_after_it()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        WriteTemplate();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        var events = new List<(string ClientId, string? PromptId, string SaveNodeId)>();
        ComfyWorkflowRunner.SubmissionObserver.Value = (clientId, promptId, saveNodeId) => events.Add((clientId, promptId, saveNodeId));
        try
        {
            Task<string> submit = runner.SubmitAndWaitAsync(TemplatePath, _ => { }, "11", TimeSpan.FromMinutes(1), CancellationToken.None);
            await comfy.WaitForPromptAsync();
            var prompts = comfy.OpenPrompts();
            comfy.CompleteAll();
            await submit;

            Assert.Equal(2, events.Count);
            Assert.Null(events[0].PromptId);
            Assert.Equal(prompts[0].ClientId, events[0].ClientId);
            Assert.Equal(prompts[0].PromptId, events[1].PromptId);
            Assert.Equal(events[0].ClientId, events[1].ClientId);
            Assert.Equal("11", events[0].SaveNodeId);
            Assert.Equal("11", events[1].SaveNodeId);
        }
        finally
        {
            ComfyWorkflowRunner.SubmissionObserver.Value = null;
        }
    }

    [Fact]
    public async Task A_custom_submission_persists_its_save_node_for_restart_recovery()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        WriteTemplate();
        var store = new MediaJobStore(StateDir);
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);
        MediaJobRecord record = Record("job-submission", "running", DateTime.UtcNow, type: "custom");
        record.CustomWorkflowId = "workflow-a";
        AddEntryAndAttachSubmissionObserver(jobs, record);

        const string saveNodeId = "42";
        Task<string> submit = runner.SubmitAndWaitAsync(
            TemplatePath, _ => { }, saveNodeId, TimeSpan.FromSeconds(10), CancellationToken.None);
        try
        {
            await comfy.WaitForPromptAsync();
            MediaJobRecord persisted = Assert.Single(store.Load());
            Assert.Equal(saveNodeId, persisted.SaveNodeId);
            Assert.Single(persisted.PromptIds);

            string promptId = Assert.Single(comfy.OpenPrompts()).PromptId;
            string savedFileName = "custom-submit-output-" + promptId + ".png";
            comfy.ConfigureHistoryOutputs(promptId, saveNodeId, savedFileName);
            WriteFakeOutput(savedFileName, FinishedImage);
            comfy.CompleteAll();
            await submit;
        }
        finally
        {
            ClearJobObservers(jobs);
        }
    }

    // Exercise the same service observer used during generation while keeping this test independent of local runtime setup.
    private static void AddEntryAndAttachSubmissionObserver(MediaJobService jobs, MediaJobRecord record)
    {
        const BindingFlags nonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags nonPublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo createEntry = typeof(MediaJobService).GetMethod("EntryFromRecord", nonPublicStatic)!;
        object entry = createEntry.Invoke(null, new object[] { record })!;
        FieldInfo jobsField = typeof(MediaJobService).GetField("_jobs", nonPublicInstance)!;
        object dictionary = jobsField.GetValue(jobs)!;
        MethodInfo tryAdd = dictionary.GetType().GetMethods()
            .Single(method => method.Name == "TryAdd" && method.GetParameters().Length == 2);
        Assert.True((bool)tryAdd.Invoke(dictionary, new[] { (object)record.Id, entry })!);
        MethodInfo attach = typeof(MediaJobService).GetMethod("SetJobObservers", nonPublicInstance)!;
        attach.Invoke(jobs, new[] { entry });
    }

    private static void ClearJobObservers(MediaJobService jobs)
    {
        typeof(MediaJobService).GetMethod("ClearJobObservers", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
    }

    [Fact]
    public async Task A_cancel_during_a_video_resume_that_recovery_started_stops_the_new_clip_and_marks_the_job_cancelled()
    {
        using IDisposable localRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "localai"));
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true, videoOutput: true);
        const string firstClientId = "parux-client-cancel-clip-1";
        string firstPromptId = await PostRawPromptAsync(comfy.BaseUrl, firstClientId);
        string runId = Guid.NewGuid().ToString("N");
        string firstFile = "PaRuX_" + runId + "_clip_1_fixture.mp4";
        comfy.SetOutputFileName(firstPromptId, firstFile, "LocalAI_videos");
        WriteFakeVideoOutput(firstFile, Encoding.ASCII.GetBytes("clip-one"));
        comfy.Complete(firstPromptId);

        string firstPath = Path.Combine(OutputRoot, "LocalAI_videos", firstFile);
        var record = Record("job-video-cancel", "running", DateTime.UtcNow, type: "video");
        record.Width = 640;
        record.Height = 360;
        record.DurationSeconds = 73d / 24;
        record.Fps = 24;
        record.Steps = 20;
        record.ClientIds = new List<string> { firstClientId };
        record.PromptIds = new List<string> { firstPromptId };
        record.VideoRunId = runId;
        record.VideoPrompt = "a continuous shot of a lighthouse at dusk";
        record.VideoRequestedFrames = 73;
        record.VideoClips = new List<MediaVideoClipRecord>
        {
            new() { FrameCount = 49, ClientId = firstClientId, PromptId = firstPromptId, OutputPath = firstPath },
            new() { FrameCount = 25 }
        };
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { record });

        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        runner.MediaTempRoot = Path.Combine(_root, "media-temp");
        runner.MediaProcessOverride = (args, outputPath, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, Encoding.ASCII.GetBytes("last-frame"));
            return Task.CompletedTask;
        };
        MediaJobService jobs = NewService(store, runner);

        // The first clip finished while PaRuX was down, so recovery generates the second clip itself and waits on ComfyUI.
        Task recovery = jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(30));
        await comfy.WaitForPromptAsync(2);
        Assert.True(jobs.Cancel("job-video-cancel"));

        await recovery.WaitAsync(TimeSpan.FromSeconds(20));
        MediaJobStatus status = jobs.Get("job-video-cancel")!;
        Assert.Equal("cancelled", status.Status);
        Assert.Null(status.Asset);
    }

    [Fact]
    public async Task An_oversized_recovered_video_is_referenced_by_path_and_survives_the_next_restart()
    {
        string videoDirectory = Path.Combine(OutputRoot, "LocalAI_videos");
        Directory.CreateDirectory(videoDirectory);
        string finalPath = Path.Combine(videoDirectory, "PaRuX_" + Guid.NewGuid().ToString("N") + "_final.mp4");
        using (var sparse = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            sparse.SetLength(MediaJobService.MaximumRecoveredArtifactBytes + 1);
        var record = Record("job-video", "running", DateTime.UtcNow, type: "video");
        record.VideoFinalPath = finalPath;
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { record });
        ComfyWorkflowRunner runner = NewRunner("http://127.0.0.1:9");
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-video")!;
        Assert.Equal("completed", status.Status);
        Assert.NotNull(status.Asset);
        Assert.Empty(status.Asset.Base64Data);
        Assert.Equal(finalPath, ReadStringProperty(status.Asset, "FilePath"));
        Assert.Equal(finalPath, ReadStringProperty(Assert.Single(store.Load()), "AssetFilePath"));

        MediaJobService nextRestart = NewService(store, runner);
        await nextRestart.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));
        MediaJobStatus? persistedStatus = nextRestart.Get("job-video");
        Assert.NotNull(persistedStatus);
        Assert.Equal("completed", persistedStatus!.Status);
        Assert.Equal(finalPath, ReadStringProperty(persistedStatus.Asset, "FilePath"));
    }

    [Fact]
    public async Task A_cancelled_reattach_queued_behind_another_jobs_gate_stops_after_the_gate_wait_bound()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-reattach-cancel");
        WriteFakeOutput(promptId);
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { Record("job-a", "running", DateTime.UtcNow, "parux-client-reattach-cancel", promptId) });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaJobService jobs = NewService(
            store,
            runner,
            async (_, _, ct) =>
            {
                // A second job that keeps the execution gate until the test releases it.
                holderStarted.TrySetResult();
                await releaseHolder.Task.WaitAsync(ct);
                return new MediaAsset
                {
                    FileName = "holder.png",
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(new byte[] { 3 })
                };
            },
            TimeSpan.FromMilliseconds(20),
            cancelGateWait: TimeSpan.FromMilliseconds(200));
        try
        {
            await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(60));
            Assert.Equal("running", jobs.Get("job-a")!.Status);

            // The reattach now yields the gate between unreachable probes, so the next job can take it.
            int queueProbesBeforeFailure = comfy.CountRequests("GET /queue");
            comfy.FailQueueReads = true;
            await WaitForRequestCountAsync(comfy, "GET /queue", queueProbesBeforeFailure + 1, TimeSpan.FromSeconds(2));
            jobs.Create(new MediaJobRequest { Type = "image", Prompt = "holder" });
            await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // ComfyUI answers again with an empty queue, so the stop itself is quick. Only the gate can hold it back.
            comfy.FailQueueReads = false;
            comfy.CompleteAll();
            Assert.True(jobs.Cancel("job-a"));

            MediaJobStatus cancelled = await WaitForTerminalAsync(jobs, "job-a");
            Assert.Equal("cancelled", cancelled.Status);
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
    }

    [Fact]
    public async Task A_custom_workflow_reattaches_using_its_saved_node_and_persists_that_node()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        string promptId = await PostRawPromptAsync(comfy.BaseUrl, "parux-client-custom-recovery");
        const string saveNodeId = "42";
        string savedFileName = "custom-output-" + promptId + ".png";
        string otherNodeFileName = "unrelated-output-" + promptId + ".png";
        comfy.ConfigureHistoryOutputs(promptId, saveNodeId, savedFileName, "PreviewImage-" + promptId + ".png",
            alternateNodeId: "11", alternateFileName: otherNodeFileName);
        WriteFakeOutput(savedFileName, FinishedImage);
        WriteFakeOutput(otherNodeFileName, Encoding.ASCII.GetBytes("wrong-node-output"));

        MediaJobRecord record = Record("job-custom", "running", DateTime.UtcNow,
            "parux-client-custom-recovery", promptId, type: "custom");
        record.CustomWorkflowId = "workflow-a";
        record.SaveNodeId = saveNodeId;
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { record });
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        MediaJobService jobs = NewService(store, runner);

        await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(30));
        Assert.Equal("running", jobs.Get("job-custom")!.Status);

        comfy.CompleteAll();
        MediaJobStatus done = await WaitForTerminalAsync(jobs, "job-custom");

        Assert.Equal("completed", done.Status);
        Assert.Equal(savedFileName, done.Asset!.FileName);
        Assert.Equal(FinishedImage, Convert.FromBase64String(done.Asset.Base64Data));
        Assert.Equal(saveNodeId, Assert.Single(store.Load()).SaveNodeId);
        Assert.Equal(1, comfy.CountRequests("POST /prompt"));
    }

    [Fact]
    public async Task A_comfyui_temp_preview_history_entry_is_skipped_in_favour_of_the_saved_output()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        WriteTemplate();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl);
        Task<string> submit = runner.SubmitAndWaitAsync(
            TemplatePath, _ => { }, "11", TimeSpan.FromSeconds(10), CancellationToken.None);
        await comfy.WaitForPromptAsync();
        string promptId = Assert.Single(comfy.OpenPrompts()).PromptId;
        string savedFileName = "saved-output-" + promptId + ".png";
        comfy.ConfigureHistoryOutputs(promptId, "11", savedFileName, "PreviewImage-" + promptId + ".png");
        WriteFakeOutput(savedFileName, FinishedImage);
        comfy.CompleteAll();

        string path = await submit;

        Assert.Equal(Path.Combine(OutputRoot, savedFileName), path);
    }

    [Fact]
    public async Task Recovered_video_file_that_cannot_be_read_is_failed_with_a_typed_status_and_logs_locally()
    {
        string videoDirectory = Path.Combine(OutputRoot, "LocalAI_videos");
        Directory.CreateDirectory(videoDirectory);
        string finalPath = Path.Combine(videoDirectory, "PaRuX_" + Guid.NewGuid().ToString("N") + "_final.mp4");
        File.WriteAllBytes(finalPath, Encoding.ASCII.GetBytes("final-video-bytes"));
        var record = Record("job-video", "running", DateTime.UtcNow, type: "video");
        record.VideoFinalPath = finalPath;
        var store = new MediaJobStore(StateDir);
        store.Save(() => new[] { record });
        ComfyWorkflowRunner runner = NewRunner("http://127.0.0.1:9");
        var logger = new CapturingLogger<MediaJobService>();
        MediaJobService jobs = NewService(store, runner, logger: logger);

        // An exclusive handle makes the recovered read fail the way a locked output file does.
        using (var locked = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            await jobs.RecoverAsync(runner, CancellationToken.None, TimeSpan.FromSeconds(5));

        MediaJobStatus status = jobs.Get("job-video")!;
        Assert.Equal("failed", status.Status);
        Assert.Equal("The finished media output could not be collected after restart.", status.Error);
        Assert.Equal("media_recovery_output_failed", status.ErrorCode);
        IOException raw = Assert.IsType<IOException>(Assert.Single(logger.Exceptions));
        Assert.Contains(finalPath, raw.Message);
        Assert.DoesNotContain(finalPath, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(finalPath, string.Join(Environment.NewLine, logger.Messages));
        Assert.Equal("media_recovery_output_failed", Assert.Single(store.Load()).ErrorCode);
    }

    private ComfyWorkflowRunner NewRunner(string baseUrl) => new(null!, null!, null!)
    {
        MediaBaseUrl = baseUrl,
        LivePreflightEnabled = false,
        CompanionResolutionEnabled = false,
        MediaOutputRoot = OutputRoot,
        MediaCancelIdleBound = TimeSpan.FromSeconds(10)
    };

    private static MediaJobService NewService(
        MediaJobStore store,
        ComfyWorkflowRunner runner,
        Func<MediaJobRequest, IProgress<(string Stage, int Percent, int? Eta)>?, CancellationToken, Task<MediaAsset>>? generateOverride = null,
        TimeSpan? reattachPollInterval = null,
        Func<string, byte[]>? recoveredOutputReader = null,
        ILogger<MediaJobService>? logger = null,
        TimeSpan? cancelGateWait = null) =>
        new(new MediaGenerationService(null!, runner), store,
            generateOverride: generateOverride, reattachPollInterval: reattachPollInterval,
            recoveredOutputReader: recoveredOutputReader, logger: logger, cancelGateWait: cancelGateWait);

    private static MediaJobRecord Record(string id, string status, DateTime updated,
        string? clientId = null, string? promptId = null, string type = "image") => new()
    {
        Id = id,
        Type = type,
        Owner = "desktop",
        Status = status,
        Stage = "Generating image",
        CreatedUtc = updated,
        UpdatedUtc = updated,
        Prompt = "a lighthouse at dusk",
        Width = 512,
        Height = 512,
        Fps = 24,
        Steps = 30,
        Seed = 7,
        ClientIds = clientId == null ? new List<string>() : new List<string> { clientId },
        PromptIds = promptId == null ? new List<string>() : new List<string> { promptId }
    };

    // ComfyUI saves a finished prompt's image under the output root; the fake names it after the prompt id.
    private string WriteFakeOutput(string promptId)
    {
        string fileName = "fake-" + promptId + ".png";
        WriteFakeOutput(fileName, FinishedImage);
        return Path.Combine(OutputRoot, fileName);
    }

    private void WriteFakeOutput(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(OutputRoot);
        File.WriteAllBytes(Path.Combine(OutputRoot, fileName), bytes);
    }

    private void WriteFakeVideoOutput(string fileName, byte[] bytes)
    {
        string videoDirectory = Path.Combine(OutputRoot, "LocalAI_videos");
        Directory.CreateDirectory(videoDirectory);
        File.WriteAllBytes(Path.Combine(videoDirectory, fileName), bytes);
    }

    private string WriteSparseFakeOutput(string promptId, long length)
    {
        Directory.CreateDirectory(OutputRoot);
        string path = Path.Combine(OutputRoot, "fake-" + promptId + ".png");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        return path;
    }

    private static string? ReadStringProperty(object? value, string propertyName) =>
        value?.GetType().GetProperty(propertyName)?.GetValue(value) as string;

    [Fact]
    public void Resumed_clips_use_their_persisted_seeds_and_older_records_keep_the_run_seed_plus_index()
    {
        var clips = new List<MediaVideoClipRecord> { new() { FrameCount = 81 }, new() { FrameCount = 81 } };
        var withSeeds = new MediaJobRecord { Seed = 100, VideoClips = clips, VideoClipSeeds = new List<long> { 100, 5_000 } };
        var older = new MediaJobRecord { Seed = 100, VideoClips = clips };

        Assert.Equal(5_000, ComfyWorkflowRunner.ClipSeedFor(withSeeds, 1));
        Assert.Equal(101, ComfyWorkflowRunner.ClipSeedFor(older, 1));
    }

    [Fact]
    public async Task A_video_job_persists_the_full_live_prompt_and_each_clip_seed()
    {
        var store = new MediaJobStore(StateDir);
        var runner = new ComfyWorkflowRunner(null!, null!, null!)
        {
            LivePreflightEnabled = false,
            MediaOutputRoot = OutputRoot
        };
        string prompt = new string('p', 4_500);
        string livePrompt = string.Empty;
        long[] liveClipSeeds = Array.Empty<long>();
        long liveSeed = 0;
        string midwayPrompt = string.Empty;
        long[] midwayClipSeeds = Array.Empty<long>();

        MediaJobService jobs = NewService(store, runner, (request, _, _) =>
        {
            liveSeed = MediaGenerationService.ResolveSeed(request.Seed);
            int fps = Math.Clamp(request.Fps <= 0 ? 24 : request.Fps, 8, 60);
            int frames = Math.Max(1, (int)Math.Ceiling(MediaGenerationService.NormalizeDuration(request.DurationSeconds) * fps));
            int clipCount = ComfyWorkflowRunner.PlanWanClipFrameCounts(frames).Length;
            (livePrompt, liveClipSeeds) = ComfyWorkflowRunner.BuildVideoPlanAndNotifyObserver(request.Prompt.Trim(), liveSeed, clipCount);
            // Read right after the plan is announced: the job record must already hold it before any clip is submitted.
            MediaJobRecord? midway = new MediaJobStore(StateDir).Load().SingleOrDefault();
            midwayPrompt = midway?.Prompt ?? string.Empty;
            midwayClipSeeds = midway?.VideoClipSeeds.ToArray() ?? Array.Empty<long>();
            return Task.FromResult(new MediaAsset
            {
                FileName = "video.mp4",
                MediaType = "video/mp4",
                Base64Data = "AA==",
                Seed = liveSeed
            });
        });

        var request = new MediaJobRequest { Type = "video", Prompt = prompt };
        MediaJobStatus created = jobs.Create(request);
        MediaJobStatus completed = await WaitForTerminalAsync(jobs, created.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(-1, request.Seed);
        Assert.True(liveSeed > 0);
        Assert.True(livePrompt.Length > 4_000);
        Assert.True(liveClipSeeds.Length > 1);
        Assert.Equal(prompt + "\nContinue one coherent shot; preserve the same subject, setting, lighting and visual style across the entire video.", livePrompt);
        Assert.Equal(livePrompt, midwayPrompt);
        Assert.Equal(liveClipSeeds, midwayClipSeeds);
        Assert.Equal(Enumerable.Range(0, liveClipSeeds.Length).Select(index => liveSeed + index), liveClipSeeds);

        MediaJobRecord persisted = Assert.Single(new MediaJobStore(StateDir).Load());
        Assert.Equal(livePrompt, persisted.Prompt);
        Assert.Equal(liveClipSeeds[0], persisted.Seed);
        Assert.Equal(liveClipSeeds, persisted.VideoClipSeeds);

        JsonObject workflow = JsonNode.Parse(
            """{"prompt":{"4":{"inputs":{"text":"before"}},"8":{"inputs":{"seed":0}}}}""")!.AsObject();
        foreach (long clipSeed in liveClipSeeds)
        {
            ComfyWorkflowRunner.SetVideoPromptAndSeed(workflow, livePrompt, clipSeed);
            Assert.Equal(livePrompt, workflow["prompt"]!["4"]!["inputs"]!["text"]!.GetValue<string>());
            Assert.Equal(clipSeed, workflow["prompt"]!["8"]!["inputs"]!["seed"]!.GetValue<long>());
        }

        Assert.Throws<InvalidDataException>(() => jobs.Create(new MediaJobRequest
        {
            Type = "video",
            Prompt = new string('p', 32_001)
        }));
    }

    private void WriteTemplate() => File.WriteAllText(TemplatePath, "{}");

    private static async Task<string> PostRawPromptAsync(string baseUrl, string clientId)
    {
        using var http = new HttpClient();
        var body = new JsonObject { ["client_id"] = clientId, ["prompt"] = new JsonObject() };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync(baseUrl + "/prompt", content);
        response.EnsureSuccessStatusCode();
        JsonNode root = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidDataException("The fake ComfyUI returned an empty prompt response.");
        return root["prompt_id"]!.GetValue<string>();
    }

    private static async Task<MediaJobStatus> WaitForTerminalAsync(MediaJobService jobs, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            MediaJobStatus status = jobs.Get(id)!;
            if (status.Status is "completed" or "failed" or "cancelled") return status;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The recovered job did not finish: " + status.Status + " / " + status.Stage);
            await Task.Delay(50);
        }
    }

    private static async Task WaitForRequestCountAsync(FakeComfyUi comfy, string prefix, int count, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (comfy.CountRequests(prefix) < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The fake ComfyUI did not receive " + prefix + " within " + timeout.TotalSeconds + " s.");
            await Task.Delay(10);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Exception> Exceptions { get; } = new();
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception != null) Exceptions.Add(exception);
            Messages.Add(formatter(state, exception));
        }
    }
}
