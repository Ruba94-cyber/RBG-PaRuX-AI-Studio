using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseTaskAppLeaseTests
{
    private static string Exe(string name) => Path.Combine(Path.GetTempPath(), "parux-fixture", name);
    private static ForegroundWindowSnapshot Screenshot(int pid = 70, long hwnd = 100) =>
        new(pid, hwnd, "User document title");
    private static ComputerUseWindowFocusPolicy.Candidate Current(
        string exe, int pid = 70, long hwnd = 100) => new("Untrusted title", pid, hwnd, Exe(exe));
    private static string[] Approved(params string[] binaries) =>
        Array.ConvertAll(binaries, Exe);

    [Fact]
    public void Unbound_task_keeps_existing_explicit_user_computer_use_authority()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.Null(lease.RefusalForAction(Screenshot(), null, Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Open_app_path_only_is_provisional_until_explicit_verified_focus()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindApprovedExecutable(Exe("Editor.exe")));
        Assert.True(lease.IsActive);
        Assert.False(lease.HasVerifiedProcess);
        Assert.Contains("focus_window", lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.True(lease.HasVerifiedProcess);
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Same_binary_in_another_process_is_not_the_authorized_window_instance()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(71, 200), Current("Editor.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe", 71, 100), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(71, 100), Current("Editor.exe", 71, 100), Approved("Editor.exe", "Excel.exe", "Helper.exe")));

        // A deliberately requested and OS-verified focus transition, while
        // the executable remains approved, may authorize the new instance.
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe", 71, 200), true));
        Assert.Null(lease.RefusalForAction(Screenshot(71, 200), Current("Editor.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Same_process_dialogs_and_secondary_top_level_windows_remain_in_scope()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.Null(lease.RefusalForAction(Screenshot(70, 201), Current("Editor.exe", 70, 201), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.Null(lease.RefusalForAction(Screenshot(70, 202), Current("Editor.exe", 70, 202), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(70, 202), Current("Editor.exe", 70, 201), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Secondary_process_with_another_executable_requires_its_own_approval()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.False(lease.BindVerifiedFocus(Current("Helper.exe", 71, 200), false));
        Assert.NotNull(lease.RefusalForAction(Screenshot(71, 200), Current("Helper.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));

        Assert.True(lease.BindVerifiedFocus(Current("Helper.exe", 71, 200), true));
        Assert.Null(lease.RefusalForAction(Screenshot(71, 200), Current("Helper.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Same_verified_process_does_not_require_regrant_for_second_dialog()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        // No additional consent dialog is necessary when the app remains in
        // Settings, but a revoked setting must NEVER be silently inherited.
        Assert.False(lease.BindVerifiedFocus(Current("Editor.exe", 70, 202), false));
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe", 70, 202), true));
        Assert.Null(lease.RefusalForAction(Screenshot(70, 203), Current("Editor.exe", 70, 203), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Untrusted_or_invalid_binding_never_destroys_preexisting_verified_scope()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.False(lease.BindApprovedExecutable("relative.exe"));
        Assert.False(lease.IsActive);
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.True(lease.HasVerifiedProcess);
        Assert.False(lease.BindApprovedExecutable(Exe("cmd.exe")));
        Assert.False(lease.BindVerifiedFocus(Current("cmd.exe"), true));
        Assert.False(lease.BindVerifiedFocus(Current("Helper.exe", 71, 201), false));
        Assert.False(lease.BindVerifiedFocus(Current("Editor.exe", 71, 201), false));
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("cmd.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.False(lease.BindVerifiedFocus(null, true));
    }

    [Fact]
    public void Approved_new_open_app_clears_old_process_authority_until_refocused()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.True(lease.BindApprovedExecutable(Exe("Excel.exe")));
        Assert.False(lease.HasVerifiedProcess);
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(90, 300), Current("Excel.exe", 90, 300), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.True(lease.BindVerifiedFocus(Current("Excel.exe", 90, 300), true));
        Assert.Null(lease.RefusalForAction(Screenshot(90, 300), Current("Excel.exe", 90, 300), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }

    [Fact]
    public void Process_scope_is_task_local_and_does_not_grant_other_agent_runs()
    {
        var first = new ComputerUseTaskAppLease();
        var second = new ComputerUseTaskAppLease();
        Assert.True(first.BindVerifiedFocus(Current("Editor.exe", 70, 100), true));
        Assert.False(second.IsActive);
        Assert.True(second.BindVerifiedFocus(Current("Editor.exe", 71, 200), true));
        Assert.NotNull(first.RefusalForAction(Screenshot(71, 200), Current("Editor.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
        Assert.Null(second.RefusalForAction(Screenshot(71, 200), Current("Editor.exe", 71, 200), Approved("Editor.exe", "Excel.exe", "Helper.exe")));
    }
    [Fact]
    public void Revocation_of_a_verified_app_immediately_stops_actions_and_completion()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.Null(lease.RefusalForAction(
            Screenshot(), Current("Editor.exe"), Approved("Editor.exe")));

        string? denial = lease.RefusalForAction(
            Screenshot(), Current("Editor.exe"), Approved("Excel.exe"));
        Assert.NotNull(denial);
        Assert.Contains("removed", denial);
        Assert.NotNull(lease.RefusalForCurrentApproval(Array.Empty<string>()));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe"), null));
        Assert.False(lease.BindVerifiedFocus(Current("Editor.exe", 70, 201), false));

        // An explicit user reapproval of the SAME exe restores input for
        // the still verified PID, without changing any other task's scope.
        Assert.Null(lease.RefusalForAction(
            Screenshot(70, 201), Current("Editor.exe", 70, 201), Approved("Editor.exe")));
    }

    [Fact]
    public void A_secondary_helper_cannot_inherit_parent_allowlist_after_focus()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindVerifiedFocus(Current("Editor.exe"), true));
        Assert.False(lease.BindVerifiedFocus(Current("Helper.exe", 71, 200), false));
        Assert.True(lease.BindVerifiedFocus(Current("Helper.exe", 71, 200), true));

        Assert.NotNull(lease.RefusalForAction(
            Screenshot(71, 200), Current("Helper.exe", 71, 200), Approved("Editor.exe")));
        Assert.Null(lease.RefusalForAction(
            Screenshot(71, 200), Current("Helper.exe", 71, 200), Approved("Editor.exe", "Helper.exe")));
    }

}
