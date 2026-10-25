using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Connected-peer attestation (BIC-15 S1). Unit tests drive the decision logic with fake OS
/// sources; integration tests use REAL child processes and REAL loopback listeners (IPv4 and IPv6)
/// and assert the security property that matters: a foreign process that holds the port at connect
/// time receives zero bytes.
/// </summary>
public sealed class ConnectedPeerAttestationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _cleanup = new();

    public ConnectedPeerAttestationTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (IDisposable item in _cleanup) { try { item.Dispose(); } catch { } }
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeTable : IPeerOwnershipSource
    {
        public Queue<Func<(bool Ok, int[] Owners)>> Reads { get; } = new();
        public Func<(bool Ok, int[] Owners)> Default { get; set; } = () => (true, Array.Empty<int>());
        public int ReadCount { get; private set; }

        public bool TryReadEstablishedOwners(IPEndPoint serverSide, IPEndPoint clientSide, out IReadOnlyList<int> owners)
        {
            ReadCount++;
            (bool ok, int[] list) = Reads.Count > 0 ? Reads.Dequeue()() : Default();
            owners = list;
            return ok;
        }
    }

    private sealed class FakeInspector : IPeerProcessInspector
    {
        public Dictionary<int, (long Start, string Image, bool Exited)> Processes { get; } = new();

        public bool TryOpen(int processId, out IPeerProcessLease lease)
        {
            lease = null!;
            if (!Processes.TryGetValue(processId, out var p)) return false;
            lease = new FakeLease(p.Start, p.Image, p.Exited);
            return true;
        }
    }

    private sealed class FakeLease : IPeerProcessLease
    {
        public FakeLease(long start, string image, bool exited) { StartTimeUtcTicks = start; ImagePath = image; HasExited = exited; }
        public long StartTimeUtcTicks { get; }
        public string ImagePath { get; }
        public bool HasExited { get; }
        public void Dispose() { }
    }

    private const string Image = @"C:\runtime\llama-server.exe";
    private static ExpectedPeerIdentity Expected(int pid = 4242, long start = 1000, string image = Image) => new(pid, start, image);

    private (Socket Client, Socket Server) ConnectedPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));
        Socket server = listener.AcceptSocket();
        listener.Stop();
        _cleanup.Add(client);
        _cleanup.Add(server);
        return (client, server);
    }

    private PeerAttestationResult Attest(FakeTable table, FakeInspector inspector, params ExpectedPeerIdentity[] expected)
    {
        (Socket client, _) = ConnectedPair();
        return new ConnectedPeerAttestation(table, inspector).Attest(client, expected);
    }

    private static FakeInspector InspectorWith(int pid = 4242, long start = 1000, string image = Image, bool exited = false)
    {
        var inspector = new FakeInspector();
        inspector.Processes[pid] = (start, image, exited);
        return inspector;
    }

    private static FakeTable TableOwning(params int[] owners) => new() { Default = () => (true, owners) };

    // ---------------------------------------------------------------- unit: decision logic

    [Fact]
    public void Exact_pid_start_time_and_image_is_attested_with_a_re_read()
    {
        var table = TableOwning(4242);
        PeerAttestationResult result = Attest(table, InspectorWith(), Expected());

        Assert.Equal(PeerAttestationStatus.Attested, result.Status);
        Assert.Equal(4242, result.OwnerProcessId);
        Assert.Equal(2, table.ReadCount); // read, pin handle, verify, re-read
    }

    [Fact]
    public void Image_path_comparison_ignores_case_and_normalises_the_path()
    {
        PeerAttestationResult result = Attest(
            TableOwning(4242), InspectorWith(image: @"c:\RUNTIME\.\llama-server.EXE"), Expected());
        Assert.True(result.IsAttested);
    }

    [Fact]
    public void A_foreign_pid_is_refused()
    {
        PeerAttestationResult result = Attest(TableOwning(777), InspectorWith(777), Expected());
        Assert.Equal(PeerAttestationStatus.ForeignPeer, result.Status);
    }

    [Fact]
    public void A_recycled_pid_with_a_different_start_time_is_refused()
    {
        PeerAttestationResult result = Attest(TableOwning(4242), InspectorWith(start: 1001), Expected(start: 1000));
        Assert.Equal(PeerAttestationStatus.IdentityMismatch, result.Status);
    }

    [Fact]
    public void A_recycled_pid_with_a_different_image_is_refused()
    {
        PeerAttestationResult result = Attest(
            TableOwning(4242), InspectorWith(image: @"C:\elsewhere\llama-server.exe"), Expected());
        Assert.Equal(PeerAttestationStatus.IdentityMismatch, result.Status);
    }

    [Fact]
    public void An_expected_identity_without_a_start_time_never_matches()
    {
        PeerAttestationResult result = Attest(TableOwning(4242), InspectorWith(start: 0), Expected(start: 0));
        Assert.Equal(PeerAttestationStatus.IdentityMismatch, result.Status);
    }

    [Fact]
    public void No_expected_peer_is_refused_before_any_table_read()
    {
        var table = TableOwning(4242);
        PeerAttestationResult result = Attest(table, InspectorWith());
        Assert.Equal(PeerAttestationStatus.NoExpectedPeer, result.Status);
        Assert.Equal(0, table.ReadCount);
    }

    [Fact]
    public void An_unreadable_table_is_refused()
    {
        var table = new FakeTable { Default = () => (false, Array.Empty<int>()) };
        Assert.Equal(PeerAttestationStatus.TableUnreadable, Attest(table, InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void A_table_read_that_throws_is_refused()
    {
        var table = new FakeTable { Default = () => throw new InvalidOperationException("boom") };
        Assert.Equal(PeerAttestationStatus.TableUnreadable, Attest(table, InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void A_missing_connection_row_is_refused()
    {
        Assert.Equal(PeerAttestationStatus.NoConnectionRow, Attest(TableOwning(), InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void Several_distinct_owners_are_refused_as_ambiguous()
    {
        Assert.Equal(
            PeerAttestationStatus.AmbiguousOwner,
            Attest(TableOwning(4242, 777), InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void Duplicate_rows_for_the_same_owner_are_not_ambiguous()
    {
        Assert.True(Attest(TableOwning(4242, 4242), InspectorWith(), Expected()).IsAttested);
    }

    [Fact]
    public void Owner_pid_zero_is_refused()
    {
        Assert.Equal(PeerAttestationStatus.InvalidOwner, Attest(TableOwning(0), InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void An_owner_process_that_cannot_be_opened_is_refused()
    {
        Assert.Equal(
            PeerAttestationStatus.ProcessIdentityUnreadable,
            Attest(TableOwning(4242), new FakeInspector(), Expected()).Status);
    }

    [Fact]
    public void An_exited_owner_is_refused()
    {
        Assert.Equal(
            PeerAttestationStatus.PeerExited,
            Attest(TableOwning(4242), InspectorWith(exited: true), Expected()).Status);
    }

    [Fact]
    public void An_owner_that_changes_between_read_and_re_read_is_refused()
    {
        var table = new FakeTable();
        table.Reads.Enqueue(() => (true, new[] { 4242 }));
        table.Reads.Enqueue(() => (true, new[] { 777 }));
        Assert.Equal(PeerAttestationStatus.OwnershipChanged, Attest(table, InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void A_row_that_vanishes_on_re_read_is_refused()
    {
        var table = new FakeTable();
        table.Reads.Enqueue(() => (true, new[] { 4242 }));
        table.Reads.Enqueue(() => (true, Array.Empty<int>()));
        Assert.Equal(PeerAttestationStatus.OwnershipChanged, Attest(table, InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void A_table_that_becomes_unreadable_on_re_read_is_refused()
    {
        var table = new FakeTable();
        table.Reads.Enqueue(() => (true, new[] { 4242 }));
        table.Reads.Enqueue(() => (false, Array.Empty<int>()));
        Assert.Equal(PeerAttestationStatus.TableUnreadable, Attest(table, InspectorWith(), Expected()).Status);
    }

    [Fact]
    public void A_closed_socket_is_refused()
    {
        (Socket client, Socket server) = ConnectedPair();
        client.Dispose();
        var result = new ConnectedPeerAttestation(TableOwning(4242), InspectorWith()).Attest(client, new[] { Expected() });
        Assert.Equal(PeerAttestationStatus.SocketNotConnected, result.Status);
        server.Dispose();
    }

    [Fact]
    public void An_unconnected_socket_is_refused()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _cleanup.Add(socket);
        var result = new ConnectedPeerAttestation(TableOwning(4242), InspectorWith()).Attest(socket, new[] { Expected() });
        Assert.Equal(PeerAttestationStatus.SocketNotConnected, result.Status);
    }

    [Fact]
    public void Ipv4_mapped_ipv6_endpoints_are_normalised_to_ipv4()
    {
        var mapped = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 80);
        IPEndPoint normal = ConnectedPeerAttestation.Normalize(mapped);
        Assert.Equal(AddressFamily.InterNetwork, normal.AddressFamily);
        Assert.Equal(IPAddress.Loopback, normal.Address);
    }

    [Fact]
    public async Task A_non_loopback_target_is_refused_without_resolving_or_connecting()
    {
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { Expected() }, new ConnectedPeerAttestation(TableOwning(4242), InspectorWith())));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync("http://example.invalid:1234/health"));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync("http://192.0.2.1:1234/health"));
    }

    [Fact]
    public async Task A_provider_that_throws_means_no_expected_peer_and_a_refusal()
    {
        var (_, listener) = StartInProcessHttp(IPAddress.Loopback);
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => throw new InvalidOperationException("identity unavailable"),
            CreateRealAttestation(), results.Add));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(UrlOf(listener)));
        Assert.Equal(PeerAttestationStatus.NoExpectedPeer, Assert.Single(results).Status);
    }

    // ---------------------------------------------------------------- integration: real OS table, in-process peer

    private static ConnectedPeerAttestation CreateRealAttestation() =>
        ConnectedPeerAttestation.CreateForCurrentPlatform()
        ?? throw new InvalidOperationException("Windows only");

    private static string UrlOf(TcpListener listener)
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string host = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1";
        return $"http://{host}:{endpoint.Port}/health";
    }

    private (int Port, TcpListener Listener) StartInProcessHttp(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        _cleanup.Add(new StopAction(listener.Stop));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            var buffer = new byte[8192];
                            byte[] response = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok");
                            while (await stream.ReadAsync(buffer) > 0) await stream.WriteAsync(response);
                        }
                        catch { }
                    }
                });
            }
        });
        return (((IPEndPoint)listener.LocalEndpoint).Port, listener);
    }

    private sealed class StopAction : IDisposable
    {
        private readonly Action _stop;
        public StopAction(Action stop) => _stop = stop;
        public void Dispose() => _stop();
    }

    private static ExpectedPeerIdentity CoreIdentity() =>
        ConnectedPeerAttestation.TryCaptureIdentity(Environment.ProcessId)
        ?? throw new InvalidOperationException("core identity unreadable");

    [Fact]
    public void Real_os_table_attests_this_process_over_ipv4()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _cleanup.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndpoint);

        PeerAttestationResult result = CreateRealAttestation().Attest(client, new[] { CoreIdentity() });
        Assert.True(result.IsAttested, result.Detail);
        Assert.Equal(Environment.ProcessId, result.OwnerProcessId);
    }

    [Fact]
    public void Real_os_table_attests_this_process_over_ipv6()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;
        TcpListener listener;
        try { (_, listener) = StartInProcessHttp(IPAddress.IPv6Loopback); } catch (SocketException) { return; }
        var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        _cleanup.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndpoint);

        PeerAttestationResult result = CreateRealAttestation().Attest(client, new[] { CoreIdentity() });
        Assert.True(result.IsAttested, result.Detail);
    }

    [Fact]
    public void Real_os_table_refuses_this_process_when_another_identity_is_expected()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _cleanup.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndpoint);

        ExpectedPeerIdentity core = CoreIdentity();
        Assert.Equal(
            PeerAttestationStatus.ForeignPeer,
            CreateRealAttestation().Attest(client, new[] { core with { ProcessId = core.ProcessId + 1 } }).Status);
        Assert.Equal(
            PeerAttestationStatus.IdentityMismatch,
            CreateRealAttestation().Attest(client, new[] { core with { StartTimeUtcTicks = core.StartTimeUtcTicks + 1 } }).Status);
        Assert.Equal(
            PeerAttestationStatus.IdentityMismatch,
            CreateRealAttestation().Attest(client, new[] { core with { ImagePath = Path.Combine(Path.GetTempPath(), "other.exe") } }).Status);
    }

    [Fact]
    public void Process_identity_is_read_with_start_time_and_image_for_a_live_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        ExpectedPeerIdentity core = CoreIdentity();
        using Process self = Process.GetCurrentProcess();
        Assert.Equal(self.StartTime.ToUniversalTime().Ticks, core.StartTimeUtcTicks);
        Assert.True(ConnectedPeerAttestation.SameImage(self.MainModule!.FileName, core.ImagePath));
        Assert.Null(ConnectedPeerAttestation.TryCaptureIdentity(0));
        Assert.Null(ConnectedPeerAttestation.TryCaptureIdentity(int.MaxValue));
    }

    [Fact]
    public async Task Production_handler_reaches_an_in_process_listener_and_pools_the_attested_connection()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            ManagedRuntimePeers.ForEndpoint, CreateRealAttestation(), results.Add));

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await http.GetAsync(UrlOf(listener));
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }

        Assert.Single(results); // pooled reuse: one attestation, three requests
        Assert.True(results[0].IsAttested);
    }

    [Fact]
    public async Task Connect_time_ownership_recheck_runs_on_the_established_socket_and_refuses_when_it_disagrees()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Fake connection table (the existing fake-table style): rows owned by this process -> accepted.
        var seen = new List<(int Server, int Client, int Pid)>();
        Func<int, int, int, bool> ownedByUs = (s, c, pid) =>
        {
            seen.Add((s, c, pid));
            return ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(s, c, pid, (_, _, f) => f == 2 ? new[] { pid } : Array.Empty<int>());
        };
        using (var ok = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            ManagedRuntimePeers.ForEndpoint, CreateRealAttestation(), null, false, ownedByUs)))
        {
            using HttpResponseMessage response = await ok.GetAsync(UrlOf(listener));
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }
        var call = Assert.Single(seen);
        Assert.Equal(serverPort, call.Server);
        Assert.Equal(Environment.ProcessId, call.Pid);
        Assert.True(call.Client > 0);

        // A different/foreign owner of the connection rows, no rows, or a throwing table: refused before any bytes.
        Func<int, int, int, bool>[] refusals =
        {
            (s, c, pid) => ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(s, c, pid, (_, _, _) => new[] { pid + 1 }),
            (s, c, pid) => ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(s, c, pid, (_, _, _) => Array.Empty<int>()),
            (s, c, pid) => throw new InvalidOperationException("table"),
        };
        foreach (var refuse in refusals)
        {
            using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                ManagedRuntimePeers.ForEndpoint, CreateRealAttestation(), null, false, refuse));
            HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(UrlOf(listener)));
            var inner = Assert.IsType<PeerAttestationException>(ex.InnerException);
            Assert.Equal(PeerAttestationStatus.OwnershipChanged, inner.Result.Status);
            Assert.Contains("No request data was sent", inner.Message);
        }
    }

    [Fact]
    public void Production_managed_runtime_handler_installs_the_connected_peer_check()
    {
        using var handler = ManagedRuntimePeers.CreateHandler();
        Assert.NotNull(handler);
        Assert.Contains("ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy",
            File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PaRuX.Server", "ConnectedPeerAttestation.cs")));
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root");
        return dir;
    }

    [Fact]
    public async Task Pre_cancelled_and_cancelled_after_attestation_connections_send_nothing_and_recovery_works()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var child = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        using var cts = new CancellationTokenSource();
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { child.Identity }, CreateRealAttestation(), r => { results.Add(r); cts.Cancel(); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.GetAsync(child.Url, cts.Token));
        await Task.Delay(300);
        Assert.Equal(0, child.BytesReceived); // attested, then cancelled: no request bytes sent

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.GetAsync(child.Url, preCancelled.Token));
        Assert.Single(results); // pre-cancelled never even connected

        using var recovered = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { child.Identity }, CreateRealAttestation()));
        using HttpResponseMessage ok = await recovered.GetAsync(child.Url);
        Assert.Equal("ok", await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overhead_per_new_connection_is_measured_and_small()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        ConnectedPeerAttestation attestation = CreateRealAttestation();
        ExpectedPeerIdentity[] expected = { CoreIdentity() };

        const int iterations = 200;
        var timings = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            long t0 = Stopwatch.GetTimestamp();
            PeerAttestationResult result = attestation.Attest(client, expected);
            timings.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Assert.True(result.IsAttested, result.Detail);
        }

        timings.Sort();
        double median = timings[iterations / 2];
        double p95 = timings[(int)(iterations * 0.95)];
        _output.WriteLine($"attest overhead ms: median={median:F3} p95={p95:F3} max={timings[^1]:F3} n={iterations}");
        Assert.True(median < 25, $"median attestation {median:F3} ms is too slow for a per-connection cost");
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- integration: real child processes

    [Fact]
    public async Task A_real_child_listener_that_is_the_expected_peer_is_attested_and_serves_over_ipv4()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var child = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { child.Identity }, CreateRealAttestation(), results.Add));

        using HttpResponseMessage first = await http.GetAsync(child.Url);
        using HttpResponseMessage second = await http.GetAsync(child.Url);
        Assert.Equal("ok", await first.Content.ReadAsStringAsync());
        Assert.Equal("ok", await second.Content.ReadAsStringAsync());
        Assert.Single(results);
        Assert.Equal(child.ProcessId, results[0].OwnerProcessId);
        Assert.True(child.BytesReceived > 0);
    }

    [Fact]
    public async Task A_real_child_listener_that_is_the_expected_peer_is_attested_and_serves_over_ipv6()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;
        ChildListener child;
        try { child = await ChildListener.StartAsync(IPAddress.IPv6Loopback, 0, _cleanup); }
        catch (InvalidOperationException) { return; } // host without IPv6 loopback
        await using (child)
        {
            using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                () => new[] { child.Identity }, CreateRealAttestation()));
            using HttpResponseMessage response = await http.GetAsync(child.Url);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_foreign_real_listener_receives_no_bytes_over_ipv4()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var foreign = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { CoreIdentity() }, CreateRealAttestation(), results.Add));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync(foreign.Url, new StringContent("SECRET-PROMPT")));
        Assert.IsType<PeerAttestationException>(ex.InnerException);
        Assert.Equal(PeerAttestationStatus.ForeignPeer, Assert.Single(results).Status);
        await Task.Delay(400);
        Assert.Equal(0, foreign.BytesReceived);
    }

    [Fact]
    public async Task A_foreign_real_listener_receives_no_bytes_over_ipv6()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;
        ChildListener foreign;
        try { foreign = await ChildListener.StartAsync(IPAddress.IPv6Loopback, 0, _cleanup); }
        catch (InvalidOperationException) { return; }
        await using (foreign)
        {
            using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                () => new[] { CoreIdentity() }, CreateRealAttestation()));
            await Assert.ThrowsAsync<HttpRequestException>(
                () => http.PostAsync(foreign.Url, new StringContent("SECRET-PROMPT")));
            await Task.Delay(400);
            Assert.Equal(0, foreign.BytesReceived);
        }
    }

    [Fact]
    public async Task A_foreign_listener_that_takes_the_port_after_the_preflight_receives_no_prompt()
    {
        if (!OperatingSystem.IsWindows()) return;
        ChildListener owner = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        int port = owner.Port;
        ExpectedPeerIdentity ownerIdentity = owner.Identity;

        // Preflight: the managed runtime is the sole listener, exactly what the prompt-boundary check sees.
        Assert.True(ManagedListenerOwnership.IsStrictlyOwnedBy(port, owner.ProcessId));

        // Race: the real runtime goes away and a foreign process claims the very same port.
        await owner.KillAsync();
        ChildListener foreign = await ChildListener.StartWithRetryAsync(IPAddress.Loopback, port, _cleanup);
        await using (foreign)
        {
            Assert.Equal(port, foreign.Port);
            var results = new List<PeerAttestationResult>();
            using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                () => new[] { ownerIdentity }, CreateRealAttestation(), results.Add));

            await Assert.ThrowsAsync<HttpRequestException>(
                () => http.PostAsync(foreign.Url, new StringContent("SECRET-PROMPT")));
            Assert.Equal(PeerAttestationStatus.ForeignPeer, Assert.Single(results).Status);
            await Task.Delay(400);
            Assert.Equal(0, foreign.BytesReceived);
        }
    }

    [Fact]
    public async Task A_matching_pid_with_a_forged_start_time_or_image_is_refused_on_a_real_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var child = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        ExpectedPeerIdentity real = child.Identity;
        var results = new List<PeerAttestationResult>();

        foreach (ExpectedPeerIdentity forged in new[]
        {
            real with { StartTimeUtcTicks = real.StartTimeUtcTicks - TimeSpan.TicksPerSecond },
            real with { ImagePath = Path.Combine(Path.GetTempPath(), "llama-server.exe") },
        })
        {
            using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                () => new[] { forged }, CreateRealAttestation(), results.Add));
            await Assert.ThrowsAsync<HttpRequestException>(
                () => http.PostAsync(child.Url, new StringContent("SECRET-PROMPT")));
        }

        Assert.All(results, r => Assert.Equal(PeerAttestationStatus.IdentityMismatch, r.Status));
        Assert.Equal(2, results.Count);
        await Task.Delay(300);
        Assert.Equal(0, child.BytesReceived);
    }

    [Fact]
    public async Task A_replacement_runtime_is_attested_again_on_a_new_connection()
    {
        if (!OperatingSystem.IsWindows()) return;
        ChildListener first = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        ExpectedPeerIdentity current = first.Identity;
        var results = new List<PeerAttestationResult>();
        using var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
            () => new[] { current }, CreateRealAttestation(), results.Add));

        using (HttpResponseMessage response = await http.GetAsync(first.Url))
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());

        int port = first.Port;
        await first.KillAsync();
        await using ChildListener second = await ChildListener.StartWithRetryAsync(IPAddress.Loopback, port, _cleanup);

        // Old identity is still what the provider returns: the replacement is NOT trusted.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(second.Url));
        Assert.Equal(0, second.BytesReceived);

        // The managed lifecycle records the new launch; the next connection attests it.
        current = second.Identity;
        using (HttpResponseMessage response = await http.GetAsync(second.Url))
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.True(results[^1].IsAttested);
        Assert.Equal(second.ProcessId, results[^1].OwnerProcessId);
    }

    [Fact]
    public async Task Core_is_an_expected_peer_only_for_a_port_Core_itself_listens_on()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAiProcessManager.ClearTextLaunchIdentity();
        Assert.Empty(ManagedRuntimePeers.Current()); // never a PID-file-derived guess

        (_, TcpListener own) = StartInProcessHttp(IPAddress.Loopback);
        Assert.Contains(ManagedRuntimePeers.ForEndpoint((IPEndPoint)own.LocalEndpoint),
            p => p.ProcessId == Environment.ProcessId && p.StartTimeUtcTicks > 0 && p.ImagePath.Length > 0);

        await using var foreign = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        Assert.Empty(ManagedRuntimePeers.ForEndpoint(new IPEndPoint(IPAddress.Loopback, foreign.Port)));
    }

    [Fact]
    public async Task Production_handler_refuses_a_foreign_listener_and_the_refusal_is_recognised_as_non_fallbackable()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAiProcessManager.ClearTextLaunchIdentity();
        await using var foreign = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        using var http = new HttpClient(ManagedRuntimePeers.CreateHandler());

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync(foreign.Url, new StringContent("SECRET-PROMPT")));

        Assert.True(ChatProxyService.IsAttestationRefusal(ex));
        Assert.Contains("identity not verified", ex.InnerException!.Message);
        await Task.Delay(300);
        Assert.Equal(0, foreign.BytesReceived);
    }

    [Fact]
    public void Attestation_refusals_are_found_through_nested_and_aggregate_exceptions()
    {
        var refusal = new PeerAttestationException(new PeerAttestationResult(PeerAttestationStatus.ForeignPeer, 1, "x"));
        Assert.True(ChatProxyService.IsAttestationRefusal(new HttpRequestException("w", refusal)));
        Assert.True(ChatProxyService.IsAttestationRefusal(
            new InvalidOperationException("outer", new AggregateException(new IOException("a"), new HttpRequestException("w", refusal)))));
        Assert.False(ChatProxyService.IsAttestationRefusal(new HttpRequestException("connection refused")));
        Assert.False(ChatProxyService.IsAttestationRefusal(null));
    }

    [Fact]
    public async Task A_pid_file_without_a_recorded_launch_is_not_owned_and_not_healthy_so_relaunch_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        string state = Path.Combine(Path.GetTempPath(), "parux-peer-state-" + Guid.NewGuid().ToString("N"));
        await using var runtime = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        using IDisposable stateScope = LocalAiPaths.UseStateDirForTests(state);
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(runtime.Port);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.TextPidFile)!);
            File.WriteAllText(LocalAiPaths.TextPidFile, runtime.ProcessId.ToString());
            LocalAiProcessManager.ClearTextLaunchIdentity();
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            var manager = new LocalAiProcessManager();
            var service = new LocalRuntimeService(null!, manager, null!);

            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchProcessId());
            Assert.False(manager.IsManagedTextRuntimeOwned());
            Assert.False(service.ManagedRuntimeOwned);
            Assert.False(await manager.IsTextHealthyAsync(CancellationToken.None)); // => EnsureReady relaunches

            // Once the launch is recorded for the live process it is owned; a dead one never is.
            LocalAiProcessManager.RecordTextLaunchIdentity(runtime.ProcessId);
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            Assert.Equal(runtime.ProcessId, LocalAiProcessManager.GetVerifiedTextLaunchProcessId());
            Assert.True(manager.IsManagedTextRuntimeOwned());
            Assert.True(service.ManagedRuntimeOwned);

            await runtime.KillAsync();
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchProcessId());
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            Assert.False(manager.IsManagedTextRuntimeOwned());
        }
        finally
        {
            LocalAiProcessManager.ClearTextLaunchIdentity();
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            try { Directory.Delete(state, true); } catch { }
        }
    }

    [Fact]
    public async Task A_recorded_identity_with_forged_start_time_is_not_verified()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var child = await ChildListener.StartAsync(IPAddress.Loopback, 0, _cleanup);
        try
        {
            LocalAiProcessManager.RecordTextLaunchIdentity(child.ProcessId);
            Assert.True(LocalAiProcessManager.TryGetTextLaunchIdentity(out ExpectedPeerIdentity recorded));
            Assert.Equal(child.Identity, recorded);

            var forged = new FakeInspector();
            forged.Processes[child.ProcessId] = (recorded.StartTimeUtcTicks + 1, recorded.ImagePath, false);
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchProcessId(forged));
            LocalAiProcessManager.ClearTextLaunchIdentity();
            Assert.False(LocalAiProcessManager.TryGetTextLaunchIdentity(out _));
        }
        finally { LocalAiProcessManager.ClearTextLaunchIdentity(); }
    }

    [Fact]
    public async Task Handler_recycles_pooled_connections_and_never_follows_redirects_or_proxies()
    {
        using SocketsHttpHandler handler = AttestedLoopbackHttp.CreateHandler(
            () => Array.Empty<ExpectedPeerIdentity>(), CreateRealAttestation());
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Ipv6_and_ipv4_rows_are_matched_on_the_exact_tuple_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        (_, TcpListener listener) = StartInProcessHttp(IPAddress.Loopback);
        var a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var b = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _cleanup.Add(a); _cleanup.Add(b);
        a.Connect((IPEndPoint)listener.LocalEndpoint);
        b.Connect((IPEndPoint)listener.LocalEndpoint);

        var source = new WindowsTcpOwnershipSource();
        var server = (IPEndPoint)a.RemoteEndPoint!;
        Assert.True(source.TryReadEstablishedOwners(server, (IPEndPoint)a.LocalEndPoint!, out var owners));
        Assert.Equal(new[] { Environment.ProcessId }, owners.Distinct());
        // A client port nobody holds matches nothing, even though the server port has live rows.
        Assert.True(source.TryReadEstablishedOwners(server, new IPEndPoint(IPAddress.Loopback, 1), out var none));
        Assert.Empty(none);
        // Mixed families are unreadable rather than guessed.
        Assert.False(source.TryReadEstablishedOwners(server, new IPEndPoint(IPAddress.IPv6Loopback, 1), out _));
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- real child process helper

    private sealed class ChildListener : IAsyncDisposable, IDisposable
    {
        private const string Script = @"
param([string]$Addr='127.0.0.1',[int]$Port=0)
$l=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Parse($Addr),$Port)
$l.Start()
[Console]::Out.WriteLine('PORT:'+$l.LocalEndpoint.Port); [Console]::Out.Flush()
$b=New-Object byte[] 8192
$r=[Text.Encoding]::ASCII.GetBytes(""HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: keep-alive`r`n`r`nok"")
while($true){
  $c=$l.AcceptTcpClient(); $s=$c.GetStream()
  try {
    while($true){
      $n=$s.Read($b,0,$b.Length); if($n -le 0){break}
      [Console]::Out.WriteLine('RX:'+$n); [Console]::Out.Flush()
      $s.Write($r,0,$r.Length); $s.Flush()
    }
  } catch {}
  $c.Close()
}
";
        private readonly Process _process;
        private long _bytes;
        private readonly TaskCompletionSource<int> _port = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _scriptPath;

        private ChildListener(Process process, string scriptPath, IPAddress address)
        {
            _process = process;
            _scriptPath = scriptPath;
            Address = address;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (e.Data.StartsWith("PORT:", StringComparison.Ordinal))
                    _port.TrySetResult(int.Parse(e.Data.AsSpan(5)));
                else if (e.Data.StartsWith("RX:", StringComparison.Ordinal))
                    Interlocked.Add(ref _bytes, long.Parse(e.Data.AsSpan(3)));
            };
        }

        public IPAddress Address { get; }
        public int Port { get; private set; }
        public int ProcessId => _process.Id;
        public ExpectedPeerIdentity Identity { get; private set; }
        public long BytesReceived => Interlocked.Read(ref _bytes);

        public string Url => Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[::1]:{Port}/health"
            : $"http://127.0.0.1:{Port}/health";

        public static async Task<ChildListener> StartAsync(IPAddress address, int port, List<IDisposable> cleanup)
        {
            string script = Path.Combine(Path.GetTempPath(), "parux-peer-" + Guid.NewGuid().ToString("N") + ".ps1");
            await File.WriteAllTextAsync(script, Script);

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Addr", address.ToString(), "-Port", port.ToString(),
            }) psi.ArgumentList.Add(arg);

            var process = new Process { StartInfo = psi };
            var child = new ChildListener(process, script, address);
            cleanup.Add(child);
            process.Start();
            process.BeginOutputReadLine();
            _ = process.StandardError.ReadToEndAsync();

            Task winner = await Task.WhenAny(child._port.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (winner != child._port.Task || process.HasExited)
            {
                child.Dispose();
                throw new InvalidOperationException("child listener did not report a port");
            }

            child.Port = child._port.Task.Result;
            ExpectedPeerIdentity? identity = ConnectedPeerAttestation.TryCaptureIdentity(process.Id);
            if (identity is null) throw new InvalidOperationException("child identity unreadable");
            child.Identity = identity.Value;
            return child;
        }

        /// <summary>Binds a specific port that a just-killed process may still be releasing.</summary>
        public static async Task<ChildListener> StartWithRetryAsync(IPAddress address, int port, List<IDisposable> cleanup)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await StartAsync(address, port, cleanup); }
                catch (InvalidOperationException) when (attempt < 6) { await Task.Delay(300); }
            }
        }

        public async Task KillAsync()
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(2000); } catch { }
            try { File.Delete(_scriptPath); } catch { }
        }
    }
}
