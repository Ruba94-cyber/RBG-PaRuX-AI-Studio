using System.Buffers.Binary;
using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A media checkpoint must end up in the right ComfyUI folder whichever UI started the download, and one an older build left in the
/// text-model folder is installed without deleting anything of the user's. Hermetic: scoped data root, scoped models folder, no network.
/// </summary>
public sealed class MediaCheckpointPlacementTests : IDisposable
{
    private static readonly string[] SdxlTensors =
    {
        "model.diffusion_model.input_blocks.0.w", "conditioner.embedders.0.transformer.w", "conditioner.embedders.1.model.w"
    };
    private static readonly string[] ZImageTensors = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-placement-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _root;
    private readonly IDisposable _models;
    private string Text => ModelDownloadService.ModelsDirectory;

    public MediaCheckpointPlacementTests()
    {
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_dir, "data"));
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_dir, "root"));
        _models = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_dir, "data", "models"));
        Directory.CreateDirectory(Text);
    }

    public void Dispose()
    {
        _models.Dispose();
        _root.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void InstallComfy() => Directory.CreateDirectory(Path.Combine(LocalAiPaths.ComfyApp, "models"));

    private string WriteStaged(string name, byte[] bytes)
    {
        string path = Path.Combine(Text, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- kinds ----

    [Theory]
    [InlineData("checkpoint", "checkpoints")]
    [InlineData("diffusion_model", "diffusion_models")]
    [InlineData("unet", "diffusion_models")]
    [InlineData("vae", "vae")]
    [InlineData("text_encoder", "text_encoders")]
    [InlineData("lora", "loras")]
    [InlineData("LoRA", "loras")]
    public void Every_media_kind_maps_to_its_comfy_folder(string kind, string folder) =>
        Assert.Equal(folder, MediaCheckpointPlacement.ComfyFolderForKind(kind));

    [Fact]
    public void An_unknown_kind_has_no_folder() => Assert.Null(MediaCheckpointPlacement.ComfyFolderForKind("gguf"));

    // ---- placement ----

    [Fact]
    public void A_single_file_sdxl_checkpoint_goes_to_checkpoints_and_a_split_model_to_diffusion_models()
    {
        InstallComfy();
        string sdxl = WriteStaged("sd_xl_turbo_1.0_fp16.safetensors", SafetensorsHeader(SdxlTensors));
        string z = WriteStaged("z_test.safetensors", SafetensorsHeader(ZImageTensors));

        MediaPlacementResult a = MediaCheckpointPlacement.PlaceMainModel(sdxl);
        MediaPlacementResult b = MediaCheckpointPlacement.PlaceMainModel(z);

        Assert.Equal(MediaPlacementOutcome.Placed, a.Outcome);
        Assert.Equal(Path.Combine(LocalAiPaths.CheckpointsPath, "sd_xl_turbo_1.0_fp16.safetensors"), a.Path);
        Assert.Equal(Path.Combine(LocalAiPaths.DiffusionModelsPath, "z_test.safetensors"), b.Path);
        Assert.False(File.Exists(sdxl));
        Assert.True(File.Exists(a.Path));
        Assert.Contains("sd_xl_turbo_1.0_fp16.safetensors", LocalAiPaths.ListDiffusionModelCheckpoints());
    }

    [Fact]
    public void Without_comfyui_the_file_stays_put_with_a_clear_reason()
    {
        string sdxl = WriteStaged("a.safetensors", SafetensorsHeader(SdxlTensors));

        MediaPlacementResult r = MediaCheckpointPlacement.PlaceMainModel(sdxl);

        Assert.Equal(MediaPlacementOutcome.Deferred, r.Outcome);
        Assert.True(File.Exists(sdxl));
        Assert.Contains("ComfyUI is not installed", r.Message);
    }

    [Fact]
    public void A_different_file_with_the_same_name_is_never_replaced()
    {
        InstallComfy();
        string staged = WriteStaged("a.safetensors", SafetensorsHeader(SdxlTensors));
        Directory.CreateDirectory(LocalAiPaths.CheckpointsPath);
        string existing = Path.Combine(LocalAiPaths.CheckpointsPath, "a.safetensors");
        File.WriteAllBytes(existing, new byte[] { 1, 2, 3 });

        MediaPlacementResult r = MediaCheckpointPlacement.PlaceMainModel(staged);

        Assert.Equal(MediaPlacementOutcome.Failed, r.Outcome);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void A_gguf_or_missing_file_is_not_applicable_and_untouched()
    {
        InstallComfy();
        string gguf = WriteStaged("chat.gguf", new byte[] { 9, 9, 9 });

        Assert.Equal(MediaPlacementOutcome.NotApplicable, MediaCheckpointPlacement.PlaceMainModel(gguf).Outcome);
        Assert.Equal(MediaPlacementOutcome.NotApplicable, MediaCheckpointPlacement.PlaceMainModel(Path.Combine(Text, "gone.safetensors")).Outcome);
        Assert.True(File.Exists(gguf));
    }

    [Fact]
    public void Activation_finds_a_file_already_placed_in_comfyui()
    {
        InstallComfy();
        string staged = WriteStaged("a.safetensors", SafetensorsHeader(SdxlTensors));
        Assert.Equal(staged, MediaCheckpointPlacement.FindActivationSource("a.safetensors"));

        MediaPlacementResult placed = MediaCheckpointPlacement.PlaceMainModel(staged);

        Assert.Equal(placed.Path, MediaCheckpointPlacement.FindActivationSource("a.safetensors"));
        Assert.Null(MediaCheckpointPlacement.FindActivationSource("missing.safetensors"));
    }

    // ---- the real download path ----

    [Fact]
    public async Task A_finished_media_download_lands_in_comfyui_and_is_handed_to_tuning_from_there()
    {
        InstallComfy();
        byte[] payload = SafetensorsHeader(SdxlTensors);
        var handler = new RecordingHandler();
        var service = new ModelDownloadService(new HttpClient(new Synthetic(payload)), new HttpClient(new Synthetic(payload)), handler);
        string name = "sd_xl_turbo_1.0_fp16.safetensors";
        var request = new ModelDownloadRequest
        {
            RepoId = "stabilityai/sdxl-turbo",
            FileName = name,
            DownloadUrl = "https://huggingface.co/stabilityai/sdxl-turbo/resolve/main/" + name,
            SizeBytes = payload.LongLength,
            MediaCheckpointType = "image"
        };

        ModelDownloadStatus queued = await service.CreateDownloadAsync(request, CancellationToken.None);
        ModelDownloadStatus done = await WaitAsync(service, queued.Id);

        string expected = Path.Combine(LocalAiPaths.CheckpointsPath, name);
        Assert.Equal("completed", done.Status);
        Assert.Equal(expected, done.LocalPath);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(Path.Combine(Text, name)));
        Assert.Equal(new[] { expected }, handler.MediaPaths);
        Assert.Contains(name, LocalAiPaths.ListDiffusionModelCheckpoints());
    }

    // ---- migration of what an older build left behind ----

    [Fact]
    public void A_paRuX_downloaded_checkpoint_in_the_text_folder_is_moved_and_the_status_says_so()
    {
        InstallComfy();
        string staged = WriteStaged("sd_xl_turbo_1.0_fp16.safetensors", SafetensorsHeader(SdxlTensors));
        MarkDownloadedByParux("sd_xl_turbo_1.0_fp16.safetensors", staged);

        var found = MediaCheckpointMigration.Find();
        Assert.Equal("found", Assert.Single(found).Status);
        Assert.True(File.Exists(staged));

        var done = MediaCheckpointMigration.Migrate(includeUserManaged: false);

        MisplacedMediaCheckpoint row = Assert.Single(done);
        Assert.Equal("moved", row.Status);
        Assert.True(row.PaRuXManaged);
        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(Path.Combine(LocalAiPaths.CheckpointsPath, "sd_xl_turbo_1.0_fp16.safetensors")));
    }

    [Fact]
    public void A_user_managed_checkpoint_is_only_offered_then_copied_with_the_original_kept()
    {
        InstallComfy();
        string staged = WriteStaged("mine.safetensors", SafetensorsHeader(SdxlTensors));

        MisplacedMediaCheckpoint offered = Assert.Single(MediaCheckpointMigration.Migrate(includeUserManaged: false));
        Assert.Equal("offered", offered.Status);
        Assert.False(offered.PaRuXManaged);
        Assert.False(File.Exists(Path.Combine(LocalAiPaths.CheckpointsPath, "mine.safetensors")));

        MisplacedMediaCheckpoint copied = Assert.Single(MediaCheckpointMigration.Migrate(includeUserManaged: true));
        Assert.Equal("copied", copied.Status);
        Assert.True(File.Exists(staged));
        Assert.True(File.Exists(Path.Combine(LocalAiPaths.CheckpointsPath, "mine.safetensors")));
    }

    [Fact]
    public void Migration_never_touches_gguf_unrecognised_safetensors_or_deletes_anything()
    {
        InstallComfy();
        string gguf = WriteStaged("chat.gguf", new byte[] { 1, 2, 3, 4 });
        string unknown = WriteStaged("random.safetensors", SafetensorsHeader("random.layer.weight"));

        var done = MediaCheckpointMigration.Migrate(includeUserManaged: true);

        Assert.Empty(done);
        Assert.True(File.Exists(gguf));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Migration_waits_for_comfyui_instead_of_failing_or_moving_anything()
    {
        string staged = WriteStaged("a.safetensors", SafetensorsHeader(SdxlTensors));
        MarkDownloadedByParux("a.safetensors", staged);

        MisplacedMediaCheckpoint row = Assert.Single(MediaCheckpointMigration.Migrate(includeUserManaged: false));

        Assert.Equal("waiting_for_comfyui", row.Status);
        Assert.True(File.Exists(staged));
    }

    // ---- helpers ----

    private void MarkDownloadedByParux(string name, string path)
    {
        ModelDownloadProvenanceManifestStore.Upsert(Text, name, new ModelDownloadProvenance
        {
            RepoId = "o/r",
            Revision = new string('a', 40),
            ModelCardUrl = "https://huggingface.co/o/r/tree/" + new string('a', 40),
            RepoFilePath = name,
            PinnedSourceUrl = ModelDiscoveryIdentity.PinnedUrl("o/r", new string('a', 40), name),
            LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided,
            ComputedSha256 = new string('b', 64),
            IntegrityStatus = "unavailable",
            SizeBytes = new FileInfo(path).Length,
            FileLastWriteUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        });
    }

    private static async Task<ModelDownloadStatus> WaitAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? current = service.Get(id);
            if (current?.Status is "completed" or "failed" or "cancelled") return current;
            await Task.Delay(20);
        }
        throw new TimeoutException("The synthetic download did not finish.");
    }

    private static byte[] SafetensorsHeader(params string[] names)
    {
        var entries = new List<string>();
        for (int i = 0; i < names.Length; i++)
            entries.Add(System.Text.Json.JsonSerializer.Serialize(names[i]) + ":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}");
        byte[] header = Encoding.UTF8.GetBytes("{" + string.Join(",", entries) + "}");
        byte[] content = new byte[8 + header.Length + names.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(content, 8);
        return content;
    }

    private sealed class Synthetic(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? Array.Empty<byte>() : payload)
            });
    }

    private sealed class RecordingHandler : IModelDownloadCompletionHandler
    {
        public List<string> MediaPaths { get; } = new();
        public void OnGgufDownloaded(string modelPath, string label) { }
        public void OnMediaCheckpointDownloaded(string modelPath, string label) => MediaPaths.Add(modelPath);
    }
}
