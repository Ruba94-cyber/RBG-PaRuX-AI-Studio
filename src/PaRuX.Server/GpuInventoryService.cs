using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Hardware inventory for every graphics adapter visible to Windows through DXGI 1.1. This is
/// intentionally independent from LocalAiProcessManager's NVIDIA-only nvidia-smi probe: the latter
/// answers a different question ("can the CUDA flagship profile run?"), while this service answers
/// "what GPU hardware is actually present?" including AMD/Intel/iGPU/eGPU adapters and dedicated
/// versus shared memory for each one.
/// </summary>
public sealed class GpuInventoryService
{
    private readonly IGpuAdapterEnumerator _enumerator;
    private readonly Func<bool> _isWindows;

    /// <summary>The DXGI enumerator is Windows-only, so it is only constructed there. Off Windows
    /// this reports no adapters, which is the same answer the service already produced - it just
    /// no longer holds a Windows-only object to produce it.</summary>
    public GpuInventoryService()
        : this(OperatingSystem.IsWindows()
                ? new DxgiGpuAdapterEnumerator()
                : NoGpuAdapterEnumerator.Instance,
            OperatingSystem.IsWindows)
    { }

    internal GpuInventoryService(IGpuAdapterEnumerator enumerator, Func<bool> isWindows)
    {
        _enumerator = enumerator;
        _isWindows = isWindows;
    }

    public GpuInventoryStatus GetStatus()
    {
        if (!_isWindows())
        {
            return new GpuInventoryStatus
            {
                Available = false,
                Source = "unsupported-platform",
                Error = "DXGI GPU inventory is available on Windows only."
            };
        }

        try
        {
            IReadOnlyList<GpuAdapterStatus> adapters = _enumerator.Enumerate();
            return BuildStatus(adapters, source: "dxgi");
        }
        catch (Exception ex)
        {
            return new GpuInventoryStatus
            {
                Available = false,
                Source = "dxgi-error",
                Error = ex.Message
            };
        }
    }

    /// <summary>NVIDIA's PCI vendor id, as DXGI reports it in <see cref="GpuAdapterStatus.VendorId"/>.</summary>
    internal const uint NvidiaVendorId = 0x10DE;

    /// <summary>
    /// True only when DXGI positively reports an NVIDIA adapter with dedicated video memory. An unavailable
    /// inventory is not evidence of an NVIDIA card, so it is false.
    /// </summary>
    internal static bool HasNvidiaAdapterWithDedicatedVram(GpuInventoryStatus status) =>
        status.Available
        && status.Adapters.Any(adapter => adapter.VendorId == NvidiaVendorId && adapter.DedicatedVideoMemoryBytes > 0);

    internal static GpuInventoryStatus BuildStatus(IReadOnlyList<GpuAdapterStatus> adapters, string source)
    {
        var copy = adapters.Select(adapter => new GpuAdapterStatus
        {
            Index = adapter.Index,
            Name = adapter.Name,
            VendorId = adapter.VendorId,
            DeviceId = adapter.DeviceId,
            DedicatedVideoMemoryBytes = Math.Max(0, adapter.DedicatedVideoMemoryBytes),
            DedicatedSystemMemoryBytes = Math.Max(0, adapter.DedicatedSystemMemoryBytes),
            SharedSystemMemoryBytes = Math.Max(0, adapter.SharedSystemMemoryBytes),
            IsSoftware = adapter.IsSoftware,
            DriverVersion = adapter.DriverVersion
        }).ToList();

        return new GpuInventoryStatus
        {
            Adapters = copy,
            TotalDedicatedVideoMemoryBytes = SaturatingSum(copy.Select(a => a.DedicatedVideoMemoryBytes)),
            TotalDedicatedSystemMemoryBytes = SaturatingSum(copy.Select(a => a.DedicatedSystemMemoryBytes)),
            TotalSharedSystemMemoryBytes = SaturatingSum(copy.Select(a => a.SharedSystemMemoryBytes)),
            LargestDedicatedVideoMemoryBytes = copy.Count == 0 ? 0 : copy.Max(a => a.DedicatedVideoMemoryBytes),
            Available = true,
            Source = source
        };
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value <= 0) continue;
            if (long.MaxValue - total < value) return long.MaxValue;
            total += value;
        }
        return total;
    }
}

internal interface IGpuAdapterEnumerator
{
    IReadOnlyList<GpuAdapterStatus> Enumerate();
}

/// <summary>What a host with no DXGI reports: nothing, rather than a fabricated adapter.</summary>
internal sealed class NoGpuAdapterEnumerator : IGpuAdapterEnumerator
{
    public static readonly NoGpuAdapterEnumerator Instance = new();
    public IReadOnlyList<GpuAdapterStatus> Enumerate() => Array.Empty<GpuAdapterStatus>();
}

/// <summary>Minimal DXGI COM interop. Only EnumAdapters1/GetDesc1 are called; the inherited COM
/// methods are declared solely to preserve the correct vtable slots. No Direct3D device is
/// created, so inventory has no GPU-compute side effects and does not reserve VRAM.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DxgiGpuAdapterEnumerator : IGpuAdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 0x2;

    public IReadOnlyList<GpuAdapterStatus> Enumerate()
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        int hr = CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory);
        Marshal.ThrowExceptionForHR(hr);

        var result = new List<GpuAdapterStatus>();
        try
        {
            for (uint index = 0; ; index++)
            {
                hr = factory.EnumAdapters1(index, out IDXGIAdapter1 adapter);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    hr = adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc);
                    Marshal.ThrowExceptionForHR(hr);
                    result.Add(new GpuAdapterStatus
                    {
                        Index = checked((int)index),
                        Name = (desc.Description ?? string.Empty).TrimEnd('\0', ' '),
                        VendorId = desc.VendorId,
                        DeviceId = desc.DeviceId,
                        DedicatedVideoMemoryBytes = ToInt64Saturating(desc.DedicatedVideoMemory),
                        DedicatedSystemMemoryBytes = ToInt64Saturating(desc.DedicatedSystemMemory),
                        SharedSystemMemoryBytes = ToInt64Saturating(desc.SharedSystemMemory),
                        IsSoftware = (desc.Flags & DxgiAdapterFlagSoftware) != 0,
                        DriverVersion = QueryDriverVersion(adapter)
                    });
                }
                finally
                {
                    if (Marshal.IsComObject(adapter)) Marshal.FinalReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }

        return result;
    }

    /// <summary>IDXGIAdapter::CheckInterfaceSupport(IDXGIDevice) reports the user-mode-driver
    /// version without creating a device. Any failure is "not attested" (null), never a guess.</summary>
    private static string? QueryDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            Guid dxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            int hr = adapter.CheckInterfaceSupport(ref dxgiDevice, out long umd);
            return hr < 0 ? null : FormatDriverVersion(umd);
        }
        catch { return null; }
    }

    internal static string? FormatDriverVersion(long umdVersion)
    {
        if (umdVersion <= 0) return null;
        ulong v = (ulong)umdVersion;
        return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
    }

    private static long ToInt64Saturating(UIntPtr value)
    {
        ulong raw = value.ToUInt64();
        return raw > long.MaxValue ? long.MaxValue : (long)raw;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 ppFactory);

    [ComImport]
    [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-086879011A05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }
}
