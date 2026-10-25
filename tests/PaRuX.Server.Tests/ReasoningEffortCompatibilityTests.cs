using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ReasoningEffortCompatibilityTests
{
    // The validation line of the served Qwen3.8 27B template (live 2026-09-30).
    private const string Qwen38Template =
        "{%- set resolved_reasoning_effort = reasoning_effort|default('xhigh') %}\n" +
        "{%- if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}\n" +
        "{{- raise_exception('Unexpected reasoning effort ' ~ reasoning_effort) }}";

    [Theory]
    [InlineData("high", "medium")]      // live failure: HTTP 500 "Unexpected reasoning effort high"
    [InlineData("xhigh", "xhigh")]
    [InlineData("medium", "medium")]
    [InlineData("minimal", "low")]      // nothing cheaper is accepted: cheapest supported value
    [InlineData("max", "xhigh")]
    public void Efforts_map_onto_the_served_templates_vocabulary(string requested, string expected) =>
        Assert.Equal(expected, ReasoningEffortCompatibility.Resolve(
            requested, ReasoningEffortCompatibility.ParseSupported(Qwen38Template)));

    [Fact]
    public void Templates_that_do_not_validate_effort_keep_the_requested_value()
    {
        Assert.Null(ReasoningEffortCompatibility.ParseSupported("{{ messages }}"));
        Assert.Equal("high", ReasoningEffortCompatibility.Resolve("high", null));
        Assert.Equal("custom", ReasoningEffortCompatibility.Resolve("custom", new[] { "low", "high" }));
    }
}
