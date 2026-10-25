using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The post-health output check. Evaluate is pure. The probe tests run against a real loopback responder, so the request,
/// the reply parsing and the refusal paths are exercised over HTTP rather than through a fake delegate.
/// </summary>
public sealed class RuntimeOutputSanityTests
{
    [Theory]
    [InlineData("4")]
    [InlineData("Four.")]
    [InlineData("The answer is 4.")]
    [InlineData("Quattro (4)")]
    public void A_plausible_reply_is_accepted(string reply)
    {
        RuntimeOutputSanity.Verdict verdict = RuntimeOutputSanity.Evaluate(reply);
        Assert.True(verdict.Plausible, verdict.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("!!!!!!!!!!!!")]
    [InlineData("��� go")]
    [InlineData("answer\u0001 is four")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(" four")]
    public void A_reply_that_is_empty_garbled_or_repetitive_is_refused(string? reply)
    {
        RuntimeOutputSanity.Verdict verdict = RuntimeOutputSanity.Evaluate(reply);
        Assert.False(verdict.Plausible);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void A_reasoning_reply_with_empty_content_is_read_from_reasoning_content()
    {
        const string json = "{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"Two plus two is four.\"}}]}";
        Assert.Equal("Two plus two is four.", RuntimeOutputSanity.ExtractReplyText(json));
    }

    [Fact]
    public void A_completion_without_choices_yields_no_reply()
    {
        Assert.Null(RuntimeOutputSanity.ExtractReplyText("{\"id\":\"x\"}"));
        Assert.Null(RuntimeOutputSanity.ExtractReplyText("{\"choices\":[]}"));
    }

    [Fact]
    public async Task A_loopback_runtime_that_answers_with_garbage_is_refused()
    {
        using var responder = LoopbackChatResponder.Start(200,
            "{\"choices\":[{\"message\":{\"content\":\"\\uFFFD\\uFFFD\\uFFFD!!!!!!!!\"}}]}");
        using var http = new HttpClient();

        RuntimeOutputSanity.Verdict verdict = await RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, CancellationToken.None);

        Assert.False(verdict.Plausible);
        Assert.Contains("replacement characters", verdict.Reason);
    }

    [Fact]
    public async Task A_loopback_runtime_that_answers_plausibly_is_accepted()
    {
        using var responder = LoopbackChatResponder.Start(200, "{\"choices\":[{\"message\":{\"content\":\"4\"}}]}");
        using var http = new HttpClient();

        RuntimeOutputSanity.Verdict verdict = await RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, CancellationToken.None);

        Assert.True(verdict.Plausible, verdict.Reason);
    }

    [Fact]
    public async Task A_runtime_that_refuses_the_probe_is_not_reported_sane()
    {
        using var responder = LoopbackChatResponder.Start(503, "{\"error\":\"loading model\"}");
        using var http = new HttpClient();

        RuntimeOutputSanity.Verdict verdict = await RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, CancellationToken.None);

        Assert.False(verdict.Plausible);
        Assert.Contains("503", verdict.Reason);
    }

    [Fact]
    public async Task A_runtime_that_is_not_listening_is_refused_without_throwing()
    {
        string endpoint = "http://127.0.0.1:" + LoopbackChatResponder.UnusedPort();
        using var http = new HttpClient();

        RuntimeOutputSanity.Verdict verdict = await RuntimeOutputSanity.ProbeAsync(endpoint, http, CancellationToken.None);

        Assert.False(verdict.Plausible);
        Assert.Contains("could not reach", verdict.Reason);
    }

    [Fact]
    public async Task Cancelling_the_caller_propagates_instead_of_being_read_as_a_refusal()
    {
        using var responder = LoopbackChatResponder.Start(200, "{\"choices\":[{\"message\":{\"content\":\"4\"}}]}");
        using var http = new HttpClient();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, cancelled.Token));
    }
}
