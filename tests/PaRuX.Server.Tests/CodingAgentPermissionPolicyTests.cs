using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentPermissionPolicyTests
{
    [Theory]
    [InlineData("replace_range")]
    [InlineData("write_file")]
    [InlineData("create_file")]
    [InlineData("delete_file")]
    [InlineData("rename_file")]
    [InlineData("run_command")]
    [InlineData("restore_packages")]
    [InlineData("browser_click")]
    [InlineData("browser_type")]
    [InlineData("browser_eval")]
    [InlineData("browser_reset")]
    [InlineData("unknown_action")]
    public void Plan_mode_refuses_mutating_and_unknown_actions(string action)
    {
        Assert.True(CodingAgentPermissionPolicy.IsRefusedInPlanMode(CodingAgentPermissionMode.Plan, action));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("git_diff")]
    [InlineData("browser_dom")]
    [InlineData("batch_read")]
    [InlineData("done")]
    public void Plan_mode_allows_known_read_only_actions(string action)
    {
        Assert.False(CodingAgentPermissionPolicy.IsRefusedInPlanMode(CodingAgentPermissionMode.Plan, action));
    }

    [Theory]
    [InlineData("git status", false)]
    [InlineData("git diff --stat", false)]
    [InlineData("dotnet --version", true)]
    [InlineData("git status && del important.txt", false)]
    [InlineData("git status | Set-Content marker.txt", false)]
    [InlineData("git status > result.txt", false)]
    [InlineData("git status; del important.txt", false)]
    [InlineData("powershell -Command Get-ChildItem", false)]
    [InlineData("type $(Get-ChildItem)", false)]
    [InlineData("dir %COMSPEC%", false)]
    [InlineData("echo (Get-ChildItem)", false)]
    [InlineData("dir /b", true)]
    [InlineData("dirtool.exe --write-output", false)]
    [InlineData("git\tdiff --output=result.txt", false)]
    [InlineData("type $env:PATH", false)]
    public void Plan_mode_shell_commands_require_a_single_allowlisted_read_only_command(string command, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed,
            CodingAgentPermissionPolicy.IsAllowedInPlanMode("run_command", command));
    }

    [Fact]
    public void Existing_default_and_unknown_modes_preserve_bypass_behavior()
    {
        Assert.Equal(CodingAgentPermissionMode.Bypass, CodingAgentPermissionMode.Normalize(null));
        Assert.Equal(CodingAgentPermissionMode.Bypass, CodingAgentPermissionMode.Normalize("unexpected"));
        Assert.False(CodingAgentPermissionPolicy.IsRefusedInPlanMode("unexpected", "write_file"));
    }

    [Fact]
    public void Command_approval_is_one_shot_and_bound_to_the_question_and_exact_command_text()
    {
        var checkpoint = new CodingAgentCheckpoint();
        const string command = "dotnet test Project.csproj --filter X";
        CodingAgentCommandApproval.Stage(checkpoint, "q-1", command);

        Assert.False(CodingAgentCommandApproval.ConsumeAnswer(checkpoint, "q-other",
            CodingAgentPermissionPolicy.ApproveExactCommandOnce));
        Assert.True(CodingAgentCommandApproval.ConsumeAnswer(checkpoint, "q-1",
            CodingAgentPermissionPolicy.ApproveExactCommandOnce));
        Assert.True(CodingAgentPermissionPolicy.IsExactCommandApproval(checkpoint.ApprovedCommandText, command));
        Assert.False(CodingAgentPermissionPolicy.IsExactCommandApproval(checkpoint.ApprovedCommandText, command + " "));
        Assert.False(CodingAgentPermissionPolicy.IsExactCommandApproval(checkpoint.ApprovedCommandText, command.ToUpperInvariant()));

        CodingAgentCommandApproval.ConsumeApproval(checkpoint);
        Assert.False(CodingAgentPermissionPolicy.IsExactCommandApproval(checkpoint.ApprovedCommandText, command));
    }

    [Fact]
    public void Decline_clears_pending_command_without_granting_approval()
    {
        var checkpoint = new CodingAgentCheckpoint();
        CodingAgentCommandApproval.Stage(checkpoint, "q-2", "dotnet test");

        Assert.True(CodingAgentCommandApproval.ConsumeAnswer(checkpoint, "q-2",
            CodingAgentPermissionPolicy.DeclineCommand));
        Assert.Empty(checkpoint.ApprovedCommandText);
        Assert.Empty(checkpoint.PendingCommandApprovalQuestionId);
        Assert.Empty(checkpoint.PendingCommandApprovalText);
    }

    [Fact]
    public void File_change_approval_is_one_shot_and_bound_to_exact_payload_and_question()
    {
        var checkpoint = new CodingAgentCheckpoint();
        string key = CodingAgentPermissionPolicy.FileMutationApprovalKey(
            "write_file", "src/App.cs", null, "class App {}", null, null);
        string differentKey = CodingAgentPermissionPolicy.FileMutationApprovalKey(
            "write_file", "src/App.cs", null, "class App { int Value; }", null, null);
        CodingAgentFileApproval.Stage(checkpoint, "q-write", key);

        Assert.False(CodingAgentFileApproval.ConsumeAnswer(checkpoint, "other-question",
            CodingAgentPermissionPolicy.ApproveExactFileChangeOnce));
        Assert.True(CodingAgentFileApproval.ConsumeAnswer(checkpoint, "q-write",
            CodingAgentPermissionPolicy.ApproveExactFileChangeOnce));
        Assert.True(CodingAgentFileApproval.IsApproved(checkpoint, key));
        Assert.False(CodingAgentFileApproval.IsApproved(checkpoint, differentKey));

        CodingAgentFileApproval.ConsumeApproval(checkpoint);
        Assert.False(CodingAgentFileApproval.IsApproved(checkpoint, key));
    }

    [Fact]
    public void File_change_decline_never_grants_the_staged_exact_payload()
    {
        var checkpoint = new CodingAgentCheckpoint();
        string key = CodingAgentPermissionPolicy.FileMutationApprovalKey(
            "write_file", "src/App.cs", null, "class App {}", null, null);
        CodingAgentFileApproval.Stage(checkpoint, "q-decline", key);

        Assert.True(CodingAgentFileApproval.ConsumeAnswer(checkpoint, "q-decline",
            CodingAgentPermissionPolicy.DeclineCommand));
        Assert.False(CodingAgentFileApproval.IsApproved(checkpoint, key));
        Assert.Empty(checkpoint.ApprovedFileApprovalKey);
        Assert.Empty(checkpoint.PendingFileApprovalQuestionId);
        Assert.Empty(checkpoint.PendingFileApprovalKey);
    }

    [Fact]
    public void File_change_approval_preview_shows_complete_content_or_refuses_oversized_payload()
    {
        string content = "class App {}";
        string preview = CodingAgentPermissionPolicy.FileMutationApprovalPreview(
            "write_file", "src/App.cs", null, content, null, null)!;
        Assert.Contains("src/App.cs", preview, StringComparison.Ordinal);
        Assert.Contains(content, preview, StringComparison.Ordinal);

        Assert.Null(CodingAgentPermissionPolicy.FileMutationApprovalPreview(
            "write_file", "src/App.cs", null,
            new string('x', CodingAgentPermissionPolicy.MaximumFileApprovalPreviewCharacters), null, null));
    }
}
