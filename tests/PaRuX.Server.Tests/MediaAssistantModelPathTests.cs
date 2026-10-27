using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real clean install: the guided setup downloads the Qwen3.5-4B media assistant into PaRuX's managed models folder, while the
/// Local AI root is a different (empty) folder. The assistant lookup must find the downloaded file, not report it missing.
/// </summary>
public sealed class MediaAssistantModelPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-assistant-" + Guid.NewGuid().ToString("N"));

    public MediaAssistantModelPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Assistant_model_in_the_managed_download_folder_is_found_when_the_local_ai_root_has_none()
    {
        string root = Path.Combine(_dir, "root");
        string managed = Path.Combine(_dir, "managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "Qwen3.5-4B-Q4_K_M.gguf"), "x");

        using IDisposable r = LocalAiPaths.UseRootForTests(root);
        using IDisposable m = ModelDownloadService.UseModelsDirectoryForTests(managed);

        Assert.Equal(Path.Combine(managed, "Qwen3.5-4B-Q4_K_M.gguf"), LocalAiPaths.SmallModel);
    }

    [Fact]
    public void Assistant_model_in_the_local_ai_root_still_wins()
    {
        string root = Path.Combine(_dir, "root");
        string managed = Path.Combine(_dir, "managed");
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(root, "models", "Qwen3.5-4B-Q4_K_M.gguf"), "x");
        File.WriteAllText(Path.Combine(managed, "Qwen3.5-4B-Q4_K_M.gguf"), "y");

        using IDisposable r = LocalAiPaths.UseRootForTests(root);
        using IDisposable m = ModelDownloadService.UseModelsDirectoryForTests(managed);

        Assert.Equal(Path.Combine(root, "models", "Qwen3.5-4B-Q4_K_M.gguf"), LocalAiPaths.SmallModel);
    }

    [Fact]
    public void Missing_assistant_model_keeps_the_local_ai_root_path_so_messages_name_the_expected_location()
    {
        string root = Path.Combine(_dir, "root");
        using IDisposable r = LocalAiPaths.UseRootForTests(root);
        using IDisposable m = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_dir, "managed"));

        Assert.Equal(Path.Combine(root, "models", "Qwen3.5-4B-Q4_K_M.gguf"), LocalAiPaths.SmallModel);
    }
}
