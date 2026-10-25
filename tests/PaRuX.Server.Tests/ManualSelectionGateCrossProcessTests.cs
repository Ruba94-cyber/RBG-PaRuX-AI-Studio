using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualSelectionGateCrossProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-manual-selection-lock", Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot;
    private readonly string _modelPath;

    public ManualSelectionGateCrossProcessTests()
    {
        _previousDataRoot = PaRuXDataRoot.Path;
        Directory.CreateDirectory(_root);
        _modelPath = Path.Combine(_root, "selected.gguf");
        File.WriteAllText(_modelPath, "isolated test model");
        PaRuXDataRoot.UseForTests(_root);
    }

    [Fact]
    public void A_second_process_gets_typed_busy_and_writes_nothing_while_a_role_pin_writer_holds_the_lock()
    {
        using ManualSelectionGate.WriteLease held = ManualSelectionGate.AcquireWrite(_root);
        string output = RunChild("manual-selection-role-write", _root, _modelPath);

        Assert.Equal(ManualSelectionGateBusyException.ErrorCode, output.Trim());
        Assert.Empty(Directory.GetFiles(_root, "model-role-overrides*"));
    }

    [Fact]
    public async Task Same_process_writer_refuses_within_bound_then_commits_after_the_holder_releases()
    {
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using ManualSelectionGate.WriteLease lease = ManualSelectionGate.AcquireWrite(_root);
            acquired.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test lock holder was not released");
        });

        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)), "same-process lock holder did not start");
            Stopwatch wait = Stopwatch.StartNew();
            ManualSelectionGateBusyException refusal = Assert.Throws<ManualSelectionGateBusyException>(
                () => LocalAiPaths.SetRoleOverride("reasoning", _modelPath));
            Assert.Equal(ManualSelectionGateBusyException.ErrorCode, refusal.Code);
            Assert.True(wait.Elapsed <= ManualSelectionGate.WriteLockWait + TimeSpan.FromSeconds(1),
                "writer exceeded the bounded wait");
            Assert.Empty(Directory.GetFiles(_root, "model-role-overrides*"));

            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            LocalAiPaths.SetRoleOverride("reasoning", _modelPath);
            Assert.Equal(_modelPath, LocalAiPaths.GetRoleOverride("reasoning"));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Write_lease_is_released_when_the_write_throws_and_cancellation_during_acquisition()
    {
        Action throwingWrite = () =>
        {
            using ManualSelectionGate.WriteLease lease = ManualSelectionGate.AcquireWrite(_root);
            throw new InvalidOperationException("exercise lease cleanup");
        };
        Assert.Throws<InvalidOperationException>(throwingWrite);
        using (ManualSelectionGate.AcquireWrite(_root)) { }

        using Process child = StartChild("manual-selection-hold", _root);
        try
        {
            Assert.Equal("held", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ManualSelectionGate.TryAcquireWrite(_root, TimeSpan.FromSeconds(5), cancellation.Token));

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (ManualSelectionGate.AcquireWrite(_root)) { }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Contended_role_reset_and_chat_selector_routes_return_typed_conflict()
    {
        using IDisposable localAiRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "localai"));
        using IDisposable localAiState = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "localai-state"));
        using IDisposable textPort = LocalAiPaths.UseTextPortForTests(GetFreeLoopbackPort());

        var runtimeBuilder = WebApplication.CreateBuilder();
        runtimeBuilder.Logging.ClearProviders();
        runtimeBuilder.WebHost.UseUrls("http://127.0.0.1:" + LocalAiPaths.TextPort);
        using WebApplication runtimeStub = runtimeBuilder.Build();
        runtimeStub.MapGet("/health", () => Results.Json(new { status = "ok" }));
        await runtimeStub.StartAsync();
        LocalAiProcessManager.ResetTextOwnershipCacheForTests();
        Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(LocalAiPaths.TextPort),
            "the test runtime must be a loopback listener owned by this process");

        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LocalAiProcessManager>();
            services.AddSingleton(new LocalAiProcessManager(0));
        }));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using ManualSelectionGate.WriteLease lease = ManualSelectionGate.AcquireWrite(_root);
            acquired.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("test lock holder was not released");
        });

        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)), "same-process lock holder did not start");

            using HttpResponseMessage reset = await client.PostAsync("/api/v1/models/roles/reasoning/reset", null);
            await AssertManualSelectionBusyAsync(reset);

            using HttpResponseMessage chat = await client.PostAsJsonAsync("/api/v1/chat/completions", new
            {
                model = "parux-auto:balanced",
                messages = new[] { new { role = "user", content = "Explain how a tree grows." } },
                stream = false
            });
            await AssertManualSelectionBusyAsync(chat);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task AssertManualSelectionBusyAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict,
            $"Expected HTTP 409 but received {(int)response.StatusCode}: {content}");
        using JsonDocument body = JsonDocument.Parse(content);
        Assert.Equal(ManualSelectionGateBusyException.ErrorCode, body.RootElement.GetProperty("error").GetString());
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void A_lock_file_left_by_a_dead_process_is_reopened_using_the_os_handle_as_owner_check()
    {
        using Process child = StartChild("manual-selection-hold", _root);
        try
        {
            string? ready = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.Equal("held", ready);

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10_000), "lock holder child did not exit");
            string lockPath = ManualSelectionGate.LockFilePath(_root);
            Assert.True(File.Exists(lockPath), "the test requires the lock file to remain after the child exits");

            Stopwatch wait = Stopwatch.StartNew();
            using ManualSelectionGate.WriteLease recovered = ManualSelectionGate.AcquireWrite(_root);
            Assert.True(wait.Elapsed <= ManualSelectionGate.WriteLockWait,
                "a stale lock file blocked acquisition beyond the configured bound");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(10_000);
            }
        }
    }

    [Fact]
    public void Auto_assignment_reports_a_skip_instead_of_throwing_when_the_lock_cannot_be_opened()
    {
        // A directory at the lock path makes the lease itself fail (not a sharing conflict). Auto-assignment must
        // report the role as skipped like its other write failures instead of aborting the whole run.
        Directory.CreateDirectory(ManualSelectionGate.LockFilePath(_root));

        bool assigned = LocalAiPaths.TrySetAutoAssignedRoleOverride("reasoning", _modelPath, out string? skipped);

        Assert.False(assigned);
        Assert.Equal("override_not_saved", skipped);
        Assert.Empty(Directory.GetFiles(_root, "model-role-overrides*"));
    }

    private string RunChild(params string[] arguments)
    {
        using Process child = StartChild(arguments);
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(15_000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("manual-selection child did not exit");
        }

        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        Assert.True(child.ExitCode == 0, "manual-selection child failed: " + error);
        return output;
    }

    private Process StartChild(params string[] arguments)
    {
        string assembly = typeof(ManualSelectionGateCrossProcessTests).Assembly.Location;
        Assert.True(File.Exists(assembly), "test assembly not found");
        var start = new ProcessStartInfo(DotNetHost())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.Environment["PARUX_DATA_ROOT"] = _root;
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start test child process.");
    }

    private static string DotNetHost()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
    }

    public void Dispose()
    {
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
