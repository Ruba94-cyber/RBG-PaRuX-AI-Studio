using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Computer Use's fast path handed any http or https address to the shell, which opened it in the default
/// browser with no Internet check. PaRuX's own browser tools were already gated on the Internet setting, so
/// the same setting now governs this launch too.
/// </summary>
public sealed class ComputerUseWebOpenPolicyTests
{
    [Fact]
    public void Opening_a_web_address_is_refused_while_the_internet_setting_is_off()
    {
        string? refusal = ComputerUseAuthorizationPolicy.WebOpenRefusal(internetEnabled: false);

        Assert.NotNull(refusal);
        Assert.Contains("Internet access is turned off", refusal);
        Assert.Contains("did not open", refusal);
    }

    [Fact]
    public void Opening_a_web_address_is_allowed_once_the_internet_setting_is_on()
    {
        Assert.Null(ComputerUseAuthorizationPolicy.WebOpenRefusal(internetEnabled: true));
    }
}
