using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalClockAnswerTests
{
    private static readonly DateTimeOffset Sample = new(2026, 8, 29, 3, 24, 17, TimeSpan.FromHours(2));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Europe/Rome-test", TimeSpan.FromHours(2), "Europe/Rome test", "Europe/Rome test");

    [Theory]
    [InlineData("che ore sono?")]
    [InlineData("Che ora è adesso?")]
    [InlineData("what time is it?")]
    public void LocalTimeIntent_ReturnsExactMachineTime(string prompt)
    {
        Assert.True(LocalClockAnswer.TryCreate(prompt, Sample, Zone, out string answer));
        Assert.Contains("03:24:17", answer);
        Assert.Contains("Europe/Rome test", answer);
    }

    [Theory]
    [InlineData("che data è oggi?")]
    [InlineData("what date is it?")]
    public void LocalDateIntent_ReturnsExactMachineDate(string prompt)
    {
        Assert.True(LocalClockAnswer.TryCreate(prompt, Sample, Zone, out string answer));
        Assert.Contains("2026", answer);
        Assert.Contains("29", answer);
    }

    [Theory]
    [InlineData("che ore sono a Tokyo?")]
    [InlineData("what time is it in New York?")]
    [InlineData("spiegami come funziona un fuso orario")]
    [InlineData("parliamo del tempo atmosferico")]
    public void AmbiguousOrRemoteRequests_FallThrough(string prompt)
    {
        Assert.False(LocalClockAnswer.TryCreate(prompt, Sample, Zone, out _));
    }
}
