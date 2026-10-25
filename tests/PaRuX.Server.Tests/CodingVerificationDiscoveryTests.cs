using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingVerificationDiscoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-verification-discovery-" + Guid.NewGuid().ToString("N"));

    public CodingVerificationDiscoveryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Dotnet_solution_discovers_build_and_test_without_guessing_project_names()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Sample.sln"), "");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Kind == "build" && c.Command == "dotnet build");
        Assert.Contains(commands, c => c.Kind == "test" && c.Command == "dotnet test");
    }

    [Fact]
    public void Restored_dotnet_repo_prefers_no_restore_so_sandbox_verification_cannot_fetch_packages()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Sample.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        File.WriteAllText(Path.Combine(_tempDir, "obj", "project.assets.json"), "{}");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "dotnet build --no-restore");
        Assert.Contains(commands, c => c.Command == "dotnet test --no-restore");
    }

    [Fact]
    public void Unverified_completion_rejection_names_the_exact_accepted_commands()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Sample.csproj"), "<Project />");

        string message = CodingVerificationDiscovery.BuildUnverifiedCompletionRejection(_tempDir);

        Assert.StartsWith("Completion was rejected", message, StringComparison.Ordinal);
        Assert.Contains("\n- \"dotnet build\"", message, StringComparison.Ordinal);
        Assert.Contains("\n- \"dotnet test\"", message, StringComparison.Ordinal);
        Assert.Contains("Other commands do not satisfy verification", message, StringComparison.Ordinal);
        // Live round 6: a 4B model kept running `dotnet test --filter ...`, which never verifies.
        Assert.Contains("Do not add --filter", message, StringComparison.Ordinal);
        // A model copied an earlier " | "-joined list verbatim as a shell pipeline that never
        // matched an accepted command; the message must not contain shell composition operators.
        Assert.DoesNotContain("|", message, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", message, StringComparison.Ordinal);
        Assert.DoesNotContain(";", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Npm_only_returns_scripts_that_really_exist()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
        { "scripts": { "build": "vite build", "lint": "eslint .", "typecheck": "tsc --noEmit" } }
        """);

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "npm run build");
        Assert.Contains(commands, c => c.Command == "npm run lint");
        Assert.Contains(commands, c => c.Command == "npm run typecheck");
        Assert.DoesNotContain(commands, c => c.Command == "npm test");
    }

    [Fact]
    public void Invalid_package_json_does_not_fabricate_npm_commands_or_throw()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), "{ definitely-not-json");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.DoesNotContain(commands, c => c.Command.StartsWith("npm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cargo_uses_offline_build_test_and_clippy()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), "");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "cargo build --offline");
        Assert.Contains(commands, c => c.Command == "cargo test --offline");
        Assert.Contains(commands, c => c.Command.StartsWith("cargo clippy --offline", StringComparison.Ordinal));
    }

    [Fact]
    public void Go_discovers_build_test_and_builtin_vet()
    {
        File.WriteAllText(Path.Combine(_tempDir, "go.mod"), "module x\n");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "go build ./...");
        Assert.Contains(commands, c => c.Command == "go test ./...");
        Assert.Contains(commands, c => c.Command == "go vet ./...");
    }

    [Fact]
    public void Pytest_and_configured_python_static_tools_are_discovered()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"), "[project]\nname='x'\n[tool.ruff]\n[tool.mypy]\n");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "python -m pytest");
        Assert.Contains(commands, c => c.Command == "python -m ruff check .");
        Assert.Contains(commands, c => c.Command == "python -m mypy .");
    }

    [Fact]
    public void Windows_gradle_wrapper_is_preferred_and_forced_offline()
    {
        File.WriteAllText(Path.Combine(_tempDir, "gradlew"), "");
        File.WriteAllText(Path.Combine(_tempDir, "gradlew.bat"), "");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == @".\gradlew.bat --offline build");
        Assert.Contains(commands, c => c.Command == @".\gradlew.bat --offline test");
        Assert.DoesNotContain(commands, c => c.Command.StartsWith("./gradlew", StringComparison.Ordinal));
    }

    [Fact]
    public void Maven_prefers_checked_in_wrapper_and_offline_mode()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(_tempDir, "mvnw.cmd"), "");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == @".\mvnw.cmd -o -DskipTests package");
        Assert.Contains(commands, c => c.Command == @".\mvnw.cmd -o test");
        Assert.Contains(commands, c => c.Command == @".\mvnw.cmd -o verify");
    }

    [Fact]
    public void Existing_cmake_configuration_discovers_build_and_ctest_without_reconfiguring()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)");
        Directory.CreateDirectory(Path.Combine(_tempDir, "build"));
        File.WriteAllText(Path.Combine(_tempDir, "build", "CMakeCache.txt"), "");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "cmake --build \"build\"");
        Assert.Contains(commands, c => c.Command == "ctest --test-dir \"build\" --output-on-failure");
    }

    [Fact]
    public void Makefile_targets_are_detected_only_when_defined()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Makefile"), "all:\n\t@echo build\n\ntest:\n\t@echo test\n\ncheck:\n\t@echo check\n");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "make");
        Assert.Contains(commands, c => c.Command == "make test");
        Assert.Contains(commands, c => c.Command == "make check");
    }

    [Fact]
    public void Flutter_and_Dart_are_distinguished_from_pubspec_content()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pubspec.yaml"), "dependencies:\n  flutter:\n    sdk: flutter\n");
        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> flutter = CodingVerificationDiscovery.Discover(_tempDir);
        Assert.Contains(flutter, c => c.Command == "flutter test");
        Assert.Contains(flutter, c => c.Command == "flutter analyze");

        File.WriteAllText(Path.Combine(_tempDir, "pubspec.yaml"), "name: plain_dart\n");
        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> dart = CodingVerificationDiscovery.Discover(_tempDir);
        Assert.Contains(dart, c => c.Command == "dart test");
        Assert.Contains(dart, c => c.Command == "dart analyze");
    }

    [Fact]
    public void Multi_repository_workspace_exposes_only_one_verify_all_command_covering_every_discovered_check()
    {
        string primary = Path.Combine(_tempDir, "primary");
        string backend = Path.Combine(_tempDir, "backend");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(backend);
        File.WriteAllText(Path.Combine(primary, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(backend, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(primary, "Primary.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(primary, "obj"));
        File.WriteAllText(Path.Combine(primary, "obj", "project.assets.json"), "{}");
        File.WriteAllText(Path.Combine(backend, "package.json"), "{\"scripts\":{\"test\":\"vitest\",\"lint\":\"eslint .\"}}");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        CodingVerificationDiscovery.VerificationCommand verify = Assert.Single(commands);
        Assert.Equal("verify-all", verify.Kind);
        Assert.Contains("pushd \"primary\"", verify.Command, StringComparison.Ordinal);
        Assert.Contains("dotnet build --no-restore", verify.Command, StringComparison.Ordinal);
        Assert.Contains("dotnet test --no-restore", verify.Command, StringComparison.Ordinal);
        Assert.Contains("popd", verify.Command, StringComparison.Ordinal);
        Assert.Contains("pushd \"backend\"", verify.Command, StringComparison.Ordinal);
        Assert.Contains("npm test", verify.Command, StringComparison.Ordinal);
        Assert.Contains("npm run lint", verify.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("cd \"primary\"", verify.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_all_fails_closed_when_the_third_git_repository_has_no_supported_checks()
    {
        foreach (string alias in new[] { "backend", "frontend", "docs" })
        {
            string root = Path.Combine(_tempDir, alias);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fixture");
        }
        File.WriteAllText(Path.Combine(_tempDir, "backend", "Backend.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_tempDir, "frontend", "package.json"),
            "{\"scripts\":{\"test\":\"vitest\"}}");
        // docs is an authorized Git worktree but has no discovered verifier.
        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));
    }

    [Fact]
    public void Aggregate_root_cannot_override_child_authorities_with_forged_package_test()
    {
        foreach (string alias in new[] { "backend", "frontend" })
        {
            string child = Path.Combine(_tempDir, alias);
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(child, "Project.csproj"), "<Project />");
        }

        Assert.Equal("verify-all",
            Assert.Single(CodingVerificationDiscovery.Discover(_tempDir)).Kind);

        File.WriteAllText(Path.Combine(_tempDir, "package.json"),
            "{\"scripts\":{\"test\":\"echo PASS\"}}");
        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));

        File.Delete(Path.Combine(_tempDir, "package.json"));
        Assert.Equal("verify-all",
            Assert.Single(CodingVerificationDiscovery.Discover(_tempDir)).Kind);
    }

    [Fact]
    public void Verify_all_fails_closed_if_a_direct_child_is_not_an_authorized_git_worktree()
    {
        foreach (string alias in new[] { "backend", "frontend" })
        {
            string root = Path.Combine(_tempDir, alias);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(root, "Project.csproj"), "<Project />");
        }
        Directory.CreateDirectory(Path.Combine(_tempDir, "untracked-repo"));
        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));
    }

    [Theory]
    [InlineData("bad & injected")]
    [InlineData("inject%PATH%")]
    [InlineData("bad!var!")]
    public void Verify_all_never_executes_a_shell_unsafe_git_directory_alias(string alias)
    {
        string primary = Path.Combine(_tempDir, "primary");
        string injected = Path.Combine(_tempDir, alias);
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(injected);
        foreach (string root in new[] { primary, injected })
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(root, "Project.csproj"), "<Project />");
        }
        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));
    }

    [Fact]
    public void Verify_all_includes_three_repositories_when_every_alias_has_coverage()
    {
        foreach (string alias in new[] { "backend", "frontend", "dashboard" })
        {
            string root = Path.Combine(_tempDir, alias);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(root, "Project.csproj"), "<Project />");
        }
        CodingVerificationDiscovery.VerificationCommand verification =
            Assert.Single(CodingVerificationDiscovery.Discover(_tempDir));
        Assert.Equal("verify-all", verification.Kind);
        foreach (string alias in new[] { "backend", "frontend", "dashboard" })
            Assert.Contains("pushd \"" + alias + "\"", verification.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifiable_root_repository_is_not_reclassified_as_multi_repo_because_of_a_nested_git_child()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Root.csproj"), "<Project />");
        string nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(nested, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_tempDir);

        Assert.Contains(commands, c => c.Command == "dotnet build");
        Assert.Contains(commands, c => c.Command == "dotnet test");
        Assert.DoesNotContain(commands, c => c.Kind == "verify-all");
    }

    [Fact]
    public void Ordinary_nested_project_without_direct_git_marker_is_not_silently_promoted_to_an_authority()
    {
        string nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");

        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));
    }

    [Fact]
    public void Unknown_repository_shape_returns_no_commands_instead_of_guessing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "README.md"), "hello");

        Assert.Empty(CodingVerificationDiscovery.Discover(_tempDir));
    }
}
