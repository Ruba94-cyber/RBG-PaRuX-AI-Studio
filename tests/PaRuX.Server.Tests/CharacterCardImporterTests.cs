using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Character cards (SillyTavern/Chub "chara_card_v2" JSON or PNG with an embedded card) become ordinary PaRuX personas.</summary>
public sealed class CharacterCardImporterTests
{
    private const string V2 = """
        {"spec":"chara_card_v2","spec_version":"2.0","data":{
          "name":"Captain Mira","description":"A weathered sea captain who has sailed every ocean.",
          "personality":"Gruff, witty, secretly kind.","scenario":"You are aboard her ship at dusk.",
          "first_mes":"Welcome aboard, stranger.","mes_example":"<START>\n{{user}}: Hi\n{{char}}: Ahoy.",
          "system_prompt":"Stay in character as {{char}}.","creator_notes":"made by someone"}}
        """;

    private static byte[] Png(string keyword, string text)
    {
        // Minimal PNG: signature + IHDR + tEXt + IEND (CRCs are not validated by the importer).
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data.Length)));
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);
        }
        Chunk("IHDR", new byte[13]);
        Chunk("tEXt", Encoding.Latin1.GetBytes(keyword + "\0" + text));
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void A_v2_json_card_becomes_a_persona_with_name_and_a_system_prompt_built_from_its_fields()
    {
        (string name, string prompt) = CharacterCardImporter.ToPersona(Encoding.UTF8.GetBytes(V2), "mira.json");

        Assert.Equal("Captain Mira", name);
        Assert.Contains("A weathered sea captain", prompt);
        Assert.Contains("Gruff, witty, secretly kind.", prompt);
        Assert.Contains("You are aboard her ship at dusk.", prompt);
        Assert.Contains("Stay in character as Captain Mira.", prompt);   // {{char}} substituted
        Assert.Contains("Ahoy.", prompt);
        Assert.DoesNotContain("{{char}}", prompt);
        Assert.DoesNotContain("made by someone", prompt);                 // creator notes are not instructions
    }

    [Fact]
    public void A_png_card_with_a_base64_chara_chunk_is_read()
    {
        byte[] png = Png("chara", Convert.ToBase64String(Encoding.UTF8.GetBytes(V2)));

        (string name, string prompt) = CharacterCardImporter.ToPersona(png, "mira.png");

        Assert.Equal("Captain Mira", name);
        Assert.Contains("weathered sea captain", prompt);
    }

    [Fact]
    public void A_v1_flat_card_without_the_data_wrapper_is_read()
    {
        string v1 = JsonSerializer.Serialize(new { name = "Bot", description = "A helpful bot.", personality = "Cheerful", scenario = "", first_mes = "Hi", mes_example = "" });

        (string name, string prompt) = CharacterCardImporter.ToPersona(Encoding.UTF8.GetBytes(v1), "bot.json");

        Assert.Equal("Bot", name);
        Assert.Contains("A helpful bot.", prompt);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"foo\":1}")]
    [InlineData("{\"data\":{\"name\":\"\",\"description\":\"\"}}")]
    public void Anything_that_is_not_a_usable_card_is_refused_with_a_clear_message(string content)
    {
        var error = Assert.Throws<InvalidDataException>(() => CharacterCardImporter.ToPersona(Encoding.UTF8.GetBytes(content), "x.json"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void A_png_without_a_card_chunk_and_an_oversized_file_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => CharacterCardImporter.ToPersona(Png("Comment", "hello"), "plain.png"));
        Assert.Throws<InvalidDataException>(() => CharacterCardImporter.ToPersona(new byte[CharacterCardImporter.MaxBytes + 1], "huge.png"));
    }

    [Fact]
    public void The_prompt_is_bounded()
    {
        string huge = JsonSerializer.Serialize(new { spec = "chara_card_v2", data = new { name = "Big", description = new string('x', 200_000) } });

        (_, string prompt) = CharacterCardImporter.ToPersona(Encoding.UTF8.GetBytes(huge), "big.json");

        Assert.True(prompt.Length <= CharacterCardImporter.MaxPromptLength);
    }
}
