using PaRuX.Server;
using PaRuX.Contracts;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeAdmissionMemoryTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Busy_igpu_host_cannot_spend_total_ram_on_model_weights()
    {
        double available = RuntimeManagerService.EstimateAdmissionMemoryMib(0, 26 * GiB, 50, 86, 0);
        Assert.Equal(26 * 1024 * .36, available, 5);
        Assert.True(available < 14 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_role_model_path_is_not_replaced_by_the_hardware_fallback(bool hasHeavyGpu)
    {
        const string selectedProfileModel = "configured-profile.gguf";
        const string requestedModelPath = "explicit-request.gguf";

        Assert.Equal(requestedModelPath, LocalAiProcessManager.ResolveTextModelPath(
            selectedProfileModel, hasHeavyGpu, requestedModelPath));
    }

    [Fact]
    public void Normal_profile_keeps_the_small_fallback_without_a_heavy_gpu()
    {
        Assert.Equal(LocalAiPaths.SmallModel, LocalAiProcessManager.ResolveTextModelPath(
            "configured-profile.gguf", hasHeavyGpu: false, requestedModelPath: null));
        Assert.Equal("configured-profile.gguf", LocalAiProcessManager.ResolveTextModelPath(
            "configured-profile.gguf", hasHeavyGpu: true, requestedModelPath: null));
    }

    [Theory]
    [InlineData("standard", "Qwen3.8-27B-UD-IQ4_XS.gguf", "mmproj-Qwen3.8-27B-Q8_0.gguf")]
    [InlineData("uncensored", "Qwen3.8-27B-Uncensored-noMTP-IQ4_XS.gguf", "Qwen3.8-27B-Uncensored-vision-f16.gguf")]
    public void Bundled_projector_follows_the_explicit_model_path_not_the_saved_role_override(
        string role, string modelFile, string projectorFile)
    {
        string modelPath = Path.Combine(LocalAiPaths.ModelsPath, modelFile);
        string? projector = LocalAiPaths.GetBundledVisionProjectorForModel(role, modelPath);

        Assert.Equal(Path.Combine(LocalAiPaths.ModelsPath, "multimodal", projectorFile), projector);
        Assert.Null(LocalAiPaths.GetBundledVisionProjectorForModel(role, "custom-model.gguf"));
        Assert.Null(LocalAiPaths.GetBundledVisionProjectorForModel(
            role == "standard" ? "uncensored" : "standard", modelPath));
    }

    [Fact]
    public void Swap_reclaims_only_the_known_resident_working_set()
    {
        double available = RuntimeManagerService.EstimateAdmissionMemoryMib(0, 26 * GiB, 96.5, 86, 14 * GiB);
        Assert.Equal((14 - 26 * .105) * 1024, available, 5);
        Assert.True(available > 4 * 1024);
        Assert.True(available < 14 * 1024);
    }

    [Fact]
    public void Exhausted_memory_is_zero_and_reclaim_cannot_exceed_budget()
    {
        Assert.Equal(0, RuntimeManagerService.EstimateAdmissionMemoryMib(0, 26 * GiB, 96, 86, 0));
        Assert.Equal(26 * 1024 * .86, RuntimeManagerService.EstimateAdmissionMemoryMib(0, 26 * GiB, 20, 86, 100 * GiB), 5);
    }

    [Fact]
    public void Live_available_RAM_caps_admission_monotonically_below_the_profile_ceiling()
    {
        var highPressure = new SystemRamSnapshot(32UL * (ulong)GiB, 4UL * (ulong)GiB);
        var lowPressure = new SystemRamSnapshot(32UL * (ulong)GiB, 20UL * (ulong)GiB);

        long highBudget = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(highPressure, 86);
        long lowerBudget = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(lowPressure, 86);

        // 28 GiB of 32 is already held by other applications, past the 86% ceiling: nothing may be admitted.
        Assert.Equal(0, highBudget);
        // 12 GiB is held elsewhere: the ceiling leaves 86% of 32 GiB minus that 12 GiB, which is below the 20 GiB free.
        Assert.Equal(32L * GiB * 86 / 100 - 12 * GiB, lowerBudget);
        Assert.True(highBudget < lowerBudget);
    }

    [Fact]
    public void Memory_held_by_other_applications_is_taken_off_the_profile_ceiling()
    {
        // 26 GiB host, half of it held by other applications, 86% ceiling. Only about 9.36 GiB may be
        // admitted. The 13 GiB that is merely free would end at 100% against an 86% ceiling.
        long budget = RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            new SystemRamSnapshot(26UL * (ulong)GiB, 13UL * (ulong)GiB), 86);

        Assert.Equal(26L * GiB * 86 / 100 - 13 * GiB, budget);
        Assert.True(budget < 10 * GiB);
    }

    [Fact]
    public void Current_RAM_reclaim_is_limited_to_total_and_profile_policy()
    {
        var snapshot = new SystemRamSnapshot(26UL * (ulong)GiB, 3UL * (ulong)GiB);

        // The resident working set is reclaimed, so 9 GiB of the 26 GiB is held by others; the ceiling leaves
        // 86% of 26 GiB minus those 9 GiB, which is below the 17 GiB that would be free after the swap.
        Assert.Equal(26L * GiB * 86 / 100 - 9 * GiB, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(snapshot, 86, 14 * GiB));
        Assert.Equal((long)(26 * GiB * .5), RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(snapshot, 50, 100 * GiB));
    }

    [Fact]
    public async Task Runtime_model_admission_uses_current_selected_GPU_free_memory_not_its_static_total()
    {
        string oldDataRoot = PaRuXDataRoot.Path;
        string testRoot = Path.Combine(Path.GetTempPath(), "parux-gpu-admission-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(testRoot, "data");
        string localAiRoot = Path.Combine(testRoot, "localai");
        string modelPath = Path.Combine(dataRoot, "models", "gpu-admission.gguf");

        try
        {
            PaRuXDataRoot.UseForTests(dataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            WriteMinimalGguf(modelPath);
            using IDisposable localAiRootScope = LocalAiPaths.UseRootForTests(localAiRoot);

            var hardware = new RuntimeHardwareProfile(
                GpuPresent: true,
                AcceleratorEligible: true,
                Backend: "text-cuda",
                Vendor: "nvidia",
                AdapterName: "NVIDIA GeForce RTX 5080",
                DedicatedVideoMemoryBytes: 16 * GiB,
                SharedSystemMemoryLimitBytes: 32 * GiB,
                SystemRamBytes: 64 * GiB,
                SharedMemoryPrimary: false,
                InventorySource: "test-dxgi");
            // 24 GiB of 64 is held by other applications. Under the 86% ceiling that leaves 31.04 GiB for RAM weights.
            var ram = new SystemRamSnapshot(64UL * (ulong)GiB, 40UL * (ulong)GiB);
            double ramBudgetMib = (64L * GiB * 86 / 100 - 24 * GiB) / (1024d * 1024);
            DateTime now = DateTime.UtcNow;
            var liveGpu = new GpuPerformanceTelemetrySnapshot(now, new[]
            {
                new PaRuX.Contracts.GpuPerformanceTelemetryDto
                {
                    Name = "NVIDIA GeForce RTX 5080",
                    MemoryTotalMib = 16384,
                    MemoryUsedMib = 12288,
                    MemoryFreeMib = 4096,
                    SampledUtc = now
                }
            });
            int liveQueries = 0;
            var liveProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
            {
                Interlocked.Increment(ref liveQueries);
                return Task.FromResult(liveGpu.Devices.ToList());
            });
            var runtime = new RuntimeManagerService(new LocalAiProcessManager(),
                new SequenceRamSnapshotProvider(ram), liveProvider);

            HardwareFitEstimate? fit = await runtime.EstimateHardwareFitForAdmissionAsync(
                modelPath, hardware, ram, CancellationToken.None);

            Assert.NotNull(fit);
            Assert.Equal(4 * 1024 + ramBudgetMib, fit.AvailableMib, precision: 3);
            Assert.Equal(1, liveQueries);

            var staleGpu = liveGpu with
            {
                ObservedUtc = DateTime.UtcNow - TimeSpan.FromSeconds(10),
                Devices = liveGpu.Devices.Select(device => new PaRuX.Contracts.GpuPerformanceTelemetryDto
                {
                    Name = device.Name,
                    MemoryTotalMib = device.MemoryTotalMib,
                    MemoryFreeMib = device.MemoryFreeMib,
                    SampledUtc = now - TimeSpan.FromSeconds(10)
                }).ToArray()
            };
            var staleProvider = new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
                Task.FromResult(staleGpu.Devices.ToList()), () => now);
            var staleRuntime = new RuntimeManagerService(new LocalAiProcessManager(),
                new SequenceRamSnapshotProvider(ram), staleProvider);
            HardwareFitEstimate? staleFit = await staleRuntime.EstimateHardwareFitForAdmissionAsync(
                modelPath, hardware, ram, CancellationToken.None);
            Assert.NotNull(staleFit);
            Assert.Equal(ramBudgetMib, staleFit.AvailableMib, precision: 3);
        }
        finally
        {
            PaRuXDataRoot.UseForTests(oldDataRoot);
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Admission_and_launch_tier_use_nvidia_smi_when_dxgi_reports_more_for_nvidia()
    {
        string localAiRoot = Path.Combine(Path.GetTempPath(), "parux-admission-tier-" + Guid.NewGuid().ToString("N"));
        try
        {
            using IDisposable localAiRootScope = LocalAiPaths.UseRootForTests(localAiRoot);
            GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory(
                GpuInventoryFixtures.Nvidia(16 * GiB));
            const int nvidiaSmiMib = 8_000;

            TextLaunchTier launchTier = TextLaunchTierSelector.Select(inventory, nvidiaSmiMib);
            RuntimeHardwareProfile admission = RuntimeManagerService.ResolveAdmissionHardwareProfile(
                inventory, nvidiaSmiMib, 64 * GiB);

            Assert.Equal(nvidiaSmiMib, launchTier.VramMib);
            Assert.Equal(TextLaunchTierSelector.NvidiaSmiSource, launchTier.VramSource);
            Assert.Equal(launchTier.VramMib * 1024L * 1024L, admission.DedicatedVideoMemoryBytes);
        }
        finally
        {
            try { Directory.Delete(localAiRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Admission_backend_inventory_checks_the_runtime_executable_not_its_directory()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "parux-admission-backend-" + Guid.NewGuid().ToString("N"));
        try
        {
            using IDisposable localAiRoot = LocalAiPaths.UseRootForTests(testRoot);
            string cudaExecutable = Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable);
            string vulkanExecutable = Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable);
            Directory.CreateDirectory(Path.GetDirectoryName(cudaExecutable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(vulkanExecutable)!);
            File.WriteAllText(cudaExecutable, "test");

            RuntimeManagerStatus status = RuntimeManagerService.BuildAdmissionHardwareStatus(16384, 64 * GiB);

            Assert.True(status.Backends.Single(backend => backend.Backend == "text-cuda").Installed);
            Assert.False(status.Backends.Single(backend => backend.Backend == "text-vulkan").Installed);
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Unknown_or_inconsistent_RAM_telemetry_fails_closed()
    {
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(null, 86));
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(new SystemRamSnapshot(0, 0), 86));
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            new SystemRamSnapshot(8UL * (ulong)GiB, 9UL * (ulong)GiB), 86));
        Assert.Equal(0, RuntimeMemoryBudget.CalculateAvailableSystemRamBytes(
            new SystemRamSnapshot(8UL * (ulong)GiB, 7UL * (ulong)GiB), double.NaN));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(8UL, 9UL)]
    [InlineData(ulong.MaxValue, 0UL)]
    public void Invalid_OS_RAM_samples_are_rejected_without_normalizing_them(ulong total, ulong available)
    {
        Assert.Null(SystemRamSnapshot.TryCreate(total, available));
    }

    [Fact]
    public async Task Arbitrary_model_start_entry_point_requires_the_exclusive_text_lease_before_probing_or_launching()
    {
        var snapshots = new SequenceRamSnapshotProvider(new SystemRamSnapshot(16UL * (ulong)GiB, 8UL * (ulong)GiB));
        var process = new LocalAiProcessManager();
        using IDisposable vram = process.OverrideVramMibForTests(0);
        var runtime = new RuntimeManagerService(process, snapshots);

        RuntimeLoadResult result = await runtime.StartArbitraryTextModelWithinLeaseAsync(
            transition: null,
            path: Path.Combine(Path.GetTempPath(), "missing-model-" + Guid.NewGuid().ToString("N") + ".gguf"),
            label: "test-model", profilePrefix: "Compare: ", adapterPaths: null, role: null,
            persistRoleAssignment: false, ct: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("this caller's active exclusive text-runtime lease", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, snapshots.Calls);

        var foreignCoordinator = new MediaExecutionCoordinator();
        await using MediaExecutionCoordinator.Lease foreignLease = await foreignCoordinator
            .AcquireAsync("text-exclusive:foreign", CancellationToken.None);
        RuntimeLoadResult foreignResult = await runtime.StartArbitraryTextModelWithinLeaseAsync(
            transition: foreignLease,
            path: Path.Combine(Path.GetTempPath(), "missing-model-" + Guid.NewGuid().ToString("N") + ".gguf"),
            label: "test-model", profilePrefix: "Compare: ", adapterPaths: null, role: null,
            persistRoleAssignment: false, ct: CancellationToken.None);

        Assert.False(foreignResult.Success);
        Assert.Contains("this caller's active exclusive text-runtime lease", foreignResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, snapshots.Calls);
    }

    [Fact]
    public async Task Role_load_rechecks_live_RAM_after_waiting_and_does_not_persist_or_launch_when_pressure_changes()
    {
        string originalDataRoot = PaRuXDataRoot.Path;
        string testRoot = Path.Combine(Path.GetTempPath(), "parux-runtime-admission-" + Guid.NewGuid().ToString("N"));
        string isolatedDataRoot = Path.Combine(testRoot, "data");
        string isolatedLocalAiRoot = Path.Combine(testRoot, "localai");
        string modelName = "admission-change-" + Guid.NewGuid().ToString("N") + ".gguf";
        string modelPath = Path.Combine(isolatedDataRoot, "models", modelName);

        try
        {
            PaRuXDataRoot.UseForTests(isolatedDataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            WriteMinimalGguf(modelPath);
            using IDisposable localAiRoot = LocalAiPaths.UseRootForTests(isolatedLocalAiRoot);
            using IDisposable textPort = LocalAiPaths.UseTextPortForTests(GetUnusedLoopbackPort());
            var snapshots = new SequenceRamSnapshotProvider(
                new SystemRamSnapshot(16UL * (ulong)GiB, 8UL * (ulong)GiB),
                new SystemRamSnapshot(16UL * (ulong)GiB, 0));
            var process = new LocalAiProcessManager();
            using IDisposable vram = process.OverrideVramMibForTests(0);
            var runtime = new RuntimeManagerService(process, snapshots);

            RuntimeLoadResult result = await runtime.LoadRoleAsync(
                modelName, "fast", persistRoleAssignment: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(result.Success);
            Assert.Contains("incompatible", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, snapshots.Calls);
            Assert.Null(LocalAiPaths.GetRoleOverride("fast"));
            Assert.False(File.Exists(LocalAiPaths.TextPidFile));
        }
        finally
        {
            PaRuXDataRoot.UseForTests(originalDataRoot);
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private sealed class SequenceRamSnapshotProvider(params SystemRamSnapshot?[] snapshots) : ISystemRamSnapshotProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public SystemRamSnapshot? TryGetSnapshot()
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            return index < snapshots.Length ? snapshots[index] : snapshots[^1];
        }
    }

    private static void WriteMinimalGguf(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("GGUF"u8);
        writer.Write(3u);
        writer.Write(0UL); // tensor count
        writer.Write(0UL); // metadata key/value count
    }

    private static int GetUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
