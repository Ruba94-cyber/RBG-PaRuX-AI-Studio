using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

// Bypass review of the run_command git refusal (9e916eb4). The sandbox writes the command text
// into a .cmd script and runs it with cmd.exe, so every spelling below reaches git when it is
// executed. A failing case is a confirmed bypass of the policy.
public sealed class CodingGitRefusalBypassTests
{
    private const string GitRefusal =
        "Git commands cannot run inside the AppContainer sandbox. Use the dedicated git_status, git_diff, or git_log actions instead.";

    public static IEnumerable<object[]> GitInvocationsInAnyCommandPosition()
    {
        // Chained or piped behind a read-only prefix that the classifier accepts.
        yield return new object[] { "echo ok && git status" };
        yield return new object[] { "dotnet --info & git log" };
        yield return new object[] { "echo ok | git status" };
        yield return new object[] { "echo ok && git -C . reset --hard" };
        // Nested interpreters.
        yield return new object[] { "cmd /c git status" };
        yield return new object[] { "cmd /d /c git status" };
        yield return new object[] { "powershell -NoProfile -Command \"git status\"" };
        yield return new object[] { "pwsh -c git log" };
        // cmd.exe builtins and FOR loops.
        yield return new object[] { "call git status" };
        yield return new object[] { "start \"\" git status" };
        yield return new object[] { "for %%g in (git) do %%g status" };
        // Quote, caret and trailing-dot spellings, and wrapper extensions.
        yield return new object[] { "g\"i\"t status" };
        yield return new object[] { "gi^t status" };
        yield return new object[] { "git.exe. status" };
        yield return new object[] { "git.cmd status" };
        // Environment-variable interpreter and PowerShell call operator.
        yield return new object[] { "%ComSpec% /c git status" };
        yield return new object[] { "& git status" };
        // Path spellings that already pass; they pin the forward-slash and single-quote branches.
        yield return new object[] { "C:/Tools/Git/cmd/git.exe status" };
        yield return new object[] { "'C:\\Tools\\Git\\cmd\\git.exe' status" };
    }

    [Theory]
    [MemberData(nameof(GitInvocationsInAnyCommandPosition))]
    public void Git_invocations_in_any_command_position_are_refused(string command)
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);

        Assert.Equal(CodingCommandPreflight.Classification.Denied, review.Kind);
        Assert.Equal(GitRefusal, review.Reason);
        Assert.Equal(GitRefusal, CodingAgentTools.DenyReasonFor(command));
    }

    public static IEnumerable<object[]> GitLookalikesThatStayAllowed()
    {
        yield return new object[] { "gitk status" };
        yield return new object[] { "gitk\t--help" };
        yield return new object[] { "D:\\fixtures\\gitk.exe status" };
        yield return new object[] { "git.exe.bak status" };
        yield return new object[] { "dotnet test --filter FullyQualifiedName~ComputerUse" };
    }

    [Theory]
    [MemberData(nameof(GitLookalikesThatStayAllowed))]
    public void Non_git_programs_that_resemble_git_are_not_refused(string command)
    {
        Assert.NotEqual(CodingCommandPreflight.Classification.Denied, CodingCommandPreflight.Analyze(command).Kind);
        Assert.Null(CodingAgentTools.DenyReasonFor(command));
    }
}
