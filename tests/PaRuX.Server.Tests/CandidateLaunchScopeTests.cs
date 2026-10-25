using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateLaunchScopeTests
{
    private static readonly TextLaunchDescriptor Launch = TextLaunchDescriptor.Create("text-cuda",
        new[] { "--model", "C:/m.gguf", "--ctx-size", "32768", "--gpu-layers", "-1" }, null).Descriptor!;
    private static readonly TextLaunchDescriptor OtherLaunch = TextLaunchDescriptor.Create("text-cuda",
        new[] { "--model", "C:/m.gguf", "--ctx-size", "2048", "--gpu-layers", "-1" }, null).Descriptor!;
    private const string Model = "C:/m.gguf";
    private static readonly CandidateLaunchHost Host = new("text-cuda", "hw2-sha256:" + new string('1', 64),
        "text-cuda:b1", "rtv2-sha256:" + new string('2', 64));

    private static CandidateProofEnvironment Env(string? configuration = null, string? hardware = null,
        string? runtime = null, string? binary = null, string workload = "w") => new(
        hardware ?? Host.HardwareFingerprint, runtime ?? Host.RuntimeFingerprint, binary ?? Host.BinaryFingerprint,
        configuration ?? Launch.Fingerprint, workload);

    [Fact]
    public void Exact_actual_launch_and_unchanged_host_are_the_only_admissible_case()
    {
        CandidateLaunchScopeResult result = CandidateLaunchScope.Check(Env(), Env(workload: "p"), Launch, Model, Host);
        Assert.True(result.Admissible);
        Assert.Equal("admissible", result.Reason);
    }

    [Fact]
    public void Each_drift_fails_closed_with_its_own_reason()
    {
        Assert.Equal("evidence_environment_unavailable", CandidateLaunchScope.Check(null, Env(), Launch, Model, Host).Reason);
        Assert.Equal("evaluation_launch_profile_not_actual", CandidateLaunchScope.Check(
            Env(LlamaTextQualitySession.ConfigurationFingerprint), Env(), Launch, Model, Host).Reason);
        Assert.Equal("evaluation_launch_profile_not_actual", CandidateLaunchScope.Check(
            Env(), Env("config-sha"), Launch, Model, Host).Reason);
        Assert.Equal("quality_performance_launch_mismatch", CandidateLaunchScope.Check(
            Env(), Env(OtherLaunch.Fingerprint), Launch, Model, Host).Reason);
        Assert.Equal("actual_launch_unavailable", CandidateLaunchScope.Check(Env(), Env(), null, Model, Host).Reason);
        Assert.Equal("launch_configuration_mismatch", CandidateLaunchScope.Check(Env(), Env(), OtherLaunch, Model, Host).Reason);
        Assert.Equal("host_attestation_unavailable", CandidateLaunchScope.Check(Env(), Env(), Launch, Model, null).Reason);
        Assert.Equal("backend_mismatch", CandidateLaunchScope.Check(Env(), Env(), Launch, Model,
            Host with { Backend = "text-vulkan" }).Reason);
        Assert.Equal("backend_mismatch", CandidateLaunchScope.Check(Env(runtime: "text-vulkan:b1"),
            Env(runtime: "text-vulkan:b1"), Launch, Model, Host with { RuntimeFingerprint = "text-vulkan:b1" }).Reason);
        Assert.Equal("hardware_changed", CandidateLaunchScope.Check(Env(), Env(), Launch, Model,
            Host with { HardwareFingerprint = "hw2-sha256:" + new string('9', 64) }).Reason);
        Assert.Equal("hardware_changed", CandidateLaunchScope.Check(Env(),
            Env(hardware: "hw2-sha256:" + new string('8', 64)), Launch, Model, Host).Reason);
        Assert.Equal("runtime_changed", CandidateLaunchScope.Check(Env(), Env(), Launch, Model,
            Host with { RuntimeFingerprint = "text-cuda:b2" }).Reason);
        Assert.Equal("runtime_dependencies_changed", CandidateLaunchScope.Check(Env(), Env(), Launch, Model,
            Host with { BinaryFingerprint = "rtv2-sha256:" + new string('7', 64) }).Reason);
        Assert.Equal("launch_model_mismatch", CandidateLaunchScope.Check(Env(), Env(), Launch, "C:/other.gguf", Host).Reason);
        Assert.Equal("launch_model_mismatch", CandidateLaunchScope.Check(Env(), Env(), Launch, null, Host).Reason);
        Assert.True(CandidateLaunchScope.Check(Env(), Env(), Launch, "c:\\m.gguf", Host).Admissible);
    }

    [Fact]
    public void Actual_launch_evidence_requires_one_identical_ld1_descriptor()
    {
        Assert.True(CandidateLaunchScope.IsActualLaunchEvidence(Launch.Fingerprint, Launch.Fingerprint));
        Assert.False(CandidateLaunchScope.IsActualLaunchEvidence(Launch.Fingerprint, OtherLaunch.Fingerprint));
        Assert.False(CandidateLaunchScope.IsActualLaunchEvidence(LlamaTextQualitySession.ConfigurationFingerprint,
            LlamaTextQualitySession.ConfigurationFingerprint));
        Assert.False(CandidateLaunchScope.IsActualLaunchEvidence(null, null));
    }
}
