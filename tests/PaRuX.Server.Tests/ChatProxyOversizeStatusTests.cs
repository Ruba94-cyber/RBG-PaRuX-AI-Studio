using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatProxyOversizeStatusTests
{
    private const int MaximumPromptTokens = 100;

    private static ConversationWindowPlanner.WindowPlan OversizedPlan()
    {
        var turns = new[]
        {
            new ConversationWindowPlanner.Turn("user", new string('x', 10_000))
        };

        return ConversationWindowPlanner.Fit(
            turns,
            contextTokens: 150,
            reservedForReplyTokens: 50,
            countTokens: text => text.Length);
    }

    [Fact]
    public async Task Exact_overflow_after_compaction_maps_to_413_with_code_and_remediation()
    {
        var turns = new[]
        {
            new ConversationWindowPlanner.Turn("user", new string('a', 80)),
            new ConversationWindowPlanner.Turn("assistant", new string('b', 80)),
            new ConversationWindowPlanner.Turn("user", "Current request.")
        };
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns,
            contextTokens: 150,
            reservedForReplyTokens: 50,
            countTokens: text => text.Length);
        Assert.True(plan.Compacted);
        Assert.True(plan.EstimatedTokens <= MaximumPromptTokens);
        int exactRenderedTokens = MaximumPromptTokens + 1;
        PromptExceedsContextException failure = Assert.IsType<PromptExceedsContextException>(
            ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
                plan,
                fixedPromptTokens: 0,
                maximumRenderedPromptTokens: MaximumPromptTokens,
                exactRenderedPromptTokens: exactRenderedTokens,
                pricedOverBudget: false,
                tightened: false));

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response, failure, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        JsonElement error = response.RootElement.GetProperty("error");
        Assert.Equal("prompt_exceeds_context", error.GetProperty("code").GetString());
        Assert.Contains("shorten", error.GetProperty("remediation").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("split", error.GetProperty("remediation").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("larger context", error.GetProperty("remediation").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Estimated_overflow_after_compaction_maps_to_413_with_code_and_remediation()
    {
        ConversationWindowPlanner.WindowPlan plan = OversizedPlan();
        Assert.True(plan.Compacted);
        Assert.True(plan.EstimatedTokens > MaximumPromptTokens);
        PromptExceedsContextException failure = Assert.IsType<PromptExceedsContextException>(
            ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
                plan,
                fixedPromptTokens: 0,
                maximumRenderedPromptTokens: MaximumPromptTokens,
                exactRenderedPromptTokens: null,
                pricedOverBudget: false,
                tightened: false));

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response, failure, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        JsonElement error = response.RootElement.GetProperty("error");
        Assert.Equal("prompt_exceeds_context", error.GetProperty("code").GetString());
        Assert.Contains("estimated size", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("split", error.GetProperty("remediation").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Irreducible_exact_overflow_after_compaction_maps_to_413()
    {
        ConversationWindowPlanner.WindowPlan plan = OversizedPlan();
        Assert.True(plan.Compacted);
        PromptExceedsContextException failure = Assert.Throws<PromptExceedsContextException>(() =>
            ChatProxyFailureResponse.TightenExactContextOrThrowTypedFailure(
                currentMessageContextTokens: 150,
                exactRenderedPromptTokens: 1_000,
                maximumRenderedPromptTokens: MaximumPromptTokens,
                reservedForReplyTokens: 50,
                plan,
                fixedPromptTokens: 0,
                pricedOverBudget: false));

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response, failure, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        Assert.Equal("prompt_exceeds_context", response.RootElement.GetProperty("error")
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task Fixed_prompt_definitions_that_exhaust_the_window_map_to_413()
    {
        PromptExceedsContextException failure = Assert.Throws<PromptExceedsContextException>(() =>
            ChatProxyFailureResponse.GetEffectiveMessageContextTokensOrThrowTypedFailure(
                physicalContextTokens: 150,
                fixedPromptTokens: 101,
                reservedForReplyTokens: 50));

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response, failure, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        JsonElement error = response.RootElement.GetProperty("error");
        Assert.Equal("prompt_exceeds_context", error.GetProperty("code").GetString());
        Assert.Contains("larger context", error.GetProperty("remediation").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_unavailable_remains_503_with_a_retry_hint()
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response,
            new InvalidOperationException("The local runtime is unavailable."),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers["Retry-After"].ToString());
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        Assert.Contains("retry", response.RootElement.GetProperty("retry_hint").GetString(), StringComparison.OrdinalIgnoreCase);

        DefaultHttpContext startupFailure = new();
        startupFailure.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteRuntimeUnavailableAsync(
            startupFailure.Response,
            "The configured local runtime could not be started.",
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, startupFailure.Response.StatusCode);
        Assert.Equal("5", startupFailure.Response.Headers["Retry-After"].ToString());
        using JsonDocument startupResponse = await ReadResponseAsync(startupFailure.Response.Body);
        Assert.Contains("could not be started", startupResponse.RootElement
            .GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Within_window_prompt_stays_unchanged_and_is_not_classified_as_oversize()
    {
        var turns = new[]
        {
            new ConversationWindowPlanner.Turn("system", "Be concise."),
            new ConversationWindowPlanner.Turn("user", "Hello.")
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns,
            contextTokens: 512,
            reservedForReplyTokens: 64,
            countTokens: text => text.Length);
        PromptExceedsContextException? failure = ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
            plan,
            fixedPromptTokens: 0,
            maximumRenderedPromptTokens: plan.AvailableTokens,
            exactRenderedPromptTokens: null,
            pricedOverBudget: false,
            tightened: false);

        Assert.False(plan.Compacted);
        Assert.Null(failure);
        Assert.Equal(turns.Select(turn => (turn.Role, turn.Content)),
            plan.Messages.Select(turn => (turn.Role, turn.Content)));
    }

    [Fact]
    public void Exact_count_equal_to_the_maximum_fits_without_a_failure()
    {
        Assert.Null(ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
            WithinWindowPlan(),
            fixedPromptTokens: 0,
            maximumRenderedPromptTokens: MaximumPromptTokens,
            exactRenderedPromptTokens: MaximumPromptTokens,
            pricedOverBudget: false,
            tightened: false));
    }

    [Fact]
    public void Priced_over_budget_without_an_exact_count_is_an_oversize_failure()
    {
        PromptExceedsContextException failure = Assert.IsType<PromptExceedsContextException>(
            ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
                WithinWindowPlan(),
                fixedPromptTokens: 0,
                maximumRenderedPromptTokens: MaximumPromptTokens,
                exactRenderedPromptTokens: null,
                pricedOverBudget: true,
                tightened: false));

        Assert.Contains("could not be verified", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tightened_priced_over_budget_estimate_within_the_window_is_not_a_failure()
    {
        Assert.Null(ChatProxyFailureResponse.CreatePromptExceedsContextFailure(
            WithinWindowPlan(),
            fixedPromptTokens: 0,
            maximumRenderedPromptTokens: MaximumPromptTokens,
            exactRenderedPromptTokens: null,
            pricedOverBudget: true,
            tightened: true));
    }

    [Fact]
    public async Task Tool_loop_growth_past_the_budget_maps_to_413()
    {
        PromptExceedsContextException failure = Assert.IsType<PromptExceedsContextException>(
            ChatProxyFailureResponse.CreateToolLoopGrowthFailure(
                exactRenderedPromptTokens: MaximumPromptTokens + 1,
                maximumRenderedPromptTokens: MaximumPromptTokens));

        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(
            context.Response, failure, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        using JsonDocument response = await ReadResponseAsync(context.Response.Body);
        Assert.Equal("prompt_exceeds_context", response.RootElement.GetProperty("error")
            .GetProperty("code").GetString());
    }

    [Fact]
    public void Tool_loop_counts_within_the_budget_are_not_failures()
    {
        Assert.Null(ChatProxyFailureResponse.CreateToolLoopGrowthFailure(null, MaximumPromptTokens));
        Assert.Null(ChatProxyFailureResponse.CreateToolLoopGrowthFailure(MaximumPromptTokens, MaximumPromptTokens));
    }

    [Fact]
    public void Chat_planner_routes_its_budget_failures_through_the_typed_helpers()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "PaRuX.Server", "ChatProxyService.cs"));

        Assert.Contains("ChatProxyFailureResponse.GetEffectiveMessageContextTokensOrThrowTypedFailure(", source, StringComparison.Ordinal);
        Assert.Contains("ChatProxyFailureResponse.TightenExactContextOrThrowTypedFailure(", source, StringComparison.Ordinal);
        Assert.Contains("ChatProxyFailureResponse.CreatePromptExceedsContextFailure(", source, StringComparison.Ordinal);
        Assert.Contains("ChatProxyFailureResponse.CreateToolLoopGrowthFailure(", source, StringComparison.Ordinal);
    }

    private static ConversationWindowPlanner.WindowPlan WithinWindowPlan() =>
        ConversationWindowPlanner.Fit(
            new[]
            {
                new ConversationWindowPlanner.Turn("system", "Be concise."),
                new ConversationWindowPlanner.Turn("user", "Hello.")
            },
            contextTokens: 512,
            reservedForReplyTokens: 64,
            countTokens: text => text.Length);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }

    private static async Task<JsonDocument> ReadResponseAsync(Stream body)
    {
        body.Position = 0;
        return await JsonDocument.ParseAsync(body);
    }
}
