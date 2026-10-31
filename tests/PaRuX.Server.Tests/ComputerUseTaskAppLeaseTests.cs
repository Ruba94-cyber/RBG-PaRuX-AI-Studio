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

    [Fact]
    public void Unbound_task_keeps_existing_explicit_user_computer_use_authority()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.Null(lease.RefusalForAction(Screenshot(), null));
    }

    [Fact]
    public void Successful_approved_app_binding_requires_fresh_matching_window()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindApprovedExecutable(Exe("Editor.exe")));
        Assert.True(lease.IsActive);
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Other.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe", 71, 100)));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe", 70, 101)));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), null));
    }

    [Fact]
    public void Untrusted_or_invalid_binding_cannot_revoke_a_preexisting_safe_scope()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.False(lease.BindApprovedExecutable("relative.exe"));
        Assert.False(lease.IsActive);
        Assert.True(lease.BindApprovedExecutable(Exe("Editor.exe")));
        Assert.False(lease.BindApprovedExecutable(Exe("cmd.exe")));
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Editor.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("cmd.exe")));
    }

    [Fact]
    public void Switching_to_a_second_approved_app_updates_only_this_run_scope()
    {
        var lease = new ComputerUseTaskAppLease();
        Assert.True(lease.BindApprovedExecutable(Exe("Editor.exe")));
        Assert.True(lease.BindApprovedExecutable(Exe("Excel.exe")));
        Assert.Null(lease.RefusalForAction(Screenshot(), Current("Excel.exe")));
        Assert.NotNull(lease.RefusalForAction(Screenshot(), Current("Editor.exe")));

        var unrelatedRun = new ComputerUseTaskAppLease();
        Assert.False(unrelatedRun.IsActive);
    }
}
