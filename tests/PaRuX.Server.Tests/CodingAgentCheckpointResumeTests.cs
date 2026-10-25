using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentCheckpointResumeTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "parux-checkpoint-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspaceA = Path.Combine(Path.GetTempPath(), "parux-checkpoint-workspace-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspaceB = Path.Combine(Path.GetTempPath(), "parux-checkpoint-workspace-b-" + Guid.NewGuid().ToString("N"));

    public CodingAgentCheckpointResumeTests()
    {
        Directory.CreateDirectory(_stateDirectory);
        Directory.CreateDirectory(_workspaceA);
        Directory.CreateDirectory(_workspaceB);
    }

    public void Dispose()
    {
        foreach (string path in new[] { _stateDirectory, _workspaceA, _workspaceB })
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Direct_derived_session_keeps_task_text_as_part_of_identity()
    {
        const string originalTask = "Fix the parser.";
        string session = CodingAgentCheckpointStore.ResolveSessionId(_workspaceA, originalTask, explicitSessionId: null);
        CodingAgentCheckpointStore.SaveToDirectory(_stateDirectory, new CodingAgentCheckpoint
        {
            SessionId = session,
            WorkspaceRoot = _workspaceA,
            Task = originalTask,
            StepsTaken = 3,
            Observations = new List<string> { "read parser.cs" }
        });

        CodingAgentCheckpoint? changed = CodingAgentCheckpointStore.LoadFromDirectory(
            _stateDirectory, session, _workspaceA, "Fix the parser and change public API.");

        Assert.Null(changed);
    }

    [Fact]
    public void Durable_orchestrator_session_resumes_when_effective_prompt_evolves()
    {
        const string session = "task-6e867b3215194afe8e4b470ae1cc7af7";
        const string originalPrompt = "Fix the parser.\n\nREPOSITORY EVIDENCE:\nold context";
        const string refreshedPrompt = "Fix the parser.\n\nREPOSITORY EVIDENCE:\nnew context\n\nVERIFIER REPAIR FEEDBACK:\nedge case failed";
        CodingAgentCheckpointStore.SaveToDirectory(_stateDirectory, new CodingAgentCheckpoint
        {
            SessionId = session,
            WorkspaceRoot = _workspaceA,
            Task = originalPrompt,
            StepsTaken = 17,
            VerificationRequired = true,
            Observations = new List<string> { "modified parser.cs", "test failed: edge case" }
        });

        CodingAgentCheckpoint? restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _stateDirectory, session, _workspaceA, refreshedPrompt);

        Assert.NotNull(restored);
        Assert.Equal(17, restored!.StepsTaken);
        Assert.True(restored.VerificationRequired);
        Assert.Contains("modified parser.cs", restored.Observations);
        Assert.Equal(refreshedPrompt, restored.Task);
    }

    [Fact]
    public void Durable_session_never_crosses_workspace_authority()
    {
        const string session = "task-8fe5d8012a0b45bb96af927f7de692bc";
        CodingAgentCheckpointStore.SaveToDirectory(_stateDirectory, new CodingAgentCheckpoint
        {
            SessionId = session,
            WorkspaceRoot = _workspaceA,
            Task = "Implement feature.",
            StepsTaken = 4
        });

        CodingAgentCheckpoint? restored = CodingAgentCheckpointStore.LoadFromDirectory(
            _stateDirectory, session, _workspaceB, "Implement feature with refreshed evidence.");

        Assert.Null(restored);
    }

    [Fact]
    public void Checkpoint_file_cannot_claim_a_different_session_id()
    {
        const string session = "task-authoritative";
        CodingAgentCheckpointStore.SaveToDirectory(_stateDirectory, new CodingAgentCheckpoint
        {
            SessionId = session,
            WorkspaceRoot = _workspaceA,
            Task = "Original",
            StepsTaken = 2
        });

        string path = CodingAgentCheckpointStore.PathFor(_stateDirectory, session);
        string json = File.ReadAllText(path).Replace("task-authoritative", "task-impersonated", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        // The impersonating file is never reused, and it is not overwritten either: it is preserved and the run stops.
        var error = Assert.Throws<CodingAgentCheckpointCorruptException>(() =>
            CodingAgentCheckpointStore.LoadFromDirectory(_stateDirectory, session, _workspaceA, "Refreshed"));
        Assert.NotNull(error.QuarantinePath);
        Assert.Contains("task-impersonated", File.ReadAllText(error.QuarantinePath!), StringComparison.Ordinal);
    }
}
