namespace PaRuX.Contracts;

/// <summary>One physical graphics adapter reported by the Windows DXGI adapter inventory.
/// Memory figures are the adapter's own DXGI totals, not process usage and not an NVIDIA-only
/// approximation. Keeping dedicated video, dedicated system and shared system memory separate is
/// important on mixed desktop/iGPU/eGPU machines where collapsing them into one "VRAM" number is
/// actively misleading.</summary>
public sealed class GpuAdapterStatus
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public long DedicatedVideoMemoryBytes { get; set; }
    public long DedicatedSystemMemoryBytes { get; set; }
    public long SharedSystemMemoryBytes { get; set; }
    public bool IsSoftware { get; set; }

    /// <summary>DXGI user-mode-driver version as a dotted quad (for example 32.0.15.6094), or null
    /// when the driver did not attest one. Never a path or machine identifier.</summary>
    public string? DriverVersion { get; set; }
}

/// <summary>Complete GPU inventory for the host. The aggregate fields are sums over every adapter
/// returned in <see cref="Adapters"/>; LargestDedicatedVideoMemoryBytes remains separate because
/// a single llama.cpp model cannot automatically treat two independent GPUs as one contiguous
/// VRAM pool unless the selected runtime is explicitly configured for multi-device offload.</summary>
public sealed class GpuInventoryStatus
{
    public List<GpuAdapterStatus> Adapters { get; set; } = new();
    public long TotalDedicatedVideoMemoryBytes { get; set; }
    public long TotalDedicatedSystemMemoryBytes { get; set; }
    public long TotalSharedSystemMemoryBytes { get; set; }

    /// <summary>
    /// Transitional source-compatibility alias for the Best-in-Class runtime transport shim.
    /// The canonical contract remains <see cref="TotalSharedSystemMemoryBytes"/> and this alias is
    /// deliberately excluded from JSON so clients never see two names for the same measurement.
    /// Remove once every internal caller has been rebuilt against the canonical property.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SharedSystemMemoryTotalBytes => TotalSharedSystemMemoryBytes;

    public long LargestDedicatedVideoMemoryBytes { get; set; }
    /// <summary>True only when the platform-specific inventory completed successfully. An empty
    /// but successful list is distinct from an unsupported/failed inventory.</summary>
    public bool Available { get; set; }
    /// <summary>"dxgi" on Windows when enumeration succeeds; otherwise a short honest reason such
    /// as "unsupported-platform" or "dxgi-error".</summary>
    public string Source { get; set; } = "unavailable";
    public string? Error { get; set; }
}

/// <summary>
/// The single adapter/backend pair selected for PaRuX text acceleration. This is distinct from
/// physical inventory: GpuPresent says hardware exists, while AcceleratorEligible is true only
/// when an installed PaRuX backend can target the selected adapter. Shared memory is a limit into
/// system RAM, never extra memory to add to the RAM pool.
/// </summary>
public sealed class RuntimeAcceleratorProfile
{
    public bool GpuPresent { get; set; }
    public bool AcceleratorEligible { get; set; }
    public string Backend { get; set; } = "cpu";
    public string Vendor { get; set; } = "none";
    public string? AdapterName { get; set; }
    public long DedicatedVideoMemoryBytes { get; set; }
    public long SharedSystemMemoryLimitBytes { get; set; }
    public long SystemRamBytes { get; set; }
    public long? UsableSystemRamBytes { get; set; }
    public bool SharedMemoryPrimary { get; set; }
    public string InventorySource { get; set; } = "unavailable";
}

/// <summary>Additive JSON shape used by GET /api/v1/runtime/status. All original
/// RuntimeManagerStatus fields stay at the root with the same names/meaning, while the new GPU
/// inventory makes mixed/multi-GPU hardware observable without breaking older clients that ignore
/// unknown properties.</summary>
public sealed class RuntimeManagerHardwareStatus
{
    public List<RuntimeBackendStatus> Backends { get; set; } = new();
    public string? CurrentManagedProviderEndpoint { get; set; }
    public bool GpuDetected { get; set; }
    public int VramTotalMib { get; set; }
    public int? VramFreeMib { get; set; }
    public long RamTotalBytes { get; set; }
    public bool GamingActive { get; set; }
    public bool GamingShutdownPending { get; set; }
    public string? LastGamingShutdownFailure { get; set; }
    public string? ActiveRole { get; set; }
    public string? ActiveProfile { get; set; }
    public string? ActiveModelPath { get; set; }
    public string? ActiveVisionProjectorPath { get; set; }
    public bool ActiveHasVisionSupport { get; set; }
    public string LaunchPlanExplanation { get; set; } = string.Empty;
    public List<string> TextLaunchTrace { get; set; } = new();

    public List<GpuAdapterStatus> GpuAdapters { get; set; } = new();
    public long GpuDedicatedVideoMemoryTotalBytes { get; set; }
    public long GpuDedicatedSystemMemoryTotalBytes { get; set; }
    public long GpuSharedSystemMemoryTotalBytes { get; set; }
    public long GpuLargestDedicatedVideoMemoryBytes { get; set; }
    public bool GpuInventoryAvailable { get; set; }
    public string GpuInventorySource { get; set; } = "unavailable";
    public string? GpuInventoryError { get; set; }
    /// <summary>One normalized adapter/backend decision shared by context planning and model-fit
    /// estimates. Null is accepted for older Core builds and deterministic legacy callers.</summary>
    public RuntimeAcceleratorProfile? AcceleratorProfile { get; set; }
}
