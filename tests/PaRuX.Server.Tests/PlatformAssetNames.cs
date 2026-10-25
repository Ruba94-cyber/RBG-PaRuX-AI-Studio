using System.Runtime.InteropServices;

namespace PaRuX.Server.Tests;

/// <summary>
/// Builds the llama.cpp release asset name for the platform the suite is running on.
///
/// The install-path fixtures used to hardcode "win-cuda-cu12.4-x64.zip", which was fine while asset
/// selection was platform-blind and became wrong the moment it stopped being: a Linux run would have
/// been asserting that PaRuX installs a Windows build. Deriving the name here means these tests
/// exercise the same selection the host would really perform, on whichever host runs them.
/// </summary>
internal static class PlatformAssetNames
{
    /// <summary>e.g. "llama-b1234-bin-win-cuda-cu12.4-x64.zip" on Windows, and the ubuntu/macos
    /// equivalent elsewhere.</summary>
    public static string For(string tag, string backend)
    {
        string accelerator = backend == "text-cuda" ? "cuda-cu12.4" : "vulkan";
        Architecture arch = RuntimeInformation.OSArchitecture;
        string cpu = arch == Architecture.Arm64 ? "arm64" : "x64";

        if (OperatingSystem.IsWindows())
            return "llama-" + tag + "-bin-win-" + accelerator + "-" + cpu + ".zip";
        if (OperatingSystem.IsMacOS())
            return "llama-" + tag + "-bin-macos-" + accelerator + "-" + cpu + ".zip";
        return "llama-" + tag + "-bin-ubuntu-" + accelerator + "-" + cpu + ".zip";
    }

    public static string UrlFor(string tag, string backend) =>
        "https://github.com/ggml-org/llama.cpp/releases/download/" + tag + "/" + For(tag, backend);
}
