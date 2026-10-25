using System;
using System.IO;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>Validation and display rules for audio attached to a chat turn. Pure: no WinUI, no file picking.</summary>
    public static class ChatAudioAttachmentPolicy
    {
        /// <summary>Audio is sent inline as base64 in the chat request, so the cap is deliberately modest.</summary>
        public const long MaxBytes = 25L * 1024 * 1024;

        public static readonly string[] Extensions = { ".wav", ".mp3", ".m4a", ".ogg", ".flac" };

        public static string? MediaTypeFor(string? extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".wav": return "audio/wav";
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/mp4";
                case ".ogg": return "audio/ogg";
                case ".flac": return "audio/flac";
                default: return null;
            }
        }

        public static bool IsAudioMediaType(string? mediaType) =>
            mediaType != null && mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

        /// <summary>Null when the file may be attached; otherwise a plain-English reason.</summary>
        public static string? Validate(string? fileName, long sizeBytes, int alreadyAttachedAudio)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? "That file" : fileName!;
            if (MediaTypeFor(Path.GetExtension(fileName ?? string.Empty)) == null)
                return name + " is not a supported audio file. Use WAV, MP3, M4A, OGG or FLAC.";
            if (sizeBytes <= 0) return name + " is empty.";
            if (sizeBytes > MaxBytes)
                return name + " is larger than " + (MaxBytes / (1024 * 1024)) + " MB. Trim it or split it into shorter clips.";
            if (alreadyAttachedAudio >= 1) return "Attach one audio clip per message.";
            return null;
        }

        /// <summary>Reads the duration from a WAV header without decoding. Null for any other or malformed input.</summary>
        public static TimeSpan? TryReadWavDuration(Stream stream)
        {
            try
            {
                using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                if (stream.Length < 44 || new string(r.ReadChars(4)) != "RIFF") return null;
                r.ReadUInt32();
                if (new string(r.ReadChars(4)) != "WAVE") return null;
                uint byteRate = 0;
                while (stream.Position + 8 <= stream.Length)
                {
                    string id = new string(r.ReadChars(4));
                    uint size = r.ReadUInt32();
                    if (id == "fmt " && size >= 16)
                    {
                        r.ReadUInt32(); // format tag + channels
                        r.ReadUInt32(); // sample rate
                        byteRate = r.ReadUInt32();
                        stream.Position += size - 12;
                    }
                    else if (id == "data")
                    {
                        long available = stream.Length - stream.Position;
                        long bytes = size == 0 || size > available ? available : size;
                        return byteRate == 0 ? null : TimeSpan.FromSeconds((double)bytes / byteRate);
                    }
                    else stream.Position += size + (size & 1);
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return null;
            }
        }

        public static string ChipText(string fileName, TimeSpan? duration)
        {
            if (duration == null || duration.Value <= TimeSpan.Zero) return fileName;
            TimeSpan d = duration.Value;
            string t = d.TotalHours >= 1
                ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
                : $"{(int)d.TotalMinutes}:{d.Seconds:00}";
            return fileName + " (" + t + ")";
        }
    }

    public sealed record ModalityRefusal(string Code, string Message, string? Capability);

    /// <summary>Turns Core's typed modality refusals into plain English with a next action. No refusal is rewritten as success.</summary>
    public static class ModalityRefusalPresenter
    {
        public const string ConsentRequired = "consent_required";

        public static ModalityRefusal? TryParse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object) return null;
                string? code = Str(error, "code");
                if (string.IsNullOrWhiteSpace(code)) return null;
                return new ModalityRefusal(code!, Str(error, "message") ?? string.Empty, Str(error, "capability"));
            }
            catch (JsonException) { return null; }
        }

        public static bool IsKnown(string? code) =>
            code is "no_eligible_engine" or "tool_calling_unverified" or "gaming_hard_off" or ConsentRequired
                or "invalid_audio" or "invalid_text" or "speech_engine_failed";

        public static string Describe(ModalityRefusal refusal)
        {
            string cap = refusal.Capability ?? string.Empty;
            bool speech = cap.StartsWith("audio", StringComparison.OrdinalIgnoreCase);
            bool speak = cap.Contains("speak", StringComparison.OrdinalIgnoreCase);
            bool computer = cap.Contains("computer", StringComparison.OrdinalIgnoreCase);
            switch (refusal.Code)
            {
                case "no_eligible_engine" when speech:
                    return (speak ? "No text-to-speech engine is installed." : "No speech engine is installed.")
                        + " Open Model Hub > Audio to install one. Nothing was substituted.";
                case "no_eligible_engine" when computer:
                    return "The Computer Use agent is not available on this device. Open Runtime Manager to check it. Nothing was started.";
                case "no_eligible_engine":
                    return "No installed local model can do that yet. Open Model Hub to install one, then try again.";
                case "tool_calling_unverified":
                    return "No installed model has verified tool calling, so PaRuX will not use tools. Open Model Hub and run the capability check on a model, or install one that supports tools.";
                case "gaming_hard_off":
                    return "Gaming mode is on, so local AI is paused. Use Return to AI in the chat header, then send this again.";
                case ConsentRequired:
                    return "Computer Use needs your approval first. Nothing was started. Send the request again and choose Allow once in the approval dialog.";
                case "invalid_audio":
                    return "That audio could not be read" + Detail(refusal) + " Try a WAV, MP3, M4A, OGG or FLAC clip.";
                case "speech_engine_failed":
                    return "The local speech engine failed" + Detail(refusal) + " Open Runtime Manager to check it, then try again.";
                default:
                    return string.IsNullOrWhiteSpace(refusal.Message)
                        ? "PaRuX could not run that request (" + refusal.Code + ")."
                        : refusal.Message;
            }
        }

        private static string Detail(ModalityRefusal r) =>
            string.IsNullOrWhiteSpace(r.Message) ? "." : ": " + r.Message.Trim().TrimEnd('.') + ".";

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Wording of the Computer Use consent dialog. Cancel is the default button; consent is per request.</summary>
    public static class ComputerUseConsentText
    {
        public const string Title = "Allow Computer Use for this request?";
        public const string Confirm = "Allow once";
        public const string Cancel = "Cancel";

        public static string Body(string? task)
        {
            string shown = string.IsNullOrWhiteSpace(task) ? "(no task text)" : task!.Trim();
            if (shown.Length > 240) shown = shown.Substring(0, 240) + "...";
            return "Task: " + shown + "\n\n"
                + "If you allow this, the PaRuX agent can see your screen and use the mouse and keyboard to carry out this one task. "
                + "Screenshots are processed locally on this PC and are not sent to any cloud service.\n\n"
                + "It stops at a step limit and you can stop it at any time. Gaming mode blocks Computer Use. "
                + "Your approval covers this request only.";
        }
    }
}
