using System.Text.RegularExpressions;

/// <summary>
/// Binds the per-request thinking preference into a model chat template before it reaches the
/// ONNX Runtime GenAI Jinja engine. That engine does not implement the boolean-literal `is` tests
/// (`x is false`) that Qwen3-family templates use, so those tests are rewritten to the equivalent
/// comparisons; templates that do not mention enable_thinking are never silently treated as
/// thinking=false. Kept dependency-free so the platform-neutral test suite compiles this file.
/// </summary>
internal static class ChatTemplateThinking
{
    private static readonly Regex BooleanIsTest = new(
        @"\benable_thinking\s+is\s+(not\s+)?(true|false)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    internal static bool Supports(string chatTemplate) =>
        !string.IsNullOrWhiteSpace(chatTemplate)
        && chatTemplate.Contains("enable_thinking", StringComparison.Ordinal);

    internal static string Bind(string chatTemplate, bool enableThinking)
    {
        if (string.IsNullOrWhiteSpace(chatTemplate))
            throw new InvalidDataException("chat_template.jinja is empty.");
        if (!Supports(chatTemplate))
        {
            if (!enableThinking)
                throw new NotSupportedException(
                    "This ONNX model chat template does not expose enable_thinking; PaRuX cannot guarantee thinking=false for this request.");
            return chatTemplate;
        }
        string normalized = BooleanIsTest.Replace(chatTemplate, match =>
            "enable_thinking " + (match.Groups[1].Success ? "!=" : "==") + " " + match.Groups[2].Value.ToLowerInvariant());
        return "{% set enable_thinking = " + (enableThinking ? "true" : "false") + " %}\n" + normalized;
    }
}
