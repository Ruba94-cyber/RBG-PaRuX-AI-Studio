using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-contract pins for late closeout work whose real acceptance is Windows/hardware-bound.
/// These tests do not pretend to replace the physical run; they prevent a future refactor from
/// silently deleting the production wiring while stale status documentation still says complete.
/// </summary>
public sealed class SeptemberCloseoutHardeningContractTests
{
    [Fact]
    public void Windows_ml_worker_keeps_thinking_and_device_evidence_fail_closed()
    {
        string root = FindRepositoryRoot();
        string protocol = Read(root, "src", "PaRuX.Server", "WindowsMlGenAiProtocol.cs");
        string worker = Read(root, "src", "PaRuX.WinMlWorker", "WinMlGenAiServer.cs");
        string benchmark = Read(root, "src", "PaRuX.Server", "WindowsMlGenAiEngineBenchmarkAdapter.cs");

        Assert.Contains("WorkerApiVersion = 4", protocol, StringComparison.Ordinal);
        Assert.Contains("ValidateReadyHandshake", protocol, StringComparison.Ordinal);
        Assert.Contains("single-compatible-ep-device", protocol, StringComparison.Ordinal);
        // Thinking binding lives in ChatTemplateThinking.cs; the worker must delegate to it.
        string thinking = Read(root, "src", "PaRuX.WinMlWorker", "ChatTemplateThinking.cs");
        Assert.Contains("enable_thinking", thinking, StringComparison.Ordinal);
        Assert.Contains("ChatTemplateThinking.Bind", worker, StringComparison.Ordinal);
        Assert.Contains("GetEpDevices", worker, StringComparison.Ordinal);
        Assert.Contains("CompatibleDevices", worker, StringComparison.Ordinal);
        Assert.Contains("LongContextMinimumPromptTokens", benchmark, StringComparison.Ordinal);
        Assert.Contains("RunSustainedValidationAsync", benchmark, StringComparison.Ordinal);
        Assert.Contains("MissingTelemetry", benchmark, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_engine_race_keeps_exact_same_family_competitor_identity()
    {
        string root = FindRepositoryRoot();
        string race = Read(root, "src", "PaRuX.Server", "RuntimeEngineRaceService.cs");
        string managed = Read(root, "src", "PaRuX.Server", "ManagedRuntimeEngineRaceService.cs");

        Assert.Contains("CompetitionId", race, StringComparison.Ordinal);
        Assert.Contains("RegisteredCompetitorIds", race, StringComparison.Ordinal);
        Assert.Contains("Multiple competitor identities", race, StringComparison.Ordinal);
        Assert.Contains("sameEngineFamily", managed, StringComparison.Ordinal);
        Assert.Contains("Pin both identities", managed, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_intelligence_keeps_native_languages_topology_history_and_batched_incremental_sync()
    {
        string root = FindRepositoryRoot();
        string repoIndex = Read(root, "src", "PaRuX.Server", "ContextGraph", "RepoIndexService.cs");
        string semanticPath = Path.Combine(root, "src", "PaRuX.Server", "ContextGraph", "SemanticIndexV2Service.cs");
        string semantic = File.ReadAllText(semanticPath);

        foreach (string extension in new[] { ".c", ".h", ".cpp", ".hpp", ".rs" })
        {
            Assert.Contains("\"" + extension + "\"", repoIndex, StringComparison.Ordinal);
            Assert.Contains("\"" + extension + "\"", semantic, StringComparison.Ordinal);
        }
        Assert.Contains("CppFunctionDeclaration", repoIndex, StringComparison.Ordinal);
        Assert.Contains("RustDeclaration", repoIndex, StringComparison.Ordinal);
        Assert.Contains("RelationDependsOn = \"DEPENDS_ON\"", repoIndex, StringComparison.Ordinal);
        Assert.Contains("RelationCalls = \"CALLS\"", repoIndex, StringComparison.Ordinal);
        Assert.Contains("ProbeGitHistory", repoIndex, StringComparison.Ordinal);
        Assert.Contains("HistoryBoost", repoIndex, StringComparison.Ordinal);
        Assert.Contains("_index.ReindexFiles(root, graphChangedPaths.Where(current.ContainsKey), graphRemoved, cancellationToken)", semantic, StringComparison.Ordinal);
        Assert.DoesNotContain("_index.ReindexFile(root, path)", semantic, StringComparison.Ordinal);
    }

    [Fact]
    public void Vs_code_hunk_decisions_are_explicit_human_git_actions()
    {
        string root = FindRepositoryRoot();
        string package = Read(root, "integrations", "vscode", "package.json");
        string main = Read(root, "integrations", "vscode", "main.js");
        string hunk = Read(root, "integrations", "vscode", "hunk-review.js");

        Assert.Contains("parux.acceptHunk", package, StringComparison.Ordinal);
        Assert.Contains("parux.rejectHunk", package, StringComparison.Ordinal);
        Assert.Contains("./main.js", package, StringComparison.Ordinal);
        Assert.Contains("registerHunkReviewCommands", main, StringComparison.Ordinal);
        Assert.Contains("git.stageSelectedRanges", hunk, StringComparison.Ordinal);
        Assert.Contains("git.revertSelectedRanges", hunk, StringComparison.Ordinal);
        Assert.DoesNotContain("createCodingTask", hunk, StringComparison.Ordinal);
    }

    [Fact]
    public void Competitive_benchmarks_withhold_wins_when_evidence_is_weak()
    {
        string root = FindRepositoryRoot();
        string inference = Read(root, "scripts", "benchmark-competitive-inference.ps1");
        string reference = Read(root, "scripts", "benchmark-reference-stream.ps1");
        string agent = Read(root, "scripts", "benchmark-coding-agent-competitors.ps1");
        string corpus = Read(root, "benchmarks", "coding-agent", "parux-corpus.json");

        Assert.Contains("inconclusiveReason", inference, StringComparison.Ordinal);
        Assert.DoesNotContain("char4-estimate", inference, StringComparison.Ordinal);
        Assert.Contains("finish_reason=length", reference, StringComparison.Ordinal);
        Assert.Contains("sameIndependentVerifier", agent, StringComparison.Ordinal);
        Assert.Contains("Invoke-IndependentVerification", agent, StringComparison.Ordinal);
        Assert.Contains("verificationCommands", corpus, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversation_branch_switch_keeps_previous_next_and_durable_failure_boundary()
    {
        string root = FindRepositoryRoot();
        string history = Read(root, "src", "PaRuX.App", "Services", "HistoryStore.cs");
        string ui = Read(root, "src", "PaRuX.App", "Views", "ChatView.ConversationBranches.cs");

        Assert.Contains("public static bool Save", history, StringComparison.Ordinal);
        Assert.Contains("if (!Save(candidate)) return false", history, StringComparison.Ordinal);
        Assert.Contains("NavigateConversationBranchAsync(-1)", ui, StringComparison.Ordinal);
        Assert.Contains("NavigateConversationBranchAsync(1)", ui, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt", ui, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(parts.Aggregate(root, Path.Combine));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
