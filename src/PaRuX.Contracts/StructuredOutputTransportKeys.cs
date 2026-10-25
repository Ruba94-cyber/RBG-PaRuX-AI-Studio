namespace PaRuX.Contracts;

/// <summary>
/// Reserved PaRuX wire keys carried inside ChatCompletionRequest.SkillParameters so structured
/// output can be added without changing the existing Chat DTO shape or breaking older desktop /
/// Android clients. The server validates every value before it can reach a provider.
/// </summary>
public static class StructuredOutputTransportKeys
{
    public const string Name = "__parux_structured_output_name";
    public const string JsonSchema = "__parux_structured_output_schema";
    public const string Strict = "__parux_structured_output_strict";

    public static bool IsReserved(string? key) =>
        string.Equals(key, Name, StringComparison.Ordinal)
        || string.Equals(key, JsonSchema, StringComparison.Ordinal)
        || string.Equals(key, Strict, StringComparison.Ordinal);
}
