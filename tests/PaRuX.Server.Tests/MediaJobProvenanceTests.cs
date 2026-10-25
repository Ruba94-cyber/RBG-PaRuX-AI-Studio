using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaJobProvenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-media-provenance-" + Guid.NewGuid().ToString("N"));

    public MediaJobProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(@"D:\fixtures\profiles\sample\model.safetensors", "D:")]
    [InlineData("/var/tmp/profiles/sample/model.safetensors", "/var")]
    public void Provenance_strings_keep_only_a_model_basename(string modelPath, string pathToken)
    {
        MediaJobProvenance provenance = MediaJobProvenanceFactory.Create(
            "ComfyUI",
            "ComfyUI|build-17|portable",
            modelPath,
            "Selected after checking " + modelPath);

        Assert.Equal("model.safetensors", provenance.ModelFileName);
        Assert.Equal("local", provenance.Locality);
        string serialized = JsonSerializer.Serialize(provenance);
        Assert.DoesNotContain(pathToken, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profiles", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", serialized);
        Assert.DoesNotContain("/", serialized);
        Assert.Matches("^[A-F0-9]{64}$", provenance.RuntimeIdentityHash);
    }

    [Fact]
    public void Workflow_model_loader_resolves_to_a_path_free_basename()
    {
        const string workflow = """{"1":{"class_type":"VAELoader","inputs":{"vae_name":"vae/file.vae"}},"2":{"class_type":"UpscaleModelLoader","inputs":{"model_name":"D:\\fixtures\\models\\upscale.pth"}}}""";

        string? modelPath = ComfyWorkflowRunner.FirstModelFileNameFromWorkflowJson(workflow);
        MediaJobProvenance provenance = MediaJobProvenanceFactory.Create("ComfyUI", "build-19", modelPath);

        Assert.Equal("upscale.pth", provenance.ModelFileName);
        Assert.DoesNotContain("fixtures", JsonSerializer.Serialize(provenance), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Race_selected_engine_provenance_is_returned_and_persisted_with_the_job()
    {
        var asset = new MediaAsset
        {
            FileName = "generated.png",
            MediaType = "image/png",
            Base64Data = "AQ=="
        };
        var primary = new MediaEngineRaceCandidate("comfyui-image", "build-a", _ => Task.FromResult(asset));
        var selected = new MediaEngineRaceMeasurement
        {
            EngineId = "diffusers-image",
            EngineVersion = "build-b",
            Success = true
        };
        const string reason = "Selected alternate renderer after accepted quality comparison.";
        var race = new MediaEngineRaceResult
        {
            WinnerEngineId = selected.EngineId,
            WinnerEngineVersion = selected.EngineVersion,
            WinnerAsset = asset,
            SelectionReason = reason,
            FallbackReason = MediaEngineRaceService.FallbackReasonFor(primary.EngineId, selected, reason),
            Measurements = { selected }
        };
        MediaGenerationOutcome outcome = MediaGenerationOutcome.FromRaceResult(
            race,
            @"D:\fixtures\models\winning.safetensors");
        var store = new MediaJobStore(Path.Combine(_root, "state"));
        var jobs = new MediaJobService(
            null!,
            store,
            generateOutcomeOverride: (_, _, _) => Task.FromResult(outcome));

        MediaJobStatus created = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "A test image." });
        MediaJobStatus status = await WaitForTerminalAsync(jobs, created.Id);

        Assert.Equal("completed", status.Status);
        Assert.Equal("Diffusers", status.Provenance?.EngineFamily);
        Assert.Matches("^[A-F0-9]{64}$", status.Provenance!.RuntimeIdentityHash);
        Assert.Equal("winning.safetensors", status.Provenance.ModelFileName);
        Assert.Equal("local", status.Provenance.Locality);
        Assert.Equal(reason, status.Provenance.FallbackReason);
        MediaJobRecord persisted = Assert.Single(store.Load());
        Assert.Equal("Diffusers", persisted.Provenance?.EngineFamily);
        Assert.Equal(reason, persisted.Provenance?.FallbackReason);
    }

    [Fact]
    public void Race_fallback_reason_uses_explicit_preference_not_candidate_order()
    {
        var winner = new MediaEngineRaceMeasurement
        {
            EngineId = "diffusers-image",
            EngineVersion = "build-b"
        };

        Assert.Equal("selected alternate", MediaEngineRaceService.FallbackReasonFor(
            "comfyui-image", winner, "selected alternate"));
        Assert.Null(MediaEngineRaceService.FallbackReasonFor(
            "diffusers-image", winner, "selected alternate"));
        Assert.Null(MediaEngineRaceService.FallbackReasonFor(
            null, winner, "selected alternate"));
    }

    [Fact]
    public async Task Failed_job_keeps_engine_and_failure_class_without_exception_text_in_provenance()
    {
        const string rawExceptionText = "raw renderer failure detail";
        var failure = new InvalidOperationException(rawExceptionText);
        MediaJobProvenance provenance = MediaJobProvenanceFactory.WithFailure(
            MediaJobProvenanceFactory.Create("ComfyUI", "ComfyUI|build-18|portable", "model.safetensors"),
            failure);
        MediaGenerationOutcome outcome = MediaGenerationOutcome.Failure(rawExceptionText, provenance);
        var store = new MediaJobStore(Path.Combine(_root, "failed-state"));
        var jobs = new MediaJobService(
            null!,
            store,
            generateOutcomeOverride: (_, _, _) => Task.FromResult(outcome));

        MediaJobStatus created = jobs.Create(new MediaJobRequest { Type = "image", Prompt = "A failing test image." });
        MediaJobStatus status = await WaitForTerminalAsync(jobs, created.Id);
        string provenanceJson = JsonSerializer.Serialize(status.Provenance);

        Assert.Equal("failed", status.Status);
        Assert.Equal("ComfyUI", status.Provenance?.EngineFamily);
        Assert.Equal(nameof(InvalidOperationException), status.Provenance?.FailureClass);
        Assert.DoesNotContain(rawExceptionText, provenanceJson, StringComparison.Ordinal);
        Assert.Equal(nameof(InvalidOperationException), Assert.Single(store.Load()).Provenance?.FailureClass);
    }

    [Fact]
    public async Task Legacy_job_record_loads_without_provenance_and_keeps_its_status_shape()
    {
        string stateDirectory = Path.Combine(_root, "legacy-state");
        Directory.CreateDirectory(stateDirectory);
        var store = new MediaJobStore(stateDirectory);
        DateTime created = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        string assetPath = Path.Combine(_root, "previous.png");
        var legacyFile = new
        {
            Version = 1,
            Jobs = new[]
            {
                new
                {
                    Id = "legacy-job",
                    Type = "image",
                    Owner = "desktop",
                    Status = "completed",
                    Stage = "Completed",
                    Error = (string?)null,
                    ErrorCode = (string?)null,
                    AssetFilePath = assetPath,
                    CreatedUtc = created,
                    UpdatedUtc = created,
                    Prompt = "Earlier result",
                    CustomWorkflowId = (string?)null,
                    HasSourceImage = false,
                    Width = 32,
                    Height = 32,
                    DurationSeconds = 0,
                    Fps = 0,
                    Steps = 0,
                    Seed = 7L,
                    VideoClipSeeds = Array.Empty<long>(),
                    ClientIds = Array.Empty<string>(),
                    PromptIds = Array.Empty<string>()
                }
            }
        };
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(legacyFile));

        MediaJobRecord oldRecord = Assert.Single(store.Load());
        Assert.Null(oldRecord.Provenance);
        var jobs = new MediaJobService(null!, store);
        var unusedRunner = new ComfyWorkflowRunner(null!, null!, null!);
        await jobs.RecoverAsync(unusedRunner, CancellationToken.None);
        MediaJobStatus status = Assert.IsType<MediaJobStatus>(jobs.Get("legacy-job"));
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(static _ => { });
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        context.RequestServices = serviceProvider;
        context.Response.Body = responseBody;
        await MediaJobStatusEndpoints.GetById("legacy-job", jobs).ExecuteAsync(context);
        responseBody.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(responseBody);

        Assert.Equal("legacy-job", status.Id);
        Assert.Equal("completed", status.Status);
        Assert.Equal("Completed", status.Stage);
        Assert.Equal("previous.png", status.Asset?.FileName);
        Assert.Null(status.Provenance);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(document.RootElement.TryGetProperty("provenance", out _));
        Assert.Equal("legacy-job", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("completed", document.RootElement.GetProperty("status").GetString());
    }

    private static async Task<MediaJobStatus> WaitForTerminalAsync(MediaJobService jobs, string id)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            MediaJobStatus? status = jobs.Get(id);
            if (status?.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(5);
        }
        throw new TimeoutException("Media job did not reach a terminal state.");
    }
}
