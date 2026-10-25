using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingVerificationManifestPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "parux-manifest-proof-" + Guid.NewGuid().ToString("N"));

    private static void Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.fsmonitor=false");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000) && process.ExitCode == 0,
            "Git fixture operation failed: " + string.Join(" ", arguments)
            + "\n" + stdout + "\n" + stderr);
    }

    private static string Repo(string root, string manifest, string text)
    {
        Directory.CreateDirectory(root);
        Git(root, "init", "-q");
        File.WriteAllText(Path.Combine(root, manifest), text);
        Git(root, "add", "--", manifest);
        Git(root, "-c", "user.name=PaRuX Fixture",
            "-c", "user.email=parux-fixture@invalid.test",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "baseline");
        return root;
    }

    [Theory]
    [InlineData("package.json", "{\"scripts\":{\"test\":\"vitest\"}}")]
    [InlineData("Project.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>")]
    [InlineData("Makefile", "test:\n\t@echo test")]
    public async Task Immutable_tracked_root_manifest_yields_a_stable_receipt(
        string filename, string content)
    {
        string root = Repo(Path.Combine(_directory, "only"), filename, content);
        string? baseline = await CodingVerificationManifestPolicy.CaptureAsync(root, CancellationToken.None);
        Assert.NotNull(baseline);
        Assert.Matches("^[0-9a-f]{64}$", baseline!);
        Assert.Equal(baseline, await CodingVerificationManifestPolicy.CaptureAsync(root, CancellationToken.None));

        // A no-op fake verifier may be written and later restored. Both
        // pre- and post-run integrity checks must reject its intermediate form.
        File.WriteAllText(Path.Combine(root, filename), "test: exit 0");
        Assert.Null(await CodingVerificationManifestPolicy.CaptureAsync(root, CancellationToken.None));
        Git(root, "checkout", "HEAD", "--", filename);
        Assert.Equal(baseline, await CodingVerificationManifestPolicy.CaptureAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task Untracked_verifier_manifest_cannot_authorize_a_new_fake_check()
    {
        string root = Repo(Path.Combine(_directory, "only"), "README.md", "baseline");
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"test\":\"echo PASS\"}}");
        Assert.Null(await CodingVerificationManifestPolicy.CaptureAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task Composite_verification_requires_every_git_alias_manifest_unchanged()
    {
        Directory.CreateDirectory(_directory);
        string backend = Repo(Path.Combine(_directory, "backend"), "Backend.csproj", "<Project />");
        string frontend = Repo(Path.Combine(_directory, "frontend"), "package.json",
            "{\"scripts\":{\"test\":\"vitest\"}}");
        string? baseline = await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None);
        Assert.NotNull(baseline);

        File.WriteAllText(Path.Combine(frontend, "package.json"),
            "{\"scripts\":{\"test\":\"echo PASS\"}}");
        Assert.Null(await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None));

        Git(frontend, "checkout", "HEAD", "--", "package.json");
        Assert.Equal(baseline, await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None));

        File.WriteAllText(Path.Combine(_directory, "package.json"),
            "{\"scripts\":{\"test\":\"echo fake green\"}}");
        Assert.Null(await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None));
        File.Delete(Path.Combine(_directory, "package.json"));
        Assert.Equal(baseline, await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None));

        string shadow = Path.Combine(_directory, "untrusted-third");
        Directory.CreateDirectory(shadow);
        Assert.Null(await CodingVerificationManifestPolicy.CaptureAsync(
            _directory, CancellationToken.None));
    }

    [Theory]
    [InlineData("package.json", true)]
    [InlineData("Directory.Build.targets", true)]
    [InlineData("build.gradle.kts", true)]
    [InlineData("App.slnx", true)]
    [InlineData("Verifier.fsproj", true)]
    [InlineData("README.md", false)]
    [InlineData("Generated.dll", false)]
    public void Recognizes_root_runner_configuration_sources(string name, bool expected)
    {
        Assert.Equal(expected, CodingVerificationManifestPolicy.IsVerifierManifest(name));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
