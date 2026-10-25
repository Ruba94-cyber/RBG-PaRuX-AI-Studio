using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A type action can run for seconds, and its text may be a secret. Four stop conditions must end it at a
/// character boundary: cancellation (the user pressed Stop), the target window losing foreground (the user moved on),
/// the run's authorization ending (Full Computer Use was turned off, or Gaming Mode started) and Windows refusing a
/// keystroke. Before this, the authorization and a refused keystroke were never checked, so a switch-off mid-payload
/// was ignored until the whole string had been typed, and a refused keystroke was counted as typed.
/// </summary>
public sealed class ComputerUseTypingSequencerTests
{
    [Fact]
    public async Task Every_character_is_sent_in_order_while_the_target_holds_foreground()
    {
        var sent = new StringBuilder();

        ComputerUseTypingSequencer.Result result = await ComputerUseTypingSequencer.SendAsync(
            "abc", c => { sent.Append(c); return true; }, () => true, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(ComputerUseTypingSequencer.Outcome.Completed, result.Outcome);
        Assert.Equal(3, result.CharactersSent);
        Assert.Equal("abc", sent.ToString());
    }

    [Fact]
    public async Task Typing_stops_before_the_next_character_once_the_target_loses_foreground()
    {
        var sent = new StringBuilder();
        int checks = 0;

        ComputerUseTypingSequencer.Result result = await ComputerUseTypingSequencer.SendAsync(
            "abcdef", c => { sent.Append(c); return true; }, () => ++checks <= 2, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(ComputerUseTypingSequencer.Outcome.TargetChanged, result.Outcome);
        Assert.Equal(2, result.CharactersSent);
        Assert.Equal("ab", sent.ToString());
    }

    [Fact]
    public async Task Cancellation_between_characters_stops_at_the_next_boundary_without_sending_more()
    {
        using var cts = new CancellationTokenSource();
        var sent = new StringBuilder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUseTypingSequencer.SendAsync(
            "abcdef",
            c =>
            {
                sent.Append(c);
                if (sent.Length == 2) cts.Cancel();
                return true;
            },
            () => true,
            TimeSpan.Zero,
            cts.Token));

        Assert.Equal("ab", sent.ToString());
    }

    [Fact]
    public async Task An_already_cancelled_run_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sent = new List<char>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUseTypingSequencer.SendAsync(
            "secret", c => { sent.Add(c); return true; }, () => true, TimeSpan.Zero, cts.Token));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Empty_text_completes_without_sending_anything()
    {
        var sent = new List<char>();

        ComputerUseTypingSequencer.Result result = await ComputerUseTypingSequencer.SendAsync(
            string.Empty, c => { sent.Add(c); return true; }, () => true, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(ComputerUseTypingSequencer.Outcome.Completed, result.Outcome);
        Assert.Equal(0, result.CharactersSent);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_revoked_authorization_stops_typing_before_the_next_character()
    {
        // Full Computer Use switched off, or Gaming Mode started, after two characters: the rest must not be sent.
        var sent = new StringBuilder();
        int checks = 0;

        ComputerUseTypingSequencer.Result result = await ComputerUseTypingSequencer.SendAsync(
            "abcdef", c => { sent.Append(c); return true; }, () => true, TimeSpan.Zero, CancellationToken.None,
            mayContinue: () => ++checks <= 2);

        Assert.Equal(ComputerUseTypingSequencer.Outcome.Revoked, result.Outcome);
        Assert.Equal(2, result.CharactersSent);
        Assert.Equal("ab", sent.ToString());
    }

    [Fact]
    public async Task A_refused_keystroke_stops_typing_and_reports_only_the_characters_that_were_accepted()
    {
        // Windows inserted no event for the third character (for example, a higher-privilege window has focus).
        var sent = new StringBuilder();

        ComputerUseTypingSequencer.Result result = await ComputerUseTypingSequencer.SendAsync(
            "abcdef",
            c =>
            {
                if (c == 'c') return false;
                sent.Append(c);
                return true;
            },
            () => true,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(ComputerUseTypingSequencer.Outcome.InputBlocked, result.Outcome);
        Assert.Equal(2, result.CharactersSent);
        Assert.Equal("ab", sent.ToString());
    }
}

/// <summary>
/// A wait action can run for five seconds. Before this it slept the full length regardless of the authorization,
/// so turning Full Computer Use off during a wait still let the wait finish and the run continue.
/// </summary>
public sealed class ComputerUseWaitTests
{
    [Fact]
    public async Task A_wait_ends_at_the_first_poll_after_its_authorization_ends()
    {
        int checks = 0;
        var clock = Stopwatch.StartNew();

        bool completed = await ComputerUseWait.WaitWhileAuthorizedAsync(
            TimeSpan.FromSeconds(10), () => ++checks <= 3, CancellationToken.None);

        Assert.False(completed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "A revoked wait must not run its full ten seconds.");
    }

    [Fact]
    public async Task A_wait_runs_its_full_length_while_the_authorization_holds()
    {
        var clock = Stopwatch.StartNew();

        bool completed = await ComputerUseWait.WaitWhileAuthorizedAsync(
            TimeSpan.FromMilliseconds(150), () => true, CancellationToken.None);

        Assert.True(completed);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public async Task Cancelling_a_wait_throws_rather_than_reporting_it_completed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUseWait.WaitWhileAuthorizedAsync(
            TimeSpan.FromSeconds(10), () => true, cts.Token));
    }
}
