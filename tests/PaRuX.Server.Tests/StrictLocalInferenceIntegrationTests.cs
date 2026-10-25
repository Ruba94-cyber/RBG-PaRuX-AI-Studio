using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Exercises strict-local inference through the registered chat route. The runtime installation
/// and port are isolated, and the configured HTTPS fallback URL is never contacted.
/// </summary>
public sealed class StrictLocalInferenceIntegrationTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public StrictLocalInferenceIntegrationTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [WindowsFact("The managed-listener proof uses the Windows TCP owner table; it did not run on this host.")]
    public async Task Chat_route_keeps_inference_local_when_the_ready_runtime_fails_and_cloud_credentials_are_restored()
    {
        string isolatedRuntimeRoot = Path.Combine(Path.GetTempPath(), "parux-strict-local-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRuntimeRoot);
        using IDisposable runtimeRoot = LocalAiPaths.UseRootForTests(isolatedRuntimeRoot);
        int textPortValue = GetUnusedLoopbackPort();
        using IDisposable textPort = LocalAiPaths.UseTextPortForTests(textPortValue);
        await using var localRuntime = await LocalRuntimeStub.StartAsync(textPortValue);
        var testServerTextPorts = new ConcurrentQueue<int>();
        Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(textPortValue),
            "The isolated runtime fixture must be the sole listener before the route test starts. Port owners: "
            + string.Join(",", ManagedListenerOwnership.FindListenerOwners(textPortValue))
            + "; test PID: " + Environment.ProcessId);
        using var fallbackListener = new LoopbackConnectionObserver();

        using WebApplicationFactory<global::Program> appFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRoleRouter>();
                services.AddSingleton<IRoleRouter>(new FakeRoleRouter { Succeed = true });
                services.AddSingleton<IStartupFilter>(new TestServerTextPortCapture(port => testServerTextPorts.Enqueue(port)));
            }));
        LocalAiProcessManager runtimeProcess = appFactory.Services.GetRequiredService<LocalAiProcessManager>();
        bool fixtureHealthy = await WaitForTextHealthAsync(runtimeProcess, TimeSpan.FromSeconds(5));
        Assert.True(fixtureHealthy,
            "The isolated listener must pass the product's actual IsTextHealthyAsync check before the chat request. "
            + "Fixture port: " + textPortValue + "; owners: "
            + string.Join(",", ManagedListenerOwnership.FindListenerOwners(textPortValue))
            + "; test PID: " + Environment.ProcessId);
        using HttpClient client = appFactory.CreateClient();
        ServerState serverState = appFactory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", serverState.Token);

        try
        {
            using HttpResponseMessage configured = await client.PostAsJsonAsync(
                "/api/v1/ai/backends/cloud-fallback",
                new SetCloudFallbackRequest
                {
                    Enabled = true,
                    ProviderId = "custom",
                    BaseUrl = "https://127.0.0.1:" + fallbackListener.Port + "/v1",
                    Model = "restored-cloud-model",
                    ApiKey = "sk-test-only-never-send"
                });

            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
            CloudFallbackService restoredService = new();
            CloudFallbackStatus restored = restoredService.GetStatus();
            Assert.True(restored.Enabled);
            Assert.True(restored.HasApiKey);
            Assert.False(restored.Armed);
            Assert.Contains("local-only", restored.NotArmedReason, StringComparison.OrdinalIgnoreCase);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/chat/completions",
                new ChatCompletionRequest
                {
                    Model = "local-default",
                    Stream = true,
                    Messages = [new ChatCompletionMessage { Role = "user", Content = "private test prompt" }]
                });

            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                "The local route should stream its provider error instead of failing the strict-local request. "
                + "Status: " + (int)response.StatusCode + "; body: " + body
                + "; runtime port: " + textPortValue + "; owners: "
                + string.Join(",", ManagedListenerOwnership.FindListenerOwners(textPortValue)));
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("provider_error", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("simulated local inference failure", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cloud_fallback", body, StringComparison.OrdinalIgnoreCase);

            string localRequest = await localRuntime.RequestBodyReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(textPortValue, testServerTextPorts);
            Assert.Contains("private test prompt", localRequest, StringComparison.Ordinal);
            Assert.True(localRuntime.HealthRequestCount > 0,
                "The managed runtime must pass its HTTP /health check before receiving the chat prompt.");
            IReadOnlyList<string> localRequests = localRuntime.RequestBodies;
            Assert.NotEmpty(localRequests);
            Assert.Equal(localRuntime.RequestCount, localRequests.Count);
            Assert.All(localRequests, attempt =>
                Assert.Contains("private test prompt", attempt, StringComparison.Ordinal));
            fallbackListener.AssertNoConnections();

            CloudFallbackStatus afterRequest = new CloudFallbackService().GetStatus();
            Assert.Equal(0, afterRequest.UseCount);
            Assert.Null(afterRequest.LastUsedUtc);
            Assert.False(File.Exists(LocalAiPaths.TextPidFile));
        }
        finally
        {
            try
            {
                using HttpResponseMessage disabled = await client.DeleteAsync("/api/v1/ai/backends/cloud-fallback");
                Assert.True(disabled.IsSuccessStatusCode, "The isolated test credential must be removed after the route test.");
            }
            finally
            {
                try { Directory.Delete(isolatedRuntimeRoot, recursive: true); }
                catch (DirectoryNotFoundException) { }
            }
        }

        // Whole-lifetime check after cleanup: a connection the fallback endpoint received at any point
        // during this test fails it. Nothing here waits on a timer to decide that.
        fallbackListener.AssertNoConnections();
    }

    [Fact]
    public void Loopback_observer_reports_a_connection_that_arrives_after_the_operation_completed()
    {
        using var observer = new LoopbackConnectionObserver();
        observer.AssertNoConnections();

        using var probe = new TcpClient();
        probe.Connect(IPAddress.Loopback, observer.Port);
        // A positive, bounded wait for the OS to queue the probe, so the check below cannot race the handshake.
        Assert.True(observer.WaitForQueuedConnection(TimeSpan.FromSeconds(5)),
            "The probe connection was not queued on the observer's listener within 5 seconds.");

        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(observer.AssertNoConnections);
        Assert.Contains("received 1 connection", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(503, "ok")]
    [InlineData(200, "starting")]
    public async Task Text_health_rejects_unavailable_or_unready_response_from_an_owned_listener(
        int healthStatusCode, string healthStatus)
    {
        if (!OperatingSystem.IsWindows()) return;

        string isolatedRuntimeRoot = Path.Combine(Path.GetTempPath(), "parux-health-response-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRuntimeRoot);
        using IDisposable runtimeRoot = LocalAiPaths.UseRootForTests(isolatedRuntimeRoot);
        int port = GetUnusedLoopbackPort();
        using IDisposable textPort = LocalAiPaths.UseTextPortForTests(port);
        await using var runtime = await LocalRuntimeStub.StartAsync(port, healthStatusCode, healthStatus);
        try
        {
            Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(port),
                "The health-response fixture must be the current process's only listener owner.");

            var process = new LocalAiProcessManager();
            bool processOwned = await WaitForManagedOwnershipAsync(process, TimeSpan.FromSeconds(5));
            Assert.True(processOwned,
                "The native owner proof must accept the current-process listener before validating its health response.");
            Assert.False(await process.IsTextHealthyAsync(CancellationToken.None),
                "Health readiness must reject an unsuccessful HTTP response or a non-ready status string.");
            Assert.Equal(1, runtime.HealthRequestCount);
        }
        finally
        {
            try { Directory.Delete(isolatedRuntimeRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Text_health_fails_closed_without_listener_and_after_owned_listener_is_lost()
    {
        if (!OperatingSystem.IsWindows()) return;

        string isolatedRuntimeRoot = Path.Combine(Path.GetTempPath(), "parux-health-lost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRuntimeRoot);
        using IDisposable runtimeRoot = LocalAiPaths.UseRootForTests(isolatedRuntimeRoot);
        int port = GetUnusedLoopbackPort();
        using IDisposable textPort = LocalAiPaths.UseTextPortForTests(port);
        try
        {
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            var process = new LocalAiProcessManager();
            Assert.False(await process.IsTextHealthyAsync(CancellationToken.None),
                "No listener must never be reported healthy.");

            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            var runtime = await LocalRuntimeStub.StartAsync(port);
            bool owned = await WaitForManagedOwnershipAsync(process, TimeSpan.FromSeconds(5));
            Assert.True(owned);
            Assert.True(await process.IsTextHealthyAsync(CancellationToken.None));

            // Ownership is now cached warm; losing the listener must still fail closed via /health.
            await runtime.DisposeAsync();
            Assert.False(await process.IsTextHealthyAsync(CancellationToken.None),
                "A warm ownership cache must not keep a vanished listener healthy.");
        }
        finally
        {
            LocalAiProcessManager.ResetTextOwnershipCacheForTests();
            try { Directory.Delete(isolatedRuntimeRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static int GetUnusedLoopbackPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return port;
    }

    private static async Task<bool> WaitForTextHealthAsync(LocalAiProcessManager process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (await process.IsTextHealthyAsync(CancellationToken.None)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (true);
    }

    private static async Task<bool> WaitForManagedOwnershipAsync(LocalAiProcessManager process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (process.IsManagedTextRuntimeOwned()) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (true);
    }

    private sealed class LocalRuntimeStub : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _requestBodies = new();
        private int _requestCount;
        private int _healthRequestCount;

        private LocalRuntimeStub(WebApplication app)
        {
            _app = app;
            RequestBodyReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<string> RequestBodyReceived { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int HealthRequestCount => Volatile.Read(ref _healthRequestCount);
        public IReadOnlyList<string> RequestBodies => _requestBodies.ToArray();

        public static async Task<LocalRuntimeStub> StartAsync(int port, int healthStatusCode = 200, string healthStatus = "ok")
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:" + port);
            WebApplication app = builder.Build();
            var stub = new LocalRuntimeStub(app);
            app.MapGet("/health", async (HttpContext context) =>
            {
                Interlocked.Increment(ref stub._healthRequestCount);
                context.Response.StatusCode = healthStatusCode;
                await context.Response.WriteAsJsonAsync(new { status = healthStatus });
            });
            app.MapGet("/props", () => Results.Json(new { default_generation_settings = new { n_ctx = 8192 } }));
            app.MapGet("/slots", () => Results.Json(Array.Empty<object>()));
            app.MapGet("/v1/models", () => Results.Json(new { data = new[] { new { id = "local-default" } } }));
            app.MapPost("/v1/chat/completions", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                string body = await reader.ReadToEndAsync(context.RequestAborted);
                Interlocked.Increment(ref stub._requestCount);
                stub._requestBodies.Enqueue(body);
                stub.RequestBodyReceived.TrySetResult(body);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new { error = new { message = "simulated local inference failure" } },
                    context.RequestAborted);
            });
            await app.StartAsync();
            return stub;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class LoopbackConnectionObserver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _connectionCount;

        public LoopbackConnectionObserver()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        /// <summary>
        /// Positive, bounded wait until the OS has queued a connection. Only the self-test uses it, so that its
        /// check does not race the handshake of the probe it has just made.
        /// </summary>
        public bool WaitForQueuedConnection(TimeSpan timeout) =>
            _listener.Server.Poll((int)timeout.TotalMilliseconds * 1000, SelectMode.SelectRead);

        /// <summary>
        /// Synchronous on purpose: callers invoke it after the operation's own completion signal, so there is no
        /// timer to wait out. It accepts and counts every connection the OS has already queued, on the calling
        /// thread, before it asserts. Nothing runs in the background, so a connection cannot be dequeued without
        /// being counted. A connection is accepted and closed here, so it is not held open for the peer.
        /// </summary>
        public void AssertNoConnections()
        {
            while (_listener.Pending())
            {
                using TcpClient accepted = _listener.AcceptTcpClient();
                _connectionCount++;
            }

            int observed = _connectionCount;
            Assert.True(observed == 0,
                "The configured fallback endpoint received " + observed + " connection(s); strict-local inference must never contact it.");
        }

        public void Dispose() => _listener.Stop();
    }
}
