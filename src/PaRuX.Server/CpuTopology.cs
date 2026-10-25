using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// Physical-core count, which llama.cpp thread sizing needs: with SMT, running one thread per logical
/// processor oversubscribes the cores that actually execute the matrix kernels.
/// </summary>
internal static class CpuTopology
{
    private const int RelationProcessorCore = 0;
    private static int _cached;

    /// <summary>Physical cores, or 0 when the platform cannot say (callers then leave thread counts alone).</summary>
    internal static int PhysicalCores()
    {
        int cached = Volatile.Read(ref _cached);
        if (cached != 0) return cached > 0 ? cached : 0;
        int value = 0;
        try { if (OperatingSystem.IsWindows()) value = CountWindowsCores(); }
        catch { value = 0; }
        Volatile.Write(ref _cached, value > 0 ? value : -1);
        return value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int returnedLength);

    private static int CountWindowsCores()
    {
        int length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length <= 0 || length > 1 << 20) return 0;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return 0;
            int count = 0;
            int offset = 0;
            while (offset + 8 <= length)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore) count++;
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
