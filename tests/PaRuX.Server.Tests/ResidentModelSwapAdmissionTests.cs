using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live functional-test regression: a Workflow planner task failed "needs 15.2 GB but only
/// 12.5 GB available" because the admission check counted PaRuX's own resident 27B model (which
/// the swap stops first) as memory in use by someone else.
/// </summary>
public sealed class ResidentModelSwapAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-swap-" + Guid.NewGuid().ToString("N"));

    public ResidentModelSwapAdmissionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Model(string name, long megabytes)
    {
        string path = Path.Combine(_root, name);
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("GGUF"u8);
            writer.Write(3u);
            writer.Write(0UL);
            writer.Write(0UL);
            stream.SetLength(megabytes * 1024 * 1024);
        }
        return path;
    }

    [Fact]
    public void The_resident_models_memory_counts_as_available_for_a_swap()
    {
        string requested = Model("standard.gguf", 64);
        string resident = Model("uncensored.gguf", 64);
        var refused = new HardwareFitEstimate(false, NeededMib: 80, AvailableMib: 30);

        HardwareFitEstimate credited = RuntimeManagerService.CreditResidentTextModel(refused, requested, resident);

        Assert.True(credited.Compatible);
        Assert.True(credited.AvailableMib >= 30 + 64);
    }

    [Fact]
    public void Nothing_is_credited_for_the_same_model_or_a_missing_resident()
    {
        string requested = Model("standard.gguf", 64);
        var refused = new HardwareFitEstimate(false, NeededMib: 80, AvailableMib: 30);

        Assert.Equal(refused, RuntimeManagerService.CreditResidentTextModel(refused, requested, requested));
        Assert.Equal(refused, RuntimeManagerService.CreditResidentTextModel(refused, requested, Path.Combine(_root, "gone.gguf")));
        Assert.Equal(refused, RuntimeManagerService.CreditResidentTextModel(refused, requested, null));
    }

    [Fact]
    public void A_model_too_big_even_after_the_swap_is_still_refused()
    {
        string requested = Model("huge.gguf", 64);
        string resident = Model("small.gguf", 8);
        var refused = new HardwareFitEstimate(false, NeededMib: 500, AvailableMib: 30);
        Assert.False(RuntimeManagerService.CreditResidentTextModel(refused, requested, resident).Compatible);
    }

    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void A_resident_held_in_system_RAM_is_not_credited_a_second_time()
    {
        // The RAM budget already returns the resident working set. Crediting its footprint again admitted a
        // candidate that does not fit once the resident is stopped. Covers CPU-only and shared-iGPU hosts.
        string requested = Model("candidate.gguf", 64);
        string resident = Model("resident.gguf", 64);
        var refused = new HardwareFitEstimate(false, NeededMib: 80, AvailableMib: 30);
        var cpuOnly = new RuntimeHardwareProfile(false, false, "cpu", "none", null, 0, 0, 32 * GiB, false, "test");
        var sharedIgpu = new RuntimeHardwareProfile(true, true, "vulkan", "amd", "igpu", 0, 16 * GiB, 32 * GiB, true, "test");

        Assert.Equal(refused, RuntimeManagerService.ApplyResidentCredit(refused, cpuOnly, requested, resident));
        Assert.Equal(refused, RuntimeManagerService.ApplyResidentCredit(refused, sharedIgpu, requested, resident));
    }

    [Fact]
    public void A_resident_held_in_dedicated_accelerator_memory_is_still_credited()
    {
        // Live free VRAM excludes the resident's own allocation, so its footprint must come back for a swap.
        string requested = Model("standard.gguf", 64);
        string resident = Model("uncensored.gguf", 64);
        var refused = new HardwareFitEstimate(false, NeededMib: 80, AvailableMib: 30);
        var dedicated = new RuntimeHardwareProfile(true, true, "cuda", "nvidia", "NVIDIA GeForce RTX 5080", 16 * GiB, 0, 32 * GiB, false, "test");

        HardwareFitEstimate credited = RuntimeManagerService.ApplyResidentCredit(refused, dedicated, requested, resident);

        Assert.True(credited.Compatible);
    }
}
