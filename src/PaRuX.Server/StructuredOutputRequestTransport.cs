using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Decodes the backward-compatible reserved SkillParameters transport into the engine-neutral
/// structured-output contract. Reserved values are never forwarded as provider fields directly;
/// only a validated LocalStructuredOutputRequest may cross the serving boundary.
/// </summary>
public static class StructuredOutputRequestTransport
{
    public static LocalStructuredOutputRequest? TryRead(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SkillParameters == null || request.SkillParameters.Count == 0) return null;
        return TryRead(request.SkillParameters);
    }

    public static LocalStructuredOutputRequest? TryRead(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        bool hasName = parameters.TryGetValue(StructuredOutputTransportKeys.Name, out string? name);
        bool hasSchema = parameters.TryGetValue(StructuredOutputTransportKeys.JsonSchema, out string? schema);
        bool hasStrict = parameters.TryGetValue(StructuredOutputTransportKeys.Strict, out string? strictText);

        if (!hasName && !hasSchema && !hasStrict) return null;
        if (!hasName || !hasSchema)
            throw new InvalidDataException("Structured-output transport requires both name and JSON Schema.");

        bool strict = true;
        if (hasStrict && !bool.TryParse(strictText, out strict))
            throw new InvalidDataException("Structured-output strict flag must be true or false.");

        return new LocalStructuredOutputRequest(name ?? string.Empty, schema ?? string.Empty, strict).Validate();
    }

    public static void RemoveReserved(IDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Remove(StructuredOutputTransportKeys.Name);
        parameters.Remove(StructuredOutputTransportKeys.JsonSchema);
        parameters.Remove(StructuredOutputTransportKeys.Strict);
    }

    public static void Set(ChatCompletionRequest request, LocalStructuredOutputRequest structuredOutput)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(structuredOutput);
        LocalStructuredOutputRequest validated = structuredOutput.Validate();
        request.SkillParameters ??= new Dictionary<string, string>();
        request.SkillParameters[StructuredOutputTransportKeys.Name] = validated.Name;
        request.SkillParameters[StructuredOutputTransportKeys.JsonSchema] = validated.JsonSchema;
        request.SkillParameters[StructuredOutputTransportKeys.Strict] = validated.Strict.ToString();
    }
}
