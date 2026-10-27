using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Reads a character card (SillyTavern/Chub "chara_card_v2/v3" JSON, a flat v1 JSON, or a PNG with the card base64-embedded in a "chara"/"ccv3" text chunk)
/// and turns it into a persona name and system prompt. Only the character definition becomes instructions; creator notes and tags do not.
/// </summary>
internal static class CharacterCardImporter
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal const int MaxPromptLength = 24_000;
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    internal static (string Name, string SystemPrompt) ToPersona(byte[] bytes, string fileName)
    {
        if (bytes is null || bytes.Length == 0) throw new InvalidDataException("The file is empty.");
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The card is larger than " + MaxBytes / (1024 * 1024) + " MB.");

        string json = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(PngSignature)
            ? ReadPngCard(bytes)
            : Encoding.UTF8.GetString(bytes).TrimStart('﻿');

        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { throw new InvalidDataException("This file is not a character card (it is not valid JSON)."); }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("This file is not a character card.");

        JsonElement data = root.TryGetProperty("data", out JsonElement wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
        string name = Str(data, "name");
        string description = Str(data, "description");
        if (name.Length == 0 || (description.Length == 0 && Str(data, "personality").Length == 0))
            throw new InvalidDataException("The card has no character name and description.");

        var prompt = new StringBuilder();
        string system = Str(data, "system_prompt");
        if (system.Length > 0) prompt.AppendLine(system).AppendLine();
        else prompt.AppendLine("You are " + name + ". Stay in character for the whole conversation.").AppendLine();
        Append(prompt, "Description", description);
        Append(prompt, "Personality", Str(data, "personality"));
        Append(prompt, "Scenario", Str(data, "scenario"));
        Append(prompt, "Example dialogue", Str(data, "mes_example"));
        string first = Str(data, "first_mes");
        if (first.Length > 0) prompt.AppendLine("When the conversation starts, you open with: " + first);

        string result = prompt.ToString().Replace("{{char}}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{{user}}", "the user", StringComparison.OrdinalIgnoreCase).Replace("<START>", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (result.Length > MaxPromptLength) result = result[..MaxPromptLength];
        return (name.Length > 80 ? name[..80] : name, result);
    }

    private static void Append(StringBuilder sb, string label, string value)
    {
        if (value.Length > 0) sb.AppendLine(label + ": " + value).AppendLine();
    }

    private static string Str(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;

    private static string ReadPngCard(byte[] png)
    {
        int pos = 8;
        string? v2 = null, v3 = null;
        while (pos + 12 <= png.Length)
        {
            int length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            int dataStart = pos + 8;
            if (length < 0 || dataStart + length > png.Length) break;
            if (type == "tEXt")
            {
                int nul = Array.IndexOf(png, (byte)0, dataStart, length);
                if (nul > dataStart)
                {
                    string keyword = Encoding.Latin1.GetString(png, dataStart, nul - dataStart);
                    string text = Encoding.Latin1.GetString(png, nul + 1, dataStart + length - nul - 1);
                    if (keyword == "chara") v2 = text;
                    else if (keyword == "ccv3") v3 = text;
                }
            }
            if (type == "IEND") break;
            pos = dataStart + length + 4;
        }
        string? encoded = v3 ?? v2;
        if (encoded is null) throw new InvalidDataException("This PNG has no embedded character card.");
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim())); }
        catch (FormatException) { throw new InvalidDataException("The character card inside this PNG is damaged."); }
    }
}
