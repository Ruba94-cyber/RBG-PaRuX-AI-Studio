using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real clean-install run: `uv python install` failed under %LOCALAPPDATA%\PaRuX with "Missing expected target directory for Python minor
/// version link" (it works in any other folder), so speech could never be installed. PaRuX now installs Python in a staging folder in that
/// case and adopts the interpreter directory into its own root.
/// </summary>
public sealed class ManagedPythonMinorLinkFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-pyadopt-" + Guid.NewGuid().ToString("N"));

    public ManagedPythonMinorLinkFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("error: Missing expected target directory for Python minor version link at C:\\x\\cpython-3.11.9-windows-x86_64-none", true)]
    [InlineData("Downloading cpython... error: Missing expected target directory for Python minor version link", true)]
    [InlineData("error: no interpreter found for Python 3.99", false)]
    [InlineData("", false)]
    public void The_uv_minor_link_failure_is_recognised_by_its_message(string stderr, bool expected)
    {
        Assert.Equal(expected, ManagedPythonRuntime.IsMinorVersionLinkFailure(stderr));
    }

    private string StagedPython(string name)
    {
        string dir = Path.Combine(_dir, "staging", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "python.exe"), "exe");
        return dir;
    }

    [Fact]
    public void A_staged_interpreter_directory_is_moved_into_the_install_root_and_its_python_exe_returned()
    {
        StagedPython("cpython-3.11.9-windows-x86_64-none");
        string installRoot = Path.Combine(_dir, "root");

        string? python = ManagedPythonRuntime.AdoptStagedPython(Path.Combine(_dir, "staging"), installRoot, "3.11.9");

        Assert.Equal(Path.Combine(installRoot, "cpython-3.11.9-windows-x86_64-none", "python.exe"), python);
        Assert.True(File.Exists(python));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "staging")));
    }

    [Fact]
    public void Adoption_ignores_other_versions_and_never_overwrites_an_existing_interpreter()
    {
        StagedPython("cpython-3.10.11-windows-x86_64-none");
        StagedPython("cpython-3.11.9-windows-x86_64-none");
        string installRoot = Path.Combine(_dir, "root");
        string existing = Path.Combine(installRoot, "cpython-3.11.9-windows-x86_64-none");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "python.exe"), "already here");

        string? python = ManagedPythonRuntime.AdoptStagedPython(Path.Combine(_dir, "staging"), installRoot, "3.11.9");

        Assert.Equal(Path.Combine(existing, "python.exe"), python);
        Assert.Equal("already here", File.ReadAllText(python!));
        Assert.False(Directory.Exists(Path.Combine(installRoot, "cpython-3.10.11-windows-x86_64-none")));
    }

    [Fact]
    public void Adoption_without_a_matching_staged_interpreter_returns_null()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "staging"));

        Assert.Null(ManagedPythonRuntime.AdoptStagedPython(Path.Combine(_dir, "staging"), Path.Combine(_dir, "root"), "3.11.9"));
    }
}
