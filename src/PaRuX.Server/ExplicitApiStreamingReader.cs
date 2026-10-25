using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Bounded OpenAI-compatible SSE reader. Reads one line at a time into a fixed buffer, so an oversized line is
/// refused as soon as it exceeds <see cref="MaxEventBytes"/> rather than after it has been buffered whole.
/// The pending event is sized with a running counter. Never logs or buffers the entire prompt/completion.
/// Caller disposes HTTP response.
/// </summary>
public static class ExplicitApiStreamingReader
{
    public const int MaxEventBytes = 128 * 1024;
    public const int MaxTotalBytes = 8 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadDeltasAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content is null)
            throw new InvalidDataException("Remote API stream has no content.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Remote API response was unsuccessful.");

        if (response.Content.Headers.ContentLength is long declared && declared > MaxTotalBytes)
            throw new InvalidDataException("Remote API stream exceeds allowed size.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var lines = new BoundedLineReader(stream);
        var data = new StringBuilder();
        long pendingEventBytes = 0;
        long total = 0;
        while (true)
        {
            string? line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidDataException("Remote API stream ended before the completion marker.");
            }
            total += lines.LastLineBytes + 1;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("Remote API stream exceeds allowed size.");

            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                string payload = data.ToString();
                data.Clear();
                pendingEventBytes = 0;
                if (payload == "[DONE]")
                {
                    yield break;
                }
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    throw new InvalidDataException("Remote API stream contains invalid JSON.");
                }
                using (doc)
                {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out _))
                    throw new InvalidDataException("Remote API stream reported a provider error.");
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("choices", out JsonElement choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].ValueKind == JsonValueKind.Object &&
                    choices[0].TryGetProperty("delta", out JsonElement delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    string? text = content.GetString();
                    if (!string.IsNullOrEmpty(text)) yield return text;
                }
                }
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string value = line.Substring(5).TrimStart();
            // Track the pending event's size incrementally (value plus the joining newline) instead of
            // re-encoding everything buffered so far on every line.
            pendingEventBytes += Encoding.UTF8.GetByteCount(value) + (data.Length > 0 ? 1 : 0);
            if (pendingEventBytes > MaxEventBytes)
                throw new InvalidDataException("Remote API event exceeds allowed size.");
            if (data.Length > 0) data.Append('\n');
            data.Append(value);
        }
    }

    /// <summary>
    /// Reads LF, CR or CRLF terminated lines. Never holds more than <see cref="MaxEventBytes"/> of one line:
    /// the moment a line exceeds that bound it is refused, so a stream with no newline cannot grow memory.
    /// </summary>
    private sealed class BoundedLineReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly Stream _stream;
        private readonly byte[] _chunk = new byte[8192];
        private readonly byte[] _line = new byte[MaxEventBytes];
        private int _chunkOffset;
        private int _chunkLength;
        private int _lineLength;
        private bool _skipLineFeed;

        public BoundedLineReader(Stream stream) => _stream = stream;

        /// <summary>UTF-8 byte count of the line most recently returned, excluding its terminator.</summary>
        public int LastLineBytes { get; private set; }

        /// <summary>Returns the next line, or null at end of stream. A final unterminated line is still returned.</summary>
        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _lineLength = 0;
            bool sawContent = false;
            while (true)
            {
                if (_chunkOffset == _chunkLength)
                {
                    _chunkLength = await _stream.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _chunkOffset = 0;
                    if (_chunkLength == 0)
                        return sawContent ? Decode() : null;
                }

                byte value = _chunk[_chunkOffset++];
                if (_skipLineFeed)
                {
                    // The LF of a CRLF pair belongs to the terminator that already ended the previous line.
                    _skipLineFeed = false;
                    if (value == (byte)'\n') continue;
                }
                if (value == (byte)'\n') return Decode();
                if (value == (byte)'\r')
                {
                    _skipLineFeed = true;
                    return Decode();
                }
                if (_lineLength == _line.Length)
                    throw new InvalidDataException("Remote API stream exceeds allowed size.");
                _line[_lineLength++] = value;
                sawContent = true;
            }
        }

        private string Decode()
        {
            LastLineBytes = _lineLength;
            try
            {
                return StrictUtf8.GetString(_line, 0, _lineLength);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("Remote API stream contains invalid text.");
            }
        }
    }
}
