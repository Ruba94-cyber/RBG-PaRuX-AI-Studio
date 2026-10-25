using System.Runtime.InteropServices;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>One release asset from the upstream llama.cpp GitHub release catalog. Digest is the
/// GitHub API's optional content digest (normally "sha256:&lt;64 hex&gt;" for current assets). It is
/// carried alongside the URL so RuntimeInstallService can authenticate the exact bytes it
/// downloaded before extracting or activating them.</summary>
public sealed record RuntimeReleaseAsset(string Name, string DownloadUrl, long SizeBytes, string? Digest = null);
public sealed record RuntimeReleaseInfo(string Tag, DateTime? PublishedUtc, IReadOnlyList<RuntimeReleaseAsset> Assets);

/// <summary>
/// Pure parsing/matching logic for RuntimeInstallService's GitHub Releases integration. No I/O
/// here at all, specifically so release parsing and asset selection get hermetic coverage.
/// </summary>
internal static class RuntimeBuildCatalog
{
    /// <summary>Parses either a GitHub "list releases" array response or a single "get release by
    /// tag" object (the caller wraps the latter in "[...]" first). Skips drafts/prereleases.</summary>
    public static List<RuntimeReleaseInfo> ParseReleases(string json, bool includePrereleases = false)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var releases = new List<RuntimeReleaseInfo>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out JsonElement draftEl) && draftEl.ValueKind == JsonValueKind.True) continue;
            if (!includePrereleases && item.TryGetProperty("prerelease", out JsonElement preEl) && preEl.ValueKind == JsonValueKind.True) continue;

            string? tag = item.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;

            DateTime? published = item.TryGetProperty("published_at", out JsonElement pubEl) && pubEl.ValueKind == JsonValueKind.String
                ? pubEl.GetDateTime()
                : null;

            var assets = new List<RuntimeReleaseAsset>();
            if (item.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assetsEl.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                    long size = a.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    string? digest = a.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new RuntimeReleaseAsset(name!, url!, size, digest));
                }
            }
            releases.Add(new RuntimeReleaseInfo(tag!, published, assets));
        }
        return releases;
    }

    /// <summary>
    /// Picks the release asset for the requested backend <em>on the platform Core is running on</em>.
    ///
    /// This used to hardcode "win-…-x64.zip", which is the last thing in the install path that made
    /// a headless Linux Core unable to install a runtime rather than merely be handed one. It is
    /// deliberately written as token matching rather than as a table of exact file names: upstream
    /// has renamed these assets before, the names differ per platform and per CUDA version, and a
    /// pinned name that stops matching would silently look like "no build for this tag". Tokens
    /// survive a rename that keeps meaning the same thing.
    ///
    /// What it will <em>not</em> do is fall back to something adjacent. If nothing matches, the
    /// caller reports the failure with the asset names it actually saw, because downloading a
    /// Windows CUDA build onto Linux because it was the only zip present is worse than an error.
    /// </summary>
    public static RuntimeReleaseAsset? SelectAsset(string backend, IReadOnlyList<RuntimeReleaseAsset> assets) =>
        SelectAsset(backend, assets, CurrentPlatform());

    /// <summary>The CUDA toolkit major whose build runs natively on this machine. Blackwell (compute capability 12.x)
    /// has no native kernels in the CUDA 12.4 builds (they JIT from PTX on first use), and CUDA 13 builds need a
    /// driver of at least 580. Anything unknown keeps the broadly compatible 12.x build.</summary>
    internal static int PreferredCudaMajor(double? computeCapability, string? dxgiDriverVersion)
    {
        double? driver = ParseNvidiaDriver(dxgiDriverVersion);
        return computeCapability is >= 12.0 && driver is >= 580.0 ? 13 : 12;
    }

    /// <summary>Maps a Windows display-driver version ("32.0.16.1714") to NVIDIA's number (617.14): the last digit
    /// of the third field followed by the fourth. Null when the shape is not an NVIDIA one.</summary>
    internal static double? ParseNvidiaDriver(string? dxgiVersion)
    {
        if (string.IsNullOrWhiteSpace(dxgiVersion)) return null;
        string[] parts = dxgiVersion.Split('.');
        if (parts.Length != 4 || parts[0] != "32" || parts[2].Length == 0 || parts[3].Length != 4
            || !parts[2].All(char.IsDigit) || !parts[3].All(char.IsDigit)) return null;
        string digits = parts[2][^1] + parts[3];
        return double.Parse(digits.Insert(3, "."), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int? CudaMajorOf(string name)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            name, @"cuda-(\d+)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Platform-explicit overload, so the selection rules for every platform are testable
    /// from any one of them.</summary>
    internal static RuntimeReleaseAsset? SelectAsset(
        string backend, IReadOnlyList<RuntimeReleaseAsset> assets, RuntimePlatform platform, int preferredCudaMajor = 12)
    {
        string accelerator = backend switch
        {
            "text-cuda" => "cuda",
            "text-vulkan" => "vulkan",
            _ => throw new InvalidDataException("Unknown runtime backend \"" + backend + "\".")
        };

        // GitHub publishes CUDA dependency bundles before the llama.cpp server bundle in some
        // releases (for example, cudart-llama-... followed by llama-b<tag>-...). The dependency
        // bundle is a valid archive with a matching platform/backend name, but contains CUDA DLLs
        // only and cannot satisfy the managed runtime install contract (llama-server.exe).
        List<RuntimeReleaseAsset> matches = assets.Where(a => !IsCudaDependencyBundle(a.Name)
            && IsArchive(a.Name)
            && MatchesPlatform(a.Name, platform)
            && Contains(a.Name, accelerator)).ToList();
        if (accelerator == "cuda" && matches.Count > 1)
        {
            RuntimeReleaseAsset? preferred = matches.FirstOrDefault(a => CudaMajorOf(a.Name) == preferredCudaMajor);
            if (preferred != null) return preferred;
        }
        return matches.FirstOrDefault();
    }

    /// <summary>Finds the exact-version CUDA dependency bundle paired with a selected server
    /// archive. Some upstream releases publish this separately from llama-server.</summary>
    internal static RuntimeReleaseAsset? SelectCudaDependencyAsset(
        RuntimeReleaseAsset serverAsset,
        IReadOnlyList<RuntimeReleaseAsset> assets,
        RuntimePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(serverAsset);
        const string ServerPrefix = "llama-";
        const string ServerMarker = "-bin-";
        const string DependencyPrefix = "cudart-llama-bin-";

        if (!serverAsset.Name.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        int marker = serverAsset.Name.IndexOf(ServerMarker, ServerPrefix.Length, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        string variant = serverAsset.Name[(marker + ServerMarker.Length)..];
        if (!Contains(variant, "cuda") || !MatchesPlatform(serverAsset.Name, platform)) return null;

        string expectedName = DependencyPrefix + variant;
        return assets.FirstOrDefault(a => string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            && IsArchive(a.Name)
            && MatchesPlatform(a.Name, platform));
    }

    internal static bool HasCudaDependencyAssetForPlatform(
        IReadOnlyList<RuntimeReleaseAsset> assets,
        RuntimePlatform platform) => assets.Any(a => a.Name.StartsWith(
            "cudart-llama-bin-", StringComparison.OrdinalIgnoreCase)
            && IsArchive(a.Name)
            && MatchesPlatform(a.Name, platform)
            && Contains(a.Name, "cuda"));

    /// <summary>The platforms PaRuX can install a text runtime for. Anything else - a 32-bit host,
    /// an architecture upstream does not publish - is answered honestly by finding no asset.</summary>
    internal enum RuntimePlatform
    {
        WindowsX64,
        LinuxX64,
        LinuxArm64,
        MacArm64,
        MacX64,
        Unsupported
    }

    internal static RuntimePlatform CurrentPlatform()
    {
        Architecture arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows()) return arch == Architecture.X64 ? RuntimePlatform.WindowsX64 : RuntimePlatform.Unsupported;
        if (OperatingSystem.IsMacOS())
            return arch switch
            {
                Architecture.Arm64 => RuntimePlatform.MacArm64,
                Architecture.X64 => RuntimePlatform.MacX64,
                _ => RuntimePlatform.Unsupported
            };
        if (OperatingSystem.IsLinux())
            return arch switch
            {
                Architecture.X64 => RuntimePlatform.LinuxX64,
                Architecture.Arm64 => RuntimePlatform.LinuxArm64,
                _ => RuntimePlatform.Unsupported
            };
        return RuntimePlatform.Unsupported;
    }

    /// <summary>A human-readable name for the platform, used in the "nothing matched" error so the
    /// message says what PaRuX was looking for rather than always saying "Windows x64".</summary>
    internal static string Describe(RuntimePlatform platform) => platform switch
    {
        RuntimePlatform.WindowsX64 => "Windows x64",
        RuntimePlatform.LinuxX64 => "Linux x64",
        RuntimePlatform.LinuxArm64 => "Linux arm64",
        RuntimePlatform.MacArm64 => "macOS arm64",
        RuntimePlatform.MacX64 => "macOS x64",
        _ => "this platform"
    };

    /// <summary>
    /// Upstream spells the same platform several ways across its history ("ubuntu" and "linux",
    /// "x64" and "amd64", "macos" and "osx"), so each platform carries the spellings that mean it
    /// and the spellings that would disqualify it. The negative half matters as much as the
    /// positive one: "llama-…-bin-win-vulkan-x64.zip" contains "x64" too.
    /// </summary>
    private static bool MatchesPlatform(string name, RuntimePlatform platform)
    {
        bool win = Contains(name, "win");
        bool linux = Contains(name, "ubuntu") || Contains(name, "linux");
        bool mac = Contains(name, "macos") || Contains(name, "osx") || Contains(name, "darwin");
        bool arm = Contains(name, "arm64") || Contains(name, "aarch64");
        bool x64 = Contains(name, "x64") || Contains(name, "amd64") || Contains(name, "x86_64");

        return platform switch
        {
            RuntimePlatform.WindowsX64 => win && !linux && !mac && x64 && !arm,
            RuntimePlatform.LinuxX64 => linux && !win && !mac && x64 && !arm,
            RuntimePlatform.LinuxArm64 => linux && !win && !mac && arm,
            RuntimePlatform.MacArm64 => mac && !win && !linux && arm,
            RuntimePlatform.MacX64 => mac && !win && !linux && x64 && !arm,
            _ => false
        };
    }

    /// <summary>Accepts the archive formats upstream actually ships and, just as importantly,
    /// rejects the sidecars that sit beside them - a ".zip.sha256" contains ".zip".</summary>
    private static bool IsArchive(string name) =>
        (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

    private static bool IsCudaDependencyBundle(string name) =>
        name.StartsWith("cudart-", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
