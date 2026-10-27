using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedModelDeletionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-del-" + Guid.NewGuid().ToString("N"));
    private readonly string _models, _diffusion, _checkpoints;
    private readonly IDisposable _scope;
    private readonly List<ModelAssignment> _assignments = new();
    private string? _loaded;
    private readonly HashSet<string> _linked = new(StringComparer.OrdinalIgnoreCase);

    public ManagedModelDeletionServiceTests()
    {
        _models = Path.Combine(_root, "models");
        _diffusion = Path.Combine(_root, "comfy", "diffusion_models");
        _checkpoints = Path.Combine(_root, "comfy", "checkpoints");
        Directory.CreateDirectory(_models);
        Directory.CreateDirectory(_diffusion);
        Directory.CreateDirectory(_checkpoints);
        _scope = ModelDownloadService.UseModelsDirectoryForTests(_models);
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private ManagedModelDeletionService Service() => new(
        new ModelDownloadService(new HttpClient(), new HttpClient()),
        _ => Task.FromResult(_loaded),
        () => _assignments.ToList(),
        () => new[] { _diffusion, _checkpoints },
        p => _linked.Contains(Path.GetFileName(p)) ? 2 : 1);

    private string Make(string folder, string name, int bytes = 8)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private void Record(string name) => ModelDownloadProvenanceManifestStore.Upsert(_models, name, new ModelDownloadProvenance
    {
        RepoId = "test/repo", LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided,
        ComputedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[8])).ToLowerInvariant(),
        IntegrityStatus = "unavailable", SizeBytes = 8, FileLastWriteUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
    });

    [Fact]
    public async Task Deletes_unassigned_text_model_and_reports_bytes()
    {
        Make(_models, "free-Q4_K_M.gguf", 100);
        ManagedModelDeleteResult r = await Service().DeleteAsync("free-Q4_K_M.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Deleted, r.Status);
        Assert.Equal(100, r.BytesFreed);
        Assert.False(File.Exists(Path.Combine(_models, "free-Q4_K_M.gguf")));
    }

    [Fact]
    public async Task Refuses_text_model_assigned_to_a_role_and_names_the_role()
    {
        string path = Make(_models, "main-Q4_K_M.gguf");
        _assignments.Add(new ModelAssignment("text", "coding", path));
        ManagedModelDeleteResult r = await Service().DeleteAsync("main-Q4_K_M.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.Contains(r.Blockers, b => b.Kind == "assigned-role" && b.Role == "coding");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Refuses_loaded_text_model()
    {
        string path = Make(_models, "live-Q4_K_M.gguf");
        _loaded = path;
        ManagedModelDeleteResult r = await Service().DeleteAsync("live-Q4_K_M.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.Contains(r.Blockers, b => b.Kind == "loaded");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Allows_delete_after_role_is_switched_to_automatic()
    {
        string path = Make(_models, "again-Q4_K_M.gguf");
        _assignments.Add(new ModelAssignment("text", "fast", path));
        Assert.Equal(ManagedModelDeleteStatus.Refused, (await Service().DeleteAsync("again-Q4_K_M.gguf", default)).Status);
        _assignments.Clear();
        Assert.Equal(ManagedModelDeleteStatus.Deleted, (await Service().DeleteAsync("again-Q4_K_M.gguf", default)).Status);
    }

    [Fact]
    public async Task Deletes_a_complete_split_set_as_a_whole()
    {
        string a = Make(_models, "big-00001-of-00002.gguf", 10);
        string b = Make(_models, "big-00002-of-00002.gguf", 20);
        ManagedModelDeleteResult r = await Service().DeleteAsync("big-00001-of-00002.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Deleted, r.Status);
        Assert.Equal(30, r.BytesFreed);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public async Task Split_set_assigned_through_any_part_is_refused()
    {
        string a = Make(_models, "set-00001-of-00002.gguf");
        string b = Make(_models, "set-00002-of-00002.gguf");
        _assignments.Add(new ModelAssignment("text", "planner", a));
        ManagedModelDeleteResult r = await Service().DeleteAsync("set-00002-of-00002.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public async Task Never_deletes_hard_linked_external_model()
    {
        string path = Make(_models, "ollama-Q4_K_M.gguf");
        _linked.Add("ollama-Q4_K_M.gguf");
        ManagedModelDeleteResult r = await Service().DeleteAsync("ollama-Q4_K_M.gguf", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.Contains(r.Blockers, b => b.Kind == "external-link");
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("..\\outside.gguf")]
    [InlineData("../outside.gguf")]
    [InlineData("sub/inner.safetensors")]
    [InlineData("C:\\Windows\\x.gguf")]
    [InlineData("")]
    public async Task Rejects_path_traversal(string name)
    {
        string outside = Path.Combine(_root, "outside.gguf");
        File.WriteAllBytes(outside, new byte[4]);
        ManagedModelDeleteResult r = await Service().DeleteAsync(name, default);
        Assert.Equal(ManagedModelDeleteStatus.Invalid, r.Status);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Unknown_model_is_not_found()
    {
        Assert.Equal(ManagedModelDeleteStatus.NotFound, (await Service().DeleteAsync("nope.gguf", default)).Status);
        Assert.Equal(ManagedModelDeleteStatus.NotFound, (await Service().DeleteAsync("nope.safetensors", default)).Status);
    }

    [Fact]
    public async Task Deletes_media_checkpoint_that_PaRuX_downloaded()
    {
        string path = Make(_diffusion, "z_image.safetensors", 64);
        Record("z_image.safetensors");
        ManagedModelDeleteResult r = await Service().DeleteAsync("z_image.safetensors", default);
        Assert.Equal(ManagedModelDeleteStatus.Deleted, r.Status);
        Assert.Equal(64, r.BytesFreed);
        Assert.False(File.Exists(path));
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(_models, "z_image.safetensors"));
    }

    [Fact]
    public async Task Never_deletes_user_managed_media_checkpoint()
    {
        string path = Make(_checkpoints, "user_own.safetensors");
        ManagedModelDeleteResult r = await Service().DeleteAsync("user_own.safetensors", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.Contains(r.Blockers, b => b.Kind == "not-managed");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Refuses_media_checkpoint_in_use_by_a_workflow()
    {
        string path = Make(_diffusion, "active.safetensors");
        Record("active.safetensors");
        _assignments.Add(new ModelAssignment("media", "image", "active.safetensors"));
        ManagedModelDeleteResult r = await Service().DeleteAsync("active.safetensors", default);
        Assert.Equal(ManagedModelDeleteStatus.Refused, r.Status);
        Assert.Contains(r.Blockers, b => b.Kind == "assigned-media" && b.Role == "image");
        Assert.True(File.Exists(path));
    }
}
