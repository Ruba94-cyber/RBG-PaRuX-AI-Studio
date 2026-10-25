using System.Text.Json;

namespace PaRuX.Server;

/// <summary>Invalid performance stream carrying a bounded, non-sensitive diagnostic code (never model text).
/// InvalidDataException is sealed, so the code travels in Exception.Data.</summary>
internal static class PerformanceStreamException
{
    internal const string CodeKey = "parux.failure_code";
    internal static InvalidDataException Create(string code, string message)
    {
        var ex = new InvalidDataException(message);
        ex.Data[CodeKey] = code;
        return ex;
    }
    internal static string? CodeOf(Exception ex) => ex.Data[CodeKey] as string;
}

/// <summary>Strict parser for the bounded OpenAI-compatible SSE stream used by the Candidate workload.</summary>
internal sealed class CandidatePerformanceStreamParser
{
    private readonly string _alias;
    private readonly int _maxTokens;
    private string _finishReason = "";
    private int _usageCompletionTokens;
    private readonly List<string> _data = new();
    private double? _firstVisibleMilliseconds;
    private int _promptTokens;
    private double _promptMilliseconds;
    private int _generatedTokens;
    private double _decodeMilliseconds;
    private bool _finished;
    private bool _sawUsageCompletionTokens;
    private bool _done;
    private bool _sawModel;
    private bool _sawUsageOnlyFrame;

    internal CandidatePerformanceStreamParser(string alias, int maxTokens = CandidateTextPerformanceWorkload.MaxTokens)
    {
        _alias = alias;
        _maxTokens = maxTokens;
    }

    internal void AcceptLine(string line, double elapsedMilliseconds)
    {
        if (_done)
        {
            if (line.Length == 0) return;
            throw new InvalidDataException("Data followed the SSE end marker.");
        }
        if (line.Length == 0) { ProcessEvent(elapsedMilliseconds); return; }
        if (line.StartsWith(':')) return;
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            string value = line[5..];
            if (value.StartsWith(' ')) value = value[1..];
            _data.Add(value);
        }
    }

    internal TextPerformanceCompletion Complete(double completionMilliseconds)
    {
        if (_data.Count > 0) ProcessEvent(completionMilliseconds);
        if (!_done || !_finished || !_sawModel || _firstVisibleMilliseconds is null
            || _promptTokens <= 0 || _generatedTokens <= 0
            || !Positive(_promptMilliseconds) || !Positive(_decodeMilliseconds)
            || !Positive(completionMilliseconds) || _firstVisibleMilliseconds > completionMilliseconds)
            throw PerformanceStreamException.Create("stream_incomplete", "Incomplete local performance stream.");
        // "length" is a valid deterministic end only when generation stopped exactly at the workload's
        // max_tokens bound and the server's own usage and timings frames agree; anything else is untrusted.
        if (_sawUsageCompletionTokens && _usageCompletionTokens != _generatedTokens)
            throw PerformanceStreamException.Create("usage_timings_disagree",
                "Performance usage and timings disagree on generated tokens.");
        if (_finishReason == "length"
            && (!_sawUsageCompletionTokens || _generatedTokens != _maxTokens))
            throw PerformanceStreamException.Create("length_not_at_bound",
                "Performance stream ended by length before the exact token bound.");
        return new(_promptTokens, _promptMilliseconds, _generatedTokens, _decodeMilliseconds,
            completionMilliseconds, _firstVisibleMilliseconds.Value, _finishReason);
    }

    private void ProcessEvent(double elapsedMilliseconds)
    {
        if (_data.Count == 0) return;
        string payload = string.Join("\n", _data);
        _data.Clear();
        if (payload == "[DONE]") { _done = true; return; }
        if (_sawUsageOnlyFrame) throw new InvalidDataException("Data followed the terminal usage frame.");
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("model", out JsonElement model)
            && (model.ValueKind != JsonValueKind.String || model.GetString() != _alias))
            throw new InvalidDataException("Unexpected model in local performance stream.");
        if (root.TryGetProperty("model", out _)) _sawModel = true;
        if (root.TryGetProperty("choices", out JsonElement choices))
        {
            if (choices.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid local performance stream choice.");
            if (choices.GetArrayLength() == 0)
            {
                if (!_finished || !root.TryGetProperty("usage", out JsonElement usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Invalid terminal usage-only performance frame.");
                if (usage.TryGetProperty("completion_tokens", out JsonElement ct) && ct.TryGetInt32(out int n))
                { _usageCompletionTokens = n; _sawUsageCompletionTokens = true; }
                _sawUsageOnlyFrame = true;
            }
            else
            {
                if (choices.GetArrayLength() != 1)
                    throw new InvalidDataException("Invalid local performance stream choice.");
                JsonElement choice = choices[0];
                if (!choice.TryGetProperty("index", out JsonElement index) || !index.TryGetInt32(out int ix) || ix != 0)
                    throw new InvalidDataException("Invalid local performance stream index.");
                if (_finished && choice.TryGetProperty("delta", out JsonElement late)
                    && late.ValueKind == JsonValueKind.Object && late.TryGetProperty("content", out JsonElement lateContent)
                    && lateContent.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lateContent.GetString()))
                    throw PerformanceStreamException.Create("content_after_finish",
                        "Performance stream carried content after the finish frame.");
                if (choice.TryGetProperty("delta", out JsonElement delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out JsonElement content))
                {
                    if (content.ValueKind != JsonValueKind.String && content.ValueKind != JsonValueKind.Null)
                        throw new InvalidDataException("Invalid visible content delta.");
                    if (content.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(content.GetString()))
                        _firstVisibleMilliseconds ??= elapsedMilliseconds;
                }
                if (choice.TryGetProperty("finish_reason", out JsonElement finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    if (_finished)
                        throw PerformanceStreamException.Create("finish_reason_repeated",
                            "Performance stream repeated its finish frame.");
                    string reason = finish.GetString() ?? "";
                    if (reason is not ("stop" or "length"))
                        throw PerformanceStreamException.Create("finish_reason_invalid", "Performance stream ended abnormally.");
                    _finishReason = reason;
                    _finished = true;
                }
            }
        }
        if (root.TryGetProperty("timings", out JsonElement timings))
        {
            if (!PositiveInt(timings, "prompt_n", out _promptTokens)
                || !PositiveDouble(timings, "prompt_ms", out _promptMilliseconds)
                || !PositiveInt(timings, "predicted_n", out _generatedTokens)
                || !PositiveDouble(timings, "predicted_ms", out _decodeMilliseconds))
                throw new InvalidDataException("Invalid local performance stream timings.");
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool PositiveInt(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement field)
            && field.TryGetInt32(out number) && number > 0;
    }
    private static bool PositiveDouble(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement field)
            && field.TryGetDouble(out number) && Positive(number);
    }
}
