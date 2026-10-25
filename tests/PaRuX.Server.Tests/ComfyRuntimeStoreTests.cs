using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyRuntimeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-comfy-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Active => Path.Combine(_root, "runtime", "ComfyUI_windows_portable");
    private string Store => Path.Combine(_root, "runtime", "comfyui-builds");

    private static void CreatePortable(string root, string marker)
    {
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        Directory.CreateDirectory(Path.Combine(root, "python_embeded"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), marker);
        File.WriteAllText(Path.Combine(root, "python_embeded", "python.exe"), "fake python " + marker);
    }

    private static void CreatePersistentUserData(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "models", "diffusion_models"));
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "output"));
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "custom_nodes", "MyNode"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "models", "diffusion_models", "user-model.safetensors"), "model");
        File.WriteAllText(Path.Combine(root, "ComfyUI", "output", "generated.png"), "image");
        File.WriteAllText(Path.Combine(root, "ComfyUI", "custom_nodes", "MyNode", "node.py"), "node");
        File.WriteAllText(Path.Combine(root, "ComfyUI", "extra_model_paths.yaml"), "models: user");
    }

    [Fact]
    public void First_managed_install_preserves_legacy_user_data_and_can_rollback_both_directions()
    {
        CreatePortable(Active, "legacy");
        CreatePersistentUserData(Active);

        string incoming = Path.Combine(_root, "incoming-nvidia");
        CreatePortable(incoming, "new");
        Directory.CreateDirectory(Path.Combine(incoming, "ComfyUI", "models"));
        File.WriteAllText(Path.Combine(incoming, "ComfyUI", "models", "upstream-placeholder.txt"), "placeholder");

        var store = new ComfyRuntimeStore(Active, Store);
        var newer = new ComfyBuildIdentity("v0.34.0", "nvidia");

        store.ActivateExtracted(incoming, newer);

        Assert.Equal(newer, store.GetActiveIdentity());
        Assert.Equal("new", File.ReadAllText(Path.Combine(Active, "ComfyUI", "main.py")));
        Assert.Contains(new ComfyBuildIdentity("legacy", "legacy"), store.ListInstalled());
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "models", "diffusion_models", "user-model.safetensors")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "output", "generated.png")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "custom_nodes", "MyNode", "node.py")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "extra_model_paths.yaml")));
        Assert.False(File.Exists(Path.Combine(Active, "ComfyUI", "models", "upstream-placeholder.txt")));

        store.Rollback(new ComfyBuildIdentity("legacy", "legacy"));
        Assert.Equal(new ComfyBuildIdentity("legacy", "legacy"), store.GetActiveIdentity());
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(Active, "ComfyUI", "main.py")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "models", "diffusion_models", "user-model.safetensors")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "output", "generated.png")));

        Assert.True(store.TryActivateInstalled(newer));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Active, "ComfyUI", "main.py")));
        Assert.True(File.Exists(Path.Combine(Active, "ComfyUI", "models", "diffusion_models", "user-model.safetensors")));
    }

    [Fact]
    public void ResolveInstalled_requires_variant_when_same_tag_has_multiple_variants()
    {
        CreatePortable(Active, "legacy");
        var store = new ComfyRuntimeStore(Active, Store);

        string nvidia = Path.Combine(_root, "incoming-nvidia");
        CreatePortable(nvidia, "nvidia");
        store.ActivateExtracted(nvidia, new ComfyBuildIdentity("v0.34.0", "nvidia"));

        string amd = Path.Combine(_root, "incoming-amd");
        CreatePortable(amd, "amd");
        store.ActivateExtracted(amd, new ComfyBuildIdentity("v0.34.0", "amd"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ResolveInstalled("v0.34.0", null));
        Assert.Contains("Specify variant", error.Message);
        Assert.Equal(new ComfyBuildIdentity("v0.34.0", "nvidia"), store.ResolveInstalled("v0.34.0", "nvidia"));
    }

    [Fact]
    public void Activation_refuses_to_overwrite_an_unknown_incomplete_active_directory()
    {
        Directory.CreateDirectory(Active);
        File.WriteAllText(Path.Combine(Active, "do-not-delete.txt"), "keep me");
        string incoming = Path.Combine(_root, "incoming");
        CreatePortable(incoming, "new");
        var store = new ComfyRuntimeStore(Active, Store);

        Assert.Throws<InvalidDataException>(() =>
            store.ActivateExtracted(incoming, new ComfyBuildIdentity("v0.34.0", "nvidia")));

        Assert.True(File.Exists(Path.Combine(Active, "do-not-delete.txt")));
        Assert.True(ComfyRuntimeStore.IsPortableRoot(incoming));
    }
}
