using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StructuredChatRoleRouteTests
{
    [Theory]
    [InlineData("parux-role:coding", "parux-coding")]
    [InlineData(" PARUX-ROLE:STANDARD ", "parux-main")]
    public void Explicit_role_selectors_resolve_to_provider_model_ids(string selector, string expected)
    {
        Assert.True(StructuredChatRoleRoute.TryResolve(selector, out string modelId));
        Assert.Equal(expected, modelId);
    }

    [Theory]
    [InlineData("parux-auto:max_intelligence")]
    [InlineData("parux-manual:model.gguf")]
    [InlineData(null)]
    public void Chat_selectors_do_not_become_direct_role_routes(string? selector)
    {
        Assert.False(StructuredChatRoleRoute.TryResolve(selector, out string modelId));
        Assert.Empty(modelId);
    }

    [Fact]
    public void Unknown_role_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => StructuredChatRoleRoute.TryResolve("parux-role:unknown", out _));
    }
}
