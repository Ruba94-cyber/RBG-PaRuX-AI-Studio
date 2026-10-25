using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Exercises StartTextAsync through the existing launch interception seam. All files and state live
/// under an isolated temporary LocalAI root; no llama-server process is started.
/// </summary>
public sealed class VisionProjectorLaunchPathTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-vision-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public VisionProjectorLaunchPathTests()
    {
        _ = LocalAiPaths.NativeExecutable("llama-server");
        _previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(Path.Combine(_work, "data"));
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));

        Directory.CreateDirectory(LocalAiPaths.RuntimeCudaPath);
        File.WriteAllBytes(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable), new byte[16]);
        WriteModel(LocalAiPaths.StandardModel);
        WriteModel(LocalAiPaths.UncensoredModel);
        WriteModel(LocalAiPaths.StandardVision!);
        WriteModel(LocalAiPaths.UncensoredVision!);
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("STANDARD", "Qwen3.8-27B-UD-IQ4_XS.gguf", "mmproj-Qwen3.8-27B-Q8_0.gguf")]
    [InlineData("UNCENSORED", "Qwen3.8-27B-Uncensored-noMTP-IQ4_XS.gguf", "Qwen3.8-27B-Uncensored-vision-f16.gguf")]
    public async Task StartTextAsync_passes_the_manifest_projector_for_the_matching_bundled_model(
        string profile, string modelFileName, string projectorFileName)
    {
        IReadOnlyList<string> arguments = await CaptureLaunchAsync(profile);

        Assert.Equal(Path.Combine(LocalAiPaths.ModelsPath, modelFileName), ValueOf(arguments, "--model"));
        Assert.Equal(Path.Combine(LocalAiPaths.ModelsPath, "multimodal", projectorFileName), ValueOf(arguments, "--mmproj"));
    }

    [Fact]
    public async Task StartTextAsync_does_not_attach_a_projector_to_a_role_override()
    {
        string customModel = Path.Combine(LocalAiPaths.ModelsPath, "custom-standard.gguf");
        WriteModel(customModel);
        LocalAiPaths.SetRoleOverride("standard", customModel);

        IReadOnlyList<string> arguments = await CaptureLaunchAsync("STANDARD");

        Assert.Equal(customModel, ValueOf(arguments, "--model"));
        Assert.DoesNotContain("--mmproj", arguments);
    }

    [Fact]
    public async Task StartTextAsync_does_not_attach_a_projector_to_an_explicit_custom_model()
    {
        string customModel = Path.Combine(LocalAiPaths.ModelsPath, "explicit-custom.gguf");
        WriteModel(customModel);

        IReadOnlyList<string> arguments = await CaptureLaunchAsync("STANDARD", customModel);

        Assert.Equal(customModel, ValueOf(arguments, "--model"));
        Assert.DoesNotContain("--mmproj", arguments);
    }

    [Fact]
    public async Task StartTextAsync_does_not_pair_a_bundled_model_under_the_wrong_role()
    {
        IReadOnlyList<string> arguments = await CaptureLaunchAsync("UNCENSORED", LocalAiPaths.StandardModel);

        Assert.Equal(LocalAiPaths.StandardModel, ValueOf(arguments, "--model"));
        Assert.DoesNotContain("--mmproj", arguments);
    }

    private async Task<IReadOnlyList<string>> CaptureLaunchAsync(string profile, string? modelPathOverride = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.TextProfileFile)!);
        File.WriteAllText(LocalAiPaths.TextProfileFile, profile);
        var launches = new List<IReadOnlyList<string>>();
        var manager = new LocalAiProcessManager(24_000)
        {
            InventoryProbe = GpuInventoryFixtures.Unavailable,
            LaunchInterceptForTests = (_, arguments) =>
            {
                launches.Add(arguments.ToArray());
                return new LocalAiProcessManager.RuntimeResult(false, "intercepted before process start");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None, modelPathOverride);

        Assert.False(result.Success);
        return launches.First(); // the first launch is the requested one; later entries are the recovery ladder retrying a failed launch
    }

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        int index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    private static void WriteModel(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }
}
