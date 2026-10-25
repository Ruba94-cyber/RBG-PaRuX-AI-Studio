using PaRuX;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateEvaluationStatusTextTests
{
    [Fact]
    public void FormatsFirstVisibleTokenDecodeAndCompletionLatency()
    {
        string text = CandidateEvaluationStatusText.FormatMetrics(new CandidateEvaluationJobStatus
        {
            TimeToFirstTokenMilliseconds = 48.25,
            DecodeTokensPerSecond = 83.26,
            CompletionMilliseconds = 514.6
        });

        Assert.Equal(" · TTFT 48 ms · measured decode 83.3 tok/s · completion 515 ms", text);
    }

    [Fact]
    public void HidesUnavailableOrInvalidLatencyMeasurements()
    {
        string text = CandidateEvaluationStatusText.FormatMetrics(new CandidateEvaluationJobStatus
        {
            TimeToFirstTokenMilliseconds = double.NaN,
            DecodeTokensPerSecond = 0,
            CompletionMilliseconds = double.PositiveInfinity
        });

        Assert.Equal(string.Empty, text);
    }
}
