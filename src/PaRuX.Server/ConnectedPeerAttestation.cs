using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaRuX.Server;

/// <summary>
/// The exact process PaRuX expects to be on the other end of a loopback connection. A bare PID is
/// never an identity: Windows recycles PIDs, so the creation time and the executable image path
/// are part of it.
/// </summary>
internal readonly record struct ExpectedPeerIdentity(int ProcessId, long StartTimeUtcTicks, string ImagePath);

internal enum PeerAttestationStatus
{
    Attested,
    /// <summary>The platform has no connection-owner API. Never reported as attested.</summary>
    Unsupported,
    NotLoopback,
    SocketNotConnected,
    TableUnreadable,
    NoConnectionRow,
    AmbiguousOwner,
    InvalidOwner,
    NoExpectedPeer,
    ForeignPeer,
    ProcessIdentityUnreadable,
    IdentityMismatch,
    PeerExited,
    OwnershipChanged,
}

internal readonly record struct PeerAttestationResult(PeerAttestationStatus Status, int? OwnerProcessId, string Detail)
{
    public bool IsAttested => Status == PeerAttestationStatus.Attested;
}

/// <summary>Reads which processes own the server side of one established loopback connection.</summary>
internal interface IPeerOwnershipSource
{
    /// <summary>
    /// Returns false when the OS table cannot be read. <paramref name="owners"/> holds the distinct
    /// owning PIDs of ESTABLISHED rows whose local endpoint is <paramref name="serverSide"/> and
    /// whose remote endpoint is <paramref name="clientSide"/>.
    /// </summary>
    bool TryReadEstablishedOwners(IPEndPoint serverSide, IPEndPoint clientSide, out IReadOnlyList<int> owners);
}

/// <summary>A process handle that pins its PID (a PID cannot be recycled while a handle is open).</summary>
internal interface IPeerProcessLease : IDisposable
{
    long StartTimeUtcTicks { get; }
    string ImagePath { get; }
    bool HasExited { get; }
}

internal interface IPeerProcessInspector
{
    /// <summary>Opens the process with query-only rights. Never elevates; access denied is a refusal.</summary>
    bool TryOpen(int processId, out IPeerProcessLease lease);
}

/// <summary>
/// Proves WHO answered a freshly connected loopback socket, closing the preflight-to-connect race
/// that a port-owner check cannot close (a foreign process can bind the port between "who listens
/// on 1234" and "connect to 1234").
///
/// The proof is made on the connected socket itself: the exact 4-tuple of the established
/// connection is looked up in the OS TCP table (IPv4 or IPv6), and the owner PID of the SERVER side
/// row must be a process whose PID, creation time and image path all match an expected identity.
/// It runs inside SocketsHttpHandler.ConnectCallback, so a refused socket is disposed before any
/// HTTP bytes (prompt, headers, bodies) are written to it. Pooled connections keep the peer they
/// were attested against; any replacement connection is attested again.
///
/// Fail-closed: unreadable table, no row, several distinct owners, owner PID 0, unreadable process
/// identity, exited process or a changed owner on re-read all refuse. Nothing here elevates
/// privileges, opens a process for anything but query, or terminates any process.
///
/// Time-of-check/time-of-use handling: (1) read owner PID from the table, (2) open a handle to that
/// PID, which pins the PID so it cannot be reused for another process from now on, (3) verify
/// start time and image against the expected identity through that handle, (4) re-read the table
/// and require the same single owner and a still-running process. Step 4 is what proves the row
/// observed in step 1 belonged to the process the handle refers to, not a recycled PID.
///
/// Residual (documented, not attested): a process that legitimately owns the connection could
/// hand the socket to another process via handle duplication; the owner PID in the table would
/// stay the duplicator's. That requires code running as the expected process and is outside this
/// boundary. Non-Windows hosts have no equivalent table API here and report Unsupported.
/// </summary>
internal sealed class ConnectedPeerAttestation
{
    private readonly IPeerOwnershipSource _table;
    private readonly IPeerProcessInspector _inspector;

    internal ConnectedPeerAttestation(IPeerOwnershipSource table, IPeerProcessInspector inspector)
    {
        _table = table;
        _inspector = inspector;
    }

    /// <summary>The production Windows implementation; null where there is no OS attestation.</summary>
    internal static ConnectedPeerAttestation? CreateForCurrentPlatform() =>
        OperatingSystem.IsWindows()
            ? new ConnectedPeerAttestation(new WindowsTcpOwnershipSource(), new WindowsPeerProcessInspector())
            : null;

    internal PeerAttestationResult Attest(Socket connected, IReadOnlyList<ExpectedPeerIdentity> expected)
    {
        IPEndPoint? client;
        IPEndPoint? server;
        try
        {
            if (!connected.Connected) return Refuse(PeerAttestationStatus.SocketNotConnected, "socket is not connected");
            client = connected.LocalEndPoint as IPEndPoint;
            server = connected.RemoteEndPoint as IPEndPoint;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            return Refuse(PeerAttestationStatus.SocketNotConnected, "socket endpoints unreadable");
        }

        if (client is null || server is null)
            return Refuse(PeerAttestationStatus.SocketNotConnected, "socket endpoints unavailable");

        client = Normalize(client);
        server = Normalize(server);
        if (!IPAddress.IsLoopback(server.Address) || !IPAddress.IsLoopback(client.Address))
            return Refuse(PeerAttestationStatus.NotLoopback, "peer is not a loopback endpoint");

        if (expected.Count == 0)
            return Refuse(PeerAttestationStatus.NoExpectedPeer, "no expected peer identity is known");

        // Step 1: who owns the server side of this exact connection right now.
        if (!TryReadSingleOwner(server, client, out int owner, out PeerAttestationResult failure)) return failure;

        // Step 2: pin the PID.
        if (!_inspector.TryOpen(owner, out IPeerProcessLease? lease))
            return Refuse(PeerAttestationStatus.ProcessIdentityUnreadable, "owner process could not be opened", owner);

        using (lease)
        {
            // Step 3: identity through the pinned handle.
            ExpectedPeerIdentity? match = null;
            bool pidKnown = false;
            foreach (ExpectedPeerIdentity candidate in expected)
            {
                if (candidate.ProcessId != owner) continue;
                pidKnown = true;
                if (candidate.StartTimeUtcTicks > 0
                    && candidate.StartTimeUtcTicks == lease.StartTimeUtcTicks
                    && SameImage(candidate.ImagePath, lease.ImagePath))
                {
                    match = candidate;
                    break;
                }
            }

            if (match is null)
            {
                return pidKnown
                    ? Refuse(PeerAttestationStatus.IdentityMismatch,
                        "owner PID matches an expected peer but start time or image differs (PID reuse)", owner)
                    : Refuse(PeerAttestationStatus.ForeignPeer, "owner is not an expected peer", owner);
            }

            if (lease.HasExited) return Refuse(PeerAttestationStatus.PeerExited, "owner process has exited", owner);

            // Step 4: the row must still be the same, owned by the same single pinned process.
            if (!TryReadSingleOwner(server, client, out int again, out PeerAttestationResult reread))
                return reread.Status == PeerAttestationStatus.NoConnectionRow
                    ? Refuse(PeerAttestationStatus.OwnershipChanged, "connection row vanished on re-read", owner)
                    : reread;
            if (again != owner) return Refuse(PeerAttestationStatus.OwnershipChanged, "owner changed on re-read", owner);
            if (lease.HasExited) return Refuse(PeerAttestationStatus.PeerExited, "owner process exited during check", owner);

            return new PeerAttestationResult(PeerAttestationStatus.Attested, owner, "attested");
        }
    }

    private bool TryReadSingleOwner(IPEndPoint server, IPEndPoint client, out int owner, out PeerAttestationResult failure)
    {
        owner = 0;
        failure = default;
        IReadOnlyList<int> owners;
        try
        {
            if (!_table.TryReadEstablishedOwners(server, client, out owners))
            {
                failure = Refuse(PeerAttestationStatus.TableUnreadable, "TCP owner table unreadable");
                return false;
            }
        }
        catch
        {
            failure = Refuse(PeerAttestationStatus.TableUnreadable, "TCP owner table read failed");
            return false;
        }

        if (owners.Count == 0)
        {
            failure = Refuse(PeerAttestationStatus.NoConnectionRow, "no established connection row for this tuple");
            return false;
        }

        if (owners.Distinct().Count() != 1)
        {
            failure = Refuse(PeerAttestationStatus.AmbiguousOwner, "several processes own this tuple");
            return false;
        }

        if (owners[0] <= 0)
        {
            failure = Refuse(PeerAttestationStatus.InvalidOwner, "owner PID is not a real process");
            return false;
        }

        owner = owners[0];
        return true;
    }

    private static PeerAttestationResult Refuse(PeerAttestationStatus status, string detail, int? owner = null) =>
        new(status, owner, detail);

    internal static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

    internal static bool SameImage(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(expected), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Captures the identity of a live process with the same reader that attestation uses, so a
    /// recorded identity and a later verification can never disagree on representation.
    /// </summary>
    internal static ExpectedPeerIdentity? TryCaptureIdentity(int processId, IPeerProcessInspector? inspector = null)
    {
        inspector ??= OperatingSystem.IsWindows() ? new WindowsPeerProcessInspector() : null;
        if (inspector is null || processId <= 0) return null;
        try
        {
            if (!inspector.TryOpen(processId, out IPeerProcessLease? lease)) return null;
            using (lease)
            {
                if (lease.HasExited || lease.StartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(lease.ImagePath))
                    return null;
                return new ExpectedPeerIdentity(processId, lease.StartTimeUtcTicks, lease.ImagePath);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Raised from the connect callback when a connected socket is not the expected peer.</summary>
internal sealed class PeerAttestationException : InvalidOperationException
{
    internal PeerAttestationException(PeerAttestationResult result)
        : base("Local runtime identity not verified: refused to use the loopback connection because its peer is not the PaRuX-managed runtime ("
               + result.Status + ": " + result.Detail + "). No request data was sent.")
    {
        Result = result;
    }

    internal PeerAttestationResult Result { get; }
}

/// <summary>
/// Builds the handler used for every HTTP call that carries prompt content to the managed runtime
/// endpoint. Only literal loopback targets (and "localhost") are connectable; proxies and redirects
/// are disabled so the socket the callback attests is the socket the request is written to.
/// </summary>
internal static class AttestedLoopbackHttp
{
    private static int _unsupportedLogged;

    internal static SocketsHttpHandler CreateHandler(
        Func<IReadOnlyList<ExpectedPeerIdentity>> expectedPeers,
        ConnectedPeerAttestation? attestation = null,
        Action<PeerAttestationResult>? observer = null,
        bool allowUnattestedWhenPlatformUnsupported = true,
        Func<int, int, int, bool>? connectedPeerCheck = null) =>
        CreateHandler(_ => expectedPeers(), attestation, observer, allowUnattestedWhenPlatformUnsupported, connectedPeerCheck);

    /// <summary>
    /// <paramref name="expectedPeers"/> receives the exact server endpoint being connected, so the
    /// trusted set can depend on which listener is being talked to, and is evaluated on every new
    /// connection (never cached).
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(
        Func<IPEndPoint, IReadOnlyList<ExpectedPeerIdentity>> expectedPeers,
        ConnectedPeerAttestation? attestation = null,
        Action<PeerAttestationResult>? observer = null,
        bool allowUnattestedWhenPlatformUnsupported = true,
        Func<int, int, int, bool>? connectedPeerCheck = null)
    {
        attestation ??= ConnectedPeerAttestation.CreateForCurrentPlatform();
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            // A pooled connection stays attested only for its own lifetime; recycling forces a
            // fresh identity check against whatever is expected then.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context, expectedPeers, attestation, observer, allowUnattestedWhenPlatformUnsupported,
                connectedPeerCheck, cancellationToken),
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        Func<IPEndPoint, IReadOnlyList<ExpectedPeerIdentity>> expectedPeers,
        ConnectedPeerAttestation? attestation,
        Action<PeerAttestationResult>? observer,
        bool allowUnattestedWhenPlatformUnsupported,
        Func<int, int, int, bool>? connectedPeerCheck,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DnsEndPoint target = context.DnsEndPoint;
        IReadOnlyList<IPAddress> addresses = ResolveLoopbackTargets(target.Host);
        Exception? lastConnectFailure = null;

        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            bool handedOff = false;
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (attestation is null)
                {
                    if (Interlocked.Exchange(ref _unsupportedLogged, 1) == 0)
                        System.Diagnostics.Trace.TraceWarning(
                            "PaRuX connected-peer attestation is unavailable on this platform; loopback runtime connections are "
                            + (allowUnattestedWhenPlatformUnsupported ? "NOT attested." : "refused."));
                    if (!allowUnattestedWhenPlatformUnsupported)
                        throw new PeerAttestationException(new PeerAttestationResult(
                            PeerAttestationStatus.Unsupported, null, "no OS attestation on this platform"));
                    observer?.Invoke(new PeerAttestationResult(
                        PeerAttestationStatus.Unsupported, null, "unattested (platform unsupported)"));
                }
                else
                {
                    IReadOnlyList<ExpectedPeerIdentity> expected;
                    try { expected = expectedPeers(new IPEndPoint(address, target.Port)); }
                    catch { expected = Array.Empty<ExpectedPeerIdentity>(); }

                    PeerAttestationResult result = attestation.Attest(socket, expected);
                    observer?.Invoke(result);
                    if (!result.IsAttested) throw new PeerAttestationException(result);
                    // Independent second proof on the established socket: every connection-table row for this exact
                    // tuple must belong to the attested owner. Fail closed (no request bytes are written yet).
                    if (connectedPeerCheck is not null)
                    {
                        int clientPort = socket.LocalEndPoint is IPEndPoint local ? local.Port : 0;
                        bool owned;
                        try { owned = result.OwnerProcessId is int pid && clientPort > 0 && connectedPeerCheck(target.Port, clientPort, pid); }
                        catch { owned = false; }
                        if (!owned)
                            throw new PeerAttestationException(new PeerAttestationResult(
                                PeerAttestationStatus.OwnershipChanged, result.OwnerProcessId,
                                "connected-peer ownership re-check refused the established connection"));
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                handedOff = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (PeerAttestationException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastConnectFailure = ex;
            }
            finally
            {
                // A refused, cancelled or failed socket is closed here, before any byte is written.
                if (!handedOff) socket.Dispose();
            }
        }

        throw new HttpRequestException(
            "Could not connect to the loopback runtime endpoint.", lastConnectFailure);
    }

    private static IReadOnlyList<IPAddress> ResolveLoopbackTargets(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };

        string trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out IPAddress? literal) && IPAddress.IsLoopback(literal))
            return new[] { literal };

        throw new HttpRequestException(
            "PaRuX only sends runtime requests to a loopback endpoint; '" + host + "' is not one.");
    }
}

/// <summary>Expected peers for the PaRuX-managed text runtime.</summary>
internal static class ManagedRuntimePeers
{
    private static readonly Lazy<ExpectedPeerIdentity?> Core = new(
        () => ConnectedPeerAttestation.TryCaptureIdentity(Environment.ProcessId),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The runtime PaRuX launched: the identity recorded by LocalAiProcessManager at Process.Start
    /// (PID + creation time + image path), never a PID read back from a file. Empty when nothing
    /// has been launched in this Core session, which makes attestation refuse.
    /// </summary>
    internal static IReadOnlyList<ExpectedPeerIdentity> Current() =>
        LocalAiProcessManager.TryGetTextLaunchIdentity(out ExpectedPeerIdentity launched)
            ? new[] { launched }
            : Array.Empty<ExpectedPeerIdentity>();

    /// <summary>
    /// Fresh on every connection. Core's own identity is added only when Core itself is the sole
    /// listener on that exact port (the in-process benchmark/test runtime), because only then is
    /// "Core answered" the same thing as "PaRuX answered". The connection owner must still equal
    /// the identity; Core is never a trusted peer for a port somebody else listens on.
    /// </summary>
    internal static IReadOnlyList<ExpectedPeerIdentity> ForEndpoint(IPEndPoint server)
    {
        IReadOnlyList<ExpectedPeerIdentity> launched = Current();
        if (Core.Value is not { } core || !ManagedListenerOwnership.IsOwnedByThisProcess(server.Port))
            return launched;
        return new List<ExpectedPeerIdentity>(launched) { core };
    }

    /// <summary>Core ships Windows-only, so a platform without OS attestation refuses rather than passes through.</summary>
    internal static HttpMessageHandler CreateHandler() => AttestedLoopbackHttp.CreateHandler(
        ForEndpoint, allowUnattestedWhenPlatformUnsupported: false,
        connectedPeerCheck: ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy);
}

internal sealed class WindowsTcpOwnershipSource : IPeerOwnershipSource
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint StateEstablished = 5;

    public bool TryReadEstablishedOwners(IPEndPoint serverSide, IPEndPoint clientSide, out IReadOnlyList<int> owners)
    {
        owners = Array.Empty<int>();
        if (!OperatingSystem.IsWindows()) return false;
        if (serverSide.AddressFamily != clientSide.AddressFamily) return false;

        bool v4 = serverSide.AddressFamily == AddressFamily.InterNetwork;
        if (!v4 && serverSide.AddressFamily != AddressFamily.InterNetworkV6) return false;

        int family = v4 ? AfInet : AfInet6;
        IntPtr table = IntPtr.Zero;
        try
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidConnections, 0);
            // The table grows between calls under load; retry with the size the OS reported.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (result != ErrorInsufficientBuffer && result != NoError) return false;
                if (table != IntPtr.Zero) { Marshal.FreeHGlobal(table); table = IntPtr.Zero; }
                size += 1024;
                table = Marshal.AllocHGlobal(size);
                result = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidConnections, 0);
                if (result == NoError) break;
            }

            if (result != NoError) return false;

            // One copy, then span reads: no per-row marshalling allocations.
            var raw = new byte[size];
            Marshal.Copy(table, raw, 0, size);
            ReadOnlySpan<byte> data = raw;
            int count = BinaryPrimitives.ReadInt32LittleEndian(data);
            int rowSize = v4 ? 24 : 56;
            if (count < 0 || 4L + ((long)count * rowSize) > size) return false;

            Span<byte> serverBytes = stackalloc byte[16];
            Span<byte> clientBytes = stackalloc byte[16];
            if (!serverSide.Address.TryWriteBytes(serverBytes, out int serverLength)
                || !clientSide.Address.TryWriteBytes(clientBytes, out int clientLength)) return false;
            ReadOnlySpan<byte> serverAddress = serverBytes[..serverLength];
            ReadOnlySpan<byte> clientAddress = clientBytes[..clientLength];

            var found = new List<int>(2);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> row = data.Slice(4 + (i * rowSize), rowSize);
                if (v4)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(row) != StateEstablished) continue;
                    if (NetworkPort(BinaryPrimitives.ReadUInt32LittleEndian(row[8..])) != serverSide.Port
                        || NetworkPort(BinaryPrimitives.ReadUInt32LittleEndian(row[16..])) != clientSide.Port) continue;
                    if (!row.Slice(4, 4).SequenceEqual(serverAddress) || !row.Slice(12, 4).SequenceEqual(clientAddress)) continue;
                    found.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
                }
                else
                {
                    // localAddr[16] localScope localPort remoteAddr[16] remoteScope remotePort state pid
                    if (BinaryPrimitives.ReadUInt32LittleEndian(row[48..]) != StateEstablished) continue;
                    if (NetworkPort(BinaryPrimitives.ReadUInt32LittleEndian(row[20..])) != serverSide.Port
                        || NetworkPort(BinaryPrimitives.ReadUInt32LittleEndian(row[44..])) != clientSide.Port) continue;
                    if (!row[..16].SequenceEqual(serverAddress) || !row.Slice(24, 16).SequenceEqual(clientAddress)) continue;
                    found.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(row[52..]));
                }
            }

            owners = found;
            return true;
        }
        finally
        {
            if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
        }
    }

    private static int NetworkPort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, bool order, int addressFamily, int tableClass, int reserved);
}

internal sealed class WindowsPeerProcessInspector : IPeerProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    public bool TryOpen(int processId, out IPeerProcessLease lease)
    {
        lease = null!;
        if (!OperatingSystem.IsWindows() || processId <= 0) return false;

        IntPtr raw = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)processId);
        if (raw == IntPtr.Zero) return false;
        var handle = new SafeProcessHandle(raw, ownsHandle: true);
        try
        {
            if (!GetProcessTimes(handle, out long creation, out _, out _, out _) || creation <= 0)
            {
                handle.Dispose();
                return false;
            }

            var buffer = new StringBuilder(1024);
            int length = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length) || length <= 0)
            {
                handle.Dispose();
                return false;
            }

            lease = new Lease(handle, DateTime.FromFileTimeUtc(creation).Ticks, buffer.ToString(0, length));
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentOutOfRangeException)
        {
            handle.Dispose();
            return false;
        }
    }

    private sealed class Lease : IPeerProcessLease
    {
        private readonly SafeProcessHandle _handle;

        public Lease(SafeProcessHandle handle, long startTicks, string imagePath)
        {
            _handle = handle;
            StartTimeUtcTicks = startTicks;
            ImagePath = imagePath;
        }

        public long StartTimeUtcTicks { get; }
        public string ImagePath { get; }

        public bool HasExited
        {
            get
            {
                // Unreadable liveness is "exited": fail closed.
                try { return WaitForSingleObject(_handle, 0) != 0x102 /* WAIT_TIMEOUT */; }
                catch { return true; }
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
