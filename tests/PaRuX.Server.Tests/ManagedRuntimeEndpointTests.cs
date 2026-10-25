using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedRuntimeEndpointTests
{
    [Fact]
    public void App_endpoint_comes_from_runtime_status_instead_of_the_preferred_port()
    {
        var status = new RuntimeManagerStatus
        {
            CurrentManagedProviderEndpoint = "http://127.0.0.1:43127"
        };

        Assert.Equal("http://127.0.0.1:43127", ManagedRuntimeEndpoint.FromStatus(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://127.0.0.1:43127")]
    [InlineData("http://example.invalid:43127")]
    [InlineData("http://127.0.0.1:43127/other")]
    [InlineData("http://127.0.0.1:43127?probe=1")]
    [InlineData("http://127.0.0.1:43127#fragment")]
    [InlineData("http://operator@127.0.0.1:43127")]
    public void App_endpoint_rejects_missing_or_non_loopback_status_values(string? value)
    {
        Assert.Null(ManagedRuntimeEndpoint.FromStatus(new RuntimeManagerStatus
        {
            CurrentManagedProviderEndpoint = value
        }));
    }
}
