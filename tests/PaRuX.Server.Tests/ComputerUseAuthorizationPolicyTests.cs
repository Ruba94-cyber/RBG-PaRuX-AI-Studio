using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Full Computer Use is a persistent switch. Before this check, a run started under it kept clicking and
/// typing for up to fifteen steps after the switch was turned off, because the setting was read only when
/// the run began. A per-task approval is a different authorization and is not revoked by a settings change.
/// </summary>
public sealed class ComputerUseAuthorizationPolicyTests
{
    [Fact]
    public void A_run_relying_on_full_automation_stops_as_soon_as_the_switch_is_turned_off()
    {
        bool enabled = true;
        Func<bool> persistent = () => enabled;

        Assert.Null(ComputerUseAuthorizationPolicy.RevocationReason(persistent));

        enabled = false;

        string? reason = ComputerUseAuthorizationPolicy.RevocationReason(persistent);
        Assert.NotNull(reason);
        Assert.Contains("Full Computer Use was turned off", reason);
    }

    [Fact]
    public void The_switch_is_read_again_on_every_check_rather_than_once_at_the_start()
    {
        int reads = 0;
        Func<bool> persistent = () =>
        {
            reads++;
            return true;
        };

        ComputerUseAuthorizationPolicy.RevocationReason(persistent);
        ComputerUseAuthorizationPolicy.RevocationReason(persistent);
        ComputerUseAuthorizationPolicy.RevocationReason(persistent);

        Assert.Equal(3, reads);
    }

    [Fact]
    public void A_run_holding_only_a_per_task_approval_is_not_revoked_by_a_settings_change()
    {
        // No persistent authorization is supplied for such a run, so there is nothing a settings change can revoke.
        Assert.Null(ComputerUseAuthorizationPolicy.RevocationReason(null));
    }

    [Fact]
    public void A_remote_task_holding_a_grant_and_the_switch_is_authorized_by_the_switch_so_turning_it_off_stops_it()
    {
        // Before this, a per-task grant suppressed the switch's revocation check, so a run that held both kept going
        // after the switch was turned off, while its audit trail still named the switch as its authority.
        ComputerUseAuthorizationPolicy.RemoteAuthority? authority =
            ComputerUseAuthorizationPolicy.ResolveRemoteAuthority(perTaskApproved: true, fullAutomationEnabled: true);

        Assert.Equal(ComputerUseAuthorizationPolicy.RemoteAuthority.FullComputerUse, authority);

        bool enabled = true;
        Func<bool> switchIsOn = () => enabled;
        Assert.Null(ComputerUseAuthorizationPolicy.RevocationReason(switchIsOn));

        enabled = false;
        Assert.NotNull(ComputerUseAuthorizationPolicy.RevocationReason(switchIsOn));
    }

    [Fact]
    public void A_per_task_grant_alone_is_the_per_task_authority()
    {
        Assert.Equal(ComputerUseAuthorizationPolicy.RemoteAuthority.PerTaskApproval,
            ComputerUseAuthorizationPolicy.ResolveRemoteAuthority(perTaskApproved: true, fullAutomationEnabled: false));
    }

    [Fact]
    public void The_switch_alone_is_the_full_computer_use_authority()
    {
        Assert.Equal(ComputerUseAuthorizationPolicy.RemoteAuthority.FullComputerUse,
            ComputerUseAuthorizationPolicy.ResolveRemoteAuthority(perTaskApproved: false, fullAutomationEnabled: true));
    }

    [Fact]
    public void A_remote_task_with_neither_authorization_has_no_authority_to_act()
    {
        Assert.Null(ComputerUseAuthorizationPolicy.ResolveRemoteAuthority(perTaskApproved: false, fullAutomationEnabled: false));
    }

    [Fact]
    public void Gaming_Mode_refuses_computer_use_at_entry_even_when_full_automation_is_on()
    {
        Assert.NotNull(ComputerUseAuthorizationPolicy.GamingRefusal(gamingActive: true));
        Assert.Null(ComputerUseAuthorizationPolicy.GamingRefusal(gamingActive: false));
    }

    [Fact]
    public void Gaming_Mode_outranks_the_switch_and_stops_a_run_that_relies_on_it()
    {
        // The switch is on, so the revocation check alone would let the run continue. Gaming Mode must still stop it.
        string? reason = ComputerUseAuthorizationPolicy.StopReason(() => true, gamingActive: true);

        Assert.NotNull(reason);
        Assert.Contains("Gaming Mode", reason);
    }

    [Fact]
    public void A_run_continues_when_neither_gaming_nor_a_revocation_applies()
    {
        Assert.Null(ComputerUseAuthorizationPolicy.StopReason(() => true, gamingActive: false));
        Assert.Null(ComputerUseAuthorizationPolicy.StopReason(null, gamingActive: false));
    }

    [Fact]
    public void A_turned_off_switch_stops_a_run_when_gaming_is_off()
    {
        Assert.Equal(ComputerUseAuthorizationPolicy.FullAutomationRevokedReason,
            ComputerUseAuthorizationPolicy.StopReason(() => false, gamingActive: false));
    }
}
