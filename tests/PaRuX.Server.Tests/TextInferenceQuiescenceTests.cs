using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TextInferenceQuiescenceTests
{
    [Fact]
    public void Idle_slots_are_accepted_only_when_every_slot_reports_not_processing()
    {
        using JsonDocument document = JsonDocument.Parse(
            "[{\"id\":0,\"is_processing\":false},{\"id\":1,\"is_processing\":false}]");

        TextInferenceQuiescence.SlotProbe probe = TextInferenceQuiescence.ParseSlots(document.RootElement);

        Assert.Equal(TextInferenceQuiescence.SlotState.Idle, probe.State);
        Assert.Equal(2, probe.Slots);
        Assert.Equal(0, probe.Processing);
    }

    [Fact]
    public void Any_processing_slot_keeps_media_takeover_blocked()
    {
        using JsonDocument document = JsonDocument.Parse(
            "[{\"id\":0,\"is_processing\":false},{\"id\":1,\"is_processing\":true}]");

        TextInferenceQuiescence.SlotProbe probe = TextInferenceQuiescence.ParseSlots(document.RootElement);

        Assert.Equal(TextInferenceQuiescence.SlotState.Busy, probe.State);
        Assert.Equal(2, probe.Slots);
        Assert.Equal(1, probe.Processing);
    }

    [Fact]
    public void Missing_processing_state_fails_closed()
    {
        using JsonDocument document = JsonDocument.Parse(
            "[{\"id\":0,\"is_processing\":false},{\"id\":1}]");

        TextInferenceQuiescence.SlotProbe probe = TextInferenceQuiescence.ParseSlots(document.RootElement);

        Assert.Equal(TextInferenceQuiescence.SlotState.Unsupported, probe.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    public void Untrustworthy_slot_shapes_fail_closed(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        TextInferenceQuiescence.SlotProbe probe = TextInferenceQuiescence.ParseSlots(document.RootElement);

        Assert.Equal(TextInferenceQuiescence.SlotState.Unsupported, probe.State);
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Unreachable_listener_owned_only_by_pid_zero_is_not_ours_and_does_not_block()
    {
        // Windows leaves orphaned listener rows with owning PID 0 behind a killed server. They cannot
        // be serving a chat, so refusing every later model load because of them was a bug.
        using IDisposable port = LocalAiPaths.UseTextPortForTests(FreePort());
        using IDisposable owners = TextInferenceQuiescence.OverrideListenerOwnersForTests(_ => new[] { 0 });

        TextInferenceQuiescence.SlotProbe probe = await TextInferenceQuiescence.ProbeAsync(CancellationToken.None);
        Assert.Equal(TextInferenceQuiescence.SlotState.TextServerAbsent, probe.State);
        await TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task Unreachable_listener_owned_by_a_live_process_fails_closed_with_a_typed_error()
    {
        using IDisposable port = LocalAiPaths.UseTextPortForTests(FreePort());
        using IDisposable owners = TextInferenceQuiescence.OverrideListenerOwnersForTests(_ => new[] { 4242 });

        TextRuntimeStateUnavailableException error = await Assert.ThrowsAsync<TextRuntimeStateUnavailableException>(
            () => TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Contains("slot processing state", error.Message);
    }

    [Fact]
    public async Task Unreadable_listener_table_stays_fail_closed()
    {
        using IDisposable port = LocalAiPaths.UseTextPortForTests(FreePort());
        using IDisposable owners = TextInferenceQuiescence.OverrideListenerOwnersForTests(
            _ => throw new InvalidOperationException("table unreadable"));

        TextInferenceQuiescence.SlotProbe probe = await TextInferenceQuiescence.ProbeAsync(CancellationToken.None);
        Assert.Equal(TextInferenceQuiescence.SlotState.Unsupported, probe.State);
    }
}
