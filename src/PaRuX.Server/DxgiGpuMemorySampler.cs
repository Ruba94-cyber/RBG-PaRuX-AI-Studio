using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// One OS-reported local video-memory sample for a physical adapter (DXGI
/// IDXGIAdapter3.QueryVideoMemoryInfo, LOCAL segment group). <see cref="BudgetBytes"/> is the
/// memory the OS currently lets this process use (it shrinks under cross-process pressure);
/// <see cref="CurrentUsageBytes"/> is what THIS process already uses (not system-wide).
/// <see cref="SystemDedicatedUsageBytes"/> is the system-wide PDH "GPU Adapter Memory" dedicated
/// usage for the adapter LUID (null when unavailable or ambiguous). The LUID is boot-scoped and
/// held in memory only; it is never persisted or fingerprinted.
/// </summary>
internal sealed record GpuAdapterMemorySample(
    uint VendorId,
    uint DeviceId,
    string Name,
    long DedicatedBytes,
    long BudgetBytes,
    long CurrentUsageBytes,
    DateTime SampledUtc,
    long Luid = 0,
    long? SystemDedicatedUsageBytes = null);

internal interface IGpuAdapterMemorySampler
{
    /// <summary>Returns one sample per hardware adapter that could be queried. Adapters whose
    /// query fails are omitted (unknown), never reported as zero-used.</summary>
    IReadOnlyList<GpuAdapterMemorySample> Sample();
}

internal sealed class NoGpuAdapterMemorySampler : IGpuAdapterMemorySampler
{
    public static readonly NoGpuAdapterMemorySampler Instance = new();
    public IReadOnlyList<GpuAdapterMemorySample> Sample() => Array.Empty<GpuAdapterMemorySample>();
}

/// <summary>Read-only DXGI 1.4 query. No Direct3D device is created and no memory reservation is
/// set, so sampling has no GPU side effects.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DxgiGpuMemorySampler : IGpuAdapterMemorySampler, IDisposable
{
    private const long DiscreteDedicatedFloorBytes = 2L * 1024 * 1024 * 1024;
    private readonly bool _nonNvidiaDiscreteOnly;
    private readonly PdhGpuAdapterMemory _pdh = new();

    /// <param name="nonNvidiaDiscreteOnly">Production default: only AMD/Intel adapters with more
    /// than 2 GiB dedicated memory are sampled (NVIDIA uses nvidia-smi). On hosts with none, no
    /// DXGI memory query and no PDH work is done at all.</param>
    public DxgiGpuMemorySampler(bool nonNvidiaDiscreteOnly = true) => _nonNvidiaDiscreteOnly = nonNvidiaDiscreteOnly;

    public void Dispose() => _pdh.Dispose();

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 0x2;
    private const int SegmentGroupLocal = 0;

    public IReadOnlyList<GpuAdapterMemorySample> Sample()
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        int hr = CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory);
        Marshal.ThrowExceptionForHR(hr);
        var result = new List<GpuAdapterMemorySample>();
        try
        {
            for (uint index = 0; ; index++)
            {
                hr = factory.EnumAdapters1(index, out object adapterObject);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    if (adapterObject is not IDXGIAdapter3 adapter) continue;
                    if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) < 0) continue;
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                    if (_nonNvidiaDiscreteOnly && !(desc.VendorId is 0x1002 or 0x8086
                            && desc.DedicatedVideoMemory.ToUInt64() > (ulong)DiscreteDedicatedFloorBytes))
                        continue;
                    if (adapter.QueryVideoMemoryInfo(0, SegmentGroupLocal, out DXGI_QUERY_VIDEO_MEMORY_INFO info) < 0)
                        continue;
                    result.Add(new GpuAdapterMemorySample(
                        desc.VendorId, desc.DeviceId,
                        (desc.Description ?? string.Empty).TrimEnd('\0', ' '),
                        Saturate(desc.DedicatedVideoMemory.ToUInt64()),
                        Saturate(info.Budget), Saturate(info.CurrentUsage), DateTime.UtcNow,
                        desc.AdapterLuid, null));
                }
                finally
                {
                    if (Marshal.IsComObject(adapterObject)) Marshal.FinalReleaseComObject(adapterObject);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
        if (result.Count == 0) return result;
        IReadOnlyDictionary<string, long>? usage = _pdh.Read();
        // New adapter since the persistent query was expanded: reopen once and retry.
        // Reopen only when an instance is ABSENT; an ambiguous match is not fixed by reopening.
        if (usage is null || result.Any(r => PdhGpuAdapterMemory.Match(usage, r.Luid, out _) == PdhLuidMatch.Absent))
            usage = _pdh.Reset().Read();
        return result.Select(r => r with
        {
            SystemDedicatedUsageBytes = usage is null ? null : PdhGpuAdapterMemory.TryMatchLuid(usage, r.Luid)
        }).ToArray();
    }

    private static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 ppFactory);

    [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out object ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    // Full vtable through IDXGIAdapter3 (COM interface inheritance is flattened by slot order).
    [ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        [PreserveSig] int GetDesc2(IntPtr desc);
        [PreserveSig] int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr hEvent, out uint cookie);
        [PreserveSig] void UnregisterHardwareContentProtectionTeardownStatus(uint cookie);
        [PreserveSig] int QueryVideoMemoryInfo(uint nodeIndex, int memorySegmentGroup,
            out DXGI_QUERY_VIDEO_MEMORY_INFO info);
        [PreserveSig] int SetVideoMemoryReservation(uint nodeIndex, int memorySegmentGroup, ulong reservation);
        [PreserveSig] int RegisterVideoMemoryBudgetChangeNotificationEvent(IntPtr hEvent, out uint cookie);
        [PreserveSig] void UnregisterVideoMemoryBudgetChangeNotification(uint cookie);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}

/// <summary>System-wide dedicated GPU memory usage from the Windows "GPU Adapter Memory" PDH
/// counter set (what Task Manager shows). Unlike DXGI CurrentUsage it includes every process,
/// including llama-server children. Any failure yields null (unknown), never zero.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PdhGpuAdapterMemory : IDisposable
{
    private const uint PdhFmtLarge = 0x400;
    private const uint PdhMoreData = 0x800007D2;
    private const string CounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private readonly object _gate = new(); // PDH query handles are not thread-safe
    private IntPtr _query;
    private IntPtr _counter;
    private bool _disposed;

    /// <summary>Reuses one query handle for the process lifetime; null on any failure.</summary>
    internal IReadOnlyDictionary<string, long>? Read()
    {
        IntPtr buffer = IntPtr.Zero;
        lock (_gate)
        {
            try
            {
                if (_disposed || !EnsureOpen()) return null;
                if (PdhCollectQueryData(_query) != 0) return null;
                uint size = 0, count = 0;
                if (PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge, ref size, ref count, IntPtr.Zero) != PdhMoreData
                    || size == 0 || size > 4 * 1024 * 1024) return null;
                buffer = Marshal.AllocHGlobal((int)size);
                if (PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge, ref size, ref count, buffer) != 0) return null;
                int stride = IntPtr.Size == 8 ? 24 : 16;
                int valueOffset = IntPtr.Size == 8 ? 16 : 8;
                var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count && i < 256; i++)
                {
                    IntPtr item = buffer + i * stride;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    uint status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if (string.IsNullOrEmpty(name) || status != 0) continue; // PDH_CSTATUS_VALID_DATA only
                    long value = Marshal.ReadInt64(item, valueOffset);
                    if (value < 0) continue;
                    result[name] = value;
                }
                return result;
            }
            catch { return null; }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
    }

    internal PdhGpuAdapterMemory Reset()
    {
        lock (_gate) CloseLocked();
        return this;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; CloseLocked(); }
    }

    private bool EnsureOpen()
    {
        if (_query != IntPtr.Zero) return true;
        if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return false;
        if (PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out IntPtr counter) != 0)
        {
            PdhCloseQuery(query);
            return false;
        }
        _query = query;
        _counter = counter;
        return true;
    }

    private void CloseLocked()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    /// <summary>Instance names look like "luid_0x00000000_0x0000D2F1_phys_0". Exactly one instance
    /// for the LUID is required; none or several (multi-node/phys) is ambiguous and returns null.</summary>
    internal static long? TryMatchLuid(IReadOnlyDictionary<string, long> byInstance, long luid) =>
        Match(byInstance, luid, out long value) == PdhLuidMatch.Found ? value : null;

    internal static PdhLuidMatch Match(IReadOnlyDictionary<string, long> byInstance, long luid, out long value)
    {
        value = 0;
        uint high = (uint)((ulong)luid >> 32), low = (uint)((ulong)luid & 0xFFFFFFFF);
        string prefix = $"luid_0x{high:X8}_0x{low:X8}_phys_";
        bool found = false;
        foreach (KeyValuePair<string, long> kv in byInstance)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return PdhLuidMatch.Ambiguous;
            found = true;
            value = kv.Value;
        }
        return found ? PdhLuidMatch.Found : PdhLuidMatch.Absent;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
        ref uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

internal enum PdhLuidMatch { Absent, Ambiguous, Found }
