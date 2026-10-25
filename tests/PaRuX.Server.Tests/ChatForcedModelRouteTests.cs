using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A one-turn model override must preserve its source authority. Registered candidates and
/// managed GGUF files use different admission gates; conflating them breaks local-first chat.
/// </summary>
public sealed class ChatForcedModelRouteTests
{
    private static readonly ChatRoutingPreferenceStore.Preference Auto = new()
    {
        Mode = RouteMode.Auto,
        AutoProfile = AutoIntelligenceProfile.Balanced
    };

    [Fact]
    public void Managed_library_override_does_not_require_candidate_registration()
    {
        var request = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "standard",
                Mode = RouteMode.Manual,
                ExactManagedModelFileName = "starter-q4.gguf"
            }
        };

        RouteRequest result = ChatProxyService.BuildRouteRequest(Auto, null, "Hello", null, request);

        Assert.Equal(RouteMode.Manual, result.Mode);
        Assert.Equal("standard", result.Role);
        Assert.Equal("starter-q4.gguf", result.ExactManagedModelFileName);
        Assert.Null(result.ExactCandidateFileName);
    }

    [Fact]
    public void Registered_candidate_override_does_not_invent_a_managed_library_lock()
    {
        var request = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "standard",
                ExactCandidateFileName = "registered-q4.gguf"
            }
        };

        RouteRequest result = ChatProxyService.BuildRouteRequest(Auto, null, "Hello", null, request);

        Assert.Equal("registered-q4.gguf", result.ExactCandidateFileName);
        Assert.Null(result.ExactManagedModelFileName);
    }

    [Fact]
    public void Scoped_candidate_authority_survives_a_one_turn_override()
    {
        var request = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "coding",
                ScopedCandidateKey = "local-coder",
                ScopedCandidateRevision = "v2"
            }
        };

        RouteRequest result = ChatProxyService.BuildRouteRequest(Auto, null, "Explain this code", null, request);

        Assert.Equal(RouteMode.Manual, result.Mode);
        Assert.Equal("local-coder", result.ScopedCandidateKey);
        Assert.Equal("v2", result.ScopedCandidateRevision);
        Assert.Null(result.ExactCandidateFileName);
        Assert.Null(result.ExactManagedModelFileName);
    }

    [Fact]
    public void Temporary_managed_override_does_not_replace_the_persistent_manual_preference()
    {
        var preference = new ChatRoutingPreferenceStore.Preference
        {
            Mode = RouteMode.Manual,
            ManualModelFileName = "personal-default.gguf"
        };
        var request = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "standard",
                ExactManagedModelFileName = "one-turn.gguf"
            }
        };

        RouteRequest result = ChatProxyService.BuildRouteRequest(preference, null, "Hi", null, request);

        Assert.Equal("one-turn.gguf", result.ExactManagedModelFileName);
        Assert.Null(result.ExactCandidateFileName);
        Assert.Equal("personal-default.gguf", preference.ManualModelFileName);
    }
}
