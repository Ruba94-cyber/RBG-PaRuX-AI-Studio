using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PaRuX.UITests.Infrastructure;

internal sealed class BootstrapAuthStubServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpListener? _redirectListener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serveTask;
    private readonly Task? _redirectServeTask;
    private readonly bool _acceptCredentials;
    private readonly bool _redirectBootstrapProbe;
    private readonly bool _redirectConversationPush;
    private readonly bool _explicitDiscoveryFixture;
    private readonly bool _speechCancellationFixture;
    private readonly int _explicitDiscoveryStatus;
    private readonly int _explicitDiscoveryDelayMs;
    private int _failDiscoveryCatalog;
    private int _explicitDiscoveryPostCount;
    private int _discoveryCatalogGetCount;
    private int _explicitDiscoveryRefreshed;
    private string[] _explicitDiscoveryRepositoryIds = Array.Empty<string>();
    private int _redirectedRequestCount;
    private int _conversationPushRequestCount;
    private int _identityRequestCount;
    private string? _identityToken;
    private string? _redirectedToken;
    private int _speechCallCount;
    private int _speechArtifactCreateCount;
    private int _speechArtifactStatusCount;
    private int _speechFirstCancelled;
    private int _speechFollowupCompleted;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<int>> _speechSequences = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _speechRequestIds = new();
    private readonly TaskCompletionSource _speechFirstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _speechFirstCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _speechFollowupDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string Endpoint { get; }
    internal int RedirectedRequestCount => Volatile.Read(ref _redirectedRequestCount);
    internal int ConversationPushRequestCount => Volatile.Read(ref _conversationPushRequestCount);
    internal int IdentityRequestCount => Volatile.Read(ref _identityRequestCount);
    internal string? IdentityToken => Volatile.Read(ref _identityToken);
    internal string? RedirectedToken => Volatile.Read(ref _redirectedToken);
    internal int ExplicitDiscoveryPostCount => Volatile.Read(ref _explicitDiscoveryPostCount);
    internal string[] ExplicitDiscoveryRepositoryIds => Volatile.Read(ref _explicitDiscoveryRepositoryIds);
    internal int DiscoveryCatalogGetCount => Volatile.Read(ref _discoveryCatalogGetCount);
    internal Task SpeechFirstAudio => _speechFirstAudio.Task;
    internal Task SpeechFirstCancellation => _speechFirstCancellation.Task;
    internal Task SpeechFollowupDone => _speechFollowupDone.Task;
    internal int SpeechArtifactCreateCount => Volatile.Read(ref _speechArtifactCreateCount);
    internal int SpeechArtifactStatusCount => Volatile.Read(ref _speechArtifactStatusCount);
    internal bool SpeechFirstWasCancelled => Volatile.Read(ref _speechFirstCancelled) == 1;
    internal bool SpeechFollowupWasCompleted => Volatile.Read(ref _speechFollowupCompleted) == 1;
    internal string[] SpeechRequestIds => _speechRequestIds.ToArray();
    internal int[] SpeechSequences(string requestId) => _speechSequences.TryGetValue(requestId, out var values)
        ? values.ToArray() : Array.Empty<int>();
    /// <summary>Makes later catalog GETs fail (HTTP 500) so tests can observe stale-catalog handling.</summary>
    internal void SetDiscoveryCatalogFailure(bool fail) => Volatile.Write(ref _failDiscoveryCatalog, fail ? 1 : 0);

    internal BootstrapAuthStubServer(bool acceptCredentials = false, bool redirectBootstrapProbe = false,
        bool redirectConversationPush = false, bool explicitDiscoveryFixture = false,
        int explicitDiscoveryStatus = 200, int explicitDiscoveryDelayMs = 0,
        bool speechCancellationFixture = false)
    {
        _explicitDiscoveryDelayMs = Math.Clamp(explicitDiscoveryDelayMs, 0, 15000);
        _acceptCredentials = acceptCredentials;
        _redirectBootstrapProbe = redirectBootstrapProbe;
        _redirectConversationPush = redirectConversationPush;
        _explicitDiscoveryFixture = explicitDiscoveryFixture;
        _speechCancellationFixture = speechCancellationFixture;
        _explicitDiscoveryStatus = explicitDiscoveryStatus;
        _listener.Start();
        var address = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = "http://127.0.0.1:" + address.Port;
        if (redirectBootstrapProbe || redirectConversationPush)
        {
            _redirectListener = new TcpListener(IPAddress.Loopback, 0);
            _redirectListener.Start();
            var redirectAddress = (IPEndPoint)_redirectListener.LocalEndpoint;
            RedirectEndpoint = "http://127.0.0.1:" + redirectAddress.Port;
            _redirectServeTask = ServeAsync(_redirectListener, redirectTarget: true);
        }
        _serveTask = ServeAsync(_listener, redirectTarget: false);
    }

    internal string? RedirectEndpoint { get; }

    private async Task ServeAsync(TcpListener listener, bool redirectTarget)
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cancellation.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = RespondAsync(client, _acceptCredentials, redirectTarget, RedirectEndpoint);
        }
    }

    private async Task RespondAsync(TcpClient client, bool acceptCredentials, bool redirectTarget, string? redirectEndpoint)
    {
        using (client)
        await using (NetworkStream stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        {
            string? requestLine = await reader.ReadLineAsync();
            string? token = null;
            int contentLength = 0;
            bool chunked = false;
            string? headerLine;
            while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
            {
                if (headerLine.StartsWith("X-PaRuX-Token:", StringComparison.OrdinalIgnoreCase))
                    token = headerLine[(headerLine.IndexOf(':') + 1)..].Trim();
                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(headerLine[(headerLine.IndexOf(':') + 1)..].Trim(), out contentLength);
                if (headerLine.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                    && headerLine.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
            }

            bool identity = requestLine?.Contains(" /api/v1/system/data-root ", StringComparison.Ordinal) == true;
            if (!redirectTarget && identity)
            {
                Interlocked.Increment(ref _identityRequestCount);
                Volatile.Write(ref _identityToken, token);
            }

            if (redirectTarget)
            {
                Interlocked.Increment(ref _redirectedRequestCount);
                Volatile.Write(ref _redirectedToken, token);
            }

            bool conversationPush = requestLine?.StartsWith("PUT /api/v1/conversations/", StringComparison.Ordinal) == true;
            if (!redirectTarget && conversationPush)
                Interlocked.Increment(ref _conversationPushRequestCount);

            bool shouldRedirect = (identity && _redirectBootstrapProbe)
                || (conversationPush && _redirectConversationPush);
            if (!redirectTarget && shouldRedirect && redirectEndpoint != null)
            {
                byte[] redirectHeader = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\n"
                    + "Location: " + redirectEndpoint + "/capture\r\n"
                    + "Content-Length: 0\r\n"
                    + "Connection: close\r\n\r\n");
                await stream.WriteAsync(redirectHeader);
                return;
            }

            if (!redirectTarget && _speechCancellationFixture)
            {
                if (identity && token == "stale-test-token")
                {
                    await WriteJsonAsync(stream, 200, "{}");
                    return;
                }
                if (token == "stale-test-token" && requestLine?.StartsWith("POST /api/v2/speech/synthesize/stream ", StringComparison.Ordinal) == true)
                {
                    _ = await ReadBodyAsync(reader, contentLength, chunked);
                    int call = Interlocked.Increment(ref _speechCallCount);
                    string id = "synthetic-speech-" + call.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _speechRequestIds.Enqueue(id);
                    await StreamSpeechAsync(stream, reader, id, call);
                    return;
                }
                if (token == "stale-test-token"
                    && requestLine?.StartsWith("POST /api/v1/artifacts ", StringComparison.Ordinal) == true)
                {
                    _ = await ReadBodyAsync(reader, contentLength, chunked);
                    int artifact = Interlocked.Increment(ref _speechArtifactCreateCount);
                    await WriteJsonAsync(stream, 200, JsonSerializer.Serialize(new
                    {
                        id = "synthetic-artifact-" + artifact.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        artifactType = "audio", title = "speech.wav", status = "draft",
                        currentVersionNumber = 1, createdUtc = DateTime.UtcNow, updatedUtc = DateTime.UtcNow
                    }));
                    return;
                }
                if (token == "stale-test-token"
                    && requestLine?.StartsWith("POST /api/v1/artifacts/synthetic-artifact-1/status ", StringComparison.Ordinal) == true)
                {
                    _ = await ReadBodyAsync(reader, contentLength, chunked);
                    Interlocked.Increment(ref _speechArtifactStatusCount);
                    await WriteJsonAsync(stream, 204, string.Empty);
                    return;
                }
                if (token == "stale-test-token" && requestLine?.StartsWith("GET ", StringComparison.Ordinal) == true)
                {
                    await WriteJsonAsync(stream, 200, "[]");
                    return;
                }
                await WriteJsonAsync(stream, token == "stale-test-token" ? 404 : 401,
                    token == "stale-test-token" ? "{}" : "{\"error\":\"Unauthorized\"}");
                return;
            }

            if (_explicitDiscoveryFixture && !redirectTarget && token == "stale-test-token")
            {
                string path = requestLine?.Split(' ').Skip(1).FirstOrDefault() ?? string.Empty;
                if (requestLine?.StartsWith("POST ", StringComparison.Ordinal) == true
                    && path == "/api/v1/models/discovery/refresh-explicit")
                {
                    string bodyText = await ReadBodyAsync(reader, contentLength, chunked);
                    try
                    {
                        using JsonDocument parsed = JsonDocument.Parse(bodyText);
                        JsonElement root = parsed.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                            || !root.TryGetProperty("repositoryIds", out JsonElement ids)
                            || ids.ValueKind != JsonValueKind.Array)
                            throw new JsonException("Unexpected explicit request shape");
                        string[] safeIds = ids.EnumerateArray().Select(id => id.GetString() ?? string.Empty).ToArray();
                        Volatile.Write(ref _explicitDiscoveryRepositoryIds, safeIds);
                        Interlocked.Increment(ref _explicitDiscoveryPostCount);
                        if (_explicitDiscoveryDelayMs > 0)
                            await Task.Delay(_explicitDiscoveryDelayMs, _cancellation.Token);
                        if (_explicitDiscoveryStatus is not (200 or 429 or 503))
                        {
                            // An unexpected Core status carries no typed outcome and changes nothing.
                            await WriteJsonAsync(stream, _explicitDiscoveryStatus, "{\"error\":\"fixture\"}");
                            return;
                        }
                        string outcome = _explicitDiscoveryStatus switch
                        { 429 => "cooldown", 503 => "failed", _ => "refreshed" };
                        // Only a confirmed refresh changes the frozen catalog, so a later GET proves
                        // the desktop refetched instead of re-rendering its earlier cards.
                        if (outcome == "refreshed") Volatile.Write(ref _explicitDiscoveryRefreshed, 1);
                        await WriteJsonAsync(stream, _explicitDiscoveryStatus,
                            JsonSerializer.Serialize(new
                            {
                                outcome, candidateCount = outcome == "refreshed" ? 62 : 61,
                                errorCode = outcome == "failed" ? "fixture_failure" : null
                            }));
                    }
                    catch (JsonException) { await WriteJsonAsync(stream, 400, "{}"); }
                    return;
                }
                if (requestLine?.StartsWith("GET ", StringComparison.Ordinal) == true
                    && path == "/api/v1/models/discovery")
                {
                    Interlocked.Increment(ref _discoveryCatalogGetCount);
                    if (Volatile.Read(ref _failDiscoveryCatalog) == 1)
                    {
                        await WriteJsonAsync(stream, 500, "{\"error\":\"fixture catalog failure\"}");
                        return;
                    }
                    await WriteJsonAsync(stream, 200,
                        DiscoveryCatalogFixture(Volatile.Read(ref _explicitDiscoveryRefreshed) == 1));
                    return;
                }
                if (requestLine?.StartsWith("GET ", StringComparison.Ordinal) == true)
                {
                    await WriteJsonAsync(stream, 200, "[]");
                    return;
                }
            }

            int status = identity && acceptCredentials ? 200 : 401;
            string reason = status == 200 ? "OK" : "Unauthorized";
            byte[] body = Encoding.UTF8.GetBytes(status == 200 ? "{}" : "{\"error\":\"Unauthorized\"}");
            byte[] responseHeader = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + " " + reason + "\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader);
            await stream.WriteAsync(body);
        }
    }

    private async Task StreamSpeechAsync(NetworkStream stream, StreamReader reader, string requestId, int call)
    {
        byte[] responseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/x-ndjson; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"
            + "X-PaRuX-Request-Id: " + requestId + "\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, _cancellation.Token);
        await WriteSpeechFrameAsync(stream, requestId, "start", 0, string.Empty, 0, _cancellation.Token);
        await WriteSpeechFrameAsync(stream, requestId, "audio", 1,
            Convert.ToBase64String(call == 1 ? new byte[65536] : new byte[] { 0x01, 0x00, 0x02, 0x00 }), call == 1 ? 32768 : 2, _cancellation.Token);
        if (call == 1)
        {
            _speechFirstAudio.TrySetResult();
            try
            {
                char[] probe = new char[1];
                _ = await reader.ReadAsync(probe, 0, 1).WaitAsync(_cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The app disposed the cancelled streaming response; no terminal frame is sent.
            }
            Volatile.Write(ref _speechFirstCancelled, 1);
            _speechFirstCancellation.TrySetResult();
            return;
        }

        await WriteSpeechFrameAsync(stream, requestId, "done", 2, string.Empty, 2, _cancellation.Token);
        Volatile.Write(ref _speechFollowupCompleted, 1);
        _speechFollowupDone.TrySetResult();
    }

    private async Task WriteSpeechFrameAsync(NetworkStream stream, string requestId, string type,
        int sequence, string base64, int samples, CancellationToken ct)
    {
        _speechSequences.GetOrAdd(requestId, _ => new System.Collections.Concurrent.ConcurrentQueue<int>()).Enqueue(sequence);
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type, requestId, sequence, sampleRate = 24000, channels = 1,
            format = "pcm_s16le", base64Data = base64, samples,
            durationSeconds = type == "done" ? samples / 24000.0 : 0
        }) + "\n");
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> ReadBodyAsync(StreamReader reader, int contentLength, bool chunked)
    {
        var body = new StringBuilder();
        if (chunked)
        {
            while (true)
            {
                string? sizeLine = await reader.ReadLineAsync();
                if (!int.TryParse(sizeLine?.Split(';')[0], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int size) || size < 0 || size > 1024)
                    return string.Empty;
                if (size == 0) break;
                await AppendExactAsync(reader, body, size);
                await reader.ReadLineAsync();
                if (body.Length > 1024) return string.Empty;
            }

            // The zero-size chunk is followed by zero or more trailers and a final empty line.
            // Consume that framing before the streaming fixture probes the request stream for
            // client disposal; otherwise the leftover CRLF looks like an immediate disconnect.
            int trailerBytes = 0;
            bool trailersTerminated = false;
            for (int trailerCount = 0; trailerCount < 64; trailerCount++)
            {
                int maximumLineCharacters = 8192 - trailerBytes - 2;
                if (maximumLineCharacters < 0)
                    throw new InvalidDataException("Chunked request trailers exceeded the fixture limit.");
                string trailer = await ReadBoundedLineAsync(reader, maximumLineCharacters);
                trailerBytes += trailer.Length + 2;
                if (trailer.Length == 0)
                {
                    trailersTerminated = true;
                    break;
                }
                if (trailerCount == 63 || trailerBytes > 8192)
                    throw new InvalidDataException("Chunked request trailers exceeded the fixture limit.");
            }
            if (!trailersTerminated)
                throw new InvalidDataException("Chunked request trailers were not terminated.");
        }
        else if (contentLength is > 0 and <= 1024)
            await AppendExactAsync(reader, body, contentLength);
        return body.ToString();
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int maximumCharacters)
    {
        var line = new StringBuilder(Math.Min(maximumCharacters, 256));
        char[] next = new char[1];
        bool pendingCarriageReturn = false;
        while (true)
        {
            int read = await reader.ReadAsync(next, 0, 1);
            if (read == 0) throw new InvalidDataException("Chunked request ended inside its trailer section.");
            if (next[0] == '\n') return line.ToString();
            if (pendingCarriageReturn)
            {
                if (line.Length >= maximumCharacters)
                    throw new InvalidDataException("A chunked request trailer line exceeded the fixture limit.");
                line.Append('\r');
                pendingCarriageReturn = false;
            }
            if (next[0] == '\r')
            {
                pendingCarriageReturn = true;
                continue;
            }
            if (line.Length >= maximumCharacters)
                throw new InvalidDataException("A chunked request trailer line exceeded the fixture limit.");
            line.Append(next[0]);
        }
    }

    private static async Task AppendExactAsync(StreamReader reader, StringBuilder body, int length)
    {
        char[] buffer = new char[Math.Min(length, 1024)];
        int remaining = length;
        while (remaining > 0)
        {
            int read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            body.Append(buffer, 0, read);
            remaining -= read;
        }
    }

    private static Task WriteJsonAsync(NetworkStream stream, int status, string json)
    {
        string reason = status switch
        { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 404 => "Not Found", 500 => "Internal Server Error", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Error" };
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (status == 204)
        {
            byte[] noContentHeader = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n"
                + "Connection: close\r\n\r\n");
            return WriteResponseAsync(stream, noContentHeader, Array.Empty<byte>());
        }
        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\n"
            + "Content-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        return WriteResponseAsync(stream, header, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, byte[] header, byte[] body)
    {
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    private static string DiscoveryCatalogFixture(bool includeExplicitResult)
    {
        DateTime now = DateTime.UtcNow;
        string revision = new('a', 40);
        // Index 61 sorts after every frozen row and exists only after a confirmed explicit refresh.
        var candidates = Enumerable.Range(0, includeExplicitResult ? 62 : 61).Select(index =>
        {
            string repo = index == 61 ? "parux-ui-test/explicit-result"
                : "parux-ui-test/catalog-" + index.ToString("D3");
            string file = index == 61 ? "weights/explicit-result.gguf"
                : "weights/model-" + index.ToString("D3") + ".gguf";
            return new
            {
                key = index.ToString("x64"), repoId = repo, revision, repoFilePath = file,
                pinnedUrl = "https://huggingface.co/" + repo + "/resolve/" + revision + "/" + file,
                lifecycle = "Candidate", disposition = "active", integrityStatus = "unverified",
                firstSeenUtc = now, lastSeenUtc = now, noticePending = false
            };
        });
        return JsonSerializer.Serialize(new { schemaVersion = 1, candidates, lastSuccessUtc = now });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        _redirectListener?.Stop();
        try { _serveTask.GetAwaiter().GetResult(); } catch { }
        try { _redirectServeTask?.GetAwaiter().GetResult(); } catch { }
        _cancellation.Dispose();
    }
}
