using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Turns PaRuX's OpenAI-style SSE stream into Ollama NDJSON without buffering the completion.
/// Visible content is forwarded immediately. Reasoning is forwarded only when the Ollama caller
/// explicitly requested think=true; otherwise it remains hidden. Terminal finish reason and real
/// OpenAI usage counts are retained when the provider supplied them. Durations are deliberately not
/// fabricated because the OpenAI compatibility response does not guarantee equivalent telemetry.
/// </summary>
internal sealed class OllamaStreamTranslator : Stream
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Stream _destination;
    private readonly string _model;
    private readonly string _createdAt;
    private readonly bool _generate;
    private readonly bool _includeThinking;
    private readonly StringBuilder _pending = new();
    private string? _doneReason;
    private int? _promptEvalCount;
    private int? _evalCount;

    internal OllamaStreamTranslator(Stream destination, string model, bool generate, bool includeThinking = false)
    {
        _destination = destination;
        _model = model;
        _generate = generate;
        _includeThinking = includeThinking;
        _createdAt = DateTime.UtcNow.ToString("o");
    }

    /// <summary>True once at least one visible answer token reached the Ollama client.</summary>
    internal bool WroteAnyContent { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _destination.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _pending.Append(Encoding.UTF8.GetString(buffer.Span));

        while (true)
        {
            string text = _pending.ToString();
            int newline = text.IndexOf('\n');
            if (newline < 0) break;

            string line = text[..newline].TrimEnd('\r');
            _pending.Clear();
            _pending.Append(text[(newline + 1)..]);

            StreamEvent? parsed = ExtractEvent(line);
            if (parsed == null) continue;
            if (!string.IsNullOrWhiteSpace(parsed.DoneReason)) _doneReason = parsed.DoneReason;
            if (parsed.PromptTokens.HasValue) _promptEvalCount = parsed.PromptTokens;
            if (parsed.CompletionTokens.HasValue) _evalCount = parsed.CompletionTokens;

            if (parsed.Content is { Length: > 0 })
            {
                WroteAnyContent = true;
                await WriteFrameAsync(parsed.Content, thinking: null, done: false, cancellationToken).ConfigureAwait(false);
            }
            if (_includeThinking && parsed.Thinking is { Length: > 0 })
            {
                WroteAnyContent = true;
                await WriteFrameAsync(string.Empty, parsed.Thinking, done: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    internal Task WriteDoneAsync(CancellationToken cancellationToken) =>
        WriteFrameAsync(string.Empty, thinking: null, done: true, cancellationToken);

    internal async Task WriteErrorAsync(string errorMessage, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["created_at"] = _createdAt,
            ["done"] = true,
            ["done_reason"] = "error",
            ["error"] = errorMessage
        };
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions) + "\n");
        await _destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(
        string content, string? thinking, bool done, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["created_at"] = _createdAt,
            ["done"] = done
        };

        if (_generate)
        {
            payload["response"] = content;
            if (_includeThinking && !string.IsNullOrEmpty(thinking)) payload["thinking"] = thinking;
        }
        else
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
            if (_includeThinking && !string.IsNullOrEmpty(thinking)) message["thinking"] = thinking;
            payload["message"] = message;
        }

        if (done)
        {
            if (!string.IsNullOrWhiteSpace(_doneReason)) payload["done_reason"] = _doneReason;
            if (_promptEvalCount.HasValue) payload["prompt_eval_count"] = _promptEvalCount.Value;
            if (_evalCount.HasValue) payload["eval_count"] = _evalCount.Value;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions) + "\n");
        await _destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Backward-compatible test seam: returns visible content from one OpenAI SSE frame. Thinking,
    /// usage-only frames, terminators and keep-alives return null.
    /// </summary>
    internal static string? ExtractDelta(string line) => ExtractEvent(line)?.Content;

    internal static StreamEvent? ExtractEvent(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        string data = line[5..].Trim();
        if (data.Length == 0 || data == "[DONE]") return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out int p))
                    promptTokens = p;
                if (usage.TryGetProperty("completion_tokens", out JsonElement completion) && completion.TryGetInt32(out int c))
                    completionTokens = c;
            }

            string? content = null;
            string? thinking = null;
            string? doneReason = null;
            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out JsonElement finish)
                    && finish.ValueKind == JsonValueKind.String)
                    doneReason = finish.GetString();

                if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out JsonElement contentElement)
                        && contentElement.ValueKind == JsonValueKind.String)
                        content = contentElement.GetString();

                    foreach (string key in new[] { "reasoning_content", "reasoning", "thinking" })
                    {
                        if (delta.TryGetProperty(key, out JsonElement reasoningElement)
                            && reasoningElement.ValueKind == JsonValueKind.String)
                        {
                            thinking = reasoningElement.GetString();
                            if (!string.IsNullOrEmpty(thinking)) break;
                        }
                    }
                }
            }

            if (content == null && thinking == null && doneReason == null
                && !promptTokens.HasValue && !completionTokens.HasValue)
                return null;

            return new StreamEvent(content, thinking, doneReason, promptTokens, completionTokens);
        }
        catch (JsonException) { return null; }
    }

    internal sealed record StreamEvent(
        string? Content,
        string? Thinking,
        string? DoneReason,
        int? PromptTokens,
        int? CompletionTokens);
}
