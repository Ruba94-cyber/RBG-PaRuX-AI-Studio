using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatLatencyPolicyTests
{
    [Fact]
    public void LowLatencyContractDisablesHiddenReasoningBudget()
    {
        var decision = AdaptiveReasoningPolicy.Decide(new ChatCompletionRequest
        {
            PreferLowLatency = true,
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "user", Content = "Rispondi con una parola." }
            }
        });

        Assert.Equal("minimal", decision.Effort);
        Assert.Equal(0, decision.Budget);
    }

    private static SkillRouteResult GeneralRoute() => new()
    {
        Skill = new SkillManifest { Id = "general", DisplayName = "General" },
        Mode = "auto"
    };

    [Fact]
    public void Simple_general_turn_can_skip_optional_retrieval_and_tool_discovery()
    {
        var request = new ChatCompletionRequest { PreferLowLatency = true };

        Assert.True(ChatProxyService.ShouldUseLowLatencyPath(request, GeneralRoute(), hasVisualInput: false));
    }

    [Fact]
    public void Visual_or_forced_skill_turn_never_uses_the_low_latency_shortcut()
    {
        var visual = new ChatCompletionRequest { PreferLowLatency = true };
        var forced = new ChatCompletionRequest { PreferLowLatency = true, SkillId = "grill-me" };

        Assert.False(ChatProxyService.ShouldUseLowLatencyPath(visual, GeneralRoute(), hasVisualInput: true));
        Assert.False(ChatProxyService.ShouldUseLowLatencyPath(forced, GeneralRoute(), hasVisualInput: false));
    }

    [Fact]
    public void Auto_selected_specialist_skill_keeps_its_full_context_path()
    {
        var request = new ChatCompletionRequest { PreferLowLatency = true };
        var specialist = new SkillRouteResult { Skill = new SkillManifest { Id = "grill-me" }, Mode = "auto" };

        Assert.False(ChatProxyService.ShouldUseLowLatencyPath(request, specialist, hasVisualInput: false));
    }
}
