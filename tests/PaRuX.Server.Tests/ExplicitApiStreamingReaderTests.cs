using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiStreamingReaderTests
{
    /// <summary>A body that the test generates on demand, so large or hostile streams never exist in memory.</summary>
    private abstract class ScriptedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span));
        public abstract override int Read(Span<byte> buffer);
    }

    /// <summary>Produces <see cref="_length"/> copies of one byte and nothing else: a line that never ends.</summary>
    private sealed class RepeatingByteStream : ScriptedStream
    {
        private readonly long _length;
        private readonly byte _value;
        private long _produced;

        public RepeatingByteStream(long length, byte value)
        {
            _length = length;
            _value = value;
        }

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, _length - _produced);
            buffer[..count].Fill(_value);
            _produced += count;
            return count;
        }
    }

    /// <summary>Returns at most one byte per read, so every terminator can straddle a read boundary.</summary>
    private sealed class TrickleStream : ScriptedStream
    {
        private readonly byte[] _bytes;
        private int _position;

        public TrickleStream(string text) => _bytes = Encoding.UTF8.GetBytes(text);

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _bytes.Length || buffer.IsEmpty) return 0;
            buffer[0] = _bytes[_position++];
            return 1;
        }
    }

    private static HttpResponseMessage Response(string s) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(s, Encoding.UTF8, "text/event-stream")
    };

    [Fact]
    public async Task ReadsDeltasUntilDone()
    {
        using var response = Response(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            ": keepalive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: [DONE]\n\n");
        var chunks = new List<string>();
        await foreach (string chunk in ExplicitApiStreamingReader.ReadDeltasAsync(
            response, CancellationToken.None))
            chunks.Add(chunk);
        Assert.Equal(new[] { "Hello", " world" }, chunks);
    }

    [Fact]
    public async Task DeclaredOversizedStreamIsRejectedBeforeReading()
    {
        using var response = Response("data: [DONE]\n\n");
        response.Content.Headers.ContentLength = ExplicitApiStreamingReader.MaxTotalBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task MissingStreamBodyIsRejected()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task RejectsOversizedEvent()
    {
        using var response = Response("data: " +
            new string('a', ExplicitApiStreamingReader.MaxEventBytes + 1) + "\n\n");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task OversizedLineIsRefusedWithoutBufferingIt()
    {
        // 10 MB with no newline. A reader that buffers the line first would allocate at least that much.
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new RepeatingByteStream(10 * 1024 * 1024, (byte)'a'))
        };
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"Allocated {allocated} bytes while refusing one oversized line.");
    }

    [Fact]
    public async Task EventSizeIsCountedAcrossLinesNotPerLine()
    {
        // Each line is under the per-line bound, but the three together exceed the per-event bound.
        string line = "data: " + new string('a', 60 * 1024);
        using var response = Response(line + "\n" + line + "\n" + line + "\n\n");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task CarriageReturnLineEndingsSplitAcrossReadsAreParsed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TrickleStream(
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n"))
        };
        var chunks = new List<string>();
        await foreach (string chunk in ExplicitApiStreamingReader.ReadDeltasAsync(
            response, CancellationToken.None))
            chunks.Add(chunk);
        Assert.Equal(new[] { "hello" }, chunks);
    }

    [Fact]
    public async Task RejectsUnsuccessfulResponse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task InvalidEventJsonDoesNotLeakProviderPayload()
    {
        const string sensitive = "private-prompt-data";
        using var response = Response("data: {not-json " + sensitive + "\n\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
        Assert.DoesNotContain(sensitive, error.Message);
    }

    [Fact]
    public async Task ProviderErrorEventFailsWithoutLeakingErrorDetails()
    {
        const string secret = "private-provider-error";
        using var response = Response(
            "data: {\"error\":{\"message\":\"" + secret + "\"}}\n\n" +
            "data: [DONE]\n\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
        Assert.DoesNotContain(secret, error.Message);
    }

    [Fact]
    public async Task MissingDoneMarkerIsRejected()
    {
        using var response = Response(
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (string _ in ExplicitApiStreamingReader.ReadDeltasAsync(
                response, CancellationToken.None)) { }
        });
    }

}
