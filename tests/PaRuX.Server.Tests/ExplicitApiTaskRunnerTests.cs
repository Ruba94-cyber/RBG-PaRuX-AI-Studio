using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiTaskRunnerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"response\"}}]}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Accepts the request and then waits, so a test can withdraw the remote opt-in while the call is in flight.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static readonly Uri Endpoint = new("https://example.org/v1");

    private static JsonElement Messages() =>
        JsonSerializer.SerializeToElement(new[] { new { role = "user", content = "test" } });

    private static ExplicitApiTaskBinding Binding(JsonElement messages) =>
        Binding(messages, 0.000001m, 16);

    private static ExplicitApiTaskBinding Binding(JsonElement messages, decimal pricePerToken, int maxOutputTokens) =>
        ExplicitApiTaskBinding.Create(
            "openai", Endpoint, "key", "model", "task-1", ExplicitApiTaskBinding.PayloadSha256Of(messages),
            pricePerToken, maxOutputTokens);

    private static ExplicitApiConsent Consent() => new(
        UserSelectedRemoteForTask: true, ProviderAllowedForTask: true, DataDisclosureApproved: true);

    [Fact]
    public async Task AuthorizedTaskDispatchesExactlyOnce()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages()), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        Assert.Equal("response", (await runner.CompleteAsync(token, "openai", "task-1",
            Endpoint, "key", "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None)).Text);
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExcessCostFailsBeforeDispatchAndConsumesGrant()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages(), 1m, 1000), TimeSpan.FromMinutes(1), approvedBudget: 1m);
        // A 1000-token completion at price 1 is far above the 1m budget, so the request is refused before dispatch.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 1000, 1m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 1, 0m, 1m, Consent(), CancellationToken.None));
    }

    [Fact]
    public async Task NoGrantNeverDispatches()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(new ExplicitApiTaskGrants(),
            new ExplicitApiInferenceTransport(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            "unknown", "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnrecordedConsentIsRefusedAndLeavesTheGrantUsable()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages()), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        // Default consent grants nothing, and each flag is required on its own.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key", "model", Messages(),
            16, 0.000001m, 1m, default, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key", "model", Messages(),
            16, 0.000001m, 1m, new ExplicitApiConsent(true, true, false), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key", "model", Messages(),
            16, 0.000001m, 1m, new ExplicitApiConsent(false, true, true), CancellationToken.None));
        Assert.Equal(0, handler.Calls);

        // The refusals did not touch the grant, so recorded consent still authorizes the task.
        Assert.Equal("response", (await runner.CompleteAsync(token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None)).Text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingCompletionLimitOrPriceIsRefusedWithoutConsumingTheGrant()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages()), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        await Assert.ThrowsAsync<ArgumentException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key", "model", Messages(),
            null, 0.000001m, 1m, Consent(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key", "model", Messages(),
            16, null, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);

        Assert.Equal("response", (await runner.CompleteAsync(token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None)).Text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GrantForOneBasePathNeverDispatchesToAnotherPathOfTheSameHost()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages()), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", new Uri("https://example.org/v2"), "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GrantApprovedAtOnePriceIsNotUsableAtALowerPrice()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages(), 0.001m, 16), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.0001m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GrantApprovedForOneCompletionLimitIsNotUsableForAnother()
    {
        var grants = new ExplicitApiTaskGrants();
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client));
        string token = grants.Issue(Binding(Messages(), 0.001m, 16), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteAsync(
            token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 17, 0.001m, 1m, Consent(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WithdrawingTheRemoteOptInCancelsAnExplicitCallThatIsStillWaiting()
    {
        var grants = new ExplicitApiTaskGrants();
        var registry = new InFlightRequestRegistry();
        var handler = new StallingHandler();
        using var client = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(client), registry);
        string token = grants.Issue(Binding(Messages()), TimeSpan.FromMinutes(1), approvedBudget: 1m);

        Task<ExplicitApiTaskResult> call = runner.CompleteAsync(token, "openai", "task-1", Endpoint, "key",
            "model", Messages(), 16, 0.000001m, 1m, Consent(), CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, registry.Cancel(InFlightRequestRegistry.RemoteProviderScope));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.Calls);
    }
}
