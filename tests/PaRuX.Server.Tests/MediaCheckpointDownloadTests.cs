using System.Buffers.Binary;
using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Exercises the real staged model download with synthetic safetensors headers and no network.</summary>
public sealed class MediaCheckpointDownloadTests : IDisposable
{
    private static readonly string[] ZImageTensors =
    {
        "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w"
    };

    private static readonly string[] Flux2Tensors =
    {
        "double_blocks.0.w", "single_blocks.0.w", "img_in.w", "txt_in.w"
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-media-download-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _modelsScope;
    private readonly IDisposable _rootScope;

    public MediaCheckpointDownloadTests()
    {
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_directory, "data"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_directory, "root"));
        _modelsScope = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_directory, "data", "models"));
        Directory.CreateDirectory(ModelDownloadService.ModelsDirectory);
    }

    public void Dispose()
    {
        _modelsScope.Dispose();
        _rootScope.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Wrong_family_download_fails_with_typed_remediation_before_promotion()
    {
        string fileName = "parux-test-" + Guid.NewGuid().ToString("N") + ".safetensors";
        ModelDownloadStatus status = await DownloadAsync(fileName, "image", SafetensorsHeader(Flux2Tensors));

        Assert.Equal("failed", status.Status);
        Assert.NotNull(status.ErrorDetail);
        Assert.Equal("checkpoint_family_unsupported", status.ErrorDetail!.Reason);
        Assert.Equal(fileName, status.ErrorDetail.ExpectedFileName);
        Assert.Equal("diffusion_models", status.ErrorDetail.ComfyFolder);
        Assert.Contains("Z-Image Turbo", status.ErrorDetail.NextAction);
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, fileName)));
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, fileName) + ".partial"));
    }

    [Fact]
    public async Task Unidentified_download_is_refused_without_exposing_a_completed_model()
    {
        string fileName = "parux-test-" + Guid.NewGuid().ToString("N") + ".safetensors";
        ModelDownloadStatus status = await DownloadAsync(fileName, "image", SafetensorsHeader("random.layer.weight"));

        Assert.Equal("failed", status.Status);
        Assert.Equal("checkpoint_family_unidentified", status.ErrorDetail?.Reason);
        Assert.Contains("Choose a .safetensors checkpoint", status.ErrorDetail?.NextAction);
        Assert.Null(status.LocalPath);
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, fileName)));
    }

    [Fact]
    public async Task Matching_family_download_is_promoted_to_the_managed_library()
    {
        string fileName = "parux-test-" + Guid.NewGuid().ToString("N") + ".safetensors";
        ModelDownloadStatus status = await DownloadAsync(fileName, "image", SafetensorsHeader(ZImageTensors));

        Assert.Equal("completed", status.Status);
        Assert.Null(status.ErrorDetail);
        Assert.Equal(Path.Combine(ModelDownloadService.ModelsDirectory, fileName), status.LocalPath);
        Assert.True(File.Exists(status.LocalPath));
    }

    [Fact]
    public async Task Recommended_checkpoint_type_is_derived_when_caller_omits_it()
    {
        RecommendedCheckpoint recommended = RecommendedCheckpointsService.List.First(checkpoint => checkpoint.Type == "image");
        ModelDownloadStatus status = await DownloadAsync(recommended.FileName, null, SafetensorsHeader(Flux2Tensors),
            recommended.HuggingFaceRepoId, recommended.DownloadUrl);

        Assert.Equal("failed", status.Status);
        Assert.Equal("checkpoint_family_unsupported", status.ErrorDetail?.Reason);
        Assert.Contains("Z-Image Turbo", status.ErrorDetail?.NextAction);
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, recommended.FileName)));
    }

    [Fact]
    public void Unclassified_safetensors_download_is_rejected_before_transfer()
    {
        var request = new ModelDownloadRequest
        {
            RepoId = "synthetic/unknown-model",
            FileName = "unknown.safetensors",
            DownloadUrl = "https://huggingface.co/synthetic/unknown-model/resolve/main/unknown.safetensors",
            SizeBytes = 100
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ModelDownloadService.ValidateDownloadRequest(request));

        Assert.Contains("require mediaCheckpointType", exception.Message);
    }

    private static async Task<ModelDownloadStatus> DownloadAsync(
        string fileName, string? type, byte[] payload,
        string repoId = "synthetic/test-model", string? downloadUrl = null)
    {
        using var http = new HttpClient(new SyntheticFileHandler(payload));
        using var metadata = new HttpClient(new SyntheticFileHandler(payload));
        var service = new ModelDownloadService(http, metadata);
        var request = new ModelDownloadRequest
        {
            RepoId = repoId,
            FileName = fileName,
            DownloadUrl = downloadUrl ?? "https://huggingface.co/" + repoId + "/resolve/main/" + fileName,
            SizeBytes = payload.LongLength,
            MediaCheckpointType = type
        };

        ModelDownloadStatus queued = await service.CreateDownloadAsync(request, CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? current = service.Get(queued.Id);
            if (current?.Status is "completed" or "failed" or "cancelled") return current;
            await Task.Delay(20);
        }

        throw new TimeoutException("Synthetic media checkpoint download did not finish within 10 seconds.");
    }

    private static byte[] SafetensorsHeader(params string[] tensorNames)
    {
        var entries = new List<string>();
        for (int i = 0; i < tensorNames.Length; i++)
        {
            entries.Add(System.Text.Json.JsonSerializer.Serialize(tensorNames[i])
                + ":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}");
        }

        byte[] header = Encoding.UTF8.GetBytes("{" + string.Join(",", entries) + "}");
        byte[] content = new byte[8 + header.Length + tensorNames.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(content, 8);
        return content;
    }

    private sealed class SyntheticFileHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] responseBytes = request.Method == HttpMethod.Head ? Array.Empty<byte>() : payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes)
            });
        }
    }
}
