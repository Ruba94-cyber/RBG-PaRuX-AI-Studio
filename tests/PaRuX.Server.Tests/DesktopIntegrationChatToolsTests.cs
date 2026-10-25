using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live functional test finding: a folder connected in Settings after Core started was invisible
/// to Chat, because the tool registry held its own DesktopIntegrationService whose in-memory cache
/// had been loaded (empty) at startup. The registry must see the same state the Settings endpoint
/// changes, without a restart.
/// </summary>
public sealed class DesktopIntegrationChatToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-desktop-tools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_source_connected_after_startup_is_offered_to_chat_immediately()
    {
        string state = Path.Combine(_root, "state");
        string notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "cabin.md"), "# Cabin\nThe key is under the blue heron statue.");

        var desktop = new DesktopIntegrationService(state);
        await using var registry = new McpClientRegistry(state, knowledge: null, desktop, new DocumentWriteService(state));

        var before = await registry.GetToolSchemasAsync(CancellationToken.None);
        Assert.DoesNotContain(before, s => s?["function"]?["name"]?.GetValue<string>() == DesktopIntegrationTools.SearchToolName);

        desktop.Connect("notes-folder", notes);                                       // what Settings does

        var after = await registry.GetToolSchemasAsync(CancellationToken.None);
        Assert.Contains(after, s => s?["function"]?["name"]?.GetValue<string>() == DesktopIntegrationTools.SearchToolName);
        string result = await registry.CallToolAsync(DesktopIntegrationTools.SearchToolName,
            "{\"query\":\"cabin key\"}", CancellationToken.None,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DesktopIntegrationTools.SearchToolName });
        Assert.Contains("heron", result);
        Assert.Contains("cabin.md", result);                                          // answers can cite the file
    }

    [Theory]
    [InlineData("general")]
    [InlineData("rag")]
    [InlineData("memory-recall")]
    public async Task Everyday_chat_skills_are_offered_connected_sources_and_document_writing(string skillId)
    {
        // Live finding: "general" had allowedTools [], which the registry reads as "no tools", so
        // ordinary chat never saw connected notes/mail/calendar nor PaRuX's own document writer.
        string repo = FindRepoRoot();
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PaRuX.Contracts.SkillManifest>(
            File.ReadAllText(Path.Combine(repo, "skills", "built-in", skillId + ".json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        SkillPermissionPolicy.Validate(manifest);                                     // no privileged permission needed

        string state = Path.Combine(_root, skillId);
        string notes = Path.Combine(_root, skillId + "-notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "a.md"), "note");
        var desktop = new DesktopIntegrationService(state);
        desktop.Connect("notes-folder", notes);
        await using var registry = new McpClientRegistry(state, knowledge: null, desktop, new DocumentWriteService(state));

        var names = (await registry.GetToolSchemasAsync(CancellationToken.None, manifest.AllowedTools))
            .Select(s => s?["function"]?["name"]?.GetValue<string>()).ToList();
        Assert.Contains(DesktopIntegrationTools.SearchToolName, names);
        Assert.Contains(DesktopIntegrationTools.ReadToolName, names);
        Assert.Contains(DocumentWriteTools.WriteWordToolName, names);
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "skills", "built-in"))) return dir.FullName;
        throw new DirectoryNotFoundException("skills/built-in not found");
    }
}
