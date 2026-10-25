using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingPackageRestoreDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-restore-discovery-" + Guid.NewGuid().ToString("N"));

    public CodingPackageRestoreDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Npm_lockfile_uses_frozen_install_without_lifecycle_scripts()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{}");

        var restore = Assert.Single(CodingPackageRestoreDiscovery.Discover(_root));

        Assert.Equal("npm", restore.Ecosystem);
        Assert.Equal("npm ci --ignore-scripts --no-audit --no-fund", restore.Command);
        Assert.Equal(CodingPackageRestoreDiscovery.SafeRestoreCapability, restore.RequiredCapability);
        Assert.False(restore.MayExecuteProjectCode);
    }

    [Fact]
    public void Dotnet_lockfile_enables_locked_mode_but_requires_scripted_restore_authority()
    {
        File.WriteAllText(Path.Combine(_root, "Sample.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "packages.lock.json"), "{}");

        var restore = CodingPackageRestoreDiscovery.Discover(_root).Single(item => item.Ecosystem == "dotnet");

        Assert.Equal("dotnet restore --locked-mode", restore.Command);
        Assert.Equal(CodingPackageRestoreDiscovery.ScriptedRestoreCapability, restore.RequiredCapability);
        Assert.True(restore.MayExecuteProjectCode);
    }

    [Fact]
    public void Cargo_and_go_fetch_without_building_project()
    {
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname='x'\nversion='0.1.0'\n");
        File.WriteAllText(Path.Combine(_root, "Cargo.lock"), "");
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module example.test/x\n");

        IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> restore = CodingPackageRestoreDiscovery.Discover(_root);

        Assert.Contains(restore, item => item.Command == "cargo fetch --locked" && !item.MayExecuteProjectCode);
        Assert.Contains(restore, item => item.Command == "go mod download" && !item.MayExecuteProjectCode);
    }

    [Fact]
    public void Script_capability_is_required_for_build_backend_or_plugin_ecosystems()
    {
        File.WriteAllText(Path.Combine(_root, "uv.lock"), "version = 1\n");
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(_root, "gradlew.bat"), "@echo off\n");

        IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> restore = CodingPackageRestoreDiscovery.Discover(_root);

        Assert.Contains(restore, item => item.Ecosystem == "uv" && item.RequiredCapability == CodingPackageRestoreDiscovery.ScriptedRestoreCapability && item.MayExecuteProjectCode);
        Assert.Contains(restore, item => item.Ecosystem == "maven" && item.RequiredCapability == CodingPackageRestoreDiscovery.ScriptedRestoreCapability && item.MayExecuteProjectCode);
        Assert.Contains(restore, item => item.Ecosystem == "gradle" && item.RequiredCapability == CodingPackageRestoreDiscovery.ScriptedRestoreCapability && item.MayExecuteProjectCode);
    }

    [Fact]
    public void Multi_repository_workspace_uses_direct_git_aliases()
    {
        string backend = Path.Combine(_root, "backend");
        string frontend = Path.Combine(_root, "frontend");
        Directory.CreateDirectory(backend);
        Directory.CreateDirectory(frontend);
        File.WriteAllText(Path.Combine(backend, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(frontend, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(backend, "go.mod"), "module example.test/backend\n");
        File.WriteAllText(Path.Combine(frontend, "package.json"), "{}");
        File.WriteAllText(Path.Combine(frontend, "package-lock.json"), "{}");

        IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> restore = CodingPackageRestoreDiscovery.Discover(_root);

        Assert.Contains(restore, item => item.RelativeRoot == "backend" && item.Command == "go mod download");
        Assert.Contains(restore, item => item.RelativeRoot == "frontend" && item.Command.StartsWith("npm ci", StringComparison.Ordinal));
        Assert.DoesNotContain(restore, item => item.RelativeRoot == ".");
    }

    [Fact]
    public void Capability_filter_and_alias_validation_are_fail_closed()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{}");

        Assert.Empty(CodingPackageRestoreDiscovery.AllowedFor(_root, ".", Array.Empty<string>()));
        Assert.Single(CodingPackageRestoreDiscovery.AllowedFor(_root, ".", new[] { CodingPackageRestoreDiscovery.SafeRestoreCapability }));
        Assert.Throws<InvalidDataException>(() => CodingPackageRestoreDiscovery.NormalizeRelativeRoot("../other"));
        Assert.Throws<InvalidDataException>(() => CodingPackageRestoreDiscovery.NormalizeRelativeRoot("nested/repo"));
        Assert.Throws<InvalidDataException>(() => CodingPackageRestoreDiscovery.NormalizeRelativeRoot("frontend & calc"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
