using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Per-request media model routing: style hints, content lane, fit and determinism. Hermetic: no GPU, disk or network.</summary>
public sealed class MediaModelRouterTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static MediaHardware Big => new(24 * Gib, 64 * Gib);
    private static readonly Func<string, double?> NoSpeed = _ => null;
    private static ISet<string> Files(params string[] f) => new HashSet<string>(f, StringComparer.OrdinalIgnoreCase);

    private static string Variant(string id, string file, string lane, int tier, bool verified = false)
    {
        string evidence = lane == "uncensored" ? "\"laneEvidence\": \"model card\", " : string.Empty;
        string listing = verified
            ? $"\"repository\": \"o/{id}\", \"path\": \"{file}\", \"sizeBytes\": 2000000000, \"sizeBasis\": \"exact_listing\", \"listingVerified\": true, "
            : "\"sizeBasis\": \"derived_from_parameters\", \"listingVerified\": false, ";
        return $"{{ \"id\": \"{id}\", \"title\": \"Title {id}\", \"lane\": \"{lane}\", {evidence}\"precision\": \"fp8\", \"fileName\": \"{file}\", {listing}\"license\": \"MIT\", \"tier\": {tier}, \"baseCompat\": \"x\" }}";
    }

    private static string Family(string id, string capability, string strengths, params string[] variants) =>
        $"{{ \"id\": \"{id}\", \"displayName\": \"{id}\", \"capability\": \"{capability}\", \"support\": \"supported\", \"registryFamily\": \"z-image\", " +
        $"\"parametersBillion\": 2, \"textEncoderGb\": 0, \"vaeGb\": 0, {(strengths.Length == 0 ? string.Empty : $"\"strengths\": [{strengths}], ")}\"variants\": [ {string.Join(", ", variants)} ] }}";

    private static MediaCatalogue Catalogue(params string[] families) => MediaCatalogue.Parse($$"""
        { "schemaVersion": 1, "catalogueVersion": 1,
          "capabilities": [
            { "id": "image.generate", "label": "I", "defaultJob": { "megapixels": 1, "frames": 1, "seconds": 0 } },
            { "id": "image.edit", "label": "E", "defaultJob": { "megapixels": 1, "frames": 1, "seconds": 0 } } ],
          "families": [ {{string.Join(", ", families)}} ] }
        """, MediaCompanionRegistry.Shipped);

    private static MediaCatalogue Std() => Catalogue(
        Family("photo-a", "image.generate", "\"photoreal\", \"fast\"", Variant("photo-a-1", "photo_a.safetensors", "standard", 4)),
        Family("anime-b", "image.generate", "\"anime\", \"illustration\"", Variant("anime-b-1", "anime_b.safetensors", "standard", 3)),
        Family("text-c", "image.generate", "\"text-rendering\"", Variant("text-c-1", "text_c.safetensors", "standard", 3)),
        Family("spicy-d", "image.generate", "\"anime\", \"photoreal\"", Variant("spicy-d-1", "spicy_d.safetensors", "uncensored", 3)),
        Family("spicy-e", "image.generate", "", Variant("spicy-e-1", "spicy_e.safetensors", "uncensored", 5, verified: true)),
        Family("edit-f", "image.edit", "\"editing\"", Variant("edit-f-1", "edit_f.safetensors", "standard", 3)),
        Family("edit-g", "image.edit", "", Variant("edit-g-1", "edit_g.safetensors", "standard", 5)));

    private static readonly string[] AllStd = { "photo_a.safetensors", "anime_b.safetensors", "text_c.safetensors" };

    private static MediaRouteResult Route(string prompt, string lane, ISet<string> installed, string capability = "image.generate",
        MediaHardware? hw = null, Func<string, double?>? speed = null, Func<string, bool>? eligible = null, MediaCatalogue? cat = null) =>
        MediaModelRouter.Route(cat ?? Std(), capability, prompt, lane, hw ?? Big, installed, speed ?? NoSpeed, eligible);

    // ---- catalogue schema ----

    [Fact]
    public void Shipped_catalogue_declares_only_known_strengths_and_every_supported_family_declares_some()
    {
        foreach (MediaCatalogueFamily f in MediaCatalogue.Shipped.Families)
        {
            Assert.All(f.Strengths, s => Assert.Contains(s, MediaCatalogue.Strengths));
            Assert.Equal(f.Strengths.Count, f.Strengths.Distinct().Count());
            if (f.IsSupported) Assert.NotEmpty(f.Strengths);
        }
    }

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("\"anime\", \"anime\"")]
    public void Parse_rejects_unknown_or_duplicate_strengths(string strengths)
    {
        Assert.Throws<InvalidDataException>(() =>
            Catalogue(Family("f", "image.generate", strengths, Variant("v1", "a.safetensors", "standard", 3))));
    }

    [Fact]
    public void Strengths_are_optional()
    {
        MediaCatalogue c = Catalogue(Family("f", "image.generate", "", Variant("v1", "a.safetensors", "standard", 3)));
        Assert.Empty(c.Families.Single().Strengths);
    }

    // ---- style hints ----

    [Theory]
    [InlineData("an anime girl in a garden", "anime")]
    [InlineData("a manga panel of a robot", "anime")]
    [InlineData("un cartone animato con un gatto", "anime")]
    [InlineData("a logo for my coffee shop", "text-rendering")]
    [InlineData("poster with the text that says OPEN", "text-rendering")]
    [InlineData("un manifesto con la scritta Ciao", "text-rendering")]
    [InlineData("a realistic portrait of an old man", "photoreal")]
    [InlineData("una foto di un cane sulla spiaggia", "photoreal")]
    [InlineData("a watercolor illustration of a fox", "illustration")]
    [InlineData("un disegno a matita", "illustration")]
    [InlineData("a cinematic drone shot over the sea", "cinematic")]
    [InlineData("una scena cinematografica", "cinematic")]
    [InlineData("a quick draft of a house", "fast")]
    public void Hints_are_detected_in_english_and_italian(string prompt, string expected)
    {
        Assert.Contains(MediaPromptStyleHints.Detect(prompt), h => h.Strength == expected);
    }

    [Fact]
    public void Hints_match_whole_words_only_and_ignore_accents_and_case()
    {
        Assert.Empty(MediaPromptStyleHints.Detect("a classic animal"));
        Assert.Empty(MediaPromptStyleHints.Detect(null));
        Assert.Contains(MediaPromptStyleHints.Detect("Una FOTOGRAFIA"), h => h.Strength == "photoreal");
    }

    [Fact]
    public void Routing_prefers_the_family_whose_declared_strength_matches_the_prompt()
    {
        ISet<string> installed = Files(AllStd);
        Assert.Equal("anime_b.safetensors", Route("an anime knight", "Standard", installed).ChosenFile);
        Assert.Equal("text_c.safetensors", Route("a poster with text that says SALE", "Standard", installed).ChosenFile);
        Assert.Equal("photo_a.safetensors", Route("a realistic portrait", "Standard", installed).ChosenFile);
        Assert.Equal("photo_a.safetensors", Route("un ritratto fotorealistico", "Standard", installed).ChosenFile);
    }

    [Fact]
    public void With_no_style_hint_the_declared_tier_decides_and_the_reason_says_so()
    {
        MediaRouteResult r = Route("a red apple", "Standard", Files(AllStd));
        Assert.Equal("photo_a.safetensors", r.ChosenFile);
        Assert.Contains("best installed model", r.Reason);
    }

    [Fact]
    public void A_style_hint_never_selects_a_model_that_is_not_installed()
    {
        MediaRouteResult r = Route("an anime knight", "Standard", Files("photo_a.safetensors"));
        Assert.Equal("photo_a.safetensors", r.ChosenFile);
        Assert.Contains(r.Ranked, c => c.Evaluated.Variant.Id == "anime-b-1" && c.ExcludedBecause == "not installed");
    }

    [Fact]
    public void Edit_capability_implicitly_prefers_a_family_declared_good_at_editing()
    {
        // edit-g has the higher tier but declares no editing strength; the capability itself asks for editing.
        MediaRouteResult r = Route("make the sky purple", "Standard", Files("edit_f.safetensors", "edit_g.safetensors"), "image.edit");
        Assert.Equal("edit_f.safetensors", r.ChosenFile);
        Assert.Contains("editing", r.Hints);
    }

    // ---- lane ----

    [Theory]
    [InlineData("Standard")]
    [InlineData("Automatic")]
    public void Standard_and_Automatic_without_adult_intent_never_pick_an_uncensored_entry(string lane)
    {
        // The uncensored entry matches the style (anime+photoreal) and is installed; it must still be ignored.
        MediaRouteResult r = Route("an anime realistic portrait", lane, Files(AllStd.Append("spicy_d.safetensors").ToArray()));
        Assert.Equal("standard", r.EffectiveLane);
        Assert.DoesNotContain("spicy", r.ChosenFile);
        Assert.Equal("ok", r.Code);
    }

    [Fact]
    public void Standard_lane_ignores_adult_wording_entirely()
    {
        MediaRouteResult r = Route("nsfw nude portrait", "Standard", Files(AllStd.Append("spicy_d.safetensors").ToArray()));
        Assert.Equal("standard", r.EffectiveLane);
        Assert.False(r.AdultIntent);
        Assert.Equal("photo_a.safetensors", r.ChosenFile);
    }

    [Fact]
    public void Uncensored_lane_uses_only_uncensored_entries()
    {
        MediaRouteResult r = Route("a red apple", "Uncensored", Files(AllStd.Append("spicy_d.safetensors").ToArray()));
        Assert.Equal("spicy_d.safetensors", r.ChosenFile);
        Assert.Equal("uncensored", r.EffectiveLane);
        Assert.Equal("ok", r.Code);
    }

    [Fact]
    public void Uncensored_lane_without_an_installed_uncensored_model_says_so_and_names_what_to_install_never_falling_back()
    {
        MediaRouteResult r = Route("a red apple", "Uncensored", Files(AllStd));
        Assert.Null(r.Chosen);
        Assert.Equal("no_uncensored_model_installed", r.Code);
        Assert.Contains("no uncensored", r.Reason);
        Assert.Contains("Title spicy-e-1", r.InstallHint);
        Assert.Contains(r.Trace, t => t.Contains("no_standard_fallback"));
    }

    [Fact]
    public void Uncensored_lane_hint_says_nothing_is_listed_when_the_catalogue_has_no_runnable_uncensored_model()
    {
        MediaCatalogue c = Catalogue(Family("photo-a", "image.generate", "\"photoreal\"", Variant("photo-a-1", "photo_a.safetensors", "standard", 4)));
        MediaRouteResult r = Route("x", "Uncensored", Files("photo_a.safetensors"), cat: c);
        Assert.Null(r.Chosen);
        Assert.Contains("lists no uncensored", r.InstallHint);
    }

    [Fact]
    public void Automatic_with_adult_intent_prefers_an_installed_uncensored_entry()
    {
        MediaRouteResult r = Route("nsfw nude woman", "Automatic", Files(AllStd.Append("spicy_d.safetensors").ToArray()));
        Assert.True(r.AdultIntent);
        Assert.Equal("spicy_d.safetensors", r.ChosenFile);
        Assert.Contains("adult intent", r.Reason);
    }

    [Fact]
    public void Automatic_with_adult_intent_and_no_uncensored_installed_uses_standard_openly_and_names_the_model_to_install()
    {
        MediaRouteResult r = Route("una donna nuda", "Automatic", Files(AllStd));
        Assert.True(r.AdultIntent);
        Assert.Equal("standard", r.Chosen!.Variant.Lane);
        Assert.Equal("ok", r.Code);
        Assert.Contains("no uncensored", r.Reason);
        Assert.Contains("Title spicy-e-1", r.Reason);
        Assert.Contains("Title spicy-e-1", r.InstallHint);
    }

    [Theory]
    [InlineData("nsfw")]
    [InlineData("a nude figure study")]
    [InlineData("una ragazza nuda")]
    [InlineData("contenuto esplicito")]
    [InlineData("senza vestiti")]
    [InlineData("hentai art")]
    public void Adult_intent_words_are_found_in_english_and_italian(string prompt) =>
        Assert.True(MediaAdultIntentLexicon.HasExplicitAdultIntent(prompt));

    [Theory]
    [InlineData("a quiet mountain lake")]
    [InlineData("Sussex countryside")]
    [InlineData("un gatto sul divano")]
    [InlineData("a bikini ad for the beach")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_prompts_are_not_adult_intent(string? prompt) =>
        Assert.False(MediaAdultIntentLexicon.HasExplicitAdultIntent(prompt));

    // ---- fit, install and speed ----

    [Fact]
    public void A_variant_that_does_not_fit_this_pc_is_never_chosen()
    {
        // A 40B-parameter family is too large for 8 GB VRAM / 16 GB RAM.
        MediaCatalogue big = MediaCatalogue.Parse($$"""
            { "schemaVersion": 1, "catalogueVersion": 1,
              "capabilities": [ { "id": "image.generate", "label": "I", "defaultJob": { "megapixels": 1, "frames": 1, "seconds": 0 } } ],
              "families": [ {{Family("huge", "image.generate", "\"photoreal\"", Variant("huge-1", "huge.safetensors", "standard", 5)).Replace("\"parametersBillion\": 2", "\"parametersBillion\": 40")}},
                            {{Family("small", "image.generate", "", Variant("small-1", "small.safetensors", "standard", 1))}} ] }
            """, MediaCompanionRegistry.Shipped);
        MediaRouteResult r = Route("realistic photo", "Standard", Files("huge.safetensors", "small.safetensors"), hw: new(8 * Gib, 16 * Gib), cat: big);
        Assert.Equal("small.safetensors", r.ChosenFile);
        Assert.Contains(r.Ranked, x => x.Evaluated.Variant.Id == "huge-1" && x.ExcludedBecause == "too large for this PC");
    }

    [Fact]
    public void An_installed_file_that_cannot_run_is_skipped_with_the_reason()
    {
        MediaRouteResult r = Route("realistic photo", "Standard", Files(AllStd), eligible: f => f != "photo_a.safetensors");
        Assert.NotEqual("photo_a.safetensors", r.ChosenFile);
        Assert.Contains(r.Ranked, c => c.Evaluated.Variant.Id == "photo-a-1" && c.ExcludedBecause!.Contains("not runnable"));
    }

    [Fact]
    public void Measured_speed_breaks_a_tie_between_otherwise_equal_models()
    {
        MediaCatalogue c = Catalogue(
            Family("twin-a", "image.generate", "\"photoreal\"", Variant("twin-a-1", "twin_a.safetensors", "standard", 3)),
            Family("twin-b", "image.generate", "\"photoreal\"", Variant("twin-b-1", "twin_b.safetensors", "standard", 3)));
        ISet<string> installed = Files("twin_a.safetensors", "twin_b.safetensors");
        Assert.Equal("twin_b.safetensors", Route("photo", "Standard", installed, speed: f => f == "twin_a.safetensors" ? 60 : 20, cat: c).ChosenFile);
        Assert.Equal("twin_a.safetensors", Route("photo", "Standard", installed, speed: f => f == "twin_a.safetensors" ? 10 : 20, cat: c).ChosenFile);
    }

    [Fact]
    public void Nothing_installed_keeps_the_workflow_default_with_a_clear_code_on_the_standard_lane()
    {
        MediaRouteResult r = Route("a red apple", "Standard", Files());
        Assert.Null(r.Chosen);
        Assert.Equal("no_installed_model", r.Code);
        Assert.Contains("workflow default", r.Reason);
    }

    // ---- determinism and explanation ----

    [Fact]
    public void Routing_is_deterministic_and_explains_itself()
    {
        ISet<string> installed = Files(AllStd);
        MediaRouteResult a = Route("an anime knight", "Standard", installed);
        MediaRouteResult b = Route("an anime knight", "Standard", installed);
        Assert.Equal(a.Trace, b.Trace);
        Assert.Equal(a.Reason, b.Reason);
        Assert.Contains("anime", a.Reason);
        Assert.Contains("fits your GPU", a.Reason);
        Assert.Contains(a.Trace, t => t.StartsWith("hint:anime:"));
        Assert.Contains(a.Trace, t => t.StartsWith("rank 1: anime-b-1"));
        Assert.Contains(a.Trace, t => t.StartsWith("skip: spicy-d-1"));
    }

    [Fact]
    public void The_trace_never_repeats_the_prompt_text()
    {
        MediaRouteResult r = Route("a secret project codename ZEBRA anime", "Standard", Files(AllStd));
        Assert.DoesNotContain(r.Trace.Concat(new[] { r.Reason }), t => t.Contains("ZEBRA", StringComparison.OrdinalIgnoreCase));
    }
}
