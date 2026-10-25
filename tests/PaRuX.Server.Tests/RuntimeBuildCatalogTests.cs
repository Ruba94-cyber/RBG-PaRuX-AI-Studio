using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// RuntimeBuildCatalog is pure JSON parsing + string matching, no I/O - so unlike the actual
/// network/filesystem side of RuntimeInstallService, this gets exhaustive hermetic coverage
/// against realistic GitHub Releases API response shapes.
/// </summary>
public sealed class RuntimeBuildCatalogTests
{
    private const string TwoReleasesJson = """
    [
      {
        "tag_name": "b10600",
        "draft": false,
        "prerelease": false,
        "published_at": "2026-08-20T10:00:00Z",
        "assets": [
          { "name": "llama-b10600-bin-win-cuda-cu12.4-x64.zip", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10600/llama-b10600-bin-win-cuda-cu12.4-x64.zip", "size": 314572800, "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
          { "name": "llama-b10600-bin-win-vulkan-x64.zip", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10600/llama-b10600-bin-win-vulkan-x64.zip", "size": 104857600 },
          { "name": "llama-b10600-bin-ubuntu-x64.tar.gz", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10600/llama-b10600-bin-ubuntu-x64.tar.gz", "size": 52428800 },
          { "name": "llama-b10600-bin-win-cuda-cu12.4-x64.zip.sha256", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10600/llama-b10600-bin-win-cuda-cu12.4-x64.zip.sha256", "size": 96 }
        ]
      },
      {
        "tag_name": "b10549",
        "draft": false,
        "prerelease": false,
        "published_at": "2026-07-01T08:30:00Z",
        "assets": [
          { "name": "llama-b10549-bin-win-cuda-cu12.4-x64.zip", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10549/llama-b10549-bin-win-cuda-cu12.4-x64.zip", "size": 300000000 },
          { "name": "llama-b10549-bin-win-vulkan-x64.zip", "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10549/llama-b10549-bin-win-vulkan-x64.zip", "size": 100000000 }
        ]
      }
    ]
    """;

    [Fact]
    public void ParseReleases_parses_tag_publish_date_assets_and_digest()
    {
        List<RuntimeReleaseInfo> releases = RuntimeBuildCatalog.ParseReleases(TwoReleasesJson);

        Assert.Equal(2, releases.Count);
        Assert.Equal("b10600", releases[0].Tag);
        Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), releases[0].PublishedUtc);
        Assert.Equal(4, releases[0].Assets.Count);
        RuntimeReleaseAsset cuda = Assert.Single(releases[0].Assets.Where(a => a.Name == "llama-b10600-bin-win-cuda-cu12.4-x64.zip"));
        Assert.Equal(314572800, cuda.SizeBytes);
        Assert.Equal("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", cuda.Digest);
        Assert.Null(releases[0].Assets.Single(a => a.Name.Contains("vulkan")).Digest);
    }

    [Fact]
    public void ParseReleases_skips_drafts_and_prereleases()
    {
        const string json = """
        [
          { "tag_name": "b99999", "draft": true, "prerelease": false, "assets": [] },
          { "tag_name": "b99998", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "b10549", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;
        List<RuntimeReleaseInfo> releases = RuntimeBuildCatalog.ParseReleases(json);
        Assert.Single(releases);
        Assert.Equal("b10549", releases[0].Tag);
    }

    [Fact]
    public void ParseReleases_skips_a_release_with_no_tag_name()
    {
        const string json = """[ { "draft": false, "prerelease": false, "assets": [] } ]""";
        Assert.Empty(RuntimeBuildCatalog.ParseReleases(json));
    }

    [Theory]
    [InlineData("text-cuda", "llama-b10600-bin-win-cuda-cu12.4-x64.zip")]
    [InlineData("text-vulkan", "llama-b10600-bin-win-vulkan-x64.zip")]
    public void SelectAsset_picks_the_matching_windows_x64_zip_and_skips_everything_else(string backend, string expectedName)
    {
        List<RuntimeReleaseInfo> releases = RuntimeBuildCatalog.ParseReleases(TwoReleasesJson);
        RuntimeReleaseAsset? asset = RuntimeBuildCatalog.SelectAsset(
            backend, releases[0].Assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64);

        Assert.NotNull(asset);
        Assert.Equal(expectedName, asset!.Name);
    }

    /// <summary>
    /// The point of the platform overload: the same release, asked for from four machines, has to
    /// give four different answers - and give none rather than a wrong one. Windows CUDA landing on
    /// a Linux box is the specific failure this pins.
    /// </summary>
    [Theory]
    [InlineData("WindowsX64", "text-vulkan", "llama-b1-bin-win-vulkan-x64.zip")]
    [InlineData("LinuxX64", "text-vulkan", "llama-b1-bin-ubuntu-vulkan-x64.tar.gz")]
    [InlineData("LinuxX64", "text-cuda", "llama-b1-bin-ubuntu-cuda-cu12.4-x64.tar.gz")]
    [InlineData("MacArm64", "text-vulkan", "llama-b1-bin-macos-vulkan-arm64.zip")]
    public void SelectAsset_picks_the_asset_for_the_platform_it_is_asked_about(
        string platform, string backend, string expectedName)
    {
        RuntimeReleaseAsset? asset = RuntimeBuildCatalog.SelectAsset(backend, MixedPlatformAssets, Platform(platform));

        Assert.NotNull(asset);
        Assert.Equal(expectedName, asset!.Name);
    }

    /// <summary>Upstream has published this release for three platforms and not for a fourth. The
    /// honest answer for the fourth is nothing, not the closest thing on the list.</summary>
    [Theory]
    [InlineData("LinuxArm64", "text-cuda")]
    [InlineData("MacX64", "text-cuda")]
    [InlineData("Unsupported", "text-vulkan")]
    public void SelectAsset_returns_nothing_rather_than_something_adjacent(
        string platform, string backend)
    {
        Assert.Null(RuntimeBuildCatalog.SelectAsset(backend, MixedPlatformAssets, Platform(platform)));
    }

    /// <summary>"x64" appears in the Windows name too, so a Linux selector written as
    /// "contains x64" would have matched it. Spelled out as its own test because it is the mistake
    /// this matcher is most likely to regress into.</summary>
    [Fact]
    public void SelectAsset_does_not_take_a_windows_build_for_linux_because_both_say_x64()
    {
        var windowsOnly = new List<RuntimeReleaseAsset>
        {
            new("llama-b1-bin-win-cuda-cu12.4-x64.zip", "https://example/x", 1)
        };
        Assert.Null(RuntimeBuildCatalog.SelectAsset("text-cuda", windowsOnly, RuntimeBuildCatalog.RuntimePlatform.LinuxX64));
    }

    /// <summary>An arm64 Mac must not be handed the x64 Mac build; both say "macos".</summary>
    [Fact]
    public void SelectAsset_does_not_confuse_the_two_mac_architectures()
    {
        var macX64Only = new List<RuntimeReleaseAsset>
        {
            new("llama-b1-bin-macos-vulkan-x64.zip", "https://example/x", 1)
        };
        Assert.Null(RuntimeBuildCatalog.SelectAsset("text-vulkan", macX64Only, RuntimeBuildCatalog.RuntimePlatform.MacArm64));
        Assert.NotNull(RuntimeBuildCatalog.SelectAsset("text-vulkan", macX64Only, RuntimeBuildCatalog.RuntimePlatform.MacX64));
    }

    /// <summary>Upstream has spelled the same platform and architecture several ways over time.
    /// Matching on meaning rather than on one exact filename is the whole reason this is token
    /// matching, so the alternatives are pinned.</summary>
    [Theory]
    [InlineData("llama-b1-linux-vulkan-amd64.tar.gz")]
    [InlineData("llama-b1-bin-ubuntu-vulkan-x86_64.tgz")]
    public void SelectAsset_accepts_the_other_spellings_upstream_has_used(string name)
    {
        var assets = new List<RuntimeReleaseAsset> { new(name, "https://example/x", 1) };
        Assert.NotNull(RuntimeBuildCatalog.SelectAsset("text-vulkan", assets, RuntimeBuildCatalog.RuntimePlatform.LinuxX64));
    }

    [Fact]
    public void SelectAsset_returns_null_when_no_matching_asset_exists()
    {
        var assets = new List<RuntimeReleaseAsset> { new("llama-b1-bin-ubuntu-x64.tar.gz", "https://example/x", 1) };
        Assert.Null(RuntimeBuildCatalog.SelectAsset("text-cuda", assets, RuntimeBuildCatalog.RuntimePlatform.LinuxX64));
    }

    /// <summary>".zip.sha256" contains ".zip", and ".tar.gz.sha256" contains ".tar.gz". Both
    /// sidecars have to be rejected by looking at the end of the name, not at a substring.</summary>
    [Theory]
    [InlineData("llama-b1-bin-win-cuda-cu12.4-x64.zip.sha256", "WindowsX64")]
    [InlineData("llama-b1-bin-ubuntu-cuda-x64.tar.gz.sha256", "LinuxX64")]
    [InlineData("llama-b1-bin-ubuntu-cuda-x64.tar.gz.asc", "LinuxX64")]
    public void SelectAsset_never_matches_a_checksum_or_signature_sidecar(
        string name, string platform)
    {
        var assets = new List<RuntimeReleaseAsset> { new(name, "https://example/x", 1) };
        Assert.Null(RuntimeBuildCatalog.SelectAsset("text-cuda", assets, Platform(platform)));
    }

    [Fact]
    public void SelectAsset_skips_cuda_dependency_bundle_and_picks_server_bundle_in_upstream_order()
    {
        // This is the order and naming used by the b10549 GitHub release. The first archive is
        // digest-valid but contains only CUDA dependency DLLs, while the second contains the
        // llama-server executable required by RuntimeInstallService.
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-12.4-x64.zip",
                "https://github.com/ggml-org/llama.cpp/releases/download/b10549/cudart-llama-bin-win-cuda-12.4-x64.zip",
                391443627,
                "sha256:8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"),
            new("llama-b10549-bin-win-cuda-12.4-x64.zip",
                "https://github.com/ggml-org/llama.cpp/releases/download/b10549/llama-b10549-bin-win-cuda-12.4-x64.zip",
                250969968,
                "sha256:2e980ae28b40c92c9c30bdbcf3f28064b40104472e213c52edbeb89b920d65fe")
        };

        RuntimeReleaseAsset? selected = RuntimeBuildCatalog.SelectAsset(
            "text-cuda", assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64);

        Assert.NotNull(selected);
        Assert.Equal("llama-b10549-bin-win-cuda-12.4-x64.zip", selected!.Name);
    }

    [Fact]
    public void SelectAsset_returns_nothing_when_release_only_has_cuda_dependency_bundle()
    {
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-12.4-x64.zip",
                "https://github.com/ggml-org/llama.cpp/releases/download/b10549/cudart-llama-bin-win-cuda-12.4-x64.zip",
                391443627,
                "sha256:8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6")
        };

        Assert.Null(RuntimeBuildCatalog.SelectAsset(
            "text-cuda", assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
    }

    [Fact]
    public void SelectAsset_still_selects_vulkan_server_bundle_when_cuda_dependency_bundle_is_present()
    {
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://example/cuda.zip", 1),
            new("llama-b10549-bin-win-vulkan-x64.zip", "https://example/vulkan.zip", 1)
        };

        RuntimeReleaseAsset? selected = RuntimeBuildCatalog.SelectAsset(
            "text-vulkan", assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64);

        Assert.NotNull(selected);
        Assert.Equal("llama-b10549-bin-win-vulkan-x64.zip", selected!.Name);
    }

    [Fact]
    public void SelectCudaDependencyAsset_requires_exact_cuda_version_and_platform_match()
    {
        var server = new RuntimeReleaseAsset(
            "llama-b10549-bin-win-cuda-12.4-x64.zip", "https://example/server.zip", 1);
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-13.4-x64.zip", "https://example/cuda13.zip", 1),
            new("cudart-llama-bin-win-cuda-12.4-arm64.zip", "https://example/arm.zip", 1),
            new("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://example/cuda12.zip", 1)
        };

        RuntimeReleaseAsset? selected = RuntimeBuildCatalog.SelectCudaDependencyAsset(
            server, assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64);

        Assert.NotNull(selected);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", selected!.Name);
        Assert.True(RuntimeBuildCatalog.HasCudaDependencyAssetForPlatform(
            assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
    }

    [Fact]
    public void SelectCudaDependencyAsset_does_not_pair_a_vulkan_server_archive()
    {
        var server = new RuntimeReleaseAsset(
            "llama-b10549-bin-win-vulkan-x64.zip", "https://example/server.zip", 1);
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://example/cuda.zip", 1)
        };

        Assert.Null(RuntimeBuildCatalog.SelectCudaDependencyAsset(
            server, assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
    }

    [Fact]
    public void HasCudaDependencyAssetForPlatform_flags_unmatched_variant_for_fail_closed_install()
    {
        var assets = new List<RuntimeReleaseAsset>
        {
            new("cudart-llama-bin-win-cuda-13.4-x64.zip", "https://example/cuda13.zip", 1)
        };

        Assert.Null(RuntimeBuildCatalog.SelectCudaDependencyAsset(
            new RuntimeReleaseAsset("llama-b10549-bin-win-cuda-12.4-x64.zip", "https://example/server.zip", 1),
            assets,
            RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
        Assert.True(RuntimeBuildCatalog.HasCudaDependencyAssetForPlatform(
            assets, RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
    }

    [Fact]
    public void SelectAsset_throws_for_an_unknown_backend()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCatalog.SelectAsset(
            "text-metal", new List<RuntimeReleaseAsset>(), RuntimeBuildCatalog.RuntimePlatform.WindowsX64));
    }

    /// <summary>Describe feeds the one message a user reads when an install finds nothing, so every
    /// supported platform needs its own name there; a value added to the enum and forgotten here
    /// would silently degrade that message to "this platform".</summary>
    [Fact]
    public void Describe_gives_every_supported_platform_its_own_name()
    {
        var named = Enum.GetValues<RuntimeBuildCatalog.RuntimePlatform>()
            .Where(p => p != RuntimeBuildCatalog.RuntimePlatform.Unsupported)
            .Select(RuntimeBuildCatalog.Describe)
            .ToList();

        Assert.DoesNotContain("this platform", named);
        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("this platform", RuntimeBuildCatalog.Describe(RuntimeBuildCatalog.RuntimePlatform.Unsupported));
    }

    /// <summary>CurrentPlatform is asked on every install, so it must answer rather than throw on
    /// whatever machine the suite happens to be running on.</summary>
    [Fact]
    public void CurrentPlatform_answers_on_this_machine()
    {
        Assert.True(Enum.IsDefined(RuntimeBuildCatalog.CurrentPlatform()));
    }

    /// <summary>InlineData carries the platform as a name rather than the enum itself: the enum
    /// is internal to Core, and a public test method cannot take an internal parameter type.</summary>
    private static RuntimeBuildCatalog.RuntimePlatform Platform(string name) =>
        Enum.Parse<RuntimeBuildCatalog.RuntimePlatform>(name);

    private static readonly List<RuntimeReleaseAsset> MixedPlatformAssets = new()
    {
        new("llama-b1-bin-win-cuda-cu12.4-x64.zip", "https://example/wc", 1),
        new("llama-b1-bin-win-vulkan-x64.zip", "https://example/wv", 1),
        new("llama-b1-bin-ubuntu-cuda-cu12.4-x64.tar.gz", "https://example/lc", 1),
        new("llama-b1-bin-ubuntu-vulkan-x64.tar.gz", "https://example/lv", 1),
        new("llama-b1-bin-ubuntu-x64.tar.gz", "https://example/lcpu", 1),
        new("llama-b1-bin-macos-vulkan-arm64.zip", "https://example/ma", 1)
    };
}
