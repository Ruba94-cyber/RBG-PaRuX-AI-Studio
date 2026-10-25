using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live parity pilot (PaRuX repository as workspace): the agent's correct fixture verifier
/// `dotnet run --project .../CSharp -- case` passed but only a whole-solution `dotnet test` at the
/// root counted, which cannot run offline in the sandbox, so the agent spiralled for 13 steps.
/// </summary>
public sealed class ScopedVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-scoped-verify-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase)
    {
        "fixtures/CSharp/EvalFixture.csproj", "fixtures/CSharp/Fixture.cs",
        "fixtures/Python/verify.py", "fixtures/Python/cache.py",
        "fixtures/verify-static.mjs", "fixtures/WinUI/Layout.xaml", "root-check.py",
        "fixtures/Python/generate.py", "gradlew.bat", "src/verify.py", "src/inventory.py"
    };

    public ScopedVerificationTests()
    {
        foreach (string file in _tracked) Write(file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private bool Accepts(string command, params string[] changed) =>
        CodingVerificationDiscovery.IsScopedVerificationCommand(_root, command, changed, _tracked.Contains);

    [Theory]
    [InlineData("dotnet run --project fixtures/CSharp -- csharp-clamp-budget", "fixtures/CSharp/Fixture.cs")]
    [InlineData("dotnet run --project fixtures/CSharp/EvalFixture.csproj", "fixtures/CSharp/Fixture.cs")]
    [InlineData("dotnet build fixtures/CSharp/EvalFixture.csproj --no-restore -m:1", "fixtures/CSharp/Fixture.cs")]
    [InlineData("python fixtures/Python/verify.py python-cache-identity", "fixtures/Python/cache.py")]
    [InlineData("node fixtures/verify-static.mjs winui-scroll-layout", "fixtures/WinUI/Layout.xaml")]
    public void Unmodified_project_or_verifier_covering_the_change_counts(string command, string changed)
    {
        Assert.True(Accepts(command, changed));
    }

    [Fact]
    public void Source_scoped_verifier_covers_the_source_file_without_trusting_a_root_script()
    {
        Assert.True(Accepts("python src/verify.py", "src/inventory.py"));
        Assert.False(Accepts("python root-check.py", "src/inventory.py"));
    }

    [Theory]
    // The change is elsewhere: the verifier does not cover it.
    [InlineData("python fixtures/Python/verify.py x", "fixtures/CSharp/Fixture.cs")]
    // The agent modified the verifier itself.
    [InlineData("python fixtures/Python/verify.py x", "fixtures/Python/verify.py")]
    // A script the agent created (untracked) can never be a verifier.
    [InlineData("python fixtures/Python/ok.py", "fixtures/Python/cache.py")]
    // Root-level scripts would "cover" every change.
    [InlineData("python root-check.py", "fixtures/Python/cache.py")]
    // Narrowing or skipping the build/test run.
    [InlineData("dotnet test fixtures/CSharp/EvalFixture.csproj --filter Clamp", "fixtures/CSharp/Fixture.cs")]
    [InlineData("dotnet run --no-build --project fixtures/CSharp", "fixtures/CSharp/Fixture.cs")]
    // Shell composition and exit-code masking.
    [InlineData("python fixtures/Python/verify.py x & exit /b 0", "fixtures/Python/cache.py")]
    [InlineData("node fixtures/verify-static.mjs x | findstr ok", "fixtures/WinUI/Layout.xaml")]
    // Outside the workspace.
    [InlineData("python ../outside/verify.py", "fixtures/Python/cache.py")]
    public void Anything_else_is_not_verification(string command, string changed)
    {
        Write("fixtures/Python/ok.py");
        Assert.False(Accepts(command, changed));
    }

    [Theory]
    // Live pilot: the model ran "cd <fixture> && dotnet run --project EvalFixture.csproj -- case".
    [InlineData("cd fixtures/CSharp && dotnet run --project EvalFixture.csproj -- csharp-clamp-budget", true)]
    [InlineData("cd fixtures/CSharp && dotnet build --no-restore", true)]
    [InlineData("cd /d \"fixtures/CSharp\" && dotnet test", true)]
    [InlineData("cd fixtures/CSharp && dotnet build && echo ok", false)]
    [InlineData("cd ..\\.. && dotnet build", false)]
    [InlineData("cd fixtures/Python && dotnet build", false)]
    public void One_leading_cd_into_the_workspace_is_evaluated_from_that_directory(string command, bool expected)
    {
        Assert.Equal(expected, Accepts(command, "fixtures/CSharp/Fixture.cs"));
    }

    [Fact]
    public void Suggestions_name_the_covering_project_and_verifiers_but_never_root_scripts()
    {
        File.WriteAllText(Path.Combine(_root, "fixtures", "CSharp", "EvalFixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        IReadOnlyList<string> csharp = CodingVerificationDiscovery.SuggestScopedCommands(_root, new[] { "fixtures/CSharp/Fixture.cs" });
        IReadOnlyList<string> winui = CodingVerificationDiscovery.SuggestScopedCommands(_root, new[] { "fixtures/WinUI/Layout.xaml" });

        Assert.Contains(csharp, s => s.StartsWith("dotnet run --project fixtures/CSharp/EvalFixture.csproj", StringComparison.Ordinal));
        Assert.Contains(winui, s => s.StartsWith("node fixtures/verify-static.mjs", StringComparison.Ordinal));
        Assert.DoesNotContain(csharp.Concat(winui), s => s.Contains("root-check.py", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_verify_in_scope()
    {
        Assert.False(Accepts("python fixtures/Python/verify.py x"));
    }
    // Independent review P1: a scoped "verification" must actually check something and must not
    // resolve agent-planted code instead of the repository's.
    [Theory]
    [InlineData("python fixtures/Python/verify.py --help", "fixtures/Python/cache.py")]
    [InlineData("node fixtures/verify-static.mjs --version", "fixtures/WinUI/Layout.xaml")]
    [InlineData("dotnet run --project fixtures/CSharp -- --help", "fixtures/CSharp/Fixture.cs")]
    // A tracked, unmodified program that is not a verifier proves nothing.
    [InlineData("python fixtures/Python/generate.py", "fixtures/Python/cache.py")]
    public void Help_version_and_non_verifier_scripts_do_not_count(string command, string changed)
    {
        Assert.False(Accepts(command, changed));
    }

    [Theory]
    [InlineData("fixtures/Python/unittest.py")]
    [InlineData("fixtures/Python/unittest.pyc")]
    [InlineData("fixtures/Python/json/__init__.py")]
    [InlineData("sitecustomize.py")]
    public void Untracked_python_module_that_can_shadow_imports_blocks_the_scoped_verifier(string planted)
    {
        Assert.True(Accepts("python fixtures/Python/verify.py x", "fixtures/Python/cache.py"));
        Write(planted);
        Assert.False(Accepts("python fixtures/Python/verify.py x", "fixtures/Python/cache.py", planted));
    }

    [Fact]
    public void Node_modules_on_the_resolution_path_blocks_the_scoped_node_verifier()
    {
        Assert.True(Accepts("node fixtures/verify-static.mjs x", "fixtures/WinUI/Layout.xaml"));
        Directory.CreateDirectory(Path.Combine(_root, "fixtures", "node_modules"));
        Assert.False(Accepts("node fixtures/verify-static.mjs x", "fixtures/WinUI/Layout.xaml"));
    }

    [Theory]
    [InlineData("fixtures/Directory.Build.props")]
    [InlineData("fixtures/CSharp/Directory.Build.targets")]
    [InlineData("Directory.Build.props")]
    public void Untracked_msbuild_override_blocks_the_scoped_project(string planted)
    {
        Write(planted);
        Assert.False(Accepts("dotnet test fixtures/CSharp/EvalFixture.csproj", "fixtures/CSharp/Fixture.cs", planted));
        _tracked.Add(planted);
        Assert.True(Accepts("dotnet test fixtures/CSharp/EvalFixture.csproj", "fixtures/CSharp/Fixture.cs"));
        Assert.False(Accepts("dotnet test fixtures/CSharp/EvalFixture.csproj", "fixtures/CSharp/Fixture.cs", planted));
    }

    private bool Hijacked(string command, params string[] changed) =>
        CodingVerificationDiscovery.UsesUntrustedWorkingDirectoryProgram(_root, command, changed, _tracked.Contains);

    [Fact]
    public void Agent_written_program_in_the_working_directory_is_not_trusted_as_the_verifier()
    {
        Assert.False(Hijacked("dotnet test"));
        Write("dotnet.cmd");
        Assert.True(Hijacked("dotnet test", "dotnet.cmd"));
        Write("fixtures/CSharp/python.bat");
        Assert.True(Hijacked("cd fixtures/CSharp && python verify.py", "fixtures/CSharp/python.bat"));
        // The repository's own wrapper is trusted only while tracked and unchanged.
        Assert.False(Hijacked("gradlew.bat test"));
        Assert.True(Hijacked("gradlew.bat test", "gradlew.bat"));
        // Review: a stub that deletes itself after exiting 0 is caught by the pre-run snapshot, and
        // every default PATHEXT extension counts (Windows Script Host runs dotnet.js).
        File.Delete(Path.Combine(_root, "dotnet.cmd"));
        Assert.False(Hijacked("dotnet test"));
        Assert.True(CodingVerificationDiscovery.UsesUntrustedWorkingDirectoryProgram(
            _root, "dotnet test", Array.Empty<string>(), _tracked.Contains, new[] { "dotnet.cmd" }));
        Write("dotnet.js");
        Assert.Contains("dotnet.js", CodingVerificationDiscovery.WorkingDirectoryProgramCandidates(_root, "dotnet test"));
        Assert.True(Hijacked("dotnet test", "dotnet.js"));
        File.Delete(Path.Combine(_root, "dotnet.js"));
        // An explicit wrapper path must be unchanged.
        Assert.False(Hijacked(@".\gradlew.bat test"));
        Assert.True(Hijacked(@".\gradlew.bat test", "gradlew.bat"));
        // An explicit path into an ignored dependency folder is not git-visible and stays accepted.
        Assert.False(Hijacked(@"vendor\bin\phpunit.bat"));
    }
}
