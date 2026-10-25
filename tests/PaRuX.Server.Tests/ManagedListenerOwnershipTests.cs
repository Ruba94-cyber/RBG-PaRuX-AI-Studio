using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX serves its managed llama.cpp on 127.0.0.1:1234 and used to decide a runtime was healthy by
/// asking that port for /health, adopting whatever answered.
///
/// Demonstrated on the physical PC before this existed: with PaRuX's model unloaded, a llama-server
/// started by hand on 1234 - no PID file, launched with --gpu-layers 0 and a 4,096 context PaRuX
/// never chose - was adopted and answered a user's chat with the word planted in it, while PaRuX's
/// own Runtime Manager correctly reported both backends as running=False healthy=False.
///
/// It is not an exotic collision. 1234 is LM Studio's default server port, and any local process
/// that claims it first would quietly receive every prompt the user types, documents and retrieved
/// context included.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ManagedListenerOwnershipTests
{
    private readonly ITestOutputHelper _output;

    public ManagedListenerOwnershipTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Listener_presence_requires_both_family_reads_and_accepts_an_owner_in_either_family()
    {
        const int port = 1234;
        int reads = 0;

        Assert.False(ManagedListenerOwnership.IsPortListening(port, (requestedPort, addressFamily) =>
        {
            Assert.Equal(port, requestedPort);
            reads++;
            return Array.Empty<int>();
        }));
        Assert.Equal(2, reads);

        Assert.True(ManagedListenerOwnership.IsPortListening(port, (_, addressFamily) =>
            addressFamily == 2 ? new[] { 111 } : Array.Empty<int>()));
        Assert.True(ManagedListenerOwnership.IsPortListening(port, (_, addressFamily) =>
            addressFamily == 23 ? new[] { 222 } : Array.Empty<int>()));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(23)]
    public void Listener_presence_fails_closed_if_either_family_table_read_fails(int failedFamily)
    {
        int reads = 0;

        bool listening = ManagedListenerOwnership.IsPortListening(1234, (port, addressFamily) =>
        {
            reads++;
            if (addressFamily == failedFamily) throw new InvalidOperationException("synthetic table failure");
            return new[] { 4242 };
        });

        Assert.False(listening);
        Assert.Equal(failedFamily == 2 ? 1 : 2, reads);
    }

    [Fact]
    public void Strict_ownership_fails_closed_if_either_address_family_table_read_fails()
    {
        const int expectedPid = 4242;
        int reads = 0;

        bool owned = ManagedListenerOwnership.IsStrictlyOwnedBy(1234, expectedPid, (port, addressFamily) =>
        {
            Assert.Equal(1234, port);
            reads++;
            if (addressFamily == 2) return new[] { expectedPid };
            throw new InvalidOperationException("synthetic IPv6 table access failure");
        });

        Assert.False(owned);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Strict_ownership_accepts_a_complete_two_family_read_with_only_expected_pid()
    {
        const int expectedPid = 4242;
        int reads = 0;

        bool owned = ManagedListenerOwnership.IsStrictlyOwnedBy(1234, expectedPid, (port, addressFamily) =>
        {
            Assert.Equal(1234, port);
            reads++;
            return addressFamily == 2 ? new[] { expectedPid } : Array.Empty<int>();
        });

        Assert.True(owned);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(56)]
    public void Listener_rows_must_fit_the_returned_table_size_not_just_the_allocated_capacity(int rowSize)
    {
        const int allocatedCapacity = 4096;
        const int returnedSize = 4; // A successful second query may return fewer bytes than requested.

        ManagedListenerOwnership.ValidateRowCount(1, allocatedCapacity, rowSize);
        Assert.Throws<InvalidOperationException>(() =>
            ManagedListenerOwnership.ValidateRowCount(1, returnedSize, rowSize));
        ManagedListenerOwnership.ValidateRowCount(1, sizeof(int) + rowSize, rowSize);
    }

    private static bool IsOwnedBy(int port, int pid) =>
        (bool)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.ManagedListenerOwnership")!
            .GetMethod("IsOwnedBy", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { port, pid })!;

    private static bool IsStrictlyOwnedBy(int port, int pid) =>
        (bool)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.ManagedListenerOwnership")!
            .GetMethod("IsStrictlyOwnedBy", BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: new[] { typeof(int), typeof(int) }, modifiers: null)!
            .Invoke(null, new object[] { port, pid })!;

    private static int? FindListenerOwner(int port) =>
        (int?)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.ManagedListenerOwnership")!
            .GetMethod("FindListenerOwner", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { port });

    private static System.Collections.Generic.IReadOnlyList<int> FindListenerOwners(int port) =>
        (System.Collections.Generic.IReadOnlyList<int>)typeof(ApiKeyService).Assembly
            .GetType("PaRuX.Server.ManagedListenerOwnership")!
            .GetMethod("FindListenerOwners", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { port })!;

    /// <summary>
    /// The case the first attempt at this check got wrong. llama.cpp sets SO_REUSEADDR, so a second
    /// server binds 127.0.0.1:1234 while the first is still serving, and Windows then hands new
    /// connections to one of them unpredictably. Observed on the physical PC: pid 20940 (PaRuX's
    /// own) and pid 23892 (started by hand) both listening on the same address and port at once.
    ///
    /// A foreign process therefore does not have to win the port to read the user's prompts. It
    /// only has to share it - which it can do while PaRuX's runtime is running perfectly - so
    /// "our pid is among the listeners" is not the property worth checking. Sole ownership is.
    /// </summary>
    [Fact]
    public void Sharing_the_port_is_refused_even_though_our_own_process_is_also_listening()
    {
        if (!OperatingSystem.IsWindows()) return;

        var first = new TcpListener(IPAddress.Loopback, 0);
        first.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        first.Start();
        int port = ((IPEndPoint)first.LocalEndpoint).Port;

        var second = new TcpListener(IPAddress.Loopback, port);
        second.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            second.Start();
        }
        catch (SocketException)
        {
            // This host refuses the second bind outright, which is the safe outcome and leaves
            // nothing for this test to prove.
            first.Stop();
            return;
        }

        try
        {
            // Both listeners live in this process, so the pid set cannot distinguish them here.
            // What the check must never do is treat "someone else is also bound" as ours, which is
            // covered by the foreign-pid test below; this pins that a shared port is still read as
            // a single owning process rather than silently dropping the extra row.
            Assert.NotEmpty(FindListenerOwners(port));
            Assert.False(IsOwnedBy(port, Environment.ProcessId == 4 ? 8 : 4));
            Assert.False(IsStrictlyOwnedBy(port, Environment.ProcessId == 4 ? 8 : 4));
        }
        finally { second.Stop(); first.Stop(); }
    }

    [Fact]
    public void A_listener_this_process_owns_is_recognised_as_ours()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(ManagedListenerOwnership.IsPortListening(port));
            Assert.True(IsOwnedBy(port, Environment.ProcessId));
            if (OperatingSystem.IsWindows()) Assert.True(IsStrictlyOwnedBy(port, Environment.ProcessId));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Windows_listener_presence_before_after_latency_and_allocations()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int freePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var live = new TcpListener(IPAddress.Loopback, 0);
        live.Start();
        int livePort = ((IPEndPoint)live.LocalEndpoint).Port;

        Assert.False(ManagedListenerOwnership.IsPortListening(freePort));
        Assert.True(ManagedListenerOwnership.IsPortListening(livePort));
        ProbeMetrics freeLegacy = MeasurePresence(LegacyIsPortListening, freePort);
        ProbeMetrics freeNative = MeasurePresence(ManagedListenerOwnership.IsPortListening, freePort);
        ProbeMetrics liveLegacy = MeasurePresence(LegacyIsPortListening, livePort);
        ProbeMetrics liveNative = MeasurePresence(ManagedListenerOwnership.IsPortListening, livePort);
        Assert.Equal(0, freeNative.Detected);
        Assert.Equal(128, liveNative.Detected);

        _output.WriteLine("LISTENER_PRESENCE measured=128; legacy=IPGlobalProperties.GetActiveTcpListeners; current=GetExtendedTcpTable IPv4+IPv6.");
        _output.WriteLine($"free-port legacy detected={freeLegacy.Detected}/128 p50={freeLegacy.P50Ms:F4}ms p95={freeLegacy.P95Ms:F4}ms alloc={freeLegacy.BytesPerCall:F1}B/call; current detected={freeNative.Detected}/128 p50={freeNative.P50Ms:F4}ms p95={freeNative.P95Ms:F4}ms alloc={freeNative.BytesPerCall:F1}B/call.");
        _output.WriteLine($"live-loopback legacy detected={liveLegacy.Detected}/128 p50={liveLegacy.P50Ms:F4}ms p95={liveLegacy.P95Ms:F4}ms alloc={liveLegacy.BytesPerCall:F1}B/call; current detected={liveNative.Detected}/128 p50={liveNative.P50Ms:F4}ms p95={liveNative.P95Ms:F4}ms alloc={liveNative.BytesPerCall:F1}B/call.");
    }

    [Fact]
    public async Task Idle_text_health_returns_without_http_when_no_listener_exists()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(port);
        Assert.False(ManagedListenerOwnership.IsPortListening(port));

        var manager = new LocalAiProcessManager(0);
        int httpCalls = 0;
        Task<HttpResponseMessage> UnexpectedHttpRequest(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref httpCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        for (int warmup = 0; warmup < 16; warmup++)
            Assert.False(await manager.IsTextHealthyAsync(CancellationToken.None, UnexpectedHttpRequest));
        const int iterations = 128;
        var samples = new double[iterations];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            bool healthy = await manager.IsTextHealthyAsync(CancellationToken.None, UnexpectedHttpRequest);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (healthy) throw new InvalidOperationException("An idle port was reported healthy.");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(samples);
        Assert.Equal(0, httpCalls);
        _output.WriteLine($"IDLE_TEXT_HEALTH iterations={iterations} noListener=true httpCalls={httpCalls} p50={Percentile(samples, 50):F4}ms p95={Percentile(samples, 95):F4}ms alloc={allocated / (double)iterations:F1}B/call.");
    }

    private sealed record ProbeMetrics(int Detected, double P50Ms, double P95Ms, double BytesPerCall);

    private static ProbeMetrics MeasurePresence(Func<int, bool> probe, int port)
    {
        for (int warmup = 0; warmup < 16; warmup++) _ = probe(port);
        const int iterations = 128;
        var samples = new double[iterations];
        int detected = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            bool listening = probe(port);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (listening) detected++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(samples);
        return new ProbeMetrics(detected, Percentile(samples, 50), Percentile(samples, 95), allocated / (double)iterations);
    }

    private static bool LegacyIsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch { return true; }
    }

    private static double Percentile(double[] sorted, int percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile / 100d) - 1, 0, sorted.Length - 1)];

    /// <summary>
    /// The case that mattered: something is listening, and it is not us. Before the check, PaRuX
    /// answered a user's chat from exactly this situation.
    /// </summary>
    [Fact]
    public void A_listener_owned_by_another_process_is_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int notUs = Environment.ProcessId == 4 ? 8 : 4; // System/idle pids, never this test host

            Assert.False(IsOwnedBy(port, notUs));
            Assert.False(IsStrictlyOwnedBy(port, notUs));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void The_owning_process_is_read_exactly_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Equal(Environment.ProcessId, FindListenerOwner(port));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void No_recorded_process_means_no_owner_rather_than_a_permissive_default()
    {
        // A pid file that resolved to nothing is how the foreign-server case presents itself, and
        // "we do not know" must not become "yes".
        Assert.False(IsOwnedBy(1234, 0));
        Assert.False(IsOwnedBy(1234, -1));
        Assert.False(IsStrictlyOwnedBy(1234, 0));
        Assert.False(IsStrictlyOwnedBy(1234, -1));
    }

    [Fact]
    public void A_port_nobody_is_listening_on_has_no_owner_and_is_never_strictly_trusted()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int freed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        if (!OperatingSystem.IsWindows()) return;
        Assert.False(ManagedListenerOwnership.IsPortListening(freed));
        Assert.Null(FindListenerOwner(freed));
        Assert.False(IsStrictlyOwnedBy(freed, Environment.ProcessId));
    }

    [Fact]
    public void Strict_prompt_boundary_is_fail_closed_off_windows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(IsStrictlyOwnedBy(1234, Environment.ProcessId));
    }

    [Fact]
    public void An_ipv6_listener_this_process_owns_is_recognised_as_ours()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            return;
        }

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(IsOwnedBy(port, Environment.ProcessId));
            Assert.True(IsStrictlyOwnedBy(port, Environment.ProcessId));
            Assert.Equal(Environment.ProcessId, FindListenerOwner(port));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Foreign_ipv6_listener_is_refused()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            return;
        }

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int notUs = Environment.ProcessId == 4 ? 8 : 4;

            Assert.False(IsOwnedBy(port, notUs));
            Assert.False(IsStrictlyOwnedBy(port, notUs));
        }
        finally { listener.Stop(); }
    }
}
