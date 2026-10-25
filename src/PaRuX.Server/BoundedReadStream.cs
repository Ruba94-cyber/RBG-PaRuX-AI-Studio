namespace PaRuX.Server;

/// <summary>Limits bytes read from a stream and probes one byte past the limit before reporting EOF.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private long _bytesRead;
    private bool _disposed;

    internal BoundedReadStream(Stream inner, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The inner stream must be readable.", nameof(inner));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _inner = inner;
        _maximumBytes = maximumBytes;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range.");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;
        long remaining = _maximumBytes - _bytesRead;
        if (remaining == 0)
        {
            Span<byte> probe = stackalloc byte[1];
            if (_inner.Read(probe) != 0) throw TooLarge();
            return 0;
        }
        int requested = (int)Math.Min(buffer.Length, remaining);
        int read = _inner.Read(buffer[..requested]);
        _bytesRead += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;
        long remaining = _maximumBytes - _bytesRead;
        if (remaining == 0)
        {
            byte[] probe = new byte[1];
            if (await _inner.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
                throw TooLarge();
            return 0;
        }
        int requested = (int)Math.Min(buffer.Length, remaining);
        int read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        _bytesRead += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BoundedReadStream));
    }

    private static InvalidDataException TooLarge() => new("Local performance stream exceeded its byte limit.");
}
