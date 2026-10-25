using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDependencyEvidenceCompilerCharacterizationTests
{
    [Fact]
    public void Null_dependencies_return_empty_compiled()
    {
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(null);

        Assert.Equal(string.Empty, result.PromptText);
        Assert.Equal(0, result.DependencyCount);
        Assert.Equal(0, result.UsedCharacters);
    }

    [Fact]
    public void Whitespace_id_is_skipped()
    {
        string json = """{"summary":"some evidence"}""";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("   ", "coding", "PASS", json),
        });

        Assert.Equal(0, result.DependencyCount);
        Assert.Equal(string.Empty, result.PromptText);
    }

    [Fact]
    public void Maximum_characters_is_clamped_to_lower_bound()
    {
        string json = """{"summary":"hello world"}""";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(
            new[] { new AgentDependencyEvidenceCompiler.Input("t1", null, null, json) },
            maximumCharacters: 0);

        // Clamped to 1_000; evidence must still fit.
        Assert.True(result.UsedCharacters <= 1_000);
        Assert.Equal(1, result.DependencyCount);
    }

    [Fact]
    public void Maximum_characters_is_clamped_to_upper_bound()
    {
        string json = """{"response":"abc"}""";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(
            new[] { new AgentDependencyEvidenceCompiler.Input("t1", "r", "PASS", json) },
            maximumCharacters: 99_999);

        Assert.True(result.UsedCharacters <= 24_000);
    }

    [Fact]
    public void Unknown_role_and_missing_verification_appear_as_unknown()
    {
        string json = """{"summary":"x"}""";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("t1", null, null, json),
        });

        Assert.Contains("role=unknown", result.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("verification=", result.PromptText, StringComparison.Ordinal);
        Assert.Contains("[DEPENDENCY RESULT t1", result.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_string_json_root_is_carried_as_evidence()
    {
        string json = "\"just a plain string result\"";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("t1", "coding", "PASS", json),
        });

        Assert.Equal(1, result.DependencyCount);
        Assert.Contains("just a plain string result", result.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_unstructured_result_without_path_keywords_is_carried_bounded()
    {
        string legacy = "all done, tests green";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("t1", "coding", null, legacy),
        });

        Assert.Equal(1, result.DependencyCount);
        Assert.Contains("tests green", result.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("withheld", result.PromptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Used_characters_matches_prompt_text_length()
    {
        string json = """{"summary":"a","diff":"b"}""";
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("t1", "coding", "PASS", json),
            new AgentDependencyEvidenceCompiler.Input("t2", "review", "FAIL", """{"summary":"c"}"""),
        });

        Assert.Equal(result.PromptText.Length, result.UsedCharacters);
        Assert.Equal(2, result.DependencyCount);
    }
}
