using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// Answers one question: is the process listening on PaRuX's runtime port the process PaRuX
/// started?
///
/// Why this exists. PaRuX serves its managed llama.cpp on 127.0.0.1:1234 and decided a runtime was
/// healthy by asking that port for /health. Anything answering {"status":"ok"} was adopted as
/// PaRuX's own runtime. Demonstrated on the physical PC: with PaRuX's model unloaded, a
/// llama-server started by hand on 1234 - no PID file, launched with --gpu-layers 0 and a 4,096
/// context that PaRuX never chose - was picked up and answered a user's chat, while PaRuX's own
/// Runtime Manager correctly showed both backends as running=False healthy=False.
///
/// Three separate things break when that happens. The product's central promise is that inference
/// runs on a runtime PaRuX manages, and it silently did not. Every measured performance profile -
/// batch size, KV cache type, GPU layers, context - describes a process that is not the one
/// answering, so the numbers in Runtime Manager become fiction. And 1234 is LM Studio's default
/// server port, so this is not a hypothetical collision: it is what happens on a machine that has
/// both, or on any machine where a local process claims the port first and quietly receives every
/// prompt the user types, documents and retrieved context included.
///
/// PaRuX supports user-managed backends deliberately, through ExternalTextProviderService, where
/// the user names the endpoint and knows what they are pointing at. Adopting whatever happens to
/// hold a port is not that.
/// </summary>
internal static class ManagedListenerOwnership
{
    /// <summary>
    /// True when <paramref name="expectedPid"/> is the <em>only</em> process listening on
    /// <paramref name="port"/>.
    ///
    /// "Only" is the part that took a second attempt to get right. The first version asked whether
    /// our pid was among the listeners, which sounds equivalent and is not: llama.cpp sets
    /// SO_REUSEADDR, so a second llama-server binds 127.0.0.1:1234 successfully while the first is
    /// still serving. Observed on the physical PC - two llama-servers, pid 20940 (PaRuX's own) and
    /// pid 23892 (started by hand), both listed as listening on the same address and port at the
    /// same moment. Windows then hands new connections to one of them, and which one is not
    /// something PaRuX can rely on.
    ///
    /// So a foreign process does not have to win the port to read the user's prompts; it only has
    /// to share it, and it can do that while PaRuX's own runtime is running perfectly. Sole
    /// ownership is the only property worth checking.
    ///
    /// This legacy health-path helper preserves the historical permissive fallback when the OS
    /// ownership table cannot be read. The prompt-bearing chat path does NOT use this helper: it
    /// uses <see cref="IsStrictlyOwnedBy"/>, which is fresh and fail-closed.
    /// </summary>
    internal static bool IsOwnedBy(int port, int expectedPid)
    {
        if (expectedPid <= 0) return false;
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            IReadOnlyList<int> owners = FindListenerOwners(port);
            if (owners.Count == 0) return true;
            return owners.All(owner => owner == expectedPid);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Security-boundary ownership check for any path that is about to send user/model context to
    /// the managed runtime endpoint. Unlike the health compatibility helper above, absence of a
    /// listener or inability to read the Windows owner table is a refusal, never permission.
    ///
    /// The call intentionally performs a fresh TCP-table read every time. A cached positive result
    /// created a real takeover window: PaRuX's child could exit and a foreign process could claim
    /// port 1234 while the old <c>owned=true</c> remained valid for two seconds. Negative caching
    /// is harmless, but positive caching is not acceptable on the prompt-egress boundary.
    /// </summary>
    internal static bool IsStrictlyOwnedBy(int port, int expectedPid)
    {
        if (expectedPid <= 0) return false;
        if (!OperatingSystem.IsWindows()) return false;

        return IsStrictlyOwnedBy(port, expectedPid, ReadListenerOwnersForFamily);
    }

    /// <summary>
    /// Test seam for the two independent address-family table reads. A failed family read must
    /// make the ownership answer false even if the other family reports only the expected pid.
    /// </summary>
    internal static bool IsStrictlyOwnedBy(
        int port,
        int expectedPid,
        Func<int, int, IReadOnlyList<int>> readFamilyOwners)
    {
        if (expectedPid <= 0) return false;
        try
        {
            var owners = new List<int>();
            owners.AddRange(readFamilyOwners(port, AfInet));
            owners.AddRange(readFamilyOwners(port, AfInet6));
            return owners.Count > 0 && owners.All(owner => owner == expectedPid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when PaRuX Core's own process is serving the port.
    ///
    /// In production Core listens on 51123 and never on the runtime port, so this is normally
    /// false. It is true when a host serves the managed port from inside this process - which is
    /// how the overhead benchmarks provide an inert runtime - and in that case the listener really
    /// is PaRuX, so treating it as foreign would be wrong on the facts rather than merely
    /// inconvenient. Stated as a rule about who owns the socket, not as a test escape hatch.
    /// </summary>
    internal static bool IsOwnedByThisProcess(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            IReadOnlyList<int> owners = FindListenerOwners(port);
            return owners.Count > 0 && owners.All(owner => owner == Environment.ProcessId);
        }
        catch { return false; }
    }

    /// <summary>The single pid listening on <paramref name="port"/>, or null when there is not
    /// exactly one. Kept for callers that want to name the process in a message.</summary>
    internal static int? FindListenerOwner(int port)
    {
        IReadOnlyList<int> owners = FindListenerOwners(port);
        return owners.Count == 1 ? owners[0] : null;
    }

    /// <summary>Every pid listening on <paramref name="port"/>. More than one is possible, and is
    /// exactly the case this class exists to catch.</summary>
    internal static IReadOnlyList<int> FindListenerOwners(int port)
    {
        var owners = new List<int>();
        AppendListenerOwners(owners, port, AfInet);
        AppendListenerOwners(owners, port, AfInet6);
        return owners;
    }

    private static void AppendListenerOwners(List<int> owners, int port, int addressFamily)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
        if (result == ErrorNoData || result == NoError && size == 0) return;
        if (result != ErrorInsufficientBuffer && result != NoError)
            throw new InvalidOperationException($"Could not read the IPv{(addressFamily == AfInet ? 4 : 6)} TCP listener table (error {result}).");
        if (size < sizeof(int))
            throw new InvalidOperationException("The TCP listener table returned an invalid buffer size.");

        int allocatedSize = size;
        IntPtr table = Marshal.AllocHGlobal(allocatedSize);
        try
        {
            result = GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
            if (result == ErrorNoData) return;
            if (result != NoError)
                throw new InvalidOperationException($"Could not read the IPv{(addressFamily == AfInet ? 4 : 6)} TCP listener table (error {result}).");
            if (size < sizeof(int) || size > allocatedSize)
                throw new InvalidOperationException("The TCP listener table returned an invalid buffer size.");

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + 4;
            if (addressFamily == AfInet)
            {
                int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
                ValidateRowCount(count, size, rowSize);
                for (int index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<TcpRowOwnerPid>(row + (index * rowSize));
                    int localPort = (int)(((entry.LocalPort & 0xFF) << 8) | ((entry.LocalPort >> 8) & 0xFF));
                    if (localPort != port) continue;
                    int owner = (int)entry.OwningPid;
                    if (!owners.Contains(owner)) owners.Add(owner);
                }
            }
            else if (addressFamily == AfInet6)
            {
                int rowSize = Marshal.SizeOf<Tcp6RowOwnerPid>();
                ValidateRowCount(count, size, rowSize);
                for (int index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<Tcp6RowOwnerPid>(row + (index * rowSize));
                    int localPort = (int)(((entry.LocalPort & 0xFF) << 8) | ((entry.LocalPort >> 8) & 0xFF));
                    if (localPort != port) continue;
                    int owner = (int)entry.OwningPid;
                    if (!owners.Contains(owner)) owners.Add(owner);
                }
            }
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    /// <summary>
    /// Closes the check-to-connect window. <see cref="IsStrictlyOwnedBy(int,int)"/> proves who was
    /// listening at the moment of the table read; the connection that follows may land on a process
    /// that bound the port in between (SO_REUSEADDR makes that possible). After connecting, the
    /// server-side endpoint of THIS connection (local port = server port, remote port = the client's
    /// ephemeral port) is looked up in the connection table, and the connection is accepted only
    /// when every matching row belongs to <paramref name="expectedPid"/>. Missing rows, unreadable
    /// tables or non-Windows hosts are refusals.
    /// </summary>
    internal static bool IsConnectedPeerStrictlyOwnedBy(int serverPort, int clientLocalPort, int expectedPid)
    {
        if (expectedPid <= 0 || !OperatingSystem.IsWindows()) return false;
        return IsConnectedPeerStrictlyOwnedBy(serverPort, clientLocalPort, expectedPid, ReadConnectionOwnersForFamily);
    }

    internal static bool IsConnectedPeerStrictlyOwnedBy(
        int serverPort,
        int clientLocalPort,
        int expectedPid,
        Func<int, int, int, IReadOnlyList<int>> readFamilyConnectionOwners)
    {
        if (expectedPid <= 0) return false;
        try
        {
            var owners = new List<int>();
            owners.AddRange(readFamilyConnectionOwners(serverPort, clientLocalPort, AfInet));
            owners.AddRange(readFamilyConnectionOwners(serverPort, clientLocalPort, AfInet6));
            return owners.Count > 0 && owners.All(owner => owner == expectedPid);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<int> ReadConnectionOwnersForFamily(int serverPort, int clientPort, int addressFamily)
    {
        var owners = new List<int>();
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
        if (result == ErrorNoData || result == NoError && size == 0) return owners;
        if (result != ErrorInsufficientBuffer && result != NoError)
            throw new InvalidOperationException($"Could not read the TCP connection table (error {result}).");
        // The table can grow between the sizing call and the read; retry with slack.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int allocated = size + 4096;
            IntPtr table = Marshal.AllocHGlobal(allocated);
            try
            {
                size = allocated;
                result = GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
                if (result == ErrorInsufficientBuffer) continue;
                if (result == ErrorNoData) return owners;
                if (result != NoError)
                    throw new InvalidOperationException($"Could not read the TCP connection table (error {result}).");
                int count = Marshal.ReadInt32(table);
                IntPtr row = table + 4;
                bool v4 = addressFamily == AfInet;
                int rowSize = v4 ? Marshal.SizeOf<TcpRowOwnerPid>() : Marshal.SizeOf<Tcp6RowOwnerPid>();
                ValidateRowCount(count, size, rowSize);
                for (int index = 0; index < count; index++)
                {
                    uint local, remote, pid;
                    if (v4)
                    {
                        var e = Marshal.PtrToStructure<TcpRowOwnerPid>(row + index * rowSize);
                        local = e.LocalPort; remote = e.RemotePort; pid = e.OwningPid;
                    }
                    else
                    {
                        var e = Marshal.PtrToStructure<Tcp6RowOwnerPid>(row + index * rowSize);
                        local = e.LocalPort; remote = e.RemotePort; pid = e.OwningPid;
                    }
                    if (SwapPort(local) == serverPort && SwapPort(remote) == clientPort)
                        owners.Add((int)pid);
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new InvalidOperationException("The TCP connection table kept changing size.");
    }

    private static int SwapPort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static IReadOnlyList<int> ReadListenerOwnersForFamily(int port, int addressFamily)
    {
        var owners = new List<int>();
        AppendListenerOwners(owners, port, addressFamily);
        return owners;
    }

    internal static void ValidateRowCount(int count, int bufferSize, int rowSize)
    {
        if (count < 0 || rowSize <= 0 || count > (bufferSize - sizeof(int)) / rowSize)
            throw new InvalidOperationException("The TCP listener table returned an invalid row count.");
    }

    /// <summary>Whether anything at all is listening - the existing cheap probe, kept beside its sibling.</summary>
    internal static bool IsPortListening(int port)
    {
        // On Windows use the same complete IPv4 + IPv6 owner-table reads as the trust boundary.
        // A failed read is unknown, so health stays fail-closed instead of falling through to HTTP.
        if (OperatingSystem.IsWindows())
            return IsPortListening(port, ReadListenerOwnersForFamily);

        try
        {
            foreach (IPEndPoint endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port) return true;
            }
            return false;
        }
        catch
        {
            // Being unable to read the table is not evidence that nothing is there.
            return true;
        }
    }

    /// <summary>Test seam for presence semantics: both family reads must complete, and either
    /// family may establish presence. A failed read never grants health.</summary>
    internal static bool IsPortListening(int port, Func<int, int, IReadOnlyList<int>> readFamilyOwners)
    {
        try
        {
            IReadOnlyList<int> ipv4 = readFamilyOwners(port, AfInet);
            IReadOnlyList<int> ipv6 = readFamilyOwners(port, AfInet6);
            return ipv4.Count > 0 || ipv6.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerPidAll = 5;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNoData = 232;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, bool order, int addressFamily, int tableClass, int reserved);
}
