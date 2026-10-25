using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Captures the port resolution made inside a TestServer request, where AsyncLocal test scopes may differ.</summary>
internal sealed class TestServerTextPortCapture(Action<int> capture) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.Equals("/api/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                capture(LocalAiPaths.TextPort);
            await nextMiddleware(context).ConfigureAwait(false);
        });
        next(app);
    };
}

/// <summary>
/// Real child-process evidence for the actual-launch fence: the production fresh-exact loader starts
/// a real executable (PaRuX.RuntimeStub installed as llama-server) and the descriptor recorded from
/// its exact argv/environment must equal what the planning-only API predicted. The fence must vanish
/// when that process dies, and must follow a change in the inherited launch environment.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ActualLaunchFenceIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-launch-fence-" + Guid.NewGuid().ToString("N"));
    private readonly string _state = Path.Combine(Path.GetTempPath(), "parux-launch-fence-state-" + Guid.NewGuid().ToString("N"));
    private readonly int _port;
    private readonly IDisposable _rootScope;
    private readonly IDisposable _stateScope;
    private readonly IDisposable _portScope;
    private WebApplication? _health;
    private int _healthHandlerTextPort;

    public ActualLaunchFenceIntegrationTests()
    {
        using (var listener = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            _port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        _rootScope = LocalAiPaths.UseRootForTests(_root);
        _stateScope = LocalAiPaths.UseStateDirForTests(_state);
        _portScope = LocalAiPaths.UseTextPortForTests(_port);
    }

    public async Task InitializeAsync()
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Assert.True(File.Exists(Path.Combine(staged, LocalAiPaths.LlamaServerExecutable)),
            "PaRuX.RuntimeStub was not staged; this test refuses to pass without a real child process.");
        Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*"))
            File.Copy(source, Path.Combine(LocalAiPaths.RuntimeVulkanPath, Path.GetFileName(source)), overwrite: true);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:" + _port);
        WebApplication app = builder.Build();
        // Healthy only while the recorded managed child is alive, so a fresh exact load observes a
        // stopped runtime before launch and a healthy one only after its own process started.
        app.MapGet("/health", () =>
        {
            Volatile.Write(ref _healthHandlerTextPort, LocalAiPaths.TextPort);
            return ManagedChildAlive()
                ? Results.Json(new { status = "ok" })
                : Results.StatusCode(503);
        });
        // The post-health output probe asks one fixed question on this same loopback port. The RuntimeStub child serves no
        // HTTP (it only stays alive), so this fixture answers the route in llama-server's non-streaming chat-completion shape
        // and the production probe judges that reply.
        app.MapPost("/v1/chat/completions", () => Results.Json(new
        {
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "4" }, finish_reason = "stop" } }
        }));
        _health = app;
        await app.StartAsync();

        Assert.True(ManagedListenerOwnership.IsPortListening(_port),
            "The Windows listener table must see the loopback health fixture before the child starts.");
        Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(_port),
            "The health fixture must be owned by this test process.");
    }

    private static bool ManagedChildAlive()
    {
        try
        {
            if (!File.Exists(LocalAiPaths.TextPidFile)) return false;
            using Process process = Process.GetProcessById(int.Parse(File.ReadAllText(LocalAiPaths.TextPidFile).Trim()));
            return !process.HasExited;
        }
        catch { return false; }
    }

    public async Task DisposeAsync()
    {
        try { new LocalAiProcessManager(0).StopText(); } catch { }
        LocalAiProcessManager.ClearTextLaunchIdentity();
        if (_health != null) await _health.DisposeAsync();
        _portScope.Dispose();
        _stateScope.Dispose();
        _rootScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_state, recursive: true); } catch { }
    }

    [Fact]
    public async Task Fresh_exact_launch_records_the_planned_descriptor_bound_to_the_live_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        string model = Path.Combine(_root, "models", "fence-candidate.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, new byte[] { 1, 2, 3 });
        var manager = new LocalAiProcessManager(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        TextLaunchDescriptorResult planned = await manager.PlanFreshExactTextLaunchAsync(model, "Standard", timeout.Token);
        Assert.Null(planned.Reason);
        Assert.Equal("text-vulkan", planned.Descriptor!.Backend);
        Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor());

        LocalAiProcessManager.RuntimeResult launched = await manager.StartFreshExactTextModelAsync(model, "Standard", timeout.Token);
        Assert.True(launched.Success, launched.Message);
        Assert.Equal(_port, Volatile.Read(ref _healthHandlerTextPort));
        Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(_port));
        Assert.True(await manager.IsTextHealthyAsync(CancellationToken.None),
            "The launched child must pass the production /health check on its recorded loopback port.");
        int childPid = int.Parse(File.ReadAllText(LocalAiPaths.TextPidFile).Trim());
        Assert.NotEqual(Environment.ProcessId, childPid);

        TextLaunchDescriptor? actual = LocalAiProcessManager.GetVerifiedTextLaunchDescriptor();
        Assert.NotNull(actual);
        Assert.Equal(planned.Descriptor.Fingerprint, actual!.Fingerprint);
        Assert.Equal(planned.Descriptor.Arguments.ToArray(), actual.Arguments.ToArray());

        // The equivalence rule admits evidence measured under exactly this launch and nothing else.
        CandidateLaunchHost host = new("text-vulkan", "hw2-sha256:" + new string('1', 64), "text-vulkan:t",
            "rtv2-sha256:" + new string('2', 64));
        CandidateProofEnvironment evidence = new(host.HardwareFingerprint, host.RuntimeFingerprint,
            host.BinaryFingerprint, actual.Fingerprint, "w");
        Assert.True(CandidateLaunchScope.Check(evidence, evidence, actual, model, host).Admissible);
        // Same configuration but another model file is not this launch.
        Assert.Equal("launch_model_mismatch", CandidateLaunchScope.Check(evidence, evidence, actual,
            Path.Combine(_root, "models", "other.gguf"), host).Reason);
        CandidateProofEnvironment fixedProfile = evidence with
        { ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint };
        Assert.Equal("evaluation_launch_profile_not_actual",
            CandidateLaunchScope.Check(fixedProfile, fixedProfile, actual, model, host).Reason);

        // The real child dies outside PaRuX's control: the fence must not outlive it.
        using (Process child = Process.GetProcessById(childPid))
        {
            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10000));
        }
        Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor());
        Assert.Equal("actual_launch_unavailable", CandidateLaunchScope.Check(evidence, evidence,
            LocalAiProcessManager.GetVerifiedTextLaunchDescriptor(), model, host).Reason);
    }

    [Fact]
    public async Task Inherited_llama_environment_changes_both_plan_and_actual_launch_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        string model = Path.Combine(_root, "models", "fence-env.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, new byte[] { 4, 5, 6 });
        var manager = new LocalAiProcessManager(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        const string variable = "GGML_PARUX_FENCE_TEST";

        TextLaunchDescriptor before = (await manager.PlanFreshExactTextLaunchAsync(model, "Standard", timeout.Token)).Descriptor!;
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            TextLaunchDescriptor after = (await manager.PlanFreshExactTextLaunchAsync(model, "Standard", timeout.Token)).Descriptor!;
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
            Assert.Contains(variable + "=1", after.Environment);

            LocalAiProcessManager.RuntimeResult launched = await manager.StartFreshExactTextModelAsync(model, "Standard", timeout.Token);
            Assert.True(launched.Success, launched.Message);
            TextLaunchDescriptor actual = LocalAiProcessManager.GetVerifiedTextLaunchDescriptor()!;
            Assert.Equal(after.Fingerprint, actual.Fingerprint);
            Assert.Equal("launch_configuration_mismatch", CandidateLaunchScope.Check(
                new CandidateProofEnvironment("h", "text-vulkan:t", "b", before.Fingerprint, "w"),
                new CandidateProofEnvironment("h", "text-vulkan:t", "b", before.Fingerprint, "w"),
                actual, model, new CandidateLaunchHost("text-vulkan", "h", "text-vulkan:t", "b")).Reason);

            // A STOP clears the fence immediately rather than waiting for liveness checks.
            Assert.True(manager.StopText());
            Assert.Null(LocalAiProcessManager.GetVerifiedTextLaunchDescriptor());
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public async Task Evaluator_resolves_the_planned_actual_launch_and_refuses_any_argv_that_does_not_reproduce_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        string model = Path.Combine(_root, "models", "fence-evaluator.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, new byte[] { 7, 8, 9 });
        var manager = new LocalAiProcessManager(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        TextLaunchDescriptor planned = (await manager.PlanFreshExactTextLaunchAsync(model,
            RuntimeManagerService.RoleDisplayLabel("standard"), timeout.Token)).Descriptor!;

        TextQualityRuntimeResolution resolution = await new LlamaCandidateTextQualityRuntime(manager)
            .ResolveForModelAsync(model, timeout.Token);
        if (resolution.Spec is null)
        {
            // Hardware/runtime attestation can be legitimately unavailable on a test host; it must say so.
            Assert.Equal("host_attestation_unavailable", resolution.Refusal);
            return;
        }
        TextQualityRuntimeSpec spec = resolution.Spec;
        Assert.Equal(planned.Fingerprint, spec.Environment.ConfigurationFingerprint);
        Assert.Equal(planned.Fingerprint, spec.Launch!.Descriptor.Fingerprint);
        Assert.Equal("text-vulkan", spec.Backend);
        Assert.DoesNotContain("--model", spec.Launch.SessionArguments);
        Assert.DoesNotContain("--port", spec.Launch.SessionArguments);

        // The session's own argv (ephemeral model/alias/port/host + planned semantic args) reproduces the plan.
        var sessionArgs = new List<string> { "--model", model, "--alias", "parux-quality-x", "--port", "1",
            "--host", "127.0.0.1" };
        sessionArgs.AddRange(spec.Launch.SessionArguments);
        Assert.Equal(planned.Fingerprint, TextLaunchDescriptor.Create(spec.Backend, sessionArgs,
            TextLaunchDescriptor.EffectiveEnvironment(spec.Launch.EnvironmentOverrides)).Descriptor!.Fingerprint);

        // A spec whose launch was tampered with (or planned for another file) never starts a process.
        TextLaunchDescriptor other = TextLaunchDescriptor.Create("text-vulkan",
            new[] { "--model", model, "--ctx-size", "2048" }, null).Descriptor!;
        InvalidDataException mismatch = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LlamaTextQualitySession.StartAsync(spec with { Launch = spec.Launch with { Descriptor = other } }, model,
                timeout.Token));
        Assert.Equal("evaluation_launch_mismatch", mismatch.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => LlamaTextQualitySession.StartAsync(spec,
            Path.Combine(_root, "models", "different.gguf"), timeout.Token));
        Assert.False(File.Exists(LocalAiPaths.TextPidFile));
    }
}
