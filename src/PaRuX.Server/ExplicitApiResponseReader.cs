using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Bounded response parsing for explicit OpenAI-compatible API calls.
/// Does not log provider content or secrets. Streaming is handled by the caller.
/// </summary>
public static class ExplicitApiResponseReader
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    internal sealed record Usage(long PromptTokens, long CompletionTokens);
    internal sealed record Completion(string Text, Usage? Usage);

    public static async Task<string> ReadTextAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Completion completion = await ReadCompletionAsync(response, cancellationToken).ConfigureAwait(false);
        return completion.Text;
    }

    internal static async Task<Completion> ReadCompletionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content is null)
            throw new InvalidDataException("Remote API response has no content.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Remote API response was unsuccessful.");

        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidDataException("Remote API response exceeds the allowed size.");

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(chunk.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("Remote API response exceeds the allowed size.");
            buffer.Write(chunk, 0, read);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Remote API response is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out _))
                throw new InvalidDataException("Remote API response reported a provider error.");
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                choices[0].ValueKind != JsonValueKind.Object ||
                !choices[0].TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Remote API response has no text completion.");

            Usage? usage = TryReadUsage(root, out long promptTokens, out long completionTokens)
                ? new Usage(promptTokens, completionTokens)
                : null;
            return new Completion(content.GetString() ?? string.Empty, usage);
        }
    }

    private static bool TryReadUsage(JsonElement root, out long promptTokens, out long completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("usage", out JsonElement usage) &&
               usage.ValueKind == JsonValueKind.Object &&
               usage.TryGetProperty("prompt_tokens", out JsonElement prompt) &&
               prompt.ValueKind == JsonValueKind.Number &&
               prompt.TryGetInt64(out promptTokens) &&
               promptTokens >= 0 &&
               usage.TryGetProperty("completion_tokens", out JsonElement completion) &&
               completion.ValueKind == JsonValueKind.Number &&
               completion.TryGetInt64(out completionTokens) &&
               completionTokens >= 0;
    }
}
