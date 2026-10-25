using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlCancellationDrainTests
{
    private static async Task<(bool Drained, string Command)> DrainAsync(
        string requestId,
        Action<Channel<WindowsMlGenAiWorkerEvent>> arrange)
    {
        Channel<WindowsMlGenAiWorkerEvent> channel = Channel.CreateUnbounded<WindowsMlGenAiWorkerEvent>();
        arrange(channel);

        await using var memory = new MemoryStream();
        await using var writer = new StreamWriter(memory, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = false
        };
        MethodInfo method = typeof(WindowsMlGenAiLocalInferenceEngine).GetMethod(
            "TryCancelAndDrainAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("TryCancelAndDrainAsync");
        var task = (Task<bool>)method.Invoke(null, new object[] { requestId, writer, channel.Reader })!;
        bool drained = await task;
        await writer.FlushAsync();
        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, leaveOpen: true);
        return (drained, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Cancel_command_is_request_scoped_and_pre_cancel_deltas_are_drained_not_replayed()
    {
        (bool drained, string command) = await DrainAsync("request-7", channel =>
        {
            channel.Writer.TryWrite(new WindowsMlGenAiWorkerEvent
            {
                ApiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
                Id = "request-7",
                Type = "delta",
                Content = "already-produced"
            });
            channel.Writer.TryWrite(new WindowsMlGenAiWorkerEvent
            {
                ApiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
                Id = "request-7",
                Type = "cancelled",
                PromptTokens = 50,
                GeneratedTokens = 3
            });
        });

        Assert.True(drained);
        string line = Assert.Single(command.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument json = JsonDocument.Parse(line);
        Assert.Equal(WindowsMlGenAiProtocol.WorkerApiVersion, json.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.Equal("request-7", json.RootElement.GetProperty("id").GetString());
        Assert.Equal("cancel", json.RootElement.GetProperty("op").GetString());
        Assert.DoesNotContain("already-produced", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    [InlineData("error")]
    public async Task Every_exact_request_terminal_restores_protocol_synchronization(string terminal)
    {
        (bool drained, _) = await DrainAsync("request-8", channel =>
        {
            channel.Writer.TryWrite(new WindowsMlGenAiWorkerEvent
            {
                ApiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
                Id = "request-8",
                Type = terminal,
                Error = terminal == "error" ? "cancel raced an upstream failure" : null
            });
        });

        Assert.True(drained);
    }

    [Fact]
    public async Task Wrong_request_terminal_is_never_accepted_as_a_successful_drain()
    {
        (bool drained, _) = await DrainAsync("request-9", channel =>
        {
            channel.Writer.TryWrite(new WindowsMlGenAiWorkerEvent
            {
                ApiVersion = WindowsMlGenAiProtocol.WorkerApiVersion,
                Id = "stale-request",
                Type = "cancelled"
            });
        });

        Assert.False(drained);
    }

    [Fact]
    public async Task Closed_event_channel_without_terminal_requires_worker_restart()
    {
        (bool drained, _) = await DrainAsync("request-10", channel => channel.Writer.TryComplete());

        Assert.False(drained);
    }
}
