using System.Diagnostics;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRepositoryReadToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-repo-read-" + Guid.NewGuid().ToString("N"));

    public AgentRepositoryReadToolsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "alpha.cs"),
            "line1\nneedle-ALPHA\nline3\nneedle-beta\nline5");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "ignored.js"), "needle-ALPHA");
    }

    [Fact]
    public void List_and_range_read_are_bounded_and_relative()
    {
        IReadOnlyList<string> root = AgentRepositoryReadTools.List(_root);
        AgentRepositoryReadTools.ReadResult? page = AgentRepositoryReadTools.Read(
            _root, "src/alpha.cs", offset: 6, count: 12);

        Assert.Contains("src/", root);
        Assert.DoesNotContain("node_modules/", root);
        Assert.NotNull(page);
        Assert.Equal("src/alpha.cs", page!.RelativePath);
        Assert.Equal(6, page.Offset);
        Assert.True(page.TotalCharacters > page.ReturnedCharacters);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Grep_returns_line_numbers_and_prunes_noise_directories()
    {
        IReadOnlyList<AgentRepositoryReadTools.GrepMatch> matches = AgentRepositoryReadTools.Grep(
            _root, "needle", regex: false);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("src/alpha.cs", match.RelativePath));
        Assert.Equal(new[] { 2, 4 }, matches.Select(match => match.LineNumber));
    }

    [Fact]
    public void Regex_grep_is_supported_without_exposing_write_authority()
    {
        IReadOnlyList<AgentRepositoryReadTools.GrepMatch> matches = AgentRepositoryReadTools.Grep(
            _root, "needle-(ALPHA|beta)", regex: true);

        Assert.Equal(2, matches.Count);
        Assert.Equal("line1\nneedle-ALPHA\nline3\nneedle-beta\nline5",
            File.ReadAllText(Path.Combine(_root, "src", "alpha.cs")));
    }

    [Fact]
    public void Traversal_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AgentRepositoryReadTools.Read(_root, "../escape.txt"));
    }

    [Fact]
    public async Task Whole_tree_git_diff_excludes_tracked_sensitive_path_categories()
    {
        Git("init");
        Git("config", "user.name", "PaRuX Tests");
        Git("config", "user.email", "tests@parux.invalid");

        string[] paths =
        {
            ".gnupg/private.txt", ".azure/profile.txt", ".kube/config", ".docker/auth.txt", ".ssh/id_rsa",
            ".aws/credentials", "state.tfstate", "state.tfstate.backup", ".git-credentials", ".env",
            ".env.production", ".npmrc", ".pypirc", ".netrc", ".htpasswd", "id_rsa", "id_dsa",
            "id_ecdsa", "id_ed25519", "private.pem", "private.key", "private.ppk", "vault.kdbx",
            "signing.gpg", "signing.asc", "cert.pfx", "cert.p12", "trust.jks", "trust.keystore",
            "credentials", "credentials.json", "credentials.yaml", "secrets.yml", "secrets.json",
            "appsettings.secrets.json", "kubeconfig", "service-account.json", "ordinary.txt"
        };
        foreach (string path in paths)
        {
            string full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "fixture-only-before");
        }
        Git("add", ".");
        Git("commit", "-m", "tracked fixture baseline");
        foreach (string path in paths)
            File.WriteAllText(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)), "fixture-only-secret-canary");

        string diff = await AgentRepositoryReadTools.GitDiffAsync(_root, null, CancellationToken.None);

        Assert.Contains("ordinary.txt", diff, StringComparison.Ordinal);
        Assert.Contains("fixture-only-secret-canary", diff, StringComparison.Ordinal);
        foreach (string path in paths.Where(path => path != "ordinary.txt"))
            Assert.DoesNotContain(path, diff, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Directory_scoped_diff_does_not_echo_tracked_secrets_beneath_it()
    {
        Git("init");
        Git("config", "user.name", "PaRuX Tests");
        Git("config", "user.email", "tests@parux.invalid");
        Directory.CreateDirectory(Path.Combine(_root, "cfg"));
        File.WriteAllText(Path.Combine(_root, "cfg", ".env"), "before");
        File.WriteAllText(Path.Combine(_root, "cfg", "prod.env"), "before");
        File.WriteAllText(Path.Combine(_root, "cfg", "app.txt"), "before");
        Git("add", ".");
        Git("commit", "-m", "cfg baseline");
        foreach (string name in new[] { ".env", "prod.env", "app.txt" })
            File.WriteAllText(Path.Combine(_root, "cfg", name), "fixture-only-secret-canary-" + name);

        string diff = await AgentRepositoryReadTools.GitDiffAsync(_root, "cfg", CancellationToken.None);

        Assert.Contains("app.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-.env", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-prod.env", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whole_tree_git_diff_does_not_run_configured_textconv_driver()
    {
        string repo = CreateTextconvRepository("server-diff");

        string diff = await AgentRepositoryReadTools.GitDiffAsync(repo, null, CancellationToken.None);

        Assert.Contains("tracked-current", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXTCONV_MARKER", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_scoped_git_diff_does_not_run_configured_textconv_driver()
    {
        string repo = CreateTextconvRepository("scoped-server-diff");

        string diff = await AgentRepositoryReadTools.GitDiffAsync(repo, "tracked.txt", CancellationToken.None);

        Assert.Contains("tracked-current", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXTCONV_MARKER", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coding_agent_diff_does_not_run_configured_textconv_driver_for_tracked_or_untracked_files()
    {
        string repo = CreateTextconvRepository("coding-agent-diff");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "untracked-current\n");

        string diff = await PaRuX.Services.CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(
            repo, CancellationToken.None);

        Assert.Contains("tracked-current", diff, StringComparison.Ordinal);
        Assert.Contains("untracked-current", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXTCONV_MARKER", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_git_diff_action_does_not_run_configured_textconv_or_external_diff_driver()
    {
        string textconvRepo = CreateTextconvRepository("agent-git-diff-textconv");
        (_, string textconvWhole) = await PaRuX.Services.CodingAgentTools.RunGitDiffAsync(
            textconvRepo, CancellationToken.None);
        (_, string textconvScoped) = await PaRuX.Services.CodingAgentTools.RunGitDiffAsync(
            textconvRepo, CancellationToken.None, "--", "tracked.txt");
        Assert.Contains("tracked-current", textconvWhole, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXTCONV_MARKER", textconvWhole, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXTCONV_MARKER", textconvScoped, StringComparison.Ordinal);

        string externalRepo = CreateTextconvRepository("agent-git-diff-external");
        GitAt(externalRepo, "config", "diff.external", "cmd.exe /c echo EXTDIFF_MARKER");
        (_, string externalWhole) = await PaRuX.Services.CodingAgentTools.RunGitDiffAsync(
            externalRepo, CancellationToken.None);
        Assert.Contains("tracked-current", externalWhole, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTDIFF_MARKER", externalWhole, StringComparison.Ordinal);
    }

    private string CreateTextconvRepository(string name)
    {
        string repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        GitAt(repo, "init");
        GitAt(repo, "config", "user.name", "PaRuX Tests");
        GitAt(repo, "config", "user.email", "tests@parux.invalid");
        GitAt(repo, "config", "diff.parux_probe.textconv", "cmd.exe /c echo TEXTCONV_MARKER");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.txt diff=parux_probe\n");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "tracked-before\n");
        GitAt(repo, "add", ".gitattributes", "tracked.txt");
        GitAt(repo, "commit", "-m", "textconv fixture baseline");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "tracked-current\n");
        return repo;
    }

    [Theory]
    [InlineData(".env::$DATA")]
    [InlineData("sub/.env::$DATA")]
    [InlineData(".git/config")]
    [InlineData("prod.env")]
    public void Sensitive_aliases_are_recognised(string relativePath)
    {
        Assert.True(AgentSensitivePaths.IsSensitiveRelativePath(relativePath));
        Assert.False(AgentSensitivePaths.IsSensitiveRelativePath("prod.env.example"));
    }

    private void Git(params string[] args)
        => GitAt(_root, args);

    private static void GitAt(string root, params string[] args)
    {
        var start = new ProcessStartInfo { FileName = "git", WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
