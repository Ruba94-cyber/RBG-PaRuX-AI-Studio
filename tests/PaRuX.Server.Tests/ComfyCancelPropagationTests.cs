using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real loopback HTTP stand-in for ComfyUI's prompt API (/prompt, /queue, /interrupt, /history and
/// /upload/image). Prompts stay where a test puts them, so the pending, running, stuck and
/// between-clip cases are deterministic, and no GPU, model or ComfyUI install is involved.
/// </summary>
internal sealed class FakeComfyUi : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<FakePrompt> _prompts = new();
    private readonly List<string> _requests = new();
    private readonly WebApplication _app;
    private int _sequence;
    private bool _disposed;

    private sealed class FakePrompt
    {
        public required string PromptId { get; init; }
        public required string ClientId { get; init; }
        public required bool Running { get; set; }
        public bool Completed { get; set; }
        public bool Interrupted { get; set; }
        public required string OutputFileName { get; set; }
        public string OutputSubfolder { get; set; } = string.Empty;
        public required string OutputCollection { get; init; }
        public string OutputNodeId { get; set; } = "11";
        public string? TempPreviewFileName { get; set; }
        public string? AlternateOutputNodeId { get; set; }
        public string? AlternateOutputFileName { get; set; }
    }

    private FakeComfyUi(WebApplication app, bool startRunning, bool ignoreInterrupt, bool videoOutput)
    {
        _app = app;
        StartRunning = startRunning;
        IgnoreInterrupt = ignoreInterrupt;
        VideoOutput = videoOutput;
    }

    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>New prompts start in ComfyUI's running slot instead of the pending queue.</summary>
    public bool StartRunning { get; set; }

    /// <summary>/interrupt is accepted but the running prompt keeps running (a stuck ComfyUI).</summary>
    public bool IgnoreInterrupt { get; set; }

    /// <summary>GET /queue answers 503, so PaRuX cannot read what ComfyUI is doing.</summary>
    public bool FailQueueReads { get; set; }

    /// <summary>GET /history answers 500, so recovery cannot confirm a prompt's state.</summary>
    public bool FailHistoryReads { get; set; }

    /// <summary>POST /queue (stop requests for queued prompts) answers 500 and removes nothing.</summary>
    public bool FailQueueDeletes { get; set; }

    /// <summary>Completed prompts expose a video output collection instead of images.</summary>
    public bool VideoOutput { get; set; }

    /// <summary>Subfolder that /history reports with a saved image when the prompt sets none (default: the output root itself).</summary>
    public string OutputSubfolder { get; set; } = string.Empty;

    /// <summary>Optional per-prompt override for the saved image subfolder.</summary>
    public Func<string, string>? OutputSubfolderForPrompt { get; set; }

    public static async Task<FakeComfyUi> StartAsync(bool startRunning = false, bool ignoreInterrupt = false, bool videoOutput = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        var fake = new FakeComfyUi(app, startRunning, ignoreInterrupt, videoOutput);
        fake.MapRoutes();
        await app.StartAsync();
        fake.BaseUrl = app.Urls.First().TrimEnd('/');
        return fake;
    }

    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public int CountRequests(string prefix) => Requests.Count(request => request.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Prompts ComfyUI still holds (queued or running), as (prompt id, client id, running).</summary>
    public IReadOnlyList<(string PromptId, string ClientId, bool Running)> OpenPrompts()
    {
        lock (_gate)
        {
            return _prompts.Where(prompt => !prompt.Completed)
                .Select(prompt => (prompt.PromptId, prompt.ClientId, prompt.Running))
                .ToArray();
        }
    }

    /// <summary>Marks every prompt finished, as if ComfyUI had written its outputs.</summary>
    public void CompleteAll()
    {
        lock (_gate)
        {
            foreach (FakePrompt prompt in _prompts)
            {
                prompt.Running = false;
                prompt.Completed = true;
            }
        }
    }

    public void Complete(string promptId)
    {
        lock (_gate)
        {
            FakePrompt prompt = _prompts.Single(candidate => candidate.PromptId == promptId);
            prompt.Running = false;
            prompt.Completed = true;
        }
    }

    public void SetOutputFileName(string promptId, string fileName, string subfolder = "")
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("The fake output filename must be a single filename.", nameof(fileName));
        lock (_gate)
        {
            FakePrompt prompt = _prompts.Single(candidate => candidate.PromptId == promptId);
            prompt.OutputFileName = fileName;
            prompt.OutputSubfolder = subfolder;
        }
    }

    public void ConfigureHistoryOutputs(string promptId, string outputNodeId, string outputFileName,
        string? tempPreviewFileName = null, string? alternateNodeId = null, string? alternateFileName = null)
    {
        static bool IsOptionalFileName(string? value) => value == null ||
            (!string.IsNullOrWhiteSpace(value) && Path.GetFileName(value) == value);
        if (string.IsNullOrWhiteSpace(outputNodeId) || string.IsNullOrWhiteSpace(outputFileName)
            || Path.GetFileName(outputFileName) != outputFileName
            || !IsOptionalFileName(tempPreviewFileName) || !IsOptionalFileName(alternateFileName)
            || (alternateNodeId == null) != (alternateFileName == null)
            || (alternateNodeId != null && (string.IsNullOrWhiteSpace(alternateNodeId) || alternateNodeId == outputNodeId)))
            throw new ArgumentException("The fake ComfyUI history output configuration is invalid.");

        lock (_gate)
        {
            FakePrompt prompt = _prompts.Single(candidate => candidate.PromptId == promptId);
            prompt.OutputNodeId = outputNodeId;
            prompt.OutputFileName = outputFileName;
            prompt.TempPreviewFileName = tempPreviewFileName;
            prompt.AlternateOutputNodeId = alternateNodeId;
            prompt.AlternateOutputFileName = alternateFileName;
        }
    }

    public async Task WaitForPromptAsync(int count = 1)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (_gate)
            {
                if (_prompts.Count >= count) return;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The fake ComfyUI did not receive a prompt.");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // Frame counts of the workflows PaRuX submits with a node "6" length (video clips), in submission order.
    private readonly List<int> _submittedClipLengths = new();

    public IReadOnlyList<int> SubmittedClipLengths
    {
        get
        {
            lock (_gate) return _submittedClipLengths.ToArray();
        }
    }

    private void MapRoutes()
    {
        _app.MapPost("/prompt", async (HttpRequest request) =>
        {
            JsonNode body = JsonNode.Parse(await ReadBodyAsync(request)) ?? new JsonObject();
            string clientId = body["client_id"]?.GetValue<string>() ?? string.Empty;
            if (body["prompt"]?["6"]?["inputs"]?["length"] is JsonNode frameLength)
            {
                lock (_gate) _submittedClipLengths.Add(frameLength.GetValue<int>());
            }
            lock (_gate)
            {
                _sequence++;
                string promptId = "prompt-" + _sequence.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
                _prompts.Add(new FakePrompt
                {
                    PromptId = promptId,
                    ClientId = clientId,
                    Running = StartRunning,
                    OutputFileName = "fake-" + promptId + (VideoOutput ? ".mp4" : ".png"),
                    OutputCollection = VideoOutput ? "videos" : "images"
                });
                _requests.Add("POST /prompt");
                return Results.Text(
                    new JsonObject
                    {
                        ["prompt_id"] = promptId,
                        ["number"] = _sequence,
                        ["node_errors"] = new JsonObject()
                    }.ToJsonString(),
                    "application/json");
            }
        });

        _app.MapGet("/queue", () =>
        {
            lock (_gate)
            {
                _requests.Add("GET /queue");
                if (FailQueueReads) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                var running = new JsonArray();
                var pending = new JsonArray();
                int number = 0;
                foreach (FakePrompt prompt in _prompts.Where(prompt => !prompt.Completed))
                {
                    number++;
                    // ComfyUI queue item: [number, prompt_id, prompt, extra_data, outputs_to_execute].
                    var item = new JsonArray(
                        JsonValue.Create(number),
                        JsonValue.Create(prompt.PromptId),
                        new JsonObject(),
                        new JsonObject { ["client_id"] = prompt.ClientId },
                        new JsonArray());
                    (prompt.Running ? running : pending).Add(item);
                }
                return Results.Text(
                    new JsonObject { ["queue_running"] = running, ["queue_pending"] = pending }.ToJsonString(),
                    "application/json");
            }
        });

        _app.MapPost("/queue", async (HttpRequest request) =>
        {
            JsonNode body = JsonNode.Parse(await ReadBodyAsync(request)) ?? new JsonObject();
            lock (_gate)
            {
                _requests.Add("POST /queue");
                if (FailQueueDeletes) return Results.StatusCode(StatusCodes.Status500InternalServerError);
                if (body["delete"] is JsonArray ids)
                {
                    foreach (JsonNode? id in ids)
                    {
                        string promptId = id!.GetValue<string>();
                        // ComfyUI deletes only queued items. A running prompt needs /interrupt instead.
                        if (_prompts.RemoveAll(prompt => prompt.PromptId == promptId && !prompt.Running) > 0)
                            _requests.Add("DELETE " + promptId);
                    }
                }
                return Results.Ok();
            }
        });

        _app.MapPost("/interrupt", () =>
        {
            lock (_gate)
            {
                _requests.Add("POST /interrupt");
                if (!IgnoreInterrupt)
                {
                    foreach (FakePrompt prompt in _prompts.Where(prompt => prompt.Running && !prompt.Completed))
                    {
                        prompt.Running = false;
                        prompt.Completed = true;
                        prompt.Interrupted = true;
                    }
                }
                return Results.Ok();
            }
        });

        _app.MapGet("/history/{id}", (string id) =>
        {
            lock (_gate)
            {
                _requests.Add("GET /history/" + id);
                if (FailHistoryReads) return Results.StatusCode(StatusCodes.Status500InternalServerError);
                FakePrompt? prompt = _prompts.FirstOrDefault(candidate => candidate.PromptId == id && candidate.Completed);
                if (prompt == null) return Results.Text("{}", "application/json");
                var status = new JsonObject
                {
                    ["completed"] = JsonValue.Create(!prompt.Interrupted),
                    ["status_str"] = prompt.Interrupted ? "error" : "success"
                };
                var outputs = new JsonObject();
                if (prompt.AlternateOutputNodeId != null && prompt.AlternateOutputFileName != null)
                {
                    outputs[prompt.AlternateOutputNodeId] = new JsonObject
                    {
                        [prompt.OutputCollection] = new JsonArray(new JsonObject
                        {
                            ["filename"] = prompt.AlternateOutputFileName,
                            ["subfolder"] = string.Empty,
                            ["type"] = "output"
                        })
                    };
                }
                var media = new JsonArray();
                if (prompt.TempPreviewFileName != null)
                {
                    media.Add(new JsonObject
                    {
                        ["filename"] = prompt.TempPreviewFileName,
                        ["subfolder"] = prompt.OutputSubfolder,
                        ["type"] = "temp"
                    });
                }
                media.Add(new JsonObject
                {
                    ["filename"] = prompt.OutputFileName,
                    ["subfolder"] = OutputSubfolderForPrompt?.Invoke(id)
                        ?? (string.IsNullOrEmpty(prompt.OutputSubfolder) ? OutputSubfolder : prompt.OutputSubfolder),
                    ["type"] = "output"
                });
                outputs[prompt.OutputNodeId] = new JsonObject { [prompt.OutputCollection] = media };
                var entry = new JsonObject
                {
                    ["status"] = status,
                    ["outputs"] = outputs
                };
                return Results.Text(new JsonObject { [id] = entry }.ToJsonString(), "application/json");
            }
        });

        _app.MapPost("/upload/image", async (HttpRequest request) =>
        {
            IFormCollection form = await request.ReadFormAsync();
            IFormFile? file = form.Files.GetFile("image");
            long bytes = file?.Length ?? 0;
            string digest = "none";
            if (file != null)
            {
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy);
                digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.ToArray()));
            }
            lock (_gate)
            {
                _requests.Add("POST /upload/image " + bytes.ToString(CultureInfo.InvariantCulture) + " " + digest);
            }
            return Results.Text(
                new JsonObject { ["name"] = "uploaded-" + Guid.NewGuid().ToString("N") + ".png" }.ToJsonString(),
                "application/json");
        });
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

/// <summary>
/// Cancel propagation for ComfyUI-backed media jobs: a queued prompt is deleted, a running prompt is
/// interrupted only when it is the one running, and the stop is confirmed (or honestly reported as
/// unconfirmed) before the job releases its admission lease.
/// </summary>
public sealed class ComfyCancelPropagationTests
{
    private static ComfyWorkflowRunner NewRunner(string baseUrl, TimeSpan idleBound) =>
        new(null!, null!, null!) { MediaBaseUrl = baseUrl, LivePreflightEnabled = false, MediaCancelIdleBound = idleBound };

    [Fact]
    public async Task Cancel_while_pending_removes_the_queued_prompt_and_never_interrupts()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1), cancel.Token);
        await comfy.WaitForPromptAsync();
        cancel.Cancel();

        ComfyCancellationException ex = await Assert.ThrowsAsync<ComfyCancellationException>(async () => await submit);
        Assert.True(ex.Report.Idle);
        Assert.True(ex.Report.Delivered);
        Assert.Null(ex.Report.Notice);
        Assert.Equal(1, ex.Report.PendingDeleteRequests);
        Assert.Equal(0, ex.Report.InterruptRequests);
        Assert.Equal(1, comfy.CountRequests("DELETE "));
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task Cancel_while_running_interrupts_because_the_prompt_is_the_running_one()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1), cancel.Token);
        await comfy.WaitForPromptAsync();
        cancel.Cancel();

        ComfyCancellationException ex = await Assert.ThrowsAsync<ComfyCancellationException>(async () => await submit);
        Assert.True(ex.Report.Idle);
        Assert.True(ex.Report.Delivered);
        Assert.Null(ex.Report.Notice);
        Assert.True(ex.Report.InterruptRequests >= 1);
        Assert.Equal(0, ex.Report.PendingDeleteRequests);
        Assert.Equal(0, comfy.CountRequests("DELETE "));
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task A_comfyui_that_keeps_running_is_reported_busy_when_the_bounded_wait_expires()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true, ignoreInterrupt: true);
        using var template = new TemplateFile();
        // The bound starts when the cancel is settled, so its first /queue and /interrupt round trips must fit inside it
        // even on a loaded machine: warm the fake's routes first (cold Kestrel/JIT paths are the intermittent cost) and
        // give the bound room. The test still proves the bound expires with the prompt open, it just no longer races it.
        using (var warm = new HttpClient())
        using (HttpResponseMessage queue = await warm.GetAsync(comfy.BaseUrl + "/queue"))
        {
            queue.EnsureSuccessStatusCode();
        }
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(4));
        using var cancel = new CancellationTokenSource();

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1), cancel.Token);
        await comfy.WaitForPromptAsync();
        cancel.Cancel();

        ComfyCancellationException ex = await Assert.ThrowsAsync<ComfyCancellationException>(async () => await submit);
        Assert.False(ex.Report.Idle);
        Assert.True(ex.Report.Delivered);
        Assert.True(ex.Report.InterruptRequests >= 1, "interrupt requests: " + ex.Report.InterruptRequests + ", notice: " + ex.Report.Notice);
        Assert.NotNull(ex.Report.Notice);
        Assert.Contains("still reports", ex.Report.Notice);
        Assert.Contains("GPU may stay busy", ex.Report.Notice);
        // The fake never stopped the prompt, so PaRuX must not have reported it as stopped.
        Assert.Single(comfy.OpenPrompts());
    }

    [Fact]
    public async Task A_media_job_holds_the_execution_gate_and_gpu_lease_until_a_stuck_stop_is_reported_unsettled()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true, ignoreInterrupt: true);
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(1.5));
        var firstLeaseReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGeneratorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLeaseAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaJobService jobs = NewJobService(runner, template.FilePath, firstLeaseReleased, secondGeneratorEntered, secondLeaseAcquired);

        MediaJobStatus first = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "first" });
        await comfy.WaitForPromptAsync();
        Assert.Equal("media-job-first", MediaExecutionCoordinator.Current.Snapshot().ActiveOwner);

        Assert.True(jobs.Cancel(first.Id));
        MediaJobStatus second = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "second" });
        await WaitForRequestCountAsync(comfy, "POST /interrupt", 1);
        comfy.FailQueueReads = true;

        MediaJobStatus pending = jobs.Get(first.Id)!;
        Assert.Equal("cancelling", pending.Status);
        Assert.False(firstLeaseReleased.Task.IsCompleted);
        Assert.False(secondGeneratorEntered.Task.IsCompleted);
        Assert.Equal(1, comfy.CountRequests("POST /prompt"));
        Assert.Equal("Stop pending confirmation", pending.Stage);

        MediaJobStatus cancelled = await WaitForTerminalAsync(jobs, first.Id);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Contains("unsettled", cancelled.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not confirm", cancelled.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(firstLeaseReleased.Task.IsCompleted);

        await secondLeaseAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("media-job-second", MediaExecutionCoordinator.Current.Snapshot().ActiveOwner);
        await comfy.WaitForPromptAsync(2);
        comfy.CompleteAll();
        MediaJobStatus completed = await WaitForTerminalAsync(jobs, second.Id);
        Assert.Equal("completed", completed.Status);
    }

    [Fact]
    public async Task A_media_job_still_releases_its_leases_after_comfyui_confirms_cancel()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(5));
        var firstLeaseReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaJobService jobs = NewJobService(runner, template.FilePath, firstLeaseReleased);

        MediaJobStatus first = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "first" });
        await comfy.WaitForPromptAsync();
        Assert.True(jobs.Cancel(first.Id));

        MediaJobStatus cancelled = await WaitForTerminalAsync(jobs, first.Id);

        Assert.Equal("cancelled", cancelled.Status);
        Assert.Null(cancelled.Error);
        Assert.True(firstLeaseReleased.Task.IsCompleted);
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task Cancel_reports_an_unconfirmed_stop_when_comfyui_cannot_be_read()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource();

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1), cancel.Token);
        await comfy.WaitForPromptAsync();
        comfy.FailQueueReads = true;
        cancel.Cancel();

        ComfyCancellationException ex = await Assert.ThrowsAsync<ComfyCancellationException>(async () => await submit);
        Assert.False(ex.Report.Idle);
        Assert.False(ex.Report.Delivered);
        Assert.NotNull(ex.Report.Notice);
        Assert.Contains("could not confirm", ex.Report.Notice);
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
    }

    [Fact]
    public async Task A_refused_stop_request_is_reported_as_not_delivered()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(1.5));
        using var cancel = new CancellationTokenSource();
        comfy.FailQueueDeletes = true;

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1), cancel.Token);
        await comfy.WaitForPromptAsync();
        cancel.Cancel();

        ComfyCancellationException ex = await Assert.ThrowsAsync<ComfyCancellationException>(async () => await submit);
        Assert.False(ex.Report.Idle);
        Assert.False(ex.Report.Delivered);
        Assert.Equal(0, ex.Report.PendingDeleteRequests);
        Assert.NotNull(ex.Report.Notice);
        Assert.Contains("Last stop request", ex.Report.Notice);
        Assert.Single(comfy.OpenPrompts());
    }

    [Fact]
    public async Task Cancel_between_clips_confirms_idle_and_sends_no_stop_for_a_finished_clip()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(5));
        var clientIds = new List<string>();

        Task<string> clip1 = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1),
            CancellationToken.None, null, (filename, _) => filename, clientIds);
        await comfy.WaitForPromptAsync();
        comfy.CompleteAll();
        string clip1Output = await clip1;

        // The cancel lands before clip 2 is submitted: nothing of this job should remain in ComfyUI.
        Assert.EndsWith(".png", clip1Output);
        Assert.Single(clientIds);
        ComfyCancelReport report = await runner.SettleForCancelAsync(clientIds, Array.Empty<string>());
        Assert.True(report.Idle);
        Assert.True(report.Delivered);
        Assert.Null(report.Notice);
        Assert.Equal(0, report.InterruptRequests);
        Assert.Equal(0, report.PendingDeleteRequests);
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
        Assert.Equal(0, comfy.CountRequests("DELETE "));
    }

    [Fact]
    public async Task A_finished_prompt_returns_its_output_without_any_stop_request()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(5));

        Task<string> submit = runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromMinutes(1),
            CancellationToken.None, null, (filename, _) => filename);
        await comfy.WaitForPromptAsync();
        comfy.CompleteAll();
        string output = await submit;

        Assert.StartsWith("fake-prompt-", output);
        Assert.Equal(0, comfy.CountRequests("DELETE "));
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
    }

    [Fact]
    public async Task A_timed_out_prompt_is_stopped_before_the_timeout_is_reported()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        using var template = new TemplateFile();
        ComfyWorkflowRunner runner = NewRunner(comfy.BaseUrl, TimeSpan.FromSeconds(5));

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.SubmitAndWaitAsync(template.FilePath, _ => { }, "11", TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("Timed out", ex.Message);
        Assert.True(comfy.CountRequests("POST /interrupt") >= 1);
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task A_prompt_whose_submit_response_was_lost_is_found_by_its_client_id()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var http = new HttpClient();
        // The POST reached ComfyUI, but PaRuX never received the prompt id.
        using (var body = new StringContent("{\"client_id\":\"parux-lost\",\"prompt\":{}}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage accepted = await http.PostAsync(comfy.BaseUrl + "/prompt", body))
        {
            accepted.EnsureSuccessStatusCode();
        }

        ComfyCancelReport report = await ComfyPromptCancellation.SettleAsync(
            http, comfy.BaseUrl, new[] { "parux-lost" }, Array.Empty<string>(),
            TimeSpan.FromSeconds(5), ComfyPromptCancellation.PollInterval);

        Assert.True(report.Idle);
        Assert.Equal(1, report.PendingDeleteRequests);
        Assert.Equal(0, report.InterruptRequests);
        Assert.Empty(comfy.OpenPrompts());
    }

    [Fact]
    public async Task Settle_leaves_prompts_of_other_jobs_untouched()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        using var http = new HttpClient();
        // Another job's prompts: one queued and one running. Neither carries this job's client id.
        await PostPromptAsync(http, comfy.BaseUrl, "parux-other-pending");
        comfy.StartRunning = true;
        await PostPromptAsync(http, comfy.BaseUrl, "parux-other-running");

        ComfyCancelReport report = await ComfyPromptCancellation.SettleAsync(
            http, comfy.BaseUrl, new[] { "parux-this-job" }, Array.Empty<string>(),
            TimeSpan.FromSeconds(5), ComfyPromptCancellation.PollInterval);

        Assert.True(report.Idle);
        Assert.Equal(0, report.PendingDeleteRequests);
        Assert.Equal(0, report.InterruptRequests);
        Assert.Equal(0, comfy.CountRequests("DELETE "));
        Assert.Equal(0, comfy.CountRequests("POST /interrupt"));
        Assert.Equal(2, comfy.OpenPrompts().Count);
    }

    private static async Task PostPromptAsync(HttpClient http, string baseUrl, string clientId)
    {
        using var body = new StringContent("{\"client_id\":\"" + clientId + "\",\"prompt\":{}}", Encoding.UTF8, "application/json");
        using HttpResponseMessage accepted = await http.PostAsync(baseUrl + "/prompt", body);
        accepted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Settle_reports_not_confirmed_when_nothing_answers_on_the_address()
    {
        int port = FreeLoopbackPort();
        using var http = new HttpClient();

        ComfyCancelReport report = await ComfyPromptCancellation.SettleAsync(
            http, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), new[] { "parux-x" }, Array.Empty<string>(),
            TimeSpan.FromSeconds(2), ComfyPromptCancellation.PollInterval);

        Assert.False(report.Idle);
        Assert.False(report.Delivered);
        Assert.NotNull(report.Notice);
        Assert.Contains("could not confirm", report.Notice);
    }

    [Fact]
    public void Queue_entries_carry_prompt_and_client_ids_and_reject_unexpected_shapes()
    {
        IReadOnlyList<ComfyPromptCancellation.QueueEntry> entries = ComfyPromptCancellation.ParseEntries(
            JsonNode.Parse("[[7,\"p-7\",{},{\"client_id\":\"parux-a\"},[]]]"));

        ComfyPromptCancellation.QueueEntry entry = Assert.Single(entries);
        Assert.Equal("p-7", entry.PromptId);
        Assert.Equal("parux-a", entry.ClientId);
        Assert.Throws<InvalidDataException>(() => ComfyPromptCancellation.ParseEntries(JsonNode.Parse("[[7]]")));
        Assert.Throws<InvalidDataException>(() => ComfyPromptCancellation.ParseEntries(null));
    }

    private static MediaJobService NewJobService(
        ComfyWorkflowRunner runner,
        string templatePath,
        TaskCompletionSource<bool>? firstLeaseReleased = null,
        TaskCompletionSource<bool>? secondGeneratorEntered = null,
        TaskCompletionSource<bool>? secondLeaseAcquired = null) =>
        new(null!, null, async (request, _, ct) =>
        {
            string owner = "media-job-" + request.Prompt;
            if (request.Prompt == "second") secondGeneratorEntered?.TrySetResult(true);
            MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current.AcquireAsync(owner, ct);
            try
            {
                if (request.Prompt == "second") secondLeaseAcquired?.TrySetResult(true);
                string output = await runner.SubmitAndWaitAsync(
                    templatePath, _ => { }, "11", TimeSpan.FromMinutes(1), ct);
                return new MediaAsset
                {
                    FileName = Path.GetFileName(output),
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(new byte[] { 1 })
                };
            }
            finally
            {
                lease.Dispose();
                if (request.Prompt == "first") firstLeaseReleased?.TrySetResult(true);
            }
        });

    private static async Task<MediaJobStatus> WaitForTerminalAsync(MediaJobService jobs, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            MediaJobStatus status = jobs.Get(id)!;
            if (status.Status is "completed" or "failed" or "cancelled") return status;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The media job did not finish: " + status.Status + " / " + status.Stage);
            await Task.Delay(20);
        }
    }

    private static async Task WaitForRequestCountAsync(FakeComfyUi comfy, string prefix, int count)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (comfy.CountRequests(prefix) < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The fake ComfyUI did not receive " + prefix + ".");
            await Task.Delay(10);
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TemplateFile : IDisposable
    {
        public TemplateFile()
        {
            FilePath = Path.Combine(Path.GetTempPath(), "parux-cancel-template-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(FilePath, "{\"1\":{\"class_type\":\"ProbeNode\",\"inputs\":{}}}");
        }

        public string FilePath { get; }

        public void Dispose()
        {
            try { File.Delete(FilePath); } catch (IOException) { }
        }
    }
}
