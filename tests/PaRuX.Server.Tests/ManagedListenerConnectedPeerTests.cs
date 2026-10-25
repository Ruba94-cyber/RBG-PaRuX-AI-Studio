using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Check-to-connect window: the listener table read proves who listened earlier, not who
/// accepted the connection that follows. The connected-peer check closes that window.</summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ManagedListenerConnectedPeerTests
{
    private static IReadOnlyList<int> Owners(params int[] pids) => pids;

    [Fact]
    public void Peer_is_accepted_only_when_every_matching_connection_row_belongs_to_the_expected_pid()
    {
        Assert.True(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 77, (_, _, f) => f == 2 ? Owners(77) : Owners()));
        Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 77, (_, _, f) => f == 2 ? Owners(77) : Owners(88)));
        Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 77, (_, _, _) => Owners(88)));
    }

    [Fact]
    public void Missing_rows_unreadable_table_or_invalid_pid_are_refusals()
    {
        Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 77, (_, _, _) => Owners()));
        Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 77,
            (_, _, f) => f == 2 ? Owners(77) : throw new InvalidOperationException("table")));
        Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(1234, 50000, 0, (_, _, _) => Owners(0)));
    }

    [Fact]
    public async Task Real_loopback_connection_resolves_to_the_accepting_process_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, serverPort);
            using TcpClient server = await accept;
            int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

            Assert.True(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(serverPort, clientPort, Environment.ProcessId));
            Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(serverPort, clientPort, Environment.ProcessId + 1));
            Assert.False(ManagedListenerOwnership.IsConnectedPeerStrictlyOwnedBy(serverPort, clientPort + 1, Environment.ProcessId));
        }
        finally { listener.Stop(); }
    }
}
