using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GpuFingerprintIdentityTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly HostEvaluationFacts Host = new("cpu-test", 16, "10.0.1");
    private readonly string _root = Directory.CreateTempSubdirectory("parux-gpu-fingerprint-").FullName;

    [Fact]
    public void Shared_adapter_returns_the_versioned_builder_key()
    {
        GpuInventoryStatus inventory = Inventory(Adapter());
        EvaluationHardwareAttestation expected = EvaluationHardwareFingerprint.Build(inventory, Host);

        string actual = RuntimePerformanceTuner.BuildGpuFingerprint(inventory, Host);
        string productionKey = RuntimePerformanceTuner.BuildGpuFingerprint(inventory);
        string productionExpected = EvaluationHardwareFingerprint.Build(
            inventory, HostEvaluationFacts.Current()).Fingerprint;

        Assert.True(expected.Complete);
        Assert.Equal(expected.Fingerprint, actual);
        Assert.Equal(productionExpected, productionKey);
        Assert.StartsWith("hw2-sha256:", actual, StringComparison.Ordinal);
        Assert.True(EvaluationHardwareFingerprint.IsCurrentVersion(actual));
    }

    [Fact]
    public void Identical_inventory_has_the_same_key_across_shared_builds()
    {
        GpuInventoryStatus first = Inventory(Adapter(), Adapter(0x1002, 0x15bf, 8 * GiB, "31.0.1"));
        GpuInventoryStatus same = Inventory(Adapter(0x1002, 0x15bf, 8 * GiB, "31.0.1"), Adapter());

        string firstKey = RuntimePerformanceTuner.BuildGpuFingerprint(first, Host);
        string sameKey = RuntimePerformanceTuner.BuildGpuFingerprint(same, Host);

        Assert.Equal(firstKey, sameKey);
        Assert.Equal(firstKey, EvaluationHardwareFingerprint.Build(same, Host).Fingerprint);
        Assert.Equal(
            RuntimePerformanceTuner.BuildGpuFingerprint(first),
            RuntimePerformanceTuner.BuildGpuFingerprint(same));
    }

    [Fact]
    public void Adapter_name_and_enumeration_index_do_not_change_the_shared_key()
    {
        // Names and enumeration indices are labels, excluded from the identity by design.
        GpuAdapterStatus relabelled = new()
        {
            Name = "Relabelled by the driver package",
            Index = 7,
            VendorId = 0x10de,
            DeviceId = 0x2c02,
            DedicatedVideoMemoryBytes = 16 * GiB,
            DriverVersion = "32.0.15.6094"
        };

        Assert.Equal(
            RuntimePerformanceTuner.BuildGpuFingerprint(Inventory(Adapter()), Host),
            RuntimePerformanceTuner.BuildGpuFingerprint(Inventory(relabelled), Host));
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("device")]
    [InlineData("dedicated-vram")]
    [InlineData("driver")]
    public void Adapter_identity_change_changes_the_shared_key(string dimension)
    {
        GpuAdapterStatus changed = dimension switch
        {
            "vendor" => Adapter(vendor: 0x1002),
            "device" => Adapter(device: 0x2c03),
            "dedicated-vram" => Adapter(dedicatedVram: 12 * GiB),
            "driver" => Adapter(driver: "32.0.15.6095"),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

        string baseline = RuntimePerformanceTuner.BuildGpuFingerprint(Inventory(Adapter()), Host);
        string changedKey = RuntimePerformanceTuner.BuildGpuFingerprint(Inventory(changed), Host);

        Assert.NotEqual(baseline, changedKey);
    }

    [Fact]
    public void Legacy_profile_is_stale_and_remains_stored()
    {
        string modelPath = Path.Combine(_root, "model.gguf");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
        var model = new FileInfo(modelPath);
        const string legacyKey = "dxgi-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var store = new RuntimePerformanceProfileStore(Path.Combine(_root, "profiles.json"));
        store.Save(new RuntimePerformanceProfileSelection
        {
            Key = "legacy-profile",
            ModelPath = model.FullName,
            ModelSizeBytes = model.Length,
            ModelLastWriteUtcTicks = model.LastWriteTimeUtc.Ticks,
            Workload = "chat",
            RuntimeBuildTag = "runtime-test",
            Backend = "Vulkan",
            HardwareKey = legacyKey,
            Profile = new LlamaPerformanceProfileDto { Id = "legacy", BatchSize = 512, UBatchSize = 128 },
            PromptTokensPerSecond = 10,
            DecodeTokensPerSecond = 5,
            TunedUtc = DateTime.UtcNow
        });

        string currentKey = RuntimePerformanceTuner.BuildGpuFingerprint(Inventory(Adapter()), Host);

        Assert.True(EvaluationHardwareFingerprint.IsCurrentVersion(currentKey));
        Assert.Null(store.FindValid(modelPath, "chat", "runtime-test", currentKey));
        RuntimePerformanceProfileSelection retained = Assert.Single(store.List());
        Assert.Equal(legacyKey, retained.HardwareKey);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static GpuInventoryStatus Inventory(params GpuAdapterStatus[] adapters) =>
        GpuInventoryService.BuildStatus(adapters, "test");

    private static GpuAdapterStatus Adapter(
        uint vendor = 0x10de,
        uint device = 0x2c02,
        long dedicatedVram = 16 * GiB,
        string driver = "32.0.15.6094") => new()
    {
        Name = "Synthetic GPU",
        VendorId = vendor,
        DeviceId = device,
        DedicatedVideoMemoryBytes = dedicatedVram,
        DriverVersion = driver
    };
}
