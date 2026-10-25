using System.Runtime.InteropServices;

/// <summary>
/// Resolves a PCI vendor:device pin to the DXGI adapter index that the ONNX Runtime DirectML
/// provider's device_id option addresses. The pin must match exactly one non-software adapter;
/// zero or several matches fail closed instead of letting DirectML pick an adapter. No Direct3D
/// device is created here.
/// </summary>
internal static class DxgiAdapterIndex
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 0x2;

    internal static int Resolve(uint vendorId, uint deviceId)
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory));
        var matches = new List<int>();
        var seen = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                int hr = factory.EnumAdapters1(index, out IDXGIAdapter1 adapter);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc));
                    seen.Add(index + ":" + desc.VendorId.ToString("x4") + ":" + desc.DeviceId.ToString("x4"));
                    if (desc.VendorId == vendorId && desc.DeviceId == deviceId
                        && (desc.Flags & DxgiAdapterFlagSoftware) == 0)
                        matches.Add((int)index);
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
        if (matches.Count != 1)
            throw new InvalidOperationException(
                "The pinned DirectML adapter " + vendorId.ToString("x4") + ":" + deviceId.ToString("x4")
                + " matched " + matches.Count + " DXGI adapters (seen: " + string.Join(", ", seen) + ").");
        return matches[0];
    }

    /// <summary>
    /// Proves WHICH adapter actually holds this process's model: IDXGIAdapter3.QueryVideoMemoryInfo
    /// reports this process's own video-memory usage per adapter (local + non-local segment groups).
    /// The ORT/GenAI DirectML provider in this build does not honor a requested adapter, so a pin is
    /// accepted only when the pinned adapter holds essentially all of the process's GPU memory.
    /// </summary>
    internal static string VerifyExecution(uint vendorId, uint deviceId, long weightBytes)
    {
        const long MinimumModelBytes = 64L * 1024 * 1024;
        Guid iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory));
        long pinnedBytes = 0, otherBytes = 0;
        var report = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                int hr = factory.EnumAdapters1(index, out IDXGIAdapter1 adapter);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc));
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                    long used = 0;
                    if (adapter is IDXGIAdapter3 a3)
                    {
                        for (int segment = 0; segment < 2; segment++)
                            if (a3.QueryVideoMemoryInfo(0, segment, out DXGI_QUERY_VIDEO_MEMORY_INFO info) >= 0)
                                used += (long)info.CurrentUsage;
                    }
                    bool isPinned = desc.VendorId == vendorId && desc.DeviceId == deviceId;
                    report.Add(desc.VendorId.ToString("x4") + ":" + desc.DeviceId.ToString("x4") + "=" + used / 1048576 + "MB");
                    if (isPinned) pinnedBytes += used; else otherBytes += used;
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
        // The pinned adapter must hold at least half of the model's on-disk weights (and the absolute
        // floor), so a small unrelated allocation can never satisfy the pin. The observed ratio is
        // part of the attestation. Lazily uploaded weights that are not yet resident fail closed.
        long required = Math.Max(MinimumModelBytes, weightBytes / 2);
        double ratio = weightBytes > 0 ? (double)pinnedBytes / weightBytes : 0;
        string evidence = "process-video-memory " + string.Join(",", report)
            + " weights=" + weightBytes / 1048576 + "MB ratio=" + ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (pinnedBytes < required || otherBytes > pinnedBytes / 10)
            throw new InvalidOperationException(
                "The pinned DirectML adapter " + vendorId.ToString("x4") + ":" + deviceId.ToString("x4")
                + " does not hold the model (" + evidence + "). This ONNX Runtime GenAI build did not honor the adapter request; refusing to report the pin as satisfied.");
        return evidence;
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
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-086879011A05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(IntPtr desc);
        [PreserveSig] int GetDesc2(IntPtr desc);
        [PreserveSig] int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr hEvent, out uint cookie);
        [PreserveSig] void UnregisterHardwareContentProtectionTeardownStatus(uint cookie);
        [PreserveSig] int QueryVideoMemoryInfo(uint nodeIndex, int memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
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
        public long AdapterLuid;
        public uint Flags;
    }
}
