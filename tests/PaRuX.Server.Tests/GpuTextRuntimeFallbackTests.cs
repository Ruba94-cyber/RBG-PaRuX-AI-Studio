using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An NVIDIA machine that has only the Vulkan runtime installed (the MSI baseline, or a user who
/// skipped CUDA) must still be able to chat. The GPU launch path used to hardwire the CUDA folder,
/// so every load failed with "File not found: ...\llama-server.exe" even though a working Vulkan
/// runtime sat beside it.
/// </summary>
public sealed class GpuTextRuntimeFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-gpu-rt-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;

    public GpuTextRuntimeFallbackTests() => _scope = LocalAiPaths.UseRootForTests(_root);

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Install(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, LocalAiPaths.LlamaServerExecutable), "x");
    }

    [Fact]
    public void Cuda_is_used_when_installed()
    {
        Install(LocalAiPaths.RuntimeCudaPath);
        Install(LocalAiPaths.RuntimeVulkanPath);
        Assert.Equal(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true));
    }

    [Fact]
    public void Vulkan_is_used_when_cuda_is_missing_but_vulkan_is_installed()
    {
        Install(LocalAiPaths.RuntimeVulkanPath);
        Assert.Equal(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true));
    }

    [Fact]
    public void Missing_runtimes_keep_naming_the_cuda_folder_so_the_error_is_actionable()
    {
        Assert.Equal(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true));
    }

    [Fact]
    public void Not_preferring_cuda_always_returns_vulkan()
    {
        Install(LocalAiPaths.RuntimeCudaPath);
        Assert.Equal(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: false));
    }
}
