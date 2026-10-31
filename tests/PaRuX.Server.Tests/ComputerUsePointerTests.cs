using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pointer clicks are decided against a fake driver, so no test moves the real mouse. Two defects are pinned here.
///
/// 1. SendInput and SetCursorPos return values were ignored, so a click that Windows refused (for example into a
///    higher-privilege window or a locked desktop) was logged and reported as delivered.
/// 2. Every button other than "right" was sent as a left click, so a "middle" request activated links with the wrong button.
/// </summary>
public sealed class ComputerUsePointerTests
{
    private sealed class FakeDriver : IComputerUseInputDriver
    {
        /// <summary>When false, SetCursorPos reports that Windows did not move the pointer.</summary>
        public bool CursorMoves { get; set; } = true;

        /// <summary>After this many mouse events, Windows reports zero inserted events, as it does for blocked input.</summary>
        public int EventsBeforeBlock { get; set; } = int.MaxValue;

        public List<(int X, int Y)> Moves { get; } = new();
        public List<uint> Events { get; } = new();
        public List<(ushort Key, uint Flags)> Keys { get; } = new();

        public bool MoveCursor(int x, int y)
        {
            Moves.Add((x, y));
            return CursorMoves;
        }

        public uint SendMouseEvent(uint flags, uint mouseData)
        {
            bool blocked = Events.Count >= EventsBeforeBlock;
            Events.Add(flags);
            return blocked ? 0u : 1u;
        }

        public uint SendKeyboardEvent(ushort virtualKey, ushort scanCode, uint flags)
        {
            Keys.Add((virtualKey, flags));
            return 1u;
        }
    }

    [Fact]
    public async Task Revoked_key_combo_releases_the_pressed_modifier_without_sending_the_next_key()
    {
        var driver = new FakeDriver();
        int checks = 0;
        bool delivered = await ComputerUseKeyboard.PressComboAsync(
            driver, new ushort[] { 0x11, 0x53 }, () => ++checks == 1, CancellationToken.None);
        Assert.False(delivered);
        Assert.Equal(new[] { ((ushort)0x11, 0u), ((ushort)0x11, ComputerUseKeyboard.KeyEventKeyUp) }, driver.Keys);
    }

    [Fact]
    public async Task Cancelled_key_combo_releases_the_pressed_modifier()
    {
        var driver = new FakeDriver();
        using var cancellation = new CancellationTokenSource();
        int checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUseKeyboard.PressComboAsync(
            driver, new ushort[] { 0x11, 0x53 }, () =>
            {
                if (++checks == 1) cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.Equal(new[] { ((ushort)0x11, 0u), ((ushort)0x11, ComputerUseKeyboard.KeyEventKeyUp) }, driver.Keys);
    }

    [Fact]
    public async Task A_left_click_presses_and_releases_the_left_button_at_the_requested_point()
    {
        var driver = new FakeDriver();

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 10, 20, null, false, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Delivered, result.Outcome);
        Assert.Equal(1, result.ClicksSent);
        Assert.Single(driver.Moves);
        Assert.Equal((10, 20), driver.Moves[0]);
        Assert.Equal(new uint[] { ComputerUsePointer.MouseLeftDown, ComputerUsePointer.MouseLeftUp }, driver.Events.ToArray());
    }

    [Fact]
    public async Task A_middle_click_sends_middle_button_events_and_never_left_ones()
    {
        var driver = new FakeDriver();

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 1, 2, "middle", false, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Delivered, result.Outcome);
        Assert.Equal(new uint[] { ComputerUsePointer.MouseMiddleDown, ComputerUsePointer.MouseMiddleUp }, driver.Events.ToArray());
    }

    [Fact]
    public async Task A_right_click_sends_right_button_events()
    {
        var driver = new FakeDriver();

        await ComputerUsePointer.ClickAsync(driver, 1, 2, "RIGHT", false, null, CancellationToken.None);

        Assert.Equal(new uint[] { ComputerUsePointer.MouseRightDown, ComputerUsePointer.MouseRightUp }, driver.Events.ToArray());
    }

    [Fact]
    public async Task An_unknown_button_is_refused_and_nothing_is_sent()
    {
        var driver = new FakeDriver();

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 1, 2, "x1", false, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Refused, result.Outcome);
        Assert.Equal(0, result.ClicksSent);
        Assert.Empty(driver.Moves);
        Assert.Empty(driver.Events);
    }

    [Fact]
    public async Task A_double_click_sends_two_full_presses()
    {
        var driver = new FakeDriver();

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", true, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Delivered, result.Outcome);
        Assert.Equal(2, result.ClicksSent);
        Assert.Equal(4, driver.Events.Count);
    }

    [Fact]
    public async Task A_pointer_that_Windows_will_not_move_sends_no_click()
    {
        var driver = new FakeDriver { CursorMoves = false };

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Blocked, result.Outcome);
        Assert.Equal(0, result.ClicksSent);
        Assert.Empty(driver.Events);
    }

    [Fact]
    public async Task Input_that_Windows_inserts_zero_events_for_is_reported_as_blocked_not_delivered()
    {
        // SendInput returning 0 means nothing was delivered. The caller must not log a click that did not happen.
        var driver = new FakeDriver { EventsBeforeBlock = 0 };

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Blocked, result.Outcome);
        Assert.Equal(0, result.ClicksSent);
    }

    [Fact]
    public async Task A_double_click_whose_second_click_is_blocked_reports_exactly_one_click_sent()
    {
        var driver = new FakeDriver { EventsBeforeBlock = 2 };

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", true, null, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Blocked, result.Outcome);
        Assert.Equal(1, result.ClicksSent);
    }

    [Fact]
    public async Task A_target_that_lost_its_point_while_the_pointer_settled_gets_no_click()
    {
        var driver = new FakeDriver();

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, () => false, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.TargetChanged, result.Outcome);
        Assert.Equal(0, result.ClicksSent);
        Assert.Empty(driver.Events);
    }

    [Fact]
    public async Task The_target_is_checked_again_before_the_second_click_of_a_double_click()
    {
        var driver = new FakeDriver();
        int checks = 0;

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", true, () => ++checks == 1, CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.TargetChanged, result.Outcome);
        Assert.Equal(1, result.ClicksSent);
        Assert.Equal(2, driver.Events.Count);
    }

    [Fact]
    public async Task Cancellation_before_the_pointer_moves_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var driver = new FakeDriver();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, null, cts.Token));

        Assert.Empty(driver.Moves);
        Assert.Empty(driver.Events);
    }

    // The window the screenshot showed, and a second top-level window of the same process that the pin does not own.
    private static readonly ForegroundWindowSnapshot PinnedWindow = new(4242, 0x1234, "Untitled - Notepad");
    private static readonly ForegroundWindowSnapshot UnownedSameProcessWindow = new(4242, 0x6000, "Other document");

    [Fact]
    public async Task A_click_is_withheld_when_an_unowned_window_of_the_pinned_process_covers_the_point()
    {
        // Same process, so the process-level rule alone allowed it. Only the owner rule keeps the click away from this window.
        var driver = new FakeDriver();
        var pin = new ComputerUseInputPin(PinnedWindow, () => PinnedWindow, (x, y) => UnownedSameProcessWindow, handle => 0);

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, () => pin.IsStillForegroundAt(5, 5), CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.TargetChanged, result.Outcome);
        Assert.Equal(0, result.ClicksSent);
        Assert.Empty(driver.Events);
    }

    [Fact]
    public async Task A_click_is_delivered_when_the_pinned_window_itself_covers_the_point()
    {
        var driver = new FakeDriver();
        var pin = new ComputerUseInputPin(PinnedWindow, () => PinnedWindow, (x, y) => PinnedWindow, handle => 0);

        ComputerUsePointer.ClickResult result = await ComputerUsePointer.ClickAsync(
            driver, 5, 5, "left", false, () => pin.IsStillForegroundAt(5, 5), CancellationToken.None);

        Assert.Equal(ComputerUsePointer.Outcome.Delivered, result.Outcome);
        Assert.Equal(1, result.ClicksSent);
    }
}
