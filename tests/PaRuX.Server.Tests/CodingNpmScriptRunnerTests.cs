using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live UI check: `npm test` hung for 8+ minutes inside the offline AppContainer (npm's real-path walk
/// cannot lstat the drive root; with --preserve-symlinks it hangs instead of failing).
/// </summary>
public sealed class CodingNpmScriptRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-npm-" + Guid.NewGuid().ToString("N"));
    private string Scripts => Path.Combine(_root, ".scripts");

    public CodingNpmScriptRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Package(string scriptsJson) =>
        File.WriteAllText(Path.Combine(_root, "package.json"), "{\"name\":\"p\",\"scripts\":" + scriptsJson + "}");

    [Theory]
    [InlineData("npm test", "test")]
    [InlineData("npm t", "test")]
    [InlineData("npm run lint", "lint")]
    [InlineData("npm run-script lint", "lint")]
    public void Npm_script_invocations_become_direct_script_runs(string command, string script)
    {
        Package("{\"test\":\"node --test\",\"lint\":\"eslint .\"}");

        string? rewritten = CodingNpmScriptRunner.TryRewrite(_root, command, Scripts, "run-x")?.CommandLine;

        Assert.NotNull(rewritten);
        Assert.Contains("npm_lifecycle_event=" + script, File.ReadAllText(Path.Combine(Scripts, "run-x-npm-0.cmd")), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "node_modules", ".bin"), rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("npm-cli", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Hooks_and_arguments_follow_npm_semantics_with_one_script_file_each()
    {
        Package("{\"pretest\":\"echo pre\",\"test\":\"mocha \\\"test/**/*.js\\\" && echo (done)\",\"posttest\":\"echo post\"}");

        CodingNpmScriptRunner.Rewrite rewrite = CodingNpmScriptRunner.TryRewrite(_root, "npm test -- --grep cart", Scripts, "run-x")!;

        Assert.Equal(3, rewrite.ScriptFiles.Count);
        Assert.Contains("npm_lifecycle_event=pretest", File.ReadAllText(rewrite.ScriptFiles[0]), StringComparison.Ordinal);
        string main = File.ReadAllText(rewrite.ScriptFiles[1]);
        Assert.Contains("mocha \"test/**/*.js\" && echo (done) --grep cart", main, StringComparison.Ordinal);
        Assert.Contains("npm_lifecycle_event=posttest", File.ReadAllText(rewrite.ScriptFiles[2]), StringComparison.Ordinal);
        // Quotes and parentheses stay inside the script files, never on the outer command line.
        Assert.DoesNotContain("mocha", rewrite.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_fingerprint_changes_when_the_agent_redefines_the_test_script()
    {
        string head = "{\"scripts\":{\"test\":\"node --test\"}}";
        Assert.Equal(CodingNpmScriptRunner.LifecycleFingerprint(head, "test"),
            CodingNpmScriptRunner.LifecycleFingerprint("{\"name\":\"x\",\"scripts\":{\"test\":\"node --test\"}}", "test"));
        Assert.NotEqual(CodingNpmScriptRunner.LifecycleFingerprint(head, "test"),
            CodingNpmScriptRunner.LifecycleFingerprint("{\"scripts\":{\"test\":\"exit 0\"}}", "test"));
        Assert.NotEqual(CodingNpmScriptRunner.LifecycleFingerprint(head, "test"),
            CodingNpmScriptRunner.LifecycleFingerprint("{\"scripts\":{\"pretest\":\"exit 0\",\"test\":\"node --test\"}}", "test"));
        Assert.Equal("test", CodingNpmScriptRunner.ScriptName("npm t"));
        Assert.Null(CodingNpmScriptRunner.ScriptName("npm install"));
    }

    [Theory]
    [InlineData("npm install")]
    [InlineData("npm ci")]
    [InlineData("npm run missing")]
    [InlineData("npm test && echo hi")]
    [InlineData("cd web && npm test")]
    public void Anything_else_is_left_unchanged(string command)
    {
        Package("{\"test\":\"node --test\"}");
        Assert.Null(CodingNpmScriptRunner.TryRewrite(_root, command, Scripts, "run-x"));
    }

    [Fact]
    public void Package_managers_keep_default_node_options_so_they_fail_fast()
    {
        Assert.True(CodingNpmScriptRunner.StartsPackageManager("npm install"));
        Assert.True(CodingNpmScriptRunner.StartsPackageManager("\"C:\\tools\\npx.cmd\" eslint"));
        Assert.False(CodingNpmScriptRunner.StartsPackageManager("node --test"));
        Assert.Equal("--x", WindowsCodingSandbox.NodeOptionsForSandbox("--x", null, "npm install"));
        Assert.Contains("--preserve-symlinks", WindowsCodingSandbox.NodeOptionsForSandbox(null, null, "node --test"));
    }
}
