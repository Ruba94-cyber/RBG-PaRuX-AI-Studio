using System;
using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pixel input is chosen from a screenshot, and inference can take minutes. Before this change the click,
/// type and key actions never checked which window was in the foreground, so a window switch during
/// inference sent the same coordinates and keystrokes to a different application. Every pixel action is
/// now pinned to the window identity captured with the screenshot, and refused when that identity changed.
/// </summary>
public sealed class ComputerUseInputPinTests
{
    private static readonly ForegroundWindowSnapshot Notepad = new(4242, 0x1234, "Untitled - Notepad");

    [Fact]
    public void Input_is_allowed_while_the_pinned_window_is_still_foreground()
    {
        var pin = new ComputerUseInputPin(Notepad, () => Notepad);

        Assert.Null(pin.RefusalIfChanged());
        Assert.True(pin.IsStillForeground());
    }

    [Fact]
    public void Retitling_the_pinned_window_does_not_count_as_a_change()
    {
        // Typing into Notepad changes its title to "*Untitled - Notepad". That is the same window.
        var retitled = Notepad with { Title = "*Untitled - Notepad" };
        var pin = new ComputerUseInputPin(Notepad, () => retitled);

        Assert.Null(pin.RefusalIfChanged());
        Assert.True(pin.IsStillForeground());
    }

    [Fact]
    public void Input_is_refused_when_another_window_has_taken_the_foreground()
    {
        var chrome = new ForegroundWindowSnapshot(777, 0x9999, "Inbox - Mail");
        var pin = new ComputerUseInputPin(Notepad, () => chrome);

        string? refusal = pin.RefusalIfChanged();

        Assert.NotNull(refusal);
        Assert.Contains("Untitled - Notepad", refusal);
        Assert.Contains("Inbox - Mail", refusal);
        Assert.Contains("No input was sent", refusal);
        Assert.False(pin.IsStillForeground());
    }

    [Fact]
    public void The_same_handle_owned_by_another_process_is_refused()
    {
        var reused = new ForegroundWindowSnapshot(5555, Notepad.WindowHandle, "Untitled - Notepad");
        var pin = new ComputerUseInputPin(Notepad, () => reused);

        Assert.NotNull(pin.RefusalIfChanged());
        Assert.False(pin.IsStillForeground());
    }

    [Fact]
    public void Input_is_refused_when_no_window_is_in_the_foreground_at_action_time()
    {
        var none = new ForegroundWindowSnapshot(0, 0, string.Empty);
        var pin = new ComputerUseInputPin(Notepad, () => none);

        string? refusal = pin.RefusalIfChanged();

        Assert.NotNull(refusal);
        Assert.Contains("no window is in the foreground", refusal);
        Assert.False(pin.IsStillForeground());
    }

    [Fact]
    public void Input_is_refused_outright_when_the_screenshot_had_no_identifiable_window()
    {
        var empty = new ForegroundWindowSnapshot(0, 0, string.Empty);
        var pin = new ComputerUseInputPin(empty, () => Notepad);

        Assert.NotNull(pin.RefusalIfChanged());
        Assert.False(pin.IsStillForeground());
    }

    [Fact]
    public void The_foreground_is_read_again_on_every_check_so_a_later_switch_is_caught()
    {
        ForegroundWindowSnapshot current = Notepad;
        var pin = new ComputerUseInputPin(Notepad, () => current);

        Assert.Null(pin.RefusalIfChanged());

        current = new ForegroundWindowSnapshot(777, 0x9999, "Inbox - Mail");

        Assert.NotNull(pin.RefusalIfChanged());
        Assert.False(pin.IsStillForeground());
    }

    [Fact]
    public void A_very_long_window_title_is_truncated_in_the_refusal()
    {
        var longTitled = new ForegroundWindowSnapshot(777, 0x9999, new string('T', 300));
        var pin = new ComputerUseInputPin(Notepad, () => longTitled);

        string? refusal = pin.RefusalIfChanged();

        Assert.NotNull(refusal);
        Assert.DoesNotContain(new string('T', 200), refusal);
        Assert.Contains("…", refusal);
    }

    // A menu the pinned application opened: same process, and owned by the pinned window.
    private static readonly ForegroundWindowSnapshot PinnedProcessMenu = new(4242, 0x5678, "Context menu");

    // Another top-level window of the pinned process that the pinned window does not own, for example a separate document window.
    private static readonly ForegroundWindowSnapshot UnownedSameProcessWindow = new(4242, 0x6000, "Other document");

    // A topmost window of another process, for example a notification or another application's always-on-top window.
    private static readonly ForegroundWindowSnapshot OtherProcessTopmost = new(999, 0x7777, "Notification");

    /// <summary>An owner lookup over a fixed table. A window missing from the table has no owner.</summary>
    private static Func<long, long> OwnerLookup(IReadOnlyDictionary<long, long> owners) =>
        handle => owners.TryGetValue(handle, out long owner) ? owner : 0;

    [Fact]
    public void A_point_over_a_menu_the_pinned_window_owns_is_still_owned_by_the_pin()
    {
        var owners = new Dictionary<long, long> { [PinnedProcessMenu.WindowHandle] = Notepad.WindowHandle };
        var pin = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => PinnedProcessMenu, OwnerLookup(owners));

        Assert.True(pin.IsStillForegroundAt(10, 10));
        Assert.Null(pin.RefusalIfPointNotOwned(10, 10));
    }

    [Fact]
    public void A_second_top_level_window_of_the_pinned_process_that_the_pin_does_not_own_is_refused()
    {
        // Same process, so the process-level rule alone accepted it. It is not the window the screenshot showed.
        var pin = new ComputerUseInputPin(
            Notepad, () => Notepad, (x, y) => UnownedSameProcessWindow, OwnerLookup(new Dictionary<long, long>()));

        Assert.Null(pin.RefusalIfChanged());
        Assert.False(pin.IsStillForegroundAt(10, 10));

        string? refusal = pin.RefusalIfPointNotOwned(10, 10);
        Assert.NotNull(refusal);
        Assert.Contains("No input was sent", refusal);
    }

    [Fact]
    public void A_popup_owned_through_a_dialog_is_accepted_and_a_window_owned_by_another_window_is_refused()
    {
        // The pinned window owns a dialog, which owns a popup. The third window is in the same process but belongs to another top-level window.
        var owners = new Dictionary<long, long>
        {
            [0x2000] = Notepad.WindowHandle,
            [0x3000] = 0x2000,
            [0x4000] = 0x9999
        };
        Func<long, long> ownerOf = OwnerLookup(owners);
        var popup = new ForegroundWindowSnapshot(4242, 0x3000, "Popup");
        var foreign = new ForegroundWindowSnapshot(4242, 0x4000, "Owned by another window");

        Assert.True(new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => popup, ownerOf).IsStillForegroundAt(10, 10));
        Assert.False(new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => foreign, ownerOf).IsStillForegroundAt(10, 10));
    }

    [Fact]
    public void Without_an_owner_lookup_only_the_pinned_window_itself_is_accepted_under_a_point()
    {
        // Ownership cannot be shown, so a same-process popup is refused rather than assumed to belong to the pin.
        var popup = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => PinnedProcessMenu);
        var self = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => Notepad);

        Assert.False(popup.IsStillForegroundAt(10, 10));
        Assert.True(self.IsStillForegroundAt(10, 10));
    }

    [Fact]
    public void A_window_of_another_process_is_refused_even_when_it_claims_the_pinned_window_as_its_owner()
    {
        var owners = new Dictionary<long, long> { [OtherProcessTopmost.WindowHandle] = Notepad.WindowHandle };
        var pin = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => OtherProcessTopmost, OwnerLookup(owners));

        Assert.False(pin.IsStillForegroundAt(10, 10));
        Assert.NotNull(pin.RefusalIfPointNotOwned(10, 10));
    }

    [Fact]
    public void An_owner_cycle_is_bounded_and_refused_rather_than_followed_forever()
    {
        var owners = new Dictionary<long, long> { [0x7000] = 0x7001, [0x7001] = 0x7000 };
        var cyclic = new ForegroundWindowSnapshot(4242, 0x7000, "Cyclic owners");
        var pin = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => cyclic, OwnerLookup(owners));

        Assert.False(pin.IsStillForegroundAt(10, 10));
    }

    [Fact]
    public void A_topmost_window_of_another_process_under_the_point_is_refused_while_the_foreground_is_unchanged()
    {
        // Notifications and other applications' topmost windows can cover the point without taking the foreground,
        // so the foreground check alone let a click reach them.
        var pin = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => OtherProcessTopmost);

        Assert.Null(pin.RefusalIfChanged());
        Assert.False(pin.IsStillForegroundAt(10, 10));

        string? refusal = pin.RefusalIfPointNotOwned(10, 10);
        Assert.NotNull(refusal);
        Assert.Contains("No input was sent", refusal);
    }

    [Fact]
    public void A_point_over_no_window_is_refused()
    {
        var pin = new ComputerUseInputPin(Notepad, () => Notepad, (x, y) => new ForegroundWindowSnapshot(0, 0, string.Empty));

        Assert.False(pin.IsStillForegroundAt(10, 10));
        Assert.NotNull(pin.RefusalIfPointNotOwned(10, 10));
    }

    [Fact]
    public void Positional_input_fails_closed_when_no_point_lookup_is_available()
    {
        var pin = new ComputerUseInputPin(Notepad, () => Notepad);

        Assert.False(pin.IsStillForegroundAt(10, 10));
        Assert.NotNull(pin.RefusalIfPointNotOwned(10, 10));
    }

    [Fact]
    public void A_foreground_change_refuses_positional_input_even_when_the_point_is_still_owned()
    {
        ForegroundWindowSnapshot current = Notepad;
        var pin = new ComputerUseInputPin(Notepad, () => current, (x, y) => Notepad);
        Assert.True(pin.IsStillForegroundAt(10, 10));

        current = new ForegroundWindowSnapshot(777, 0x9999, "Inbox - Mail");

        Assert.False(pin.IsStillForegroundAt(10, 10));
    }

    [Fact]
    public void A_UI_tree_read_from_another_window_is_refused_and_the_pinned_window_tree_is_allowed()
    {
        // The screenshot and the UIA tree must describe the same window, or a UIA action could target a window the model never saw.
        var pin = new ComputerUseInputPin(Notepad, () => Notepad);

        Assert.Null(pin.RefusalForUiaObservation(Notepad.ProcessId, Notepad.WindowHandle));
        Assert.NotNull(pin.RefusalForUiaObservation(777, 0x9999));
        Assert.NotNull(pin.RefusalForUiaObservation(Notepad.ProcessId, 0x9999));
        Assert.NotNull(pin.RefusalForUiaObservation(0, 0));
    }

    [Fact]
    public void A_matching_UI_tree_is_refused_when_the_foreground_moved_after_it_was_read()
    {
        ForegroundWindowSnapshot current = Notepad;
        var pin = new ComputerUseInputPin(Notepad, () => current);
        current = new ForegroundWindowSnapshot(777, 0x9999, "Inbox - Mail");

        Assert.NotNull(pin.RefusalForUiaObservation(Notepad.ProcessId, Notepad.WindowHandle));
    }
    [Fact]
    public void Modal_dialog_foreground_invalidates_old_main_screenshot_even_same_PID()
    {
        var modal = new ForegroundWindowSnapshot(
            Notepad.ProcessId, 0x5000, "Owned modal dialog");
        var owners = new Dictionary<long, long> { [modal.WindowHandle] = Notepad.WindowHandle };
        ForegroundWindowSnapshot foreground = Notepad;
        var oldMainPin = new ComputerUseInputPin(
            Notepad, () => foreground, (x, y) => modal, OwnerLookup(owners));

        // A modal can open between screenshot and dispatch. Even though it is
        // a valid owned child, old main-window coordinates and UIA are stale.
        foreground = modal;
        Assert.NotNull(oldMainPin.RefusalIfChanged());
        Assert.False(oldMainPin.IsStillForegroundAt(10, 10));
        Assert.NotNull(oldMainPin.RefusalForUiaObservation(modal.ProcessId, modal.WindowHandle));

        // The NEXT independently captured screenshot may pin that modal, and
        // only then allow an observed control of its exact HWND.
        var freshModalPin = new ComputerUseInputPin(
            modal, () => foreground, (x, y) => modal, OwnerLookup(owners));
        Assert.Null(freshModalPin.RefusalIfChanged());
        Assert.True(freshModalPin.IsStillForegroundAt(10, 10));
        Assert.Null(freshModalPin.RefusalForUiaObservation(modal.ProcessId, modal.WindowHandle));

        // Closing the modal returns focus to its owner and invalidates
        // the dialog's own screenshot in exactly the same way.
        foreground = Notepad;
        Assert.NotNull(freshModalPin.RefusalIfChanged());
        Assert.NotNull(freshModalPin.RefusalForUiaObservation(Notepad.ProcessId, Notepad.WindowHandle));
    }

    [Fact]
    public void Helper_process_modal_cannot_inherit_the_parent_screenshot_scope()
    {
        var helper = new ForegroundWindowSnapshot(5001, 0x8000, "External helper modal");
        var owners = new Dictionary<long, long> { [helper.WindowHandle] = Notepad.WindowHandle };
        var mainPin = new ComputerUseInputPin(
            Notepad, () => Notepad, (x, y) => helper, OwnerLookup(owners));
        Assert.False(mainPin.IsStillForegroundAt(42, 42));
        Assert.NotNull(mainPin.RefusalIfPointNotOwned(42, 42));
        Assert.NotNull(mainPin.RefusalForUiaObservation(helper.ProcessId, helper.WindowHandle));
    }

}
