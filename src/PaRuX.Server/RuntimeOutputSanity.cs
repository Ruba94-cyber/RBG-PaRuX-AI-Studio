using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// The post-health output check. llama-server reports <c>/health</c> as soon as its weights are loaded, and a build or
/// quantization that computes garbage still answers "ok". A runtime is therefore accepted only after one fixed, tiny
/// question on the loopback endpoint gets a plausible text reply. <see cref="Evaluate"/> is pure; <see cref="ProbeAsync"/>
/// is the only network step and takes its HttpClient, so a test can point it at a loopback listener.
/// Limitation: the check is structural (text-like, not repetitive, not empty). It cannot judge whether the answer is correct.
/// </summary>
internal static class RuntimeOutputSanity
{
    /// <summary>The fixed question. Short, answerable by any chat model, and its reply is never shown to the user.</summary>
    internal const string Question = "What is two plus two? Reply with the digit only.";

    internal const int MaxReplyTokens = 64;
    internal const int MaxReplyCharacters = 2000;
    internal const int MaxRepeatedCharacterRun = 12;

    /// <summary>Upper bound for the whole probe, so a runtime that accepts the request and never answers cannot stall a launch.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The outcome of one sanity check. <see cref="Reason"/> is a short clause for the launch message.</summary>
    internal readonly record struct Verdict(bool Plausible, string Reason);

    /// <summary>Classifies a reply as plausible text. Null, empty and whitespace-only replies are not plausible.</summary>
    internal static Verdict Evaluate(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return new Verdict(false, "the reply was empty");
        if (reply.Length > MaxReplyCharacters) return new Verdict(false, "the reply was longer than " + MaxReplyCharacters + " characters");

        int letterOrDigit = 0;
        int unusable = 0;
        int run = 0;
        int longestRun = 0;
        char previous = '\0';
        for (int i = 0; i < reply.Length; i++)
        {
            char c = reply[i];
            if (c == '�') return new Verdict(false, "the reply contained replacement characters");
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                return new Verdict(false, "the reply contained control characters");

            if (char.IsLetterOrDigit(c)) letterOrDigit++;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            // A surrogate that is half of a valid pair is one assigned character (an emoji, for example), not an unusable one.
            if (category is UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse
                || (category == UnicodeCategory.Surrogate && !IsPairedSurrogate(reply, i)))
                unusable++;

            if (c == previous) run++;
            else { run = 1; previous = c; }
            longestRun = Math.Max(longestRun, run);
        }

        if (letterOrDigit == 0) return new Verdict(false, "the reply had no letters or digits");
        if (unusable * 10 > reply.Length) return new Verdict(false, "most of the reply was unassigned or private-use characters");
        if (longestRun > MaxRepeatedCharacterRun)
            return new Verdict(false, "the reply repeated one character " + longestRun + " times in a row");
        return new Verdict(true, "the reply was plausible text");
    }

    /// <summary>
    /// Extracts the reply from an OpenAI-compatible chat completion. When a reasoning build ends its token budget inside
    /// the reasoning block, the answer is absent from <c>content</c>, so <c>reasoning_content</c> is used as the text
    /// instead of failing a healthy runtime for a budget it was never given. Returns null when no reply field exists.
    /// </summary>
    internal static string? ExtractReplyText(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        // Valid JSON that is not a completion object (an array, a bare string) is "no reply", never an exception.
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        JsonElement first = choices[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (first.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
        {
            string? content = StringProperty(message, "content");
            return !string.IsNullOrWhiteSpace(content) ? content : StringProperty(message, "reasoning_content");
        }
        return StringProperty(first, "text");
    }

    /// <summary>
    /// Sends the fixed question to <paramref name="endpoint"/> (a loopback base URL) and classifies the reply.
    /// Every failure is a refusal, never an exception: a runtime that answers with an error, a body that is not
    /// JSON, or a timeout all mean the launch is not accepted. Cancellation of <paramref name="ct"/> itself propagates.
    /// </summary>
    internal static async Task<Verdict> ProbeAsync(string endpoint, HttpClient http, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(http);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ProbeTimeout);

        string body = JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = Question } },
            max_tokens = MaxReplyTokens,
            temperature = 0,
            stream = false,
            chat_template_kwargs = new { enable_thinking = false }
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/v1/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage response = await http.SendAsync(request, bounded.Token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Verdict(false, "the probe returned HTTP " + (int)response.StatusCode);

            string? reply;
            try { reply = ExtractReplyText(json); }
            catch (JsonException) { return new Verdict(false, "the probe reply was not JSON"); }
            return reply is null ? new Verdict(false, "the probe reply had no message") : Evaluate(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Verdict(false, "the probe timed out after " + (int)ProbeTimeout.TotalSeconds + " seconds");
        }
        catch (HttpRequestException ex)
        {
            return new Verdict(false, "the probe could not reach the runtime (" + ex.Message + ")");
        }
    }

    /// <summary>True when the UTF-16 surrogate at <paramref name="index"/> is half of a valid pair.</summary>
    private static bool IsPairedSurrogate(string text, int index)
    {
        if (char.IsHighSurrogate(text[index])) return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
        return index > 0 && char.IsHighSurrogate(text[index - 1]);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
