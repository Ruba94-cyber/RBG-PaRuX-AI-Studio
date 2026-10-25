using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// RuntimeFolderName/TryParseRuntimeBuildTag/RuntimeFolderSuffix are pure string functions - fully
/// safe to test directly. GetActiveRuntimeBuildTag/SetActiveRuntimeBuildTag/
/// ListInstalledRuntimeBuildTags touch a real, shared %LOCALAPPDATA%\PaRuX\config\
/// runtime-build-config.json plus real runtime\ folders on disk - same hazard this codebase's own
/// convention already documents for SetRoleAdapter/SetRoleOverride (see
/// LocalAiPathsRoleAdapterTests' own doc comment), so only the validation-before-any-write paths
/// are exercised here; the full real install/rollback pipeline against real (test-created) files
/// is covered by RuntimeInstallServiceTests instead.
/// </summary>
public sealed class LocalAiPathsRuntimeBuildTests
{
    [Theory]
    [InlineData("text-cuda", "b10549", "llama.cpp-b10549-cuda12.4")]
    [InlineData("text-vulkan", "b10549", "llama.cpp-b10549-vulkan")]
    [InlineData("text-cuda", "b10600", "llama.cpp-b10600-cuda12.4")]
    public void RuntimeFolderName_matches_this_machines_pre_existing_naming_convention(string backend, string tag, string expected)
    {
        Assert.Equal(expected, LocalAiPaths.RuntimeFolderName(backend, tag));
    }

    [Fact]
    public void RuntimeFolderSuffix_throws_for_an_unknown_backend()
    {
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.RuntimeFolderSuffix("text-metal"));
    }

    [Theory]
    [InlineData("text-cuda", "llama.cpp-b10549-cuda12.4", "b10549")]
    [InlineData("text-vulkan", "llama.cpp-b10549-vulkan", "b10549")]
    [InlineData("text-cuda", "llama.cpp-b10600-cuda12.4", "b10600")]
    public void TryParseRuntimeBuildTag_is_the_exact_inverse_of_RuntimeFolderName(string backend, string folderName, string expectedTag)
    {
        Assert.Equal(expectedTag, LocalAiPaths.TryParseRuntimeBuildTag(backend, folderName));
    }

    [Theory]
    [InlineData("text-cuda", "llama.cpp-b10549-vulkan")] // wrong backend's suffix
    [InlineData("text-cuda", "ComfyUI_windows_portable")] // not a runtime build folder at all
    [InlineData("text-cuda", "llama.cpp--cuda12.4")] // empty tag
    public void TryParseRuntimeBuildTag_returns_null_for_a_non_matching_folder_name(string backend, string folderName)
    {
        Assert.Null(LocalAiPaths.TryParseRuntimeBuildTag(backend, folderName));
    }

    [Fact]
    public void RuntimeFolderName_and_TryParseRuntimeBuildTag_round_trip_for_every_known_backend()
    {
        foreach (string backend in LocalAiPaths.KnownRuntimeBackends)
        {
            string folder = LocalAiPaths.RuntimeFolderName(backend, "bTEST");
            Assert.Equal("bTEST", LocalAiPaths.TryParseRuntimeBuildTag(backend, folder));
        }
    }

    [Fact]
    public void SetActiveRuntimeBuildTag_rejects_an_unknown_backend_before_any_write()
    {
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.SetActiveRuntimeBuildTag("text-metal", "b10549"));
    }

    [Fact]
    public void SetActiveRuntimeBuildTag_rejects_a_tag_with_no_installed_folder_before_any_write()
    {
        string neverInstalledTag = "b-never-installed-" + Guid.NewGuid().ToString("N");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", neverInstalledTag));
        Assert.Contains(neverInstalledTag, ex.Message);
    }

    [Fact]
    public void GetActiveRuntimeBuildTag_rejects_an_unknown_backend()
    {
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.GetActiveRuntimeBuildTag("text-metal"));
    }

    [Fact]
    public void ListInstalledRuntimeBuildTags_rejects_an_unknown_backend()
    {
        Assert.Throws<InvalidDataException>(() => LocalAiPaths.ListInstalledRuntimeBuildTags("text-metal"));
    }

    [Fact]
    public void ClearActiveRuntimeBuildTag_on_a_backend_with_no_override_is_a_safe_no_op()
    {
        LocalAiPaths.ClearActiveRuntimeBuildTag("text-vulkan");
        LocalAiPaths.ClearActiveRuntimeBuildTag("text-vulkan"); // calling it again must not throw
    }

    [Fact]
    public void Default_role_model_paths_follow_a_scoped_root_after_initial_resolution()
    {
        // Force LocalAiPaths' type initializer to run before installing the test root. A static
        // readonly path here would otherwise capture the developer's real LocalAI models folder.
        _ = LocalAiPaths.StandardModel;

        string isolatedRoot = Path.Combine(Path.GetTempPath(), "parux-localai-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (LocalAiPaths.UseRootForTests(isolatedRoot))
            {
                Assert.Equal(Path.Combine(isolatedRoot, "models", "Qwen3.8-27B-UD-IQ4_XS.gguf"), LocalAiPaths.StandardModel);
                Assert.Equal(Path.Combine(isolatedRoot, "models", "Qwen3.8-27B-Uncensored-noMTP-IQ4_XS.gguf"), LocalAiPaths.UncensoredModel);
            }
        }
        finally
        {
            if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, recursive: true);
        }
    }
}
