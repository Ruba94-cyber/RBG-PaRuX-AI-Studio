using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EvaluationHardwareFingerprintTests
{
    private static HostEvaluationFacts Host(string? cpu = "AMD64 Family 25 Model 116 Stepping 1, AuthenticAMD|X64",
        int threads = 16, string? os = "10.0.26220.1") => new(cpu, threads, os);

    private static GpuAdapterStatus Gpu(uint vendor = 0x10de, uint device = 0x2c02, long dedicated = 16L << 30,
        long shared = 32L << 30, string? driver = "32.0.15.6094", string name = "GPU", bool software = false) => new()
    {
        Name = name, VendorId = vendor, DeviceId = device, DedicatedVideoMemoryBytes = dedicated,
        SharedSystemMemoryBytes = shared, DriverVersion = driver, IsSoftware = software
    };

    private static GpuInventoryStatus Inv(params GpuAdapterStatus[] adapters) =>
        GpuInventoryService.BuildStatus(adapters, "dxgi");

    private static string Fp(GpuInventoryStatus inv, HostEvaluationFacts? facts = null) =>
        EvaluationHardwareFingerprint.Build(inv, facts ?? Host()).Fingerprint;

    [Fact]
    public void Fingerprint_is_versioned_deterministic_and_order_independent()
    {
        GpuAdapterStatus a = Gpu(), b = Gpu(0x1002, 0x15bf, 512L << 20, 16L << 30, "31.0.21001.0");
        string one = Fp(Inv(a, b));
        string two = Fp(Inv(b, a));
        Assert.Equal(one, two);
        Assert.StartsWith("hw2-sha256:", one);
        Assert.Equal("hw2-sha256:".Length + 64, one.Length);
        Assert.True(EvaluationHardwareFingerprint.IsCurrentVersion(one));
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("device")]
    [InlineData("dedicated")]
    [InlineData("driver")]
    [InlineData("cpu")]
    [InlineData("threads")]
    [InlineData("os")]
    public void Fingerprint_changes_on_every_dimension(string dimension)
    {
        string baseline = Fp(Inv(Gpu()));
        GpuAdapterStatus gpu = dimension switch
        {
            "vendor" => Gpu(vendor: 0x1002),
            "device" => Gpu(device: 0x2c03),
            "dedicated" => Gpu(dedicated: 12L << 30),
            "driver" => Gpu(driver: "32.0.15.6095"),
            _ => Gpu()
        };
        HostEvaluationFacts host = dimension switch
        {
            "cpu" => Host(cpu: "AMD64 Family 25 Model 117 Stepping 2, AuthenticAMD|X64"),
            "threads" => Host(threads: 8),
            "os" => Host(os: "10.0.26220.2"),
            _ => Host()
        };
        Assert.NotEqual(baseline, Fp(Inv(gpu), host));
    }

    [Fact]
    public void Volatile_uma_and_ram_dependent_sizes_do_not_change_the_fingerprint()
    {
        string baseline = Fp(Inv(Gpu()));
        Assert.Equal(baseline, Fp(Inv(Gpu(shared: 8L << 30))));
        GpuAdapterStatus withSystemMem = Gpu();
        withSystemMem.DedicatedSystemMemoryBytes = 4L << 30;
        Assert.Equal(baseline, Fp(Inv(withSystemMem)));
    }

    [Fact]
    public void Cuda_scope_ignores_a_hybrid_igpu_but_vulkan_scope_does_not()
    {
        GpuAdapterStatus igpu = Gpu(0x1002, 0x1900, 6L << 30, 13L << 30, "32.0.31041.1004");
        string cudaAlone = EvaluationHardwareFingerprint.Build(Inv(Gpu()), Host(), "text-cuda").Fingerprint;
        string cudaHybrid = EvaluationHardwareFingerprint.Build(Inv(Gpu(), igpu), Host(), "text-cuda").Fingerprint;
        Assert.Equal(cudaAlone, cudaHybrid);
        Assert.NotEqual(cudaAlone, Fp(Inv(Gpu()))); // scope itself is part of identity
        Assert.NotEqual(
            EvaluationHardwareFingerprint.Build(Inv(Gpu()), Host(), "text-vulkan").Fingerprint,
            EvaluationHardwareFingerprint.Build(Inv(Gpu(), igpu), Host(), "text-vulkan").Fingerprint);
        Assert.False(EvaluationHardwareFingerprint.Build(Inv(igpu), Host(), "text-cuda").Complete); // no NVIDIA adapter
    }

    [Fact]
    public void Adapter_name_and_index_are_not_identity_and_software_adapters_are_ignored()
    {
        string baseline = Fp(Inv(Gpu(name: "RTX A")));
        Assert.Equal(baseline, Fp(Inv(Gpu(name: "RTX B"))));
        Assert.Equal(baseline, Fp(Inv(Gpu(name: "x"), Gpu(0x1414, 0x8c, 0, 0, null, "Microsoft Basic Render Driver", true))));
    }

    [Fact]
    public void Unavailable_attestation_is_explicit_and_incomplete()
    {
        EvaluationHardwareAttestation noDriver = EvaluationHardwareFingerprint.Build(Inv(Gpu(driver: null)), Host());
        Assert.False(noDriver.Complete);
        Assert.Contains(noDriver.Unavailable, u => u.StartsWith("gpu-driver:", StringComparison.Ordinal));

        EvaluationHardwareAttestation noCpuOs = EvaluationHardwareFingerprint.Build(Inv(Gpu()), Host(cpu: null, os: " "));
        Assert.False(noCpuOs.Complete);
        Assert.Contains("cpu", noCpuOs.Unavailable);
        Assert.Contains("os-build", noCpuOs.Unavailable);

        Assert.False(EvaluationHardwareFingerprint.Build(null, Host()).Complete);
        Assert.False(EvaluationHardwareFingerprint.Build(
            new GpuInventoryStatus { Available = false }, Host()).Complete);
        Assert.False(EvaluationHardwareFingerprint.Build(Inv(), Host()).Complete);
        Assert.True(EvaluationHardwareFingerprint.Build(Inv(Gpu()), Host()).Complete);
    }

    [Fact]
    public void Unattested_fingerprint_differs_from_attested_one()
    {
        Assert.NotEqual(Fp(Inv(Gpu(driver: null))), Fp(Inv(Gpu(driver: "unavailable-x"))));
        Assert.NotEqual(Fp(Inv(Gpu(driver: null))), Fp(Inv(Gpu())));
    }

    [Fact]
    public void Legacy_fingerprint_is_recognized_and_never_equals_a_current_one()
    {
        string legacy = "dxgi-sha256:" + new string('a', 64);
        string current = Fp(Inv(Gpu()));
        Assert.True(EvaluationHardwareFingerprint.IsLegacyVersion(legacy));
        Assert.False(EvaluationHardwareFingerprint.IsCurrentVersion(legacy));
        Assert.False(EvaluationHardwareFingerprint.IsLegacyVersion(current));
        Assert.False(EvaluationHardwareFingerprint.IsCurrentVersion(null));
        Assert.False(EvaluationHardwareFingerprint.IsCurrentVersion(string.Empty));
        Assert.NotEqual(legacy, current);
    }

    [Fact]
    public void Fingerprint_contains_no_raw_identifiers()
    {
        string fp = Fp(Inv(Gpu(name: "Secret Name")), Host(cpu: "SerialABC123|X64", os: "10.0.1.1"));
        Assert.DoesNotContain("SerialABC123", fp);
        Assert.DoesNotContain("Secret", fp);
        Assert.Matches("^hw2-sha256:[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void Real_host_facts_are_populated_without_throwing()
    {
        HostEvaluationFacts facts = HostEvaluationFacts.Current();
        Assert.True(facts.LogicalProcessors > 0);
        if (OperatingSystem.IsWindows()) Assert.False(string.IsNullOrWhiteSpace(facts.OsBuild));
    }

    [Theory]
    [InlineData(0x0020_0000_000F_17BEL, "32.0.15.6078")]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    public void Dxgi_driver_version_formats_as_dotted_quad(long umd, string? expected) =>
        Assert.Equal(expected, DxgiGpuAdapterEnumerator.FormatDriverVersion(umd));

    [Fact]
    public void Driver_version_survives_inventory_copy() =>
        Assert.Equal("1.2.3.4", Inv(Gpu(driver: "1.2.3.4")).Adapters[0].DriverVersion);

    [Fact]
    public async Task Runtime_dependency_fingerprint_changes_when_a_sibling_dll_changes()
    {
        string dir = Directory.CreateTempSubdirectory("parux-rtdep-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "llama-server.exe"), new byte[] { 1, 2, 3 });
            string dll = Path.Combine(dir, "ggml-vulkan.dll");
            File.WriteAllBytes(dll, new byte[] { 9, 9, 9 });
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "ignored");

            string a = await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None);
            Assert.StartsWith("rtv2-sha256:", a);
            Assert.Equal(a, await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None));

            File.WriteAllText(Path.Combine(dir, "readme.txt"), "still ignored");
            Assert.Equal(a, await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None));

            File.WriteAllBytes(dll, new byte[] { 9, 9, 8 }); // same size, different content
            string b = await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None);
            Assert.NotEqual(a, b);
            Assert.NotEqual(b, await RuntimeDependencyFingerprint.ComputeAsync(dir, "bb", CancellationToken.None));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Large_same_size_dll_swap_changes_the_fingerprint_even_with_identical_timestamps()
    {
        string dir = Directory.CreateTempSubdirectory("parux-rtdep-big-").FullName;
        try
        {
            string dll = Path.Combine(dir, "ggml-cuda.dll");
            byte[] one = new byte[9 * 1024 * 1024]; // above any size-only threshold
            byte[] two = (byte[])one.Clone();
            two[one.Length / 2] = 1;
            File.WriteAllBytes(dll, one);
            DateTime stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(dll, stamp);
            string a = await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None);

            File.WriteAllBytes(dll, two);
            File.SetLastWriteTimeUtc(dll, stamp); // robocopy/zip-style preserved timestamp
            string b = await RuntimeDependencyFingerprint.ComputeAsync(dir, "aa", CancellationToken.None);
            Assert.NotEqual(a, b);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
