using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingVerificationDiscoveryNoiseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-verification-noise-" + Guid.NewGuid().ToString("N"));

    public CodingVerificationDiscoveryNoiseTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Dependency_and_build_trees_do_not_fabricate_recursive_test_evidence()
    {
        File.WriteAllText(Path.Combine(_root, "Gemfile"), "source 'https://example.invalid'\n");
        foreach (string noise in new[] { "vendor", "node_modules", "target", "bin", ".gradle", ".venv", "dist" })
        {
            string deep = Path.Combine(_root, noise, "deep");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "fake_spec.rb"), "raise 'not repository evidence'\n");
        }

        IReadOnlyList<CodingVerificationDiscovery.VerificationCommand> commands = CodingVerificationDiscovery.Discover(_root);

        Assert.DoesNotContain(commands, command => command.Command == "bundle exec rspec");
    }

    [Fact]
    public void Multi_repo_detection_uses_git_authority_not_directory_name_noise_heuristics()
    {
        string bin = Path.Combine(_root, "bin");
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(bin, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(target, ".git"), "gitdir: fixture");
        File.WriteAllText(Path.Combine(bin, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");
        File.WriteAllText(Path.Combine(target, "Target.csproj"), "<Project />");

        CodingVerificationDiscovery.VerificationCommand verify = Assert.Single(CodingVerificationDiscovery.Discover(_root));

        Assert.Equal("verify-all", verify.Kind);
        Assert.Contains("pushd \"bin\"", verify.Command, StringComparison.Ordinal);
        Assert.Contains("pushd \"target\"", verify.Command, StringComparison.Ordinal);
        Assert.Contains("npm test", verify.Command, StringComparison.Ordinal);
        Assert.Contains("dotnet build", verify.Command, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
