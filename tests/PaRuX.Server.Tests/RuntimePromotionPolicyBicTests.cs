using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimePromotionPolicyBicTests
{
    [Fact]
    public void Faster_decode_cannot_hide_large_ttft_regression()
    {
        RuntimeCandidateEvidence baseline = Evidence("baseline", decode: 50, ttft: 200, load: 1000, power: 180);
        RuntimeCandidateEvidence candidate = Evidence("candidate", decode: 70, ttft: 280, load: 900, power: 185);

        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.Contains("TTFT", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Faster_decode_cannot_hide_large_load_regression()
    {
        RuntimeCandidateEvidence baseline = Evidence("baseline", decode: 50, ttft: 200, load: 1000, power: 180);
        RuntimeCandidateEvidence candidate = Evidence("candidate", decode: 70, ttft: 190, load: 1500, power: 185);

        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.Contains("load", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Faster_decode_cannot_hide_large_power_regression()
    {
        RuntimeCandidateEvidence baseline = Evidence("baseline", decode: 50, ttft: 200, load: 1000, power: 180);
        RuntimeCandidateEvidence candidate = Evidence("candidate", decode: 70, ttft: 190, load: 900, power: 245);

        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.Contains("power", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Balanced_real_improvement_can_promote()
    {
        RuntimeCandidateEvidence baseline = Evidence("baseline", decode: 50, ttft: 220, load: 1100, power: 190);
        RuntimeCandidateEvidence candidate = Evidence("candidate", decode: 58, ttft: 205, load: 1000, power: 185);

        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.True(decision.Promote);
        Assert.True(decision.ImprovementPercent >= 3);
    }

    [Fact]
    public void Missing_optional_telemetry_is_neutral_not_an_advantage()
    {
        RuntimeCandidateEvidence baseline = Evidence("baseline", decode: 50, ttft: 200, load: 0, power: 0, vram: 0, temp: 0);
        RuntimeCandidateEvidence candidate = Evidence("candidate", decode: 50, ttft: 200, load: 0, power: 0, vram: 0, temp: 0);

        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.InRange(decision.ImprovementPercent!.Value, -0.001, 0.001);
    }

    private static RuntimeCandidateEvidence Evidence(
        string id,
        double decode,
        double ttft,
        double load,
        double power,
        double vram = 12000,
        double temp = 70) => new(
            CandidateId: id,
            Comparable: true,
            QualityScore: 1,
            TimeToFirstTokenMilliseconds: ttft,
            PromptTokensPerSecond: 1000,
            DecodeTokensPerSecond: decode,
            SustainedDecodeTokensPerSecond: decode * 0.98,
            PeakVramMib: vram,
            MaxGpuTemperatureC: temp,
            ErrorRate: 0,
            LoadMilliseconds: load,
            AverageGpuPowerWatts: power);
}
