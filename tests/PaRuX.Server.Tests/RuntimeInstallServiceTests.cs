using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// RuntimeInstallService's HTTP calls are mocked. Filesystem coverage exercises a complete isolated
/// managed-runtime root, proving the authenticated download -> bounded extraction -> swap ->
/// activation path without adopting or mutating a developer's real runtime.
/// </summary>
public sealed class RuntimeInstallServiceTests : IDisposable
{
    /// <summary>
    /// The backend this machine would actually execute, not a hardcoded "text-cuda".
    /// RuntimeUpdateRegressionGuard deliberately refuses to auto-promote a build for a backend the
    /// local hardware would not run - a CUDA build cannot be performance-compared on a box with no
    /// heavy GPU - so pinning "text-cuda" made the promotion assertions below unreachable on any
    /// machine without a >=12GB NVIDIA card: the guard correctly returned "deferred" and the tests
    /// failed for an environment reason rather than a product one. Resolving it the same way the
    /// guard does keeps these tests exercising the real download -> extract -> swap -> promote path
    /// on CUDA and Vulkan hardware alike.
    /// </summary>
    private static readonly string Backend =
        new LocalAiProcessManager().HasHeavyGpuAsync(CancellationToken.None).GetAwaiter().GetResult()
            ? "text-cuda"
            : "text-vulkan";

    private readonly string _testTag = "b-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _isolatedRoot = Path.Combine(
        Path.GetTempPath(), "parux-runtime-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _isolatedStateDir = Path.Combine(
        Path.GetTempPath(), "parux-runtime-install-state-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;
    private readonly IDisposable _stateDirScope;
    private readonly string? _originalActiveTag;
    private readonly string? _originalCudaActiveTag;

    public RuntimeInstallServiceTests()
    {
        _rootScope = LocalAiPaths.UseRootForTests(_isolatedRoot);
        _stateDirScope = LocalAiPaths.UseStateDirForTests(_isolatedStateDir);
        _originalActiveTag = LocalAiPaths.GetActiveRuntimeBuildTag(Backend);
        _originalCudaActiveTag = Backend == "text-cuda" ? _originalActiveTag : LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda");
    }

    public void Dispose()
    {
        string folder = Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(Backend, _testTag));
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }

        if (_originalActiveTag == null) LocalAiPaths.ClearActiveRuntimeBuildTag(Backend);
        else if (LocalAiPaths.ListInstalledRuntimeBuildTags(Backend).Contains(_originalActiveTag))
            LocalAiPaths.SetActiveRuntimeBuildTag(Backend, _originalActiveTag);
        if (Backend != "text-cuda")
        {
            string cudaFolder = Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName("text-cuda", _testTag));
            try { if (Directory.Exists(cudaFolder)) Directory.Delete(cudaFolder, recursive: true); } catch { }
            if (_originalCudaActiveTag == null) LocalAiPaths.ClearActiveRuntimeBuildTag("text-cuda");
            else if (LocalAiPaths.ListInstalledRuntimeBuildTags("text-cuda").Contains(_originalCudaActiveTag))
                LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", _originalCudaActiveTag);
        }
        _rootScope.Dispose();
        _stateDirScope.Dispose();
        try { Directory.Delete(_isolatedRoot, recursive: true); } catch { }
        try { Directory.Delete(_isolatedStateDir, recursive: true); } catch { }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real SocketsHttpHandler pipeline always populates response.RequestMessage with
            // the final (post-redirect) request; RuntimeArtifactSecurityPolicy.ValidateDownloadResponse
            // relies on that to confirm downloads didn't get redirected off GitHub. This stub must
            // replicate that or every response looks like an unexplained redirect to that check.
            HttpResponseMessage response = await OnSend(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    /// <summary>Deterministic, instant stand-in for the real llama-bench probe. A developer machine
    /// with a genuine LocalAI setup (real installed "text-cuda" runtime + real GGUF models under
    /// its Documents\LocalAI root - exactly this repository's own working environment) makes
    /// RuntimeUpdateRegressionGuard.EvaluateAsync take its real-comparison path instead of the
    /// "first install" fast path these tests assume, which would otherwise try to launch the real
    /// llama-server.exe against a real model and hang far past any reasonable test timeout. The
    /// guard always measures the original tag first and the candidate tag second (never reordered),
    /// so a call counter deterministically makes the freshly-installed candidate measure faster and
    /// get promoted, matching what these tests assert, without touching any real GPU/model.</summary>
    private sealed class FakeLlamaPerformanceProbe : ILlamaPerformanceProbe
    {
        private int _calls;
        public Task<LlamaPerformanceProbeMeasurement> RunAsync(
            string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken)
        {
            double decode = Interlocked.Increment(ref _calls) == 1 ? 10.0 : 100.0;
            return Task.FromResult(new LlamaPerformanceProbeMeasurement(decode, decode, "text-cuda", "test-hardware", 0));
        }
    }

    private static RuntimeInstallService CreateService(HttpMessageHandler handler)
    {
        var process = new LocalAiProcessManager();
        var guard = new RuntimeUpdateRegressionGuard(
            process,
            new FakeLlamaPerformanceProbe(),
            new RuntimeRegressionEvidenceStore(Path.Combine(
                Path.GetTempPath(), "parux-regression-evidence-" + Guid.NewGuid().ToString("N") + ".json")));
        return new RuntimeInstallService(process, new HttpClient(handler), new GpuInventoryService(), new ComfyRuntimeStore(), guard);
    }

    private static byte[] BuildFakeReleaseZip(string innerFolder = "")
    {
        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Named for the platform this test is running on, because that is what the real archive
            // for this platform contains - the install pipeline looks for the binary it would
            // actually be installing, not for the Windows spelling of it.
            string binary = LocalAiPaths.LlamaServerExecutable;
            string entryName = string.IsNullOrEmpty(innerFolder) ? binary : innerFolder + "/" + binary;
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using Stream s = entry.Open();
            using var writer = new StreamWriter(s);
            writer.Write("fake binary content");
        }
        return mem.ToArray();
    }

    private static byte[] BuildFakeCudaDependencyZip()
    {
        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in new[] { "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll" })
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.WriteByte(1);
            }
        }
        return mem.ToArray();
    }

    private static string CudaDependencyName(string serverAssetName)
    {
        int marker = serverAssetName.IndexOf("-bin-", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) throw new InvalidOperationException("Unexpected llama.cpp asset name: " + serverAssetName);
        return "cudart-llama-bin-" + serverAssetName[(marker + "-bin-".Length)..];
    }

    private static string AssetUrl(string tag, string assetName) =>
        "https://github.com/ggml-org/llama.cpp/releases/download/" + tag + "/" + assetName;

    /// <summary>Mirrors the real GitHub asset naming for whichever backend <see cref="Backend"/>
    /// resolved to <em>and</em> for the platform the suite is running on, so SelectAsset finds it on
    /// a CUDA host, a Vulkan host, Windows and Linux alike.</summary>
    private static string AssetName(string tag) => PlatformAssetNames.For(tag, Backend);
    private static string AssetUrl(string tag) => PlatformAssetNames.UrlFor(tag, Backend);
    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseByTagJson(string tag, byte[] bytes) => $$"""
    {
      "tag_name": "{{tag}}",
      "draft": false,
      "prerelease": false,
      "published_at": "2026-08-01T00:00:00Z",
      "assets": [
        {
          "name": "{{AssetName(tag)}}",
          "browser_download_url": "{{AssetUrl(tag)}}",
          "size": {{bytes.LongLength}},
          "digest": "{{Digest(bytes)}}"
        }
      ]
    }
    """;

    private static string ReleaseWithCudaDependenciesJson(
        string tag,
        byte[] serverBytes,
        byte[] dependencyBytes,
        string? dependencyDigestOverride = null,
        string? dependencyAssetNameOverride = null)
    {
        string serverName = PlatformAssetNames.For(tag, "text-cuda");
        string dependencyName = dependencyAssetNameOverride ?? CudaDependencyName(serverName);
        return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-08-01T00:00:00Z",
          "assets": [
            { "name": "{{serverName}}", "browser_download_url": "{{AssetUrl(tag, serverName)}}", "size": {{serverBytes.LongLength}}, "digest": "{{Digest(serverBytes)}}" },
            { "name": "{{dependencyName}}", "browser_download_url": "{{AssetUrl(tag, dependencyName)}}", "size": {{dependencyBytes.LongLength}}, "digest": "{{dependencyDigestOverride ?? Digest(dependencyBytes)}}" }
          ]
        }
        """;
    }

    private static string ListReleasesJson(string tag) => $$"""
    [
      {
        "tag_name": "{{tag}}",
        "draft": false,
        "prerelease": false,
        "published_at": "2026-08-01T00:00:00Z",
        "assets": [
          {
            "name": "{{AssetName(tag)}}",
            "browser_download_url": "{{AssetUrl(tag)}}",
            "size": 20,
            "digest": "sha256:{{new string('a', 64)}}"
          }
        ]
      }
    ]
    """;

    private static string PrereleaseOnlyPageJson(int count = 100)
    {
        string[] releases = Enumerable.Range(1, count)
            .Select(index => $$"""{"tag_name":"nightly-{{index}}","draft":false,"prerelease":true,"assets":[]}""")
            .ToArray();
        return "[" + string.Join(",", releases) + "]";
    }

    private static async Task<RuntimeInstallStatus> WaitForTerminalAsync(RuntimeInstallService service, string id, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            RuntimeInstallStatus? status = service.GetJob(id);
            if (status != null && status.Stage is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Install job did not reach a terminal state within " + timeout);
    }

    private static async Task WaitForFileMissingAsync(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && File.Exists(path)) await Task.Delay(20);
        Assert.False(File.Exists(path), "Expected installer cleanup to remove " + path);
    }

    [Fact]
    public void CreateInstall_rejects_missing_backend_or_tag_before_any_work()
    {
        var service = CreateService(new FakeHandler());
        Assert.Throws<InvalidDataException>(() => service.CreateInstall(new RuntimeInstallRequest { Backend = "", Tag = "b1" }));
        Assert.Throws<InvalidDataException>(() => service.CreateInstall(new RuntimeInstallRequest { Backend = "text-cuda", Tag = "" }));
    }

    [Fact]
    public void CreateInstall_rejects_an_unknown_backend_before_any_work()
    {
        var service = CreateService(new FakeHandler());
        Assert.Throws<InvalidDataException>(() => service.CreateInstall(new RuntimeInstallRequest { Backend = "text-metal", Tag = "b1" }));
    }

    [Fact]
    public void Rollback_rejects_a_tag_that_isnt_installed()
    {
        var service = CreateService(new FakeHandler());
        string neverInstalled = "b-never-" + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidDataException>(() => service.Rollback(new RuntimeRollbackRequest { Backend = Backend, Tag = neverInstalled }));
    }

    [Fact]
    public async Task ListAvailableAsync_parses_the_real_response_shape_and_marks_installed_active_flags()
    {
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                Assert.Contains("api.github.com/repos/ggml-org/llama.cpp/releases", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ListReleasesJson(_testTag))
                });
            }
        };
        var service = CreateService(handler);

        List<RuntimeBuildOption> options = await service.ListAvailableAsync(Backend, CancellationToken.None);

        RuntimeBuildOption option = Assert.Single(options);
        Assert.Equal(_testTag, option.Tag);
        Assert.False(option.InstalledLocally);
        Assert.False(option.IsActive);
    }

    [Fact]
    public async Task ListAvailableAsync_pages_past_full_prerelease_page_to_find_stable_builds()
    {
        int requests = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                requests++;
                Assert.Contains("per_page=100", req.RequestUri!.Query);
                if (req.RequestUri.Query.EndsWith("page=1", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(PrereleaseOnlyPageJson())
                    });
                Assert.Contains("page=2", req.RequestUri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ListReleasesJson(_testTag))
                });
            }
        };
        var service = CreateService(handler);

        List<RuntimeBuildOption> options = await service.ListAvailableAsync(Backend, CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.Single(options);
        Assert.Equal(_testTag, options[0].Tag);
    }

    [Fact]
    public async Task ListAvailableAsync_stops_after_bounded_release_pages()
    {
        int requests = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                requests++;
                Assert.Contains("per_page=100", req.RequestUri!.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PrereleaseOnlyPageJson())
                });
            }
        };
        var service = CreateService(handler);

        List<RuntimeBuildOption> options = await service.ListAvailableAsync(Backend, CancellationToken.None);

        Assert.Empty(options);
        Assert.Equal(8, requests);
    }

    [Fact]
    public async Task ListAvailableAsync_observes_cancellation_between_release_pages()
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                requests++;
                cancellation.Cancel();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PrereleaseOnlyPageJson())
                });
            }
        };
        var service = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAvailableAsync(Backend, cancellation.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Full_install_pipeline_downloads_verifies_extracts_swaps_and_activates_a_real_build()
    {
        byte[] zipBytes = BuildFakeReleaseZip();
        string assetUrl = AssetUrl(_testTag);
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ReleaseByTagJson(_testTag, zipBytes))
                    });
                }
                if (url == assetUrl)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zipBytes)
                    });
                }
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };
        var service = CreateService(handler);

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _testTag });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", final.Stage);
        Assert.Null(final.Error);
        Assert.Contains(_testTag, LocalAiPaths.ListInstalledRuntimeBuildTags(Backend));
        Assert.Equal(_testTag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
        // The resolved runtime path for the backend under test - the point of the assertion is that
        // activation actually repoints the path the launcher will use, so it must follow Backend
        // rather than always reading the CUDA path.
        string resolvedPath = Backend == "text-cuda" ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
        Assert.EndsWith(LocalAiPaths.RuntimeFolderName(Backend, _testTag), resolvedPath);
        Assert.True(File.Exists(Path.Combine(resolvedPath, LocalAiPaths.LlamaServerExecutable)));
    }

    [Fact]
    public async Task Cuda_install_downloads_and_extracts_matching_dependency_bundle()
    {
        byte[] serverBytes = BuildFakeReleaseZip("nested/server");
        byte[] dependencyBytes = BuildFakeCudaDependencyZip();
        string serverName = PlatformAssetNames.For(_testTag, "text-cuda");
        string dependencyName = CudaDependencyName(serverName);
        string serverUrl = AssetUrl(_testTag, serverName);
        string dependencyUrl = AssetUrl(_testTag, dependencyName);
        int serverDownloads = 0;
        int dependencyDownloads = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ReleaseWithCudaDependenciesJson(_testTag, serverBytes, dependencyBytes))
                    });
                if (url == serverUrl)
                {
                    serverDownloads++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(serverBytes)
                    });
                }
                if (url == dependencyUrl)
                {
                    dependencyDownloads++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(dependencyBytes)
                    });
                }
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };
        var service = CreateService(handler);

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = "text-cuda", Tag = _testTag });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", final.Stage);
        Assert.Equal(serverBytes.LongLength + dependencyBytes.LongLength, final.TotalBytes);
        Assert.Equal(serverBytes.LongLength + dependencyBytes.LongLength, final.BytesDownloaded);
        Assert.Equal(1, serverDownloads);
        Assert.Equal(1, dependencyDownloads);
        string installed = Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName("text-cuda", _testTag));
        Assert.True(File.Exists(Path.Combine(installed, LocalAiPaths.LlamaServerExecutable)));
        Assert.True(File.Exists(Path.Combine(installed, "cudart64_12.dll")));
        Assert.True(File.Exists(Path.Combine(installed, "cublas64_12.dll")));
        Assert.True(File.Exists(Path.Combine(installed, "cublasLt64_12.dll")));
    }

    [Fact]
    public async Task Cuda_install_rejects_dependency_bundle_with_wrong_digest_before_activation()
    {
        byte[] serverBytes = BuildFakeReleaseZip();
        byte[] dependencyBytes = BuildFakeCudaDependencyZip();
        string serverName = PlatformAssetNames.For(_testTag, "text-cuda");
        string dependencyName = CudaDependencyName(serverName);
        string serverUrl = AssetUrl(_testTag, serverName);
        string dependencyUrl = AssetUrl(_testTag, dependencyName);
        string wrongDigest = "sha256:" + new string('0', 64);
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ReleaseWithCudaDependenciesJson(
                            _testTag, serverBytes, dependencyBytes, dependencyDigestOverride: wrongDigest))
                    });
                if (url == serverUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(serverBytes)
                    });
                if (url == dependencyUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(dependencyBytes)
                    });
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };
        var service = CreateService(handler);

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = "text-cuda", Tag = _testTag });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("failed", final.Stage);
        Assert.Contains("integrity", final.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(
            LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName("text-cuda", _testTag))));
        await WaitForFileMissingAsync(
            Path.Combine(LocalAiPaths.RuntimeRoot, ".staging-" + created.Id + ".cudart.zip"),
            TestTimeouts.JobCompletion);
        await WaitForFileMissingAsync(
            Path.Combine(LocalAiPaths.RuntimeRoot, ".staging-" + created.Id + ".zip"),
            TestTimeouts.JobCompletion);
    }

    [Fact]
    public async Task Cuda_install_rejects_wrong_platform_cuda_version_when_dependency_bundle_is_advertised()
    {
        byte[] serverBytes = BuildFakeReleaseZip();
        byte[] dependencyBytes = BuildFakeCudaDependencyZip();
        string serverName = PlatformAssetNames.For(_testTag, "text-cuda");
        string matchingName = CudaDependencyName(serverName);
        string wrongName = matchingName.Replace("12.4", "13.4", StringComparison.Ordinal);
        int assetDownloads = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ReleaseWithCudaDependenciesJson(
                            _testTag, serverBytes, dependencyBytes, dependencyAssetNameOverride: wrongName))
                    });
                assetDownloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(serverBytes)
                });
            }
        };
        var service = CreateService(handler);

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = "text-cuda", Tag = _testTag });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("failed", final.Stage);
        Assert.Contains("matches the selected server CUDA version", final.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, assetDownloads);
        Assert.False(Directory.Exists(Path.Combine(
            LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName("text-cuda", _testTag))));
    }

    [Fact]
    public async Task CreateInstall_for_an_already_installed_tag_makes_zero_network_calls()
    {
        byte[] zipBytes = BuildFakeReleaseZip();
        string assetUrl = AssetUrl(_testTag);
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseByTagJson(_testTag, zipBytes)) });
                if (url == assetUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };
        var service = CreateService(handler);
        RuntimeInstallStatus firstInstall = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _testTag });
        RuntimeInstallStatus firstFinal = await WaitForTerminalAsync(service, firstInstall.Id, TestTimeouts.JobCompletion);
        Assert.Equal("completed", firstFinal.Stage);

        service.ResetToDefault(Backend);
        Assert.NotEqual(_testTag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(Backend));

        var noNetworkHandler = new FakeHandler
        {
            OnSend = (req, _) => throw new InvalidOperationException("Must not call the network for an already-installed tag: " + req.RequestUri)
        };
        var secondService = CreateService(noNetworkHandler);
        RuntimeInstallStatus secondInstall = secondService.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _testTag });
        RuntimeInstallStatus secondFinal = await WaitForTerminalAsync(secondService, secondInstall.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", secondFinal.Stage);
        Assert.Null(secondFinal.Error);
        Assert.Equal(_testTag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
    }

    [Fact]
    public async Task Rollback_after_install_switches_back_to_the_original_default_with_no_network_call()
    {
        byte[] zipBytes = BuildFakeReleaseZip();
        string assetUrl = AssetUrl(_testTag);
        var handler = new FakeHandler
        {
            OnSend = (req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/releases/tags/"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseByTagJson(_testTag, zipBytes)) });
                if (url == assetUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };
        var service = CreateService(handler);
        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _testTag });
        RuntimeInstallStatus installed = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);
        Assert.Equal("completed", installed.Stage);
        Assert.Equal(_testTag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));

        var noNetworkHandler = new FakeHandler { OnSend = (_, _) => throw new InvalidOperationException("Rollback must not call the network.") };
        var rollbackService = CreateService(noNetworkHandler);
        string resetTarget = _originalActiveTag ?? LocalAiPaths.DefaultRuntimeBuildTag(Backend);
        if (LocalAiPaths.ListInstalledRuntimeBuildTags(Backend).Contains(resetTarget))
        {
            rollbackService.Rollback(new RuntimeRollbackRequest { Backend = Backend, Tag = resetTarget });
            Assert.Equal(resetTarget, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
        }
        else
        {
            rollbackService.ResetToDefault(Backend);
            Assert.Null(LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
        }
    }
}
