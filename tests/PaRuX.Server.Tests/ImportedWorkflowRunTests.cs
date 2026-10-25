using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ImportedWorkflowRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-imprun-" + Guid.NewGuid().ToString("N"));
    public ImportedWorkflowRunTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Workflow = """
    {"3":{"class_type":"KSampler","inputs":{"seed":5,"steps":20,"positive":["6",0]}},
     "6":{"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
     "11":{"class_type":"SaveImage","inputs":{"filename_prefix":"out","images":["3",0]}}}
    """;

    private (ComfyWorkflowRunner Runner, ComfyWorkflowImportService Svc, string Id) Setup(string baseUrl)
    {
        var svc = new ComfyWorkflowImportService(_dir);
        string id = svc.Save("Cat", Workflow, null).Id;
        var runner = new ComfyWorkflowRunner(null!, null!, null!, svc)
        {
            MediaBaseUrl = baseUrl, LivePreflightEnabled = false, MediaOutputRoot = _dir,
            MediaCancelIdleBound = TimeSpan.FromSeconds(5)
        };
        return (runner, svc, id);
    }

    [Fact]
    public void Job_service_accepts_imported_type_and_requires_a_template_id()
    {
        var jobs = new MediaJobService(null!, null, (_, _, _) => Task.FromResult(new MediaAsset()));
        Assert.Throws<InvalidDataException>(() => jobs.Create(new MediaJobRequest { Type = "imported" }));
        MediaJobStatus s = jobs.Create(new MediaJobRequest { Type = "imported", CustomWorkflowId = "user-cat" });
        Assert.Equal("imported", s.Type);
    }

    [Fact]
    public async Task Imported_run_submits_through_comfyui_and_reports_the_save_node_for_recovery()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        var (runner, _, id) = Setup(comfy.BaseUrl);
        string? observedSave = null;
        ComfyWorkflowRunner.SubmissionObserver.Value = (_, promptId, save) =>
        {
            observedSave = save;
            if (promptId != null)
            {
                File.WriteAllBytes(Path.Combine(_dir, "fake-" + promptId + ".png"), new byte[] { 1 });
                comfy.Complete(promptId);
            }
        };
        try
        {
            var overrides = new Dictionary<string, JsonElement> { ["3.steps"] = JsonDocument.Parse("8").RootElement };
            var result = await runner.RunImportedCoreAsync(id, "a dog", 42, overrides, null, null, CancellationToken.None);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(42, result.Seed);
            Assert.Equal("11", observedSave);
            Assert.Equal(1, comfy.CountRequests("POST /prompt"));
        }
        finally { ComfyWorkflowRunner.SubmissionObserver.Value = null; }
    }

    [Fact]
    public async Task Bad_override_is_refused_before_anything_is_queued()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        var (runner, _, id) = Setup(comfy.BaseUrl);
        var bad = new Dictionary<string, JsonElement> { ["3.steps"] = JsonDocument.Parse("99999").RootElement };
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunImportedCoreAsync(id, "x", null, bad, null, null, CancellationToken.None));
        Assert.Equal(0, comfy.CountRequests("POST /prompt"));
    }

    [Fact]
    public async Task Cancelling_an_imported_job_stops_the_comfyui_prompt()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync(startRunning: true);
        var (runner, _, id) = Setup(comfy.BaseUrl);
        var jobs = new MediaJobService(null!, null, async (request, _, ct) =>
        {
            await using var lease = await MediaExecutionCoordinator.Current.AcquireAsync("media:imported", ct);
            var r = await runner.RunImportedCoreAsync(request.CustomWorkflowId!, "p", null, null, null, null, ct);
            return new MediaAsset { FileName = Path.GetFileName(r.Path), MediaType = "image/png", Base64Data = "AQ==" };
        });
        MediaJobStatus job = jobs.Create(new MediaJobRequest { Type = "imported", CustomWorkflowId = id });
        await comfy.WaitForPromptAsync();
        Assert.True(jobs.Cancel(job.Id));
        for (int i = 0; i < 400 && jobs.Get(job.Id)!.Status is not ("cancelled" or "failed" or "completed"); i++) await Task.Delay(25);
        Assert.Equal("cancelled", jobs.Get(job.Id)!.Status);
        Assert.Empty(comfy.OpenPrompts());
    }
}
