using System.Text.Json;

namespace PaRuX.Intelligence;

/// <summary>
/// Fast, bounded inspection of Training Lab JSONL. Existing SFT/DPO positional fields stay source-
/// compatible; advanced-method capabilities are additive properties so older tests/callers do not
/// need to manufacture counts they never knew about.
/// </summary>
public static class TrainingDatasetShape
{
    public const int SampleLineLimit = 2_000;

    public sealed record Shape(
        bool SupportsSupervisedFineTuning,
        bool SupportsPreferenceTuning,
        int LinesRead,
        int ValidChatRecords,
        int ValidPreferenceRecords,
        int UnparseableLines,
        IReadOnlyList<int> FirstProblemLines,
        bool Truncated,
        string Summary)
    {
        public bool SupportsContinuedPretraining { get; init; }
        public bool SupportsGrpo { get; init; }
        public bool SupportsVisionFineTuning { get; init; }
        public bool SupportsTtsFineTuning { get; init; }
        public int ValidTextRecords { get; init; }
        public int ValidGrpoRecords { get; init; }
        public int ValidVisionRecords { get; init; }
        public int ValidTtsRecords { get; init; }
        public int MissingMediaFiles { get; init; }
    }

    public static readonly Shape Empty = new(
        false, false, 0, 0, 0, 0, Array.Empty<int>(), false,
        "This file has no readable records.");

    public static Shape Inspect(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty with { Summary = "There is no file at " + path + "." };
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
            return Inspect(File.ReadLines(path), baseDirectory);
        }
        catch (IOException ex) { return Empty with { Summary = "Could not read the file: " + ex.Message }; }
        catch (UnauthorizedAccessException ex) { return Empty with { Summary = "Could not read the file: " + ex.Message }; }
    }

    public static Shape Inspect(IEnumerable<string> lines) => Inspect(lines, mediaBaseDirectory: null);

    internal static Shape Inspect(IEnumerable<string> lines, string? mediaBaseDirectory)
    {
        if (lines == null) return Empty;

        int read = 0, chat = 0, preference = 0, text = 0, grpo = 0, vision = 0, tts = 0;
        int unparseable = 0, missingMedia = 0;
        var problems = new List<int>();
        bool truncated = false;

        foreach (string line in lines)
        {
            if (read >= SampleLineLimit) { truncated = true; break; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            read++;

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                unparseable++;
                if (problems.Count < 10) problems.Add(read);
                continue;
            }

            bool isChat = IsChatRecord(root);
            bool isPreference = IsPreferenceRecord(root);
            bool isText = HasNonEmptyString(root, "text");
            bool isGrpo = IsGrpoRecord(root);
            bool isVision = IsVisionRecord(root);
            bool isTts = IsTtsRecord(root);
            if (isChat) chat++;
            if (isPreference) preference++;
            if (isText) text++;
            if (isGrpo) grpo++;
            if (isVision)
            {
                vision++;
                if (!MediaExists(root, "image", mediaBaseDirectory)) missingMedia++;
            }
            if (isTts)
            {
                tts++;
                if (!MediaExists(root, "audio", mediaBaseDirectory)) missingMedia++;
            }
            if (!isChat && !isPreference && !isText && !isGrpo && !isVision && !isTts && problems.Count < 10)
                problems.Add(read);
        }

        if (read == 0) return Empty;
        int required = (int)Math.Ceiling(read * 0.95);
        bool sft = chat > 0 && chat >= required;
        bool dpo = preference > 0 && preference >= required;
        bool cpt = text > 0 && text >= required;
        bool grpoSupported = grpo > 0 && grpo >= required;
        bool visionSupported = vision > 0 && vision >= required && missingMedia == 0;
        bool ttsSupported = tts > 0 && tts >= required && missingMedia == 0;

        string summary = Describe(read, chat, preference, text, grpo, vision, tts, unparseable,
            missingMedia, sft, dpo, cpt, grpoSupported, visionSupported, ttsSupported, truncated);
        return new Shape(sft, dpo, read, chat, preference, unparseable, problems, truncated, summary)
        {
            SupportsContinuedPretraining = cpt,
            SupportsGrpo = grpoSupported,
            SupportsVisionFineTuning = visionSupported,
            SupportsTtsFineTuning = ttsSupported,
            ValidTextRecords = text,
            ValidGrpoRecords = grpo,
            ValidVisionRecords = vision,
            ValidTtsRecords = tts,
            MissingMediaFiles = missingMedia
        };
    }

    private static bool IsChatRecord(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("messages", out JsonElement messages)) return false;
        if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0) return false;
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("role", out JsonElement role) || role.ValueKind != JsonValueKind.String) return false;
            if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String) return false;
        }
        return true;
    }

    private static bool IsPreferenceRecord(JsonElement root) =>
        HasNonEmptyString(root, "prompt") && HasNonEmptyString(root, "chosen") && HasNonEmptyString(root, "rejected");

    private static bool IsGrpoRecord(JsonElement root)
    {
        if (!HasNonEmptyString(root, "prompt")) return false;
        // Reference-free format rewards are valid; reference-bearing records support exact/contains.
        return true;
    }

    private static bool IsVisionRecord(JsonElement root) =>
        HasNonEmptyString(root, "image") && (IsChatRecord(root)
            || (HasNonEmptyString(root, "prompt") && HasNonEmptyString(root, "response")));

    private static bool IsTtsRecord(JsonElement root) =>
        HasNonEmptyString(root, "audio") && HasNonEmptyString(root, "text");

    private static bool HasNonEmptyString(JsonElement root, string field) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool MediaExists(JsonElement root, string field, string? baseDirectory)
    {
        if (baseDirectory == null) return true; // In-memory shape tests cannot prove filesystem presence.
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String) return false;
        string raw = value.GetString() ?? string.Empty;
        if (raw.Length == 0) return false;
        try
        {
            string path = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(baseDirectory, raw));
            return File.Exists(path);
        }
        catch { return false; }
    }

    private static string Describe(
        int read, int chat, int preference, int text, int grpo, int vision, int tts, int unparseable,
        int missingMedia, bool sft, bool dpo, bool cpt, bool supportsGrpo, bool supportsVision,
        bool supportsTts, bool truncated)
    {
        string scope = truncated ? $"the first {read:N0} records" : $"all {read:N0} records";
        var supported = new List<string>();
        if (sft) supported.Add("supervised fine-tuning");
        if (dpo) supported.Add("DPO preference tuning");
        if (cpt) supported.Add("continued pretraining");
        if (supportsGrpo) supported.Add("GRPO");
        if (supportsVision) supported.Add("vision fine-tuning");
        if (supportsTts) supported.Add("TTS fine-tuning");
        if (supported.Count > 0)
        {
            string mediaWarning = missingMedia > 0 ? $" {missingMedia:N0} referenced media file(s) are missing." : string.Empty;
            return $"Inspected {scope}; compatible with {string.Join(", ", supported)}.{mediaWarning}";
        }
        if (unparseable == read)
            return $"None of {scope} is valid JSON. Training data must be JSONL - one JSON object per line.";
        return $"Inspected {scope}: chat={chat:N0}, preference={preference:N0}, text={text:N0}, "
            + $"GRPO prompts={grpo:N0}, vision={vision:N0}, TTS={tts:N0}, invalid JSON={unparseable:N0}"
            + (missingMedia > 0 ? $", missing media={missingMedia:N0}" : string.Empty) + ".";
    }
}
