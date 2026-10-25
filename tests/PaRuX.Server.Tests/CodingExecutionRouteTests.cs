using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingExecutionRouteTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "parux-skill-router-" + Guid.NewGuid().ToString("N"));

    public CodingExecutionRouteTests() => Directory.CreateDirectory(_stateDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_stateDirectory, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("/code")]
    [InlineData("/code fix the failing test")]
    [InlineData("/CODE\trefactor this repository")]
    public void Slash_code_is_a_native_execution_route(string input)
    {
        Assert.Equal(RouteKind.CodeHandoff, RouterService.Classify(input));
    }

    // Live UI check: this exact request went to plain chat, which then claimed "npm test" passed
    // although nothing was edited or run.
    [Theory]
    [InlineData("Nel progetto \"C:\\Users\\ExampleUser\\Documents\\cart-demo\" correggi src/cart.mjs: subtotal deve moltiplicare prezzo per quantita. Verifica con npm test.")]
    [InlineData("correggi src/cart.mjs in modo che total applichi lo sconto percentuale")]
    [InlineData("Add input validation to Services/OrderService.cs and update the tests")]
    [InlineData("rinomina la funzione parse in parser.py")]
    [InlineData("fix the null check in web/src/app.js")]
    public void Change_request_aimed_at_a_repository_or_source_file_goes_to_the_coding_agent(string input)
    {
        Assert.Equal(RouteKind.CodeHandoff, RouterService.Classify(input));
    }

    [Theory]
    // Explanations and snippets stay conversational.
    [InlineData("spiegami cosa fa src/cart.mjs")]
    [InlineData("scrivimi una funzione che somma una lista")]
    [InlineData("come si aggiunge un elemento a una lista in Python?")]
    // A change verb without any repository/file target is not repository work.
    [InlineData("aggiungi un tocco di ironia alla risposta")]
    // Prose mentions of extensions that are also words (review finding).
    [InlineData("add Node.js to my CV")]
    [InlineData("update the example config.yaml in your answer")]
    public void Discussions_and_untargeted_requests_stay_in_chat(string input)
    {
        Assert.NotEqual(RouteKind.CodeHandoff, RouterService.Classify(input));
    }

    [Theory]
    [InlineData(RouteKind.ChatNormal)]
    [InlineData(RouteKind.ChatFast)]
    [InlineData(RouteKind.DeepReasoning)]
    public void Plain_chat_prompts_forbid_claiming_commands_were_run(RouteKind route)
    {
        string prompt = RouterService.SystemPrompt(route) ?? string.Empty;
        Assert.Contains("cannot run commands, execute tests, or edit files", prompt, StringComparison.Ordinal);
        Assert.Contains("/code", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Slash_code_keeps_execution_priority_even_when_an_attachment_exists()
    {
        Assert.Equal(RouteKind.CodeHandoff, RouterService.ClassifyWithAttachment("/code inspect and fix this repository"));
    }

    [Fact]
    public void Generic_skill_router_refuses_forced_legacy_coding_skill()
    {
        var registry = new SkillRegistryService(_stateDirectory);
        var router = new SkillRouter(registry);

        var result = router.Route("fix this bug", "coding");

        Assert.Null(result.Skill);
        Assert.Equal("none", result.Mode);
        Assert.Contains("native execution route", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_skill_router_never_auto_routes_to_legacy_coding_manifest()
    {
        var registry = new SkillRegistryService(_stateDirectory);
        var router = new SkillRouter(registry);

        var result = router.Route("fix this bug", forcedSkillId: null);

        Assert.NotEqual("coding", result.Skill?.Id);
    }
}
