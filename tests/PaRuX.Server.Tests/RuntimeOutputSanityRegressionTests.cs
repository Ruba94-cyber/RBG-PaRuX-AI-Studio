using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression cases from the batch review. An assigned emoji is a surrogate pair and is plausible text. A body that is valid JSON but
/// not a completion object is "no reply" and a refusal, never an exception that escapes the probe.
/// </summary>
public sealed class RuntimeOutputSanityRegressionTests
{
    [Theory]
    [InlineData("Sure 🙂 4")]
    [InlineData("4 😀")]
    public void A_reply_with_an_assigned_emoji_pair_is_plausible(string reply)
    {
        RuntimeOutputSanity.Verdict verdict = RuntimeOutputSanity.Evaluate(reply);

        Assert.True(verdict.Plausible, verdict.Reason);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public void A_repeated_character_run_is_refused_only_beyond_the_allowed_length(int repeats, bool plausible)
    {
        RuntimeOutputSanity.Verdict verdict = RuntimeOutputSanity.Evaluate(new string('a', repeats));

        Assert.Equal(plausible, verdict.Plausible);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"choices\":[\"not an object\"]}")]
    [InlineData("{\"choices\":[{\"message\":\"not an object\"}]}")]
    public void A_json_body_that_is_not_a_completion_object_yields_no_reply_instead_of_throwing(string json)
    {
        Assert.Null(RuntimeOutputSanity.ExtractReplyText(json));
    }

    [Fact]
    public async Task A_loopback_runtime_that_answers_with_a_json_array_is_refused_without_throwing()
    {
        using var responder = LoopbackChatResponder.Start(200, "[1,2,3]");
        using var http = new HttpClient();

        RuntimeOutputSanity.Verdict verdict = await RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, CancellationToken.None);

        Assert.False(verdict.Plausible);
    }
}
