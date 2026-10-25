using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A text launch moves to the next free loopback port when another program holds the preferred one, and every consumer reads that
/// choice. The holder is a separate process (the runtime stand-in started as a child of this test), so the listener is foreign to this
/// process, as a real other program would be. PaRuX must leave that process running. The launches are intercepted before any PaRuX
/// runtime starts, so the port choice and the arguments that carry it are what this exercises.
/// </summary>
public sealed class TextRuntimePortLaunchTests : IDisposable
{
    private const long Gib = 1024L * 1024L * 1024L;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-port-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _foreign = [];
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public TextRuntimePortLaunchTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        foreach (Process foreign in _foreign)
        {
            try { if (!foreign.HasExited) foreign.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            foreign.Dispose();
        }
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_preferred_port_held_by_another_program_moves_the_launch_and_every_consumer_follows()
    {
        WriteRuntimeFiles();
        int preferred = StartForeignProgramOnFreePort(out Process foreign);
        using IDisposable scope = LocalAiPaths.UseTextPortForTests(preferred);
        var launches = new List<IReadOnlyList<string>>();

        LocalAiProcessManager.RuntimeResult result = await NewManager(launches).StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEmpty(launches);
        int chosen = int.Parse(ValueOf(launches[0], "--port") ?? throw new InvalidOperationException("no --port argument"));
        Assert.True(chosen > preferred, "the launch must move past the port another program holds");
        Assert.All(launches, args => Assert.Equal(chosen.ToString(), ValueOf(args, "--port")));
        Assert.Equal(chosen, LocalAiPaths.TextPort);
        Assert.Equal("http://127.0.0.1:" + chosen, LocalRuntimeService.CurrentManagedProviderEndpoint);
        Assert.False(foreign.HasExited, "PaRuX must not stop the program that holds the preferred port");
        Assert.True(ManagedListenerOwnership.IsPortListening(preferred));
    }

    [Fact]
    public async Task A_free_preferred_port_is_kept_and_no_candidate_is_skipped()
    {
        WriteRuntimeFiles();
        int preferred = FreeLoopbackPort();
        using IDisposable scope = LocalAiPaths.UseTextPortForTests(preferred);
        var launches = new List<IReadOnlyList<string>>();

        await NewManager(launches).StartTextAsync(CancellationToken.None);

        Assert.NotEmpty(launches);
        Assert.All(launches, args => Assert.Equal(preferred.ToString(), ValueOf(args, "--port")));
        Assert.Equal(preferred, LocalAiPaths.TextPort);
    }

    [Fact]
    public async Task Runtime_status_reports_and_serializes_the_current_managed_endpoint()
    {
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(40101);
        LocalAiPaths.SetChosenTextPort(40127);
        string endpoint = "http://127.0.0.1:40127";

        RuntimeManagerStatus status = await new RuntimeManagerService(NewManager(new List<IReadOnlyList<string>>()))
            .GetStatusAsync(CancellationToken.None, ramSnapshot: null);

        Assert.Equal(endpoint, status.CurrentManagedProviderEndpoint);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(
            status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(endpoint, document.RootElement.GetProperty("currentManagedProviderEndpoint").GetString());
    }

    [Fact]
    public void Entering_a_port_scope_clears_any_earlier_choice_and_leaving_it_restores_that_choice()
    {
        int before = LocalAiPaths.TextPort;

        using (LocalAiPaths.UseTextPortForTests(40101))
        {
            Assert.Equal(40101, LocalAiPaths.TextPort);
            LocalAiPaths.SetChosenTextPort(40102);
            Assert.Equal(40102, LocalAiPaths.TextPort);
        }

        Assert.Equal(before, LocalAiPaths.TextPort);
    }

    private LocalAiProcessManager NewManager(List<IReadOnlyList<string>> launches) => new(0)
    {
        InventoryProbe = () => GpuInventoryFixtures.Inventory(Discrete(0x1002, 16 * Gib)),
        LaunchInterceptForTests = (exe, arguments) =>
        {
            launches.Add(arguments);
            return new LocalAiProcessManager.RuntimeResult(false, "intercepted failure");
        }
    };

    private static void WriteRuntimeFiles()
    {
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.StandardModel);
        WriteFile(LocalAiPaths.StandardVision!);
    }

    /// <summary>Starts a separate process that holds a free loopback port, and waits until that port is listening. The holder is a plain
    /// PowerShell TCP listener, so it is a foreign program with no PaRuX code in it. The test stops it on the way out.</summary>
    private int StartForeignProgramOnFreePort(out Process foreign)
    {
        int port = FreeLoopbackPort();
        var startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, " + port
            + "); $listener.Start(); Start-Sleep -Seconds 120");
        foreign = Process.Start(startInfo) ?? throw new InvalidOperationException("The foreign listener process did not start.");
        _foreign.Add(foreign);

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ManagedListenerOwnership.IsPortListening(port))
        {
            if (foreign.HasExited || DateTime.UtcNow > deadline)
                throw new InvalidOperationException("The foreign listener did not start listening on port " + port + ".");
            Thread.Sleep(100);
        }
        return port;
    }

    /// <summary>Returns a loopback port that is free right now. The probe listener is released before the port is used.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static GpuAdapterStatus Discrete(uint vendorId, long dedicatedBytes) => new()
    {
        Index = 0,
        Name = "Discrete test adapter",
        VendorId = vendorId,
        DeviceId = 0x1,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = 16 * Gib
    };

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        int index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }
}
