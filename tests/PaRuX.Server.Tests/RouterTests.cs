using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RouterTests
{
    [Theory]
    [InlineData("4+4", RouteKind.ChatFast)]
    [InlineData("Spiegami una VLAN", RouteKind.ChatNormal)]
    [InlineData("Analizza in profondità questa architettura", RouteKind.DeepReasoning)]
    [InlineData("Decompila questo DLL e spiegami le funzioni", RouteKind.BinaryAnalysis)]
    [InlineData("Analizza questo exe e le sue sezioni PE", RouteKind.BinaryAnalysis)]
    [InlineData("Reverse engineer this ELF binary", RouteKind.BinaryAnalysis)]
    [InlineData("Disassemble the firmware binary", RouteKind.BinaryAnalysis)]
    [InlineData("Apri questa app e configurala", RouteKind.ComputerUse)]
    [InlineData("Apri Blocco note", RouteKind.ComputerUse)]
    [InlineData("Apri note", RouteKind.ComputerUse)]
    [InlineData("Avvia la calcolatrice", RouteKind.ComputerUse)]
    [InlineData("Open Notepad", RouteKind.ComputerUse)]
    [InlineData("Vai su openai.com", RouteKind.ComputerUse)]
    [InlineData("Navigate to https://example.com/docs", RouteKind.ComputerUse)]
    [InlineData("Per favore apri Blocco note", RouteKind.ComputerUse)]
    [InlineData("Can you open Notepad?", RouteKind.ComputerUse)]
    [InlineData("Take a screenshot of my desktop", RouteKind.ComputerUse)]
    // Live functional-test regression: a mid-sentence "launch"/"start"/"run" is not an app request.
    [InlineData("Create a Word document titled Meeting Notes with three bullet points: budget approved, launch on 15 November, next review in December.", RouteKind.ChatNormal)]
    [InlineData("What is the start date of the Q3 project plan we discussed?", RouteKind.ChatNormal)]
    [InlineData("Summarize why teams run out of time before a launch deadline and what helps", RouteKind.ChatNormal)]
    [InlineData("Genera un'immagine", RouteKind.Image)]
    [InlineData("Rigenera questa immagine", RouteKind.ImageEdit)]
    [InlineData("Rigenera questa immagine e avvia davvero la rigenerazione", RouteKind.ImageEdit)]
    [InlineData("Genera un video di 30 secondi", RouteKind.Video)]
    [InlineData("Creiamo una piccola app Windows", RouteKind.CodeHandoff)]
    [InlineData("Crea una nuova applicazione", RouteKind.CodeHandoff)]
    [InlineData("Migliora il codice di questa repository", RouteKind.CodeHandoff)]
    [InlineData("Ottimizza questo progetto", RouteKind.CodeHandoff)]
    [InlineData("Improve this codebase", RouteKind.CodeHandoff)]
    [InlineData("Migliora l'accessibilità del navigatore delle versioni", RouteKind.CodeHandoff)]
    [InlineData("Implementa i test della UI", RouteKind.CodeHandoff)]
    [InlineData("Fix the conversation navigator", RouteKind.CodeHandoff)]
    [InlineData("Fai i test su un app, correggi eventuali bug e utilizza l'ui", RouteKind.CodeHandoff)]
    [InlineData("Fai test sia di UI e UX sia di codice", RouteKind.CodeHandoff)]
    [InlineData("Correggi eventuali bug", RouteKind.CodeHandoff)]
    [InlineData("Esegui i test sul progetto", RouteKind.CodeHandoff)]
    [InlineData("Test this app and fix bugs", RouteKind.CodeHandoff)]
    [InlineData("Spiegami come migliorare il codice", RouteKind.ChatNormal)]
    [InlineData("fai i vari benchmark e i vari test", RouteKind.ChatNormal)]
    [InlineData("fai i benchmark e i test", RouteKind.ChatNormal)]
    [InlineData("Scrivi una funzione in Python per calcolare la distanza di Levenshtein", RouteKind.ChatNormal)]
    [InlineData("Come posso correggere questo bug nel mio snippet?", RouteKind.ChatNormal)]
    [InlineData("Qual è il bug in questa funzione?", RouteKind.ChatNormal)]
    [InlineData("/code crea una classe Helper", RouteKind.CodeHandoff)]
    public void Canonical_intents_are_routed_without_a_model_call(string input, RouteKind expected) =>
        Assert.Equal(expected, RouterService.Classify(input));

    [Theory]
    [InlineData("Rigenera questa immagine", RouteKind.ImageEdit)]
    [InlineData("Rigenerare con una bocca più naturale", RouteKind.ImageEdit)]
    [InlineData("Regenerate this image", RouteKind.ImageEdit)]
    public void Explicit_regeneration_with_an_attachment_is_a_real_image_job(string input, RouteKind expected) =>
        Assert.Equal(expected, RouterService.ClassifyWithAttachment(input));

    [Theory]
    [InlineData("Analizza questo EXE")]
    [InlineData("Decompile this DLL")]
    [InlineData("Reverse engineering del firmware")]
    public void Binary_analysis_intent_survives_attachment_routing(string input) =>
        Assert.Equal(RouteKind.BinaryAnalysis, RouterService.ClassifyWithAttachment(input));

    [Theory]
    [InlineData("")]
    [InlineData("Descrivi questa immagine")]
    [InlineData("Che cosa c'è nella foto?")]
    public void Ordinary_visual_chat_uses_the_delegated_route(string input)
    {
        RouteKind route = RouterService.ClassifyWithAttachment(input);
        Assert.Equal(RouteKind.VisionDelegated, route);
        Assert.True(RouterService.IsChatRoute(route));
        Assert.Equal("Vision", RouterService.Label(route));
        Assert.Contains("vision-subagent evidence", RouterService.SystemPrompt(route)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_vision_route_remains_distinct_for_paths_that_require_pixel_capability()
    {
        Assert.NotEqual(RouteKind.Vision, RouteKind.VisionDelegated);
        Assert.True(RouterService.IsChatRoute(RouteKind.Vision));
        Assert.Contains("attached image", RouterService.SystemPrompt(RouteKind.Vision)!, StringComparison.OrdinalIgnoreCase);
    }

}
