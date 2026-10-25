using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The Internet switch is authoritative for web reads: nothing starts while it is OFF, and turning it OFF stops a
/// request that is already in flight. The transports here are scripted, so no network is used.
/// </summary>
public sealed class WebSearchInternetSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private InternetSettingsService Internet(bool enabled)
    {
        var settings = new InternetSettingsService(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        settings.SetEnabled(enabled);
        return settings;
    }

    [Fact]
    public async Task A_fetch_is_refused_before_any_request_while_internet_is_off()
    {
        var handler = new CountingHandler();
        var web = new WebSearchService(Internet(enabled: false), handler);

        await Assert.ThrowsAsync<InternetAccessDisabledException>(() =>
            web.FetchPageTextAsync("https://example.test/page", CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_search_is_refused_before_any_request_while_internet_is_off()
    {
        var handler = new CountingHandler();
        var web = new WebSearchService(Internet(enabled: false), handler);

        await Assert.ThrowsAsync<InternetAccessDisabledException>(() =>
            web.SearchAsync("history of rome", 5, CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Turning_internet_off_stops_a_fetch_that_is_already_in_flight()
    {
        InternetSettingsService internet = Internet(enabled: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var web = new WebSearchService(internet, new BlockingHandler(started));

        Task<string> fetch = web.FetchPageTextAsync("https://example.test/slow", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        internet.SetEnabled(false);

        await Assert.ThrowsAsync<InternetAccessDisabledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A stop the user asks for is still a cancellation. It must not be reported as a switch change, because
    /// callers treat the switch case as "stop reading" and the cancellation case as "stop the whole run".</summary>
    [Fact]
    public async Task A_caller_cancellation_stays_a_cancellation_and_is_not_reported_as_a_switch_change()
    {
        InternetSettingsService internet = Internet(enabled: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var web = new WebSearchService(internet, new BlockingHandler(started));
        using var cancellation = new CancellationTokenSource();

        Task<string> fetch = web.FetchPageTextAsync("https://example.test/slow", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(internet.GetEnabled());
    }

    [Fact]
    public async Task Switching_internet_back_on_lets_new_reads_start_again()
    {
        InternetSettingsService internet = Internet(enabled: true);
        internet.SetEnabled(false);
        internet.SetEnabled(true);
        var handler = new CountingHandler();
        var web = new WebSearchService(internet, handler);

        string text = await web.FetchPageTextAsync("https://example.test/page", CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("Hello", text);
    }

    /// <summary>Returns a small HTML page and counts the requests it has been asked to send.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<p>Hello</p>", Encoding.UTF8, "text/html")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Signals that a request has started, then waits until something cancels it.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started;

        public BlockingHandler(TaskCompletionSource started) => _started = started;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
