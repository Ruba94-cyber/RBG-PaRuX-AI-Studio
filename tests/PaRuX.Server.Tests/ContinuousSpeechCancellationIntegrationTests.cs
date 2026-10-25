using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// TestServer contract coverage for request cancellation and clean stream follow-up. The worker is
/// synthetic and in-process: this verifies HTTP request-abort propagation and endpoint framing, not
/// the production Python process tree, local model inference, playback, or voice quality.
/// </summary>
public sealed class ContinuousSpeechCancellationIntegrationTests : IAsyncLifetime
{
    private readonly CancellationFixtureWorker _worker = new();
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiSecurityTests.PaRuXFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContinuousSpeechStatusProvider>();
                services.AddSingleton<IContinuousSpeechStatusProvider>(new InstalledSpeechStatus());
                services.RemoveAll<IContinuousSpeechStreamWorker>();
                services.AddSingleton<IContinuousSpeechStreamWorker>(_worker);
            }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_http_stream_reaches_worker_and_followup_starts_cleanly()
    {
        using var sibling = new CancellationTokenSource();
        Task siblingTask = Task.Delay(Timeout.InfiniteTimeSpan, sibling.Token);
        using var firstCts = new CancellationTokenSource();
        using HttpRequestMessage firstRequest = CreateRequest();
        using HttpResponseMessage firstResponse = await _client.SendAsync(
            firstRequest, HttpCompletionOption.ResponseHeadersRead, firstCts.Token);
        firstResponse.EnsureSuccessStatusCode();
        string firstId = firstResponse.Headers.GetValues("X-PaRuX-Request-Id").Single();
        await using Stream firstStream = await firstResponse.Content.ReadAsStreamAsync();
        using var firstReader = new StreamReader(firstStream);

        SpeechStreamFrame firstStart = await ReadFrameAsync(firstReader);
        AssertFrame(firstStart, firstId, "start", 0);
        SpeechStreamFrame firstAudio = await ReadFrameAsync(firstReader);
        AssertFrame(firstAudio, firstId, "audio", 1);
        Assert.True(_worker.FirstAudioObserved.Task.IsCompleted);

        firstCts.Cancel();
        firstResponse.Dispose(); // A streamed response has already returned; dispose its body to abort the HTTP request.
        await _worker.FirstRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(siblingTask.IsCanceled, "Cancelling speech must not cancel an unrelated sibling operation.");

        using HttpRequestMessage followupRequest = CreateRequest("synthetic follow-up");
        using HttpResponseMessage followupResponse = await _client.SendAsync(
            followupRequest, HttpCompletionOption.ResponseHeadersRead);
        followupResponse.EnsureSuccessStatusCode();
        string followupId = followupResponse.Headers.GetValues("X-PaRuX-Request-Id").Single();
        Assert.NotEqual(firstId, followupId);
        await using Stream followupStream = await followupResponse.Content.ReadAsStreamAsync();
        using var followupReader = new StreamReader(followupStream);
        SpeechStreamFrame[] followupFrames = await ReadToEndAsync(followupReader);

        Assert.Collection(followupFrames,
            frame => AssertFrame(frame, followupId, "start", 0),
            frame => AssertFrame(frame, followupId, "audio", 1),
            frame => AssertFrame(frame, followupId, "done", 2));
        Assert.DoesNotContain(followupFrames, frame => frame.RequestId == firstId);
        Assert.Equal(new[] { 0, 1 }, _worker.SequencesFor(firstId));
        Assert.Equal(new[] { 0, 1, 2 }, _worker.SequencesFor(followupId));
    }

    private static HttpRequestMessage CreateRequest(string text = "synthetic cancellation test") => new(
        HttpMethod.Post,
        "/api/v2/speech/synthesize/stream")
    {
        Content = JsonContent.Create(new SynthesizeRequest { Text = text, Voice = "af_heart" })
    };

    private static async Task<SpeechStreamFrame> ReadFrameAsync(StreamReader reader)
    {
        string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))
            ?? throw new InvalidDataException("Speech stream ended before expected frame.");
        return JsonSerializer.Deserialize<SpeechStreamFrame>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Speech stream contained an empty frame.");
    }

    private static async Task<SpeechStreamFrame[]> ReadToEndAsync(StreamReader reader)
    {
        var frames = new List<SpeechStreamFrame>();
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
            frames.Add(JsonSerializer.Deserialize<SpeechStreamFrame>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Speech stream contained an empty frame."));
        return frames.ToArray();
    }

    private static void AssertFrame(SpeechStreamFrame frame, string requestId, string type, int sequence)
    {
        Assert.Equal(requestId, frame.RequestId);
        Assert.Equal(type, frame.Type);
        Assert.Equal(sequence, frame.Sequence);
    }

    private sealed class InstalledSpeechStatus : IContinuousSpeechStatusProvider
    {
        public Task<SpeechEngineStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SpeechEngineStatus { TtsInstalled = true });
    }

    private sealed class CancellationFixtureWorker : IContinuousSpeechStreamWorker
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _sequences = new();
        private int _calls;
        public TaskCompletionSource FirstAudioObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StreamAsync(SynthesizeRequest request, string requestId,
            Func<SpeechStreamFrame, CancellationToken, Task> onFrame, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            _sequences.GetOrAdd(requestId, _ => new ConcurrentQueue<int>()).Enqueue(0); // endpoint start frame
            await Emit("audio", 1, cancellationToken).ConfigureAwait(false);
            if (call == 1)
            {
                FirstAudioObserved.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    FirstRequestCancelled.TrySetResult();
                    throw;
                }
            }
            await Emit("done", 2, cancellationToken).ConfigureAwait(false);

            Task Emit(string type, int sequence, CancellationToken ct)
            {
                _sequences.GetOrAdd(requestId, _ => new ConcurrentQueue<int>()).Enqueue(sequence);
                return onFrame(new SpeechStreamFrame
                {
                    Type = type,
                    RequestId = requestId,
                    Sequence = sequence,
                    SampleRate = SpeechStreamingProtocol.SampleRate,
                    Channels = SpeechStreamingProtocol.Channels,
                    Format = SpeechStreamingProtocol.Format,
                    Base64Data = type == "audio" ? Convert.ToBase64String(new byte[] { 0x01, 0x00 }) : string.Empty,
                    Samples = type == "audio" ? 1 : 1,
                    DurationSeconds = type == "done" ? 1.0 / SpeechStreamingProtocol.SampleRate : 0
                }, ct);
            }
        }

        public int[] SequencesFor(string id) => _sequences.TryGetValue(id, out ConcurrentQueue<int>? values)
            ? values.ToArray()
            : Array.Empty<int>();
    }
}
