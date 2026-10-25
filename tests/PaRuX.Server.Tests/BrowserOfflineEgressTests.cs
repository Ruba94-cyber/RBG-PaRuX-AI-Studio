using System.Net;
using System.Net.Sockets;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// While Internet is OFF the developer browser is routed through a PaRuX-owned sink that closes every proxied connection.
/// These tests cover the switches that route it there and the sink's refusal, on real loopback sockets. They do
/// not prove the live browser's behaviour; that needs the CDP egress probe on a machine with the browser installed.
/// </summary>
public sealed class BrowserOfflineEgressTests
{
    [Fact]
    public void Offline_switches_route_non_loopback_traffic_to_the_sink_and_stop_direct_webrtc_udp()
    {
        IReadOnlyList<string> switches = BrowserOfflineEgressPolicy.SwitchesFor(4242);

        Assert.Contains("--proxy-server=http://127.0.0.1:4242", switches);
        Assert.Contains(BrowserOfflineEgressPolicy.WebRtcIpHandlingSwitch, switches);
        // Loopback must keep Chromium's default direct route. A bypass rule that removes the loopback exemption would
        // send local dev servers and the DevTools endpoint through the sink.
        Assert.DoesNotContain(switches, argument => argument.Contains("<-loopback>", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Offline_switches_reject_a_port_that_cannot_be_a_sink(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserOfflineEgressPolicy.SwitchesFor(port));

    [Fact]
    public async Task The_sink_refuses_every_connection_so_nothing_is_relayed()
    {
        using BrowserOfflineEgressSink sink = BrowserOfflineEgressSink.Start();
        Assert.InRange(sink.Port, 1, 65535);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, sink.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        int read;
        try
        {
            read = await client.GetStream().ReadAsync(new byte[16], timeout.Token);
        }
        catch (IOException)
        {
            read = 0;   // the connection was reset, which is also a refusal
        }

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Disposing_the_sink_makes_its_port_refuse_new_connections()
    {
        var sink = BrowserOfflineEgressSink.Start();
        int port = sink.Port;
        sink.Dispose();

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }
}
