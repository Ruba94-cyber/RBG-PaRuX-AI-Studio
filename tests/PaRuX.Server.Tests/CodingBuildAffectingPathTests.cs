using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// CF-01: a coding task must not complete on a model verdict after an edit to a file that the build or
/// test run reads. Each path below is one the completion gate treats as testable, so editing it requires
/// a deterministic verification run before the task can finish.
/// </summary>
public sealed class CodingBuildAffectingPathTests
{
    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("src/PaRuX.App/PaRuX.App.csproj")]
    [InlineData("src/Directory.Build.props")]
    [InlineData("Directory.Build.rsp")]
    [InlineData("global.json")]
    [InlineData("NuGet.config")]
    [InlineData("package.json")]
    [InlineData("web/package-lock.json")]
    [InlineData("native/Cargo.lock")]
    [InlineData("Views/MainPage.xaml")]
    [InlineData("Strings/Resources.resx")]
    [InlineData("scripts/build.ps1")]
    [InlineData("scripts/build.sh")]
    [InlineData("tests/pytest.ini")]
    [InlineData("packages.lock.json")]
    [InlineData("web/tsconfig.json")]
    [InlineData("tests/App.runsettings")]
    [InlineData("pom.xml")]
    public void Build_affecting_and_source_edits_require_deterministic_verification(string path)
    {
        Assert.True(CodingVerificationDiscovery.IsBuildOrTestableCodePath(path), path);
    }

    [Theory]
    [InlineData("docs/readme.md")]
    [InlineData("notes.txt")]
    [InlineData("assets/logo.png")]
    [InlineData("")]
    public void Documentation_and_assets_do_not_require_a_build(string path)
    {
        Assert.False(CodingVerificationDiscovery.IsBuildOrTestableCodePath(path), path);
    }

    // JSON and XML data files are far more common than build inputs, so only the named build inputs above count.
    [Theory]
    [InlineData("benchmarks/coding-agent/parux-corpus.json")]
    [InlineData(".ai/templates/TASK.json")]
    [InlineData("src/Properties/launchSettings.json")]
    [InlineData("src/Data/seed.json")]
    [InlineData("docs/schema.xml")]
    [InlineData("Assets/feed.xml")]
    public void Data_json_and_xml_files_do_not_require_a_build(string path)
    {
        Assert.False(CodingVerificationDiscovery.IsBuildOrTestableCodePath(path), path);
    }
}
