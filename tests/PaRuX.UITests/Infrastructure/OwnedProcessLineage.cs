using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaRuX.UITests.Infrastructure;

internal readonly record struct OwnedProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ImagePath);
internal readonly record struct ProcessParent(int ProcessId, int ParentProcessId);

/// <summary>Process-tree discovery and identity checks used by the opt-in real-product fixture.</summary>
internal static class OwnedProcessLineage
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal static OwnedProcessIdentity Capture(int processId) =>
        TryCapture(processId, out OwnedProcessIdentity identity)
            ? identity
            : throw new InvalidOperationException("Cannot establish process identity for PID " + processId + ".");

    /// <summary>Retries metadata reads on the retained process handle; it never reopens the PID.</summary>
    internal static bool TryCaptureWhenReady(Process process, TimeSpan timeout, out OwnedProcessIdentity identity)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (TryCapture(process, out identity)) return true;
            try { if (process.HasExited) break; }
            catch (InvalidOperationException) { break; }
            Thread.Sleep(25);
        } while (elapsed.Elapsed < timeout);
        identity = default;
        return false;
    }

    /// <summary>Terminates only the process represented by this retained Process handle.</summary>
    internal static bool TerminateExactProcess(Process process, TimeSpan timeout)
    {
        try
        {
            var handle = process.SafeHandle;
            if (handle.IsInvalid || handle.IsClosed) return false;
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            return process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException or
                                   UnauthorizedAccessException or IOException)
        { return false; }
    }

    internal static bool TryCapture(int processId, out OwnedProcessIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return TryCapture(process, out identity);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException or
                                   UnauthorizedAccessException or IOException)
        {
            identity = default;
            return false;
        }
    }

    internal static bool TryCapture(Process process, out OwnedProcessIdentity identity)
    {
        try
        {
            // Retain the kernel process handle before reading identity metadata. Callers that
            // later terminate this Process then act on this exact process instance, not a PID
            // that could have been recycled between verification and the operation.
            var handle = process.SafeHandle;
            if (handle.IsInvalid || handle.IsClosed) { identity = default; return false; }
            int processId = process.Id;
            string? imagePath = null;
            try { imagePath = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
                                       UnauthorizedAccessException or NotSupportedException) { }
            imagePath ??= QueryImagePath(handle);
            if (string.IsNullOrWhiteSpace(imagePath)) { identity = default; return false; }
            identity = new OwnedProcessIdentity(processId, process.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(imagePath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or NotSupportedException or
                                   UnauthorizedAccessException or IOException)
        {
            identity = default;
            return false;
        }
    }

    private static string? QueryImagePath(SafeProcessHandle processHandle)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        return QueryFullProcessImageName(processHandle, 0, buffer, ref length) && length > 0
            ? buffer.ToString(0, checked((int)length))
            : null;
    }

    internal static bool IsSameIdentity(OwnedProcessIdentity expected, OwnedProcessIdentity actual) =>
        expected.ProcessId == actual.ProcessId && expected.StartTimeUtcTicks == actual.StartTimeUtcTicks &&
        string.Equals(expected.ImagePath, actual.ImagePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns only stable identities still reachable through the exact root's parent chain.</summary>
    internal static IReadOnlyList<OwnedProcessIdentity> CaptureDescendants(OwnedProcessIdentity root)
    {
        if (!TryCapture(root.ProcessId, out OwnedProcessIdentity liveRoot) || !IsSameIdentity(root, liveRoot))
            return Array.Empty<OwnedProcessIdentity>();

        List<ProcessParent> firstSnapshot = ReadProcessParents();
        HashSet<int> descendantIds = DescendantPids(root.ProcessId, firstSnapshot);
        var firstIdentities = CaptureIdentities(descendantIds.Append(root.ProcessId));
        List<ProcessParent> secondSnapshot = ReadProcessParents();
        HashSet<int> secondDescendants = DescendantPids(root.ProcessId, secondSnapshot);
        var secondIdentities = CaptureIdentities(secondDescendants.Append(root.ProcessId));
        if (!secondSnapshot.Any(entry => entry.ProcessId == root.ProcessId) ||
            !secondIdentities.TryGetValue(root.ProcessId, out liveRoot) || !IsSameIdentity(root, liveRoot))
            return Array.Empty<OwnedProcessIdentity>();

        return descendantIds.Intersect(secondDescendants)
            .Where(processId => IsStableLineage(root, processId, firstSnapshot, firstIdentities, secondSnapshot, secondIdentities))
            .Select(processId => secondIdentities[processId])
            .ToArray();
    }

    internal static bool IsStableLineage(OwnedProcessIdentity root, int childProcessId,
        IReadOnlyCollection<ProcessParent> firstSnapshot,
        IReadOnlyDictionary<int, OwnedProcessIdentity> firstIdentities,
        IReadOnlyCollection<ProcessParent> secondSnapshot,
        IReadOnlyDictionary<int, OwnedProcessIdentity> secondIdentities)
    {
        if (!TryGetLineage(root.ProcessId, childProcessId, firstSnapshot, out int[]? firstPath) ||
            !TryGetLineage(root.ProcessId, childProcessId, secondSnapshot, out int[]? secondPath) ||
            !firstPath.SequenceEqual(secondPath)) return false;

        for (int i = 0; i < firstPath.Length; i++)
        {
            int processId = firstPath[i];
            if (!firstIdentities.TryGetValue(processId, out OwnedProcessIdentity first) ||
                !secondIdentities.TryGetValue(processId, out OwnedProcessIdentity second) ||
                first.ProcessId != processId || second.ProcessId != processId ||
                !IsSameIdentity(first, second)) return false;
            if (processId == root.ProcessId && (!IsSameIdentity(root, first) || !IsSameIdentity(root, second))) return false;
            if (i + 1 < firstPath.Length)
            {
                int parentId = firstPath[i + 1];
                if (!firstIdentities.TryGetValue(parentId, out OwnedProcessIdentity firstParent) ||
                    !secondIdentities.TryGetValue(parentId, out OwnedProcessIdentity secondParent) ||
                    first.StartTimeUtcTicks < firstParent.StartTimeUtcTicks ||
                    second.StartTimeUtcTicks < secondParent.StartTimeUtcTicks) return false;
            }
        }
        return true;
    }

    private static Dictionary<int, OwnedProcessIdentity> CaptureIdentities(IEnumerable<int> processIds)
    {
        var identities = new Dictionary<int, OwnedProcessIdentity>();
        foreach (int processId in processIds.Distinct())
            if (TryCapture(processId, out OwnedProcessIdentity identity)) identities[processId] = identity;
        return identities;
    }

    private static bool TryGetLineage(int rootProcessId, int childProcessId,
        IReadOnlyCollection<ProcessParent> snapshot, out int[] path)
    {
        var parents = new Dictionary<int, int>();
        foreach (ProcessParent entry in snapshot)
            if (!parents.TryAdd(entry.ProcessId, entry.ParentProcessId)) { path = Array.Empty<int>(); return false; }
        var lineage = new List<int> { childProcessId };
        var seen = new HashSet<int> { childProcessId };
        int current = childProcessId;
        while (current != rootProcessId)
        {
            if (!parents.TryGetValue(current, out int parentId) || !seen.Add(parentId))
            { path = Array.Empty<int>(); return false; }
            lineage.Add(parentId);
            current = parentId;
        }
        if (!parents.ContainsKey(rootProcessId)) { path = Array.Empty<int>(); return false; }
        path = lineage.ToArray();
        return true;
    }

    internal static HashSet<int> DescendantPids(int rootProcessId, IReadOnlyCollection<ProcessParent> snapshot)
    {
        var children = snapshot.GroupBy(entry => entry.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.ProcessId).ToArray());
        var result = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        while (pending.Count > 0)
        {
            int parentId = pending.Dequeue();
            if (!children.TryGetValue(parentId, out int[]? descendants)) continue;
            foreach (int processId in descendants)
                if (processId != rootProcessId && result.Add(processId)) pending.Enqueue(processId);
        }
        return result;
    }

    private static List<ProcessParent> ReadProcessParents()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            var result = new List<ProcessParent>();
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ProcessParent(unchecked((int)entry.ProcessId), unchecked((int)entry.ParentProcessId)));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imagePath, ref uint size);
}
