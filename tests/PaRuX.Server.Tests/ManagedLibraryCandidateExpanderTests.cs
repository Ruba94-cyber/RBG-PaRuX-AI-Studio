using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedLibraryCandidateExpanderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-managed-auto-" + Guid.NewGuid().ToString("N"));

    public ManagedLibraryCandidateExpanderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Expand_adds_every_top_level_managed_text_gguf_without_duplicate_registration()
    {
        string registered = Path.Combine(_root, "Qwen-27B.gguf");
        string fourB = Path.Combine(_root, "Qwen-4B.gguf");
        string fourteenB = Path.Combine(_root, "Qwen-14B.gguf");
        File.WriteAllText(registered, "x");
        File.WriteAllText(fourB, "x");
        File.WriteAllText(fourteenB, "x");

        List<string> expanded = ManagedLibraryCandidateExpander.Expand(
            new List<string> { registered, registered }, _root);

        Assert.Equal(3, expanded.Count);
        Assert.Contains(expanded, path => Path.GetFileName(path) == "Qwen-4B.gguf");
        Assert.Contains(expanded, path => Path.GetFileName(path) == "Qwen-14B.gguf");
        Assert.Contains(expanded, path => Path.GetFileName(path) == "Qwen-27B.gguf");
    }

    [Fact]
    public void Expand_ignores_projectors_nested_files_and_non_gguf_files()
    {
        File.WriteAllText(Path.Combine(_root, "model.gguf"), "x");
        File.WriteAllText(Path.Combine(_root, "mmproj-model.gguf"), "x");
        File.WriteAllText(Path.Combine(_root, "vision-projector.gguf"), "x");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        string nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "nested-model.gguf"), "x");

        List<string> expanded = ManagedLibraryCandidateExpander.Expand(
            Array.Empty<string>(), _root);

        Assert.Single(expanded);
        Assert.Equal("model.gguf", Path.GetFileName(expanded[0]));
    }
}
