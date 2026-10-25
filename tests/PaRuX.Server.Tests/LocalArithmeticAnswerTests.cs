using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalArithmeticAnswerTests
{
    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("Quanto fa (47 + 3) * 2?", "100")]
    [InlineData("what is 12 × 8?", "96")]
    [InlineData("calcola 7,5 / 2", "3.75")]
    [InlineData("-3 + 10", "7")]
    public void Explicit_bounded_arithmetic_is_answered_locally(string prompt, string expected)
    {
        Assert.True(LocalArithmeticAnswer.TryCreate(prompt, out string answer));
        Assert.Equal(expected, answer);
    }

    [Fact]
    public void Division_by_zero_is_a_clear_result_not_a_false_success()
    {
        Assert.True(LocalArithmeticAnswer.TryCreate("calcola 1/0", out string answer));
        Assert.Equal("Impossibile dividere per zero.", answer);
    }

    [Theory]
    [InlineData("2026-08-29")]
    [InlineData("La versione 2+2 non funziona")]
    [InlineData("2^8")]
    [InlineData("123")]
    [InlineData("Quanto fa 47 volte 89? Ragiona passo passo")]
    [InlineData("(2+3")]
    public void Ambiguous_or_unsupported_text_falls_through_to_normal_routing(string prompt)
    {
        Assert.False(LocalArithmeticAnswer.TryCreate(prompt, out _));
    }
}
