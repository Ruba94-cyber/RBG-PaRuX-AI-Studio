using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal static class SpeechStreamingProtocol
{
    internal const string ApiVersion = "1";
    internal const int SampleRate = 24_000;
    internal const int Channels = 1;
    internal const string Format = "pcm_s16le";
    internal const int MaximumTextCharacters = 100_000;

    internal static string NormalizeVoice(SynthesizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidDataException("text is required.");
        if (request.Text.Length > MaximumTextCharacters)
            throw new InvalidDataException($"text must be at most {MaximumTextCharacters:N0} characters for one streaming turn.");

        string voice = SpeechVoiceSelector.Resolve(request.Voice, request.Text);
        if (voice.Length > 80 || voice.Any(char.IsControl)
            || voice.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
            throw new InvalidDataException("voice contains unsupported characters.");
        return voice;
    }

    internal static SpeechStreamFrame ParseWorkerFrame(
        string json,
        string expectedRequestId,
        int expectedSequence)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Continuous speech worker closed its response stream unexpectedly.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string id = root.TryGetProperty("id", out JsonElement idElement)
            ? idElement.GetString() ?? string.Empty : string.Empty;
        if (!string.Equals(id, expectedRequestId, StringComparison.Ordinal))
            throw new InvalidDataException("Continuous speech worker returned a frame for the wrong request id.");

        string type = root.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString() ?? string.Empty : string.Empty;
        if (type is not ("audio" or "done" or "error"))
            throw new InvalidDataException("Continuous speech worker returned unsupported frame type '" + type + "'.");

        int sequence = root.TryGetProperty("sequence", out JsonElement sequenceElement)
            && sequenceElement.TryGetInt32(out int parsedSequence) ? parsedSequence : -1;
        if (sequence != expectedSequence)
            throw new InvalidDataException(
                $"Continuous speech worker sequence mismatch: expected {expectedSequence}, received {sequence}.");

        var frame = new SpeechStreamFrame
        {
            Type = type,
            RequestId = id,
            Sequence = sequence,
            SampleRate = SampleRate,
            Channels = Channels,
            Format = Format,
            Base64Data = root.TryGetProperty("base64", out JsonElement audioElement)
                ? audioElement.GetString() ?? string.Empty : string.Empty,
            Samples = root.TryGetProperty("samples", out JsonElement samplesElement)
                && samplesElement.TryGetInt32(out int samples) ? samples : 0,
            TtfaMilliseconds = root.TryGetProperty("ttfaMilliseconds", out JsonElement ttfaElement)
                && ttfaElement.TryGetInt64(out long ttfa) ? ttfa : 0,
            DurationSeconds = root.TryGetProperty("durationSeconds", out JsonElement durationElement)
                && durationElement.TryGetDouble(out double duration) ? duration : 0,
            Error = root.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() : null
        };

        if (type == "audio")
        {
            if (frame.Samples <= 0 || string.IsNullOrWhiteSpace(frame.Base64Data))
                throw new InvalidDataException("Continuous speech audio frame has no PCM payload.");
            try
            {
                byte[] pcm = Convert.FromBase64String(frame.Base64Data);
                if (pcm.Length != frame.Samples * sizeof(short))
                    throw new InvalidDataException("Continuous speech PCM byte count does not match the declared sample count.");
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Continuous speech worker returned invalid base64 PCM audio.", ex);
            }
        }
        else if (!string.IsNullOrEmpty(frame.Base64Data))
        {
            throw new InvalidDataException("Continuous speech terminal frames must not carry audio payloads.");
        }

        if (type == "error" && string.IsNullOrWhiteSpace(frame.Error))
            frame.Error = "Continuous speech worker failed without an error detail.";
        return frame;
    }
}