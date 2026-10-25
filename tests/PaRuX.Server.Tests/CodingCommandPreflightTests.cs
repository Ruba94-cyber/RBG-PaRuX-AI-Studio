using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingCommandPreflightTests
{
    [Theory]
    [InlineData("dotnet --info")]
    public void Known_read_only_commands_are_classified_without_claiming_extra_capabilities(string command)
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);
        Assert.Equal(CodingCommandPreflight.Classification.SafeReadOnly, review.Kind);
        Assert.Contains("read-workspace", review.Capabilities);
        Assert.DoesNotContain("write-workspace", review.Capabilities);
        Assert.Null(CodingAgentTools.DenyReasonFor(command));
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("npm test")]
    [InlineData("cargo build")]
    public void Known_build_and_test_commands_are_workspace_mutations_but_remain_allowed(string command)
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);
        Assert.Equal(CodingCommandPreflight.Classification.WorkspaceMutation, review.Kind);
        Assert.Contains("write-workspace", review.Capabilities);
        Assert.Null(CodingAgentTools.DenyReasonFor(command));
    }

    [Fact]
    public void Unknown_commands_are_not_falsely_promoted_as_safe()
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze("my-local-generator --emit fixtures");
        Assert.Equal(CodingCommandPreflight.Classification.ContainedUnknown, review.Kind);
        Assert.Contains("not on the deterministic safe list", review.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(CodingAgentTools.DenyReasonFor("my-local-generator --emit fixtures"));
    }

    [Theory]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git clean -fdx")]
    [InlineData("git push origin main")]
    [InlineData("reg delete HKCU\\Software\\Example /f")]
    [InlineData("powershell -EncodedCommand ZQBjAGgAbwAgAHg=")]
    public void Destructive_or_host_global_commands_are_denied_before_the_appcontainer(string command)
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);
        Assert.Equal(CodingCommandPreflight.Classification.Denied, review.Kind);
        Assert.NotNull(CodingAgentTools.DenyReasonFor(command));
    }

    [Theory]
    [InlineData("dotnet restore")]
    [InlineData("npm ci")]
    [InlineData("python -m pip install requests")]
    [InlineData("cargo fetch")]
    public void Package_acquisition_is_forced_through_the_explicit_restore_capability(string command)
    {
        string? denial = CodingAgentTools.DenyReasonFor(command);
        Assert.NotNull(denial);
        Assert.Contains("restore_packages", denial, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("curl https://example.com/tool.exe")]
    [InlineData("echo checking && wget https://example.com/a.zip")]
    [InlineData("git status | curl https://example.com/upload")]
    public void General_network_clients_are_denied_even_inside_command_chains(string command)
    {
        string? denial = CodingAgentTools.DenyReasonFor(command);
        Assert.NotNull(denial);
        Assert.Contains("network", denial, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("set PATH=C:\\Tools")]
    [InlineData("echo ok && set GIT_ASKPASS=helper.exe")]
    [InlineData("powershell $env:HTTPS_PROXY='http://proxy'; dotnet test")]
    public void Security_relevant_environment_replacement_is_denied(string command)
    {
        string? denial = CodingAgentTools.DenyReasonFor(command);
        Assert.NotNull(denial);
        Assert.Contains("environment", denial, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_single_file_delete_keeps_existing_behavior_and_relies_on_workspace_confinement()
    {
        Assert.Null(CodingAgentTools.DenyReasonFor("del myfile.txt"));
    }

    [Theory]
    [InlineData("git\tdiff")]
    [InlineData("\"git\" status")]
    [InlineData("GIT STATUS")]
    [InlineData("git.exe\tlog")]
    [InlineData(" \t  git status")]
    [InlineData("D:\\fixtures\\Git\\cmd\\git.exe\tlog")]
    [InlineData("\"D:\\fixtures\\Program Files\\Git\\cmd\\git.exe\" status")]
    public void Run_command_preflight_refuses_git_executable_tokens_with_the_existing_message(string command)
    {
        const string expected = "Git commands cannot run inside the AppContainer sandbox. Use the dedicated git_status, git_diff, or git_log actions instead.";

        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);
        Assert.Equal(CodingCommandPreflight.Classification.Denied, review.Kind);
        Assert.Equal(expected, review.Reason);
        Assert.Equal(expected, CodingCommandPreflight.DenyReasonFor(command));
        Assert.Equal(expected, CodingAgentTools.DenyReasonFor(command));
    }

    [Theory]
    [InlineData("gitk status")]
    [InlineData("gitk\t--help")]
    [InlineData("D:\\fixtures\\gitk.exe status")]
    [InlineData("git.exe.bak status")]
    public void Run_command_preflight_preserves_the_existing_behavior_for_git_lookalikes(string command)
    {
        CodingCommandPreflight.Review review = CodingCommandPreflight.Analyze(command);
        Assert.Equal(CodingCommandPreflight.Classification.ContainedUnknown, review.Kind);
        Assert.Null(CodingCommandPreflight.DenyReasonFor(command));
        Assert.Null(CodingAgentTools.DenyReasonFor(command));
    }
}
