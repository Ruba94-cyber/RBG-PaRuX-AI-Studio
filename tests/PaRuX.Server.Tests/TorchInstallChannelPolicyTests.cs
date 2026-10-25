using PaRuX.Server;
using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TorchInstallChannelPolicyTests
{
    [Fact]
    public void Capable_nvidia_uses_the_pinned_cu128_channel()
    {
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(
            new TorchHardwareProfile(TorchHardwareVendor.Nvidia, TorchInstallChannelPolicy.MinimumNvidiaComputeCapability));

        Assert.Equal(TorchInstallChannel.Cuda128, plan.Channel);
        Assert.True(plan.CanInstall);
        AssertPinnedTorch(plan);
        Assert.Contains(ExLlamaV3BuildCatalog.TorchIndexUrl, plan.PipArguments);
    }

    [Theory]
    [InlineData("Amd")]
    [InlineData("Intel")]
    [InlineData("None")]
    public void Non_nvidia_hardware_uses_the_pinned_cpu_channel(string vendor)
    {
        TorchHardwareVendor parsedVendor = Enum.Parse<TorchHardwareVendor>(vendor);
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(new TorchHardwareProfile(parsedVendor));

        Assert.Equal(TorchInstallChannel.Cpu, plan.Channel);
        Assert.True(plan.CanInstall);
        AssertPinnedTorch(plan);
        Assert.Contains(TorchInstallChannelPolicy.CpuTorchIndexUrl, plan.PipArguments);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void Unknown_nvidia_capability_falls_back_to_cpu_and_says_so()
    {
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(
            new TorchHardwareProfile(TorchHardwareVendor.Nvidia));

        Assert.Equal(TorchInstallChannel.Cpu, plan.Channel);
        AssertPinnedTorch(plan);
        Assert.Contains(TorchInstallChannelPolicy.CpuTorchIndexUrl, plan.PipArguments);
        Assert.Contains("unknown", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falling back", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-7.5)]
    public void Non_finite_or_non_positive_nvidia_capability_never_selects_cuda(double capability)
    {
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(
            new TorchHardwareProfile(TorchHardwareVendor.Nvidia, capability));

        Assert.Equal(TorchInstallChannel.Cpu, plan.Channel);
        AssertPinnedTorch(plan);
        Assert.Contains(TorchInstallChannelPolicy.CpuTorchIndexUrl, plan.PipArguments);
        Assert.DoesNotContain(ExLlamaV3BuildCatalog.TorchIndexUrl, plan.PipArguments);
    }

    [Fact]
    public void Nvidia_below_the_documented_capability_threshold_uses_cpu()
    {
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(
            new TorchHardwareProfile(TorchHardwareVendor.Nvidia, TorchInstallChannelPolicy.MinimumNvidiaComputeCapability - 0.1));

        Assert.Equal(TorchInstallChannel.Cpu, plan.Channel);
        AssertPinnedTorch(plan);
        Assert.Contains(TorchInstallChannelPolicy.CpuTorchIndexUrl, plan.PipArguments);
        Assert.Contains("below the assumed minimum", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_hardware_is_refused_with_a_reason()
    {
        TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(
            new TorchHardwareProfile(TorchHardwareVendor.Unknown));

        Assert.Equal(TorchInstallChannel.Refused, plan.Channel);
        Assert.False(plan.CanInstall);
        Assert.Empty(plan.PipArguments);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void Every_installable_policy_result_contains_only_a_pinned_torch_package()
    {
        TorchHardwareProfile[] hardwareProfiles =
        {
            new(TorchHardwareVendor.Nvidia, TorchInstallChannelPolicy.MinimumNvidiaComputeCapability),
            new(TorchHardwareVendor.Nvidia),
            new(TorchHardwareVendor.Amd),
            new(TorchHardwareVendor.Intel),
            new(TorchHardwareVendor.None)
        };

        TorchInstallChannelPlan[] plans = hardwareProfiles
            .Select(TorchInstallChannelPolicy.Select)
            .Where(plan => plan.CanInstall)
            .ToArray();

        Assert.Equal(hardwareProfiles.Length, plans.Length);
        Assert.All(plans, AssertPinnedTorch);
    }

    [Theory]
    [InlineData("SpeechService.cs", "VenvRoot")]
    [InlineData("ThreedService.cs", "VenvRoot")]
    [InlineData("AvatarEngineService.cs", "VenvRoot")]
    [InlineData("LoraGgufConversionService.cs", "VenvDirectory")]
    public void Managed_install_sites_pass_the_policy_plan_to_pip(string sourceFileName, string environmentArgument)
    {
        string sourcePath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.Server", sourceFileName);
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("TorchInstallChannelPolicy.Select(torchHardware)", source);
        Assert.Contains("if (!torchPlan.CanInstall)", source);
        Assert.Matches(
            $@"PipInstallAsync\(\s*{Regex.Escape(environmentArgument)}\s*,\s*torchPlan\.PipArguments",
            source);
    }

    /// <summary>
    /// The training engine installs from hash-locked files instead of the open torch plan: the policy still picks the
    /// channel (and refuses unsupported hardware), but only TrainingRuntimeLock installs, always with --require-hashes
    /// --no-deps, and the service never hands the policy's unlocked pip arguments to pip.
    /// </summary>
    [Fact]
    public void Training_install_site_uses_the_policy_channel_and_only_hash_locked_installs()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "TrainingLabService.cs"));
        string lockSource = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "TrainingRuntimeLock.cs"));

        Assert.Contains("TorchInstallChannelPolicy.Select(torchHardware)", source);
        Assert.Contains("if (!torchPlan.CanInstall)", source);
        Assert.Contains("TrainingRuntimeLock.TorchLockPath(torchPlan.Channel)", source);
        Assert.Contains("TrainingRuntimeLock.InstallAsync(", source);
        Assert.DoesNotContain("torchPlan.PipArguments", source);
        Assert.Contains("\"--require-hashes\", \"--no-deps\"", lockSource);
        // Every pip call in the training service goes through a TrainingRuntimeLock.InstallAsync callback.
        foreach (Match call in Regex.Matches(source, @"PipInstallAsync\("))
        {
            string before = source[Math.Max(0, call.Index - 160)..call.Index];
            Assert.Contains("args =>", before);
        }
        Assert.Matches(@"PipInstallAsync\(\s*VenvRoot\s*,\s*args\b", source);
    }

    /// <summary>
    /// The nearest ancestor of the test binaries that holds both the server source and this test project. A fixed number of
    /// parent hops is wrong for a relocated run (binaries copied under another folder, such as a worktree's artifacts), so the
    /// directory markers decide instead of the depth.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "PaRuX.Server"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests", "PaRuX.Server.Tests")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "No ancestor of the test binaries holds both src/PaRuX.Server and tests/PaRuX.Server.Tests.");
    }

    private static void AssertPinnedTorch(TorchInstallChannelPlan plan)
    {
        Assert.Contains(plan.PipArguments, argument =>
            argument.StartsWith("torch==", StringComparison.Ordinal));
        Assert.DoesNotContain("torch", plan.PipArguments);
        Assert.Equal("torch==" + ExLlamaV3BuildCatalog.TorchVersion, plan.PipArguments[0]);
        Assert.Equal("--index-url", plan.PipArguments[1]);
    }
}
