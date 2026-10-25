using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiUsageCostTests
{
    private const string ProviderId = "openai-compatible";
    private const string TaskId = "task-usage-1";
    private const string Model = "fixture-model";
    private const string ApiKey = "test-key-material";
    private static readonly Uri Endpoint = new("https://api.example.org/v1");

    private sealed class CannedResponseHandler(string responseJson) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record TaskFixture(
        ExplicitApiTaskGrants Grants,
        ExplicitApiTaskRunner Runner,
        CannedResponseHandler Handler,
        HttpClient Client,
        JsonElement Messages,
        string GrantToken,
        decimal PricePerToken,
        int MaxOutputTokens,
        decimal ApprovedBudget);

    private static TaskFixture CreateTask(
        string responseJson, decimal pricePerToken, int maxOutputTokens, decimal approvedBudget = 100m)
    {
        JsonElement messages = JsonSerializer.SerializeToElement(
            new[] { new { role = "user", content = "fixture prompt" } });
        var grants = new ExplicitApiTaskGrants();
        string grantToken = grants.Issue(
            ExplicitApiTaskBinding.Create(
                ProviderId, Endpoint, ApiKey, Model, TaskId,
                ExplicitApiTaskBinding.PayloadSha256Of(messages), pricePerToken, maxOutputTokens),
            TimeSpan.FromMinutes(1), approvedBudget);
        var handler = new CannedResponseHandler(responseJson);
        var client = new HttpClient(handler);
        return new TaskFixture(
            grants, new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client)),
            handler, client, messages, grantToken, pricePerToken, maxOutputTokens, approvedBudget);
    }

    private static ExplicitApiConsent Consent() => new(
        UserSelectedRemoteForTask: true, ProviderAllowedForTask: true, DataDisclosureApproved: true);

    private static Task<ExplicitApiTaskResult> CompleteAsync(TaskFixture fixture) =>
        fixture.Runner.CompleteAsync(
            fixture.GrantToken, ProviderId, TaskId, Endpoint, ApiKey, Model,
            fixture.Messages, fixture.MaxOutputTokens, fixture.PricePerToken,
            fixture.ApprovedBudget, Consent(), CancellationToken.None);

    [Fact]
    public async Task ReportedPromptAndCompletionTokensProduceDecimalActualCost()
    {
        const decimal pricePerToken = 0.00000017m;
        var fixture = CreateTask(
            """{"choices":[{"message":{"content":"done"}}],"usage":{"prompt_tokens":1234,"completion_tokens":5678}}""",
            pricePerToken, maxOutputTokens: 20_000);
        using var client = fixture.Client;

        ExplicitApiTaskResult result = await CompleteAsync(fixture);

        Assert.Equal("done", result.Text);
        Assert.Equal("usage_reported", result.AuditRecord.UsageStatus);
        Assert.Equal(1234L, result.AuditRecord.PromptTokens);
        Assert.Equal(5678L, result.AuditRecord.CompletionTokens);
        Assert.Equal(0.00117504m, result.AuditRecord.ActualCost);
        Assert.Empty(result.AuditRecord.Flags);
    }

    [Fact]
    public async Task MissingUsageReturnsUsageUnreportedAndTheWorstCaseEstimate()
    {
        const decimal pricePerToken = 0.01m;
        const int maxOutputTokens = 128;
        var fixture = CreateTask(
            """{"choices":[{"message":{"content":"done"}}]}""",
            pricePerToken, maxOutputTokens);
        using var client = fixture.Client;
        decimal expectedEstimate =
            (Encoding.UTF8.GetByteCount(fixture.Messages.GetRawText()) + maxOutputTokens) * pricePerToken;

        ExplicitApiTaskResult result = await CompleteAsync(fixture);

        Assert.Equal("usage_unreported", result.AuditRecord.UsageStatus);
        Assert.Null(result.AuditRecord.ActualCost);
        Assert.Equal(expectedEstimate, result.AuditRecord.WorstCaseEstimate);
        Assert.NotEqual(0m, result.AuditRecord.WorstCaseEstimate);
    }

    [Fact]
    public async Task ActualCostAboveEstimateIsFlaggedWithoutAutomaticRetry()
    {
        var fixture = CreateTask(
            """{"choices":[{"message":{"content":"done"}}],"usage":{"prompt_tokens":500,"completion_tokens":1000}}""",
            pricePerToken: 0.01m, maxOutputTokens: 1);
        using var client = fixture.Client;

        ExplicitApiTaskResult result = await CompleteAsync(fixture);

        Assert.True(result.AuditRecord.ActualCost.HasValue);
        Assert.True(result.AuditRecord.ActualCost.Value > result.AuditRecord.WorstCaseEstimate);
        Assert.Contains(ExplicitApiTaskAuditRecord.CostExceededEstimate, result.AuditRecord.Flags);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task AuditKeepsProviderHostWithoutApiKeyAndGrantRemainsSingleUse()
    {
        var fixture = CreateTask(
            """{"choices":[{"message":{"content":"done"}}],"usage":{"prompt_tokens":12,"completion_tokens":8}}""",
            pricePerToken: 0.001m, maxOutputTokens: 128);
        using var client = fixture.Client;

        ExplicitApiTaskResult result = await CompleteAsync(fixture);
        string serializedAudit = JsonSerializer.Serialize(result.AuditRecord);

        Assert.Equal("api.example.org", result.AuditRecord.ProviderHost);
        Assert.DoesNotContain(ApiKey, serializedAudit, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(fixture));
        Assert.Equal(1, fixture.Handler.Calls);
    }
}
