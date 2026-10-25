using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>CodingAgentService is not compiled into this project; guard its BIC-09 wiring from source.</summary>
public sealed class CodingAgentRuntimeWiringSentinelTests
{
    private static string Service()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "src", "PaRuX.App", "Services", "CodingAgentService.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("CodingAgentService.cs");
    }

    [Fact]
    public void Service_measures_phases_and_emits_timing_lines_in_a_finally()
    {
        string s = Service();
        Assert.Contains("new CodingAgentPhaseTimer()", s);
        Assert.Contains("phaseTimer.BeginStep(step)", s);
        Assert.Contains("CodingAgentPhase.FirstToken", s);
        Assert.Contains("CodingAgentPhaseTimer.PhaseForAction(action.Action)", s);
        int total = s.IndexOf("phaseTimer.RenderRunTotal()", StringComparison.Ordinal);
        Assert.True(total > 0);
        Assert.Contains("finally", s.Substring(Math.Max(0, total - 400), 400));
    }

    // The action schema enumeration lives in the prompt builder; the dispatch and validator checks stay in the service.
    private static string Builder()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "src", "PaRuX.App", "Services", "CodingAgentPromptBuilder.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("CodingAgentPromptBuilder.cs");
    }

    [Fact]
    public void Batch_read_is_in_schema_validation_dispatch_and_repeat_guard()
    {
        string s = Service();
        Assert.Contains("\\\"batch_read\\\",\\\"done\\\"", Builder());
        Assert.Contains("case \"batch_read\":", s);
        Assert.Contains("CodingAgentReadBatch.TryValidate(action.Requests", s);
        Assert.Contains("CodingAgentReadBatch.ExecuteAsync(", s);
        Assert.Contains("\"batch_read\" => action.Requests == null", s);
    }

    [Fact]
    public void Replace_range_credits_progress_and_mutation_only_when_the_applied_file_content_changes()
    {
        string s = Service();
        int start = s.IndexOf("case \"replace_range\":", StringComparison.Ordinal);
        int end = s.IndexOf("case \"create_file\":", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string dispatch = s.Substring(start, end - start);

        Assert.Contains("CodingAgentTools.TryApplyReplaceRangeFileAsync(full, action.OldText, action.NewText, ct)", dispatch);
        int failed = dispatch.IndexOf("if (!success)", StringComparison.Ordinal);
        int unchanged = dispatch.IndexOf("if (!changed)", StringComparison.Ordinal);
        int mutationCredit = dispatch.IndexOf("IsTestableCodePath(action.Path), progress: true", StringComparison.Ordinal);
        Assert.True(failed >= 0 && unchanged > failed && mutationCredit > unchanged,
            "No-op content must return before mutation/progress is credited.");

        string noOpOutcome = dispatch.Substring(unchanged,
            dispatch.IndexOf("// A tolerated near-miss", unchanged, StringComparison.Ordinal) - unchanged);
        Assert.DoesNotContain("IsTestableCodePath(action.Path)", noOpOutcome);
        Assert.DoesNotContain("progress: true", noOpOutcome);
    }
}
