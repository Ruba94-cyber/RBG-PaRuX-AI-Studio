using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>Pure terminal-frame recognition for OpenAI-compatible SSE streams.</summary>
    public static class StructuredChatStreamPolicy
    {
        public static bool TryReadFinishReason(string data, out string? reason)
        {
            reason = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                    return false;
                JsonElement first = choices[0];
                if (!first.TryGetProperty("finish_reason", out JsonElement finish)
                    || finish.ValueKind != JsonValueKind.String)
                    return false;
                reason = finish.GetString();
                return !string.IsNullOrWhiteSpace(reason);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
