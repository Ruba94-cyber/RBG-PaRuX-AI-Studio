using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDependencyEvidenceCompilerTests
{
    [Fact]
    public void Coding_result_keeps_review_evidence_but_not_unknown_path_fields()
    {
        string json = """
        {
          "summary":"Fixed the parser and added tests.",
          "stepsTaken":8,
          "goalReported":true,
          "branch":"parux/task-1",
          "diff":"diff --git a/a.cs b/a.cs\n+fixed",
          "worktree":"C:\\secret\\repo\\.parux-agents\\task-1"
        }
        """;

        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("task-1", "coding", "PASS", json)
        });

        Assert.Contains("Fixed the parser", result.PromptText, StringComparison.Ordinal);
        Assert.Contains("diff --git", result.PromptText, StringComparison.Ordinal);
        Assert.Contains("verification=PASS", result.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\secret", result.PromptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worktree:", result.PromptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multiple_dependencies_are_bounded_by_total_budget()
    {
        string large = "{\"response\":\"" + new string('x', 7900) + "\"}";
        var inputs = Enumerable.Range(0, 8)
            .Select(i => new AgentDependencyEvidenceCompiler.Input("task-" + i, "research", "PASS", large));

        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(inputs, 12_000);

        Assert.InRange(result.PromptText.Length, 1, 12_000);
        Assert.InRange(result.DependencyCount, 1, 2);
    }

    [Fact]
    public void Legacy_path_bearing_unstructured_result_is_withheld()
    {
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input(
                "legacy", "coding", "PASS", "worktree=C:\\Users\\name\\repo; completed successfully")
        });

        Assert.Contains("withheld", result.PromptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users\\name", result.PromptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_results_do_not_create_fake_dependency_evidence()
    {
        AgentDependencyEvidenceCompiler.Compiled result = AgentDependencyEvidenceCompiler.Compile(new[]
        {
            new AgentDependencyEvidenceCompiler.Input("task-a", "review", "PASS", null),
            new AgentDependencyEvidenceCompiler.Input("task-b", "testing", "PASS", "{}")
        });

        Assert.Equal(0, result.DependencyCount);
        Assert.Equal(string.Empty, result.PromptText);
    }
}
