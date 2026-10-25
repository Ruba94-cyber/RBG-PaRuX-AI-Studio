using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Cli;

/// <summary>
/// Foreground launcher for the exact same PaRuX.Server executable the desktop app uses. It owns
/// no server/runtime logic: discovery -> start process -> wait for the Server-authored bootstrap
/// and /api/v1/health -> wait until Ctrl+C. An already-running healthy Server is never killed or
/// adopted; `parux serve` simply reports it and returns.
/// </summary>
internal static class CliServerLauncher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HealthClient =
        PaRuXControlPlaneHttpClient.CreateNoRedirectClient(TimeSpan.FromSeconds(2));

    internal sealed record LaunchSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string bootstrapPath = Path.Combine(
            PaRuX.Contracts.PaRuXDataRoot.Path, "server", "bootstrap.json");

        ServerBootstrap? existing = ReadBootstrap(bootstrapPath);
        if (await IsHealthyAsync(existing, cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"PaRuX Server is already running at {existing!.Endpoint} (pid {existing.ProcessId}).");
            PrintWebClientUrl(existing);
            return 0;
        }

        // A dead process may have left a stale record. Never kill anything based on it; remove only
        // the file so the new Server can publish its own authoritative bootstrap atomically.
        try { if (File.Exists(bootstrapPath)) File.Delete(bootstrapPath); } catch { }

        string? configuredPath = Environment.GetEnvironmentVariable("PARUX_SERVER_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            configuredPath = Environment.GetEnvironmentVariable("PARUX_CORE_PATH"); // previous PaRuX branding compatibility

        LaunchSpec spec;
        try
        {
            spec = ResolveServerLaunch(AppContext.BaseDirectory, configuredPath);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var start = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = spec.WorkingDirectory
        };
        foreach (string argument in spec.Arguments) start.ArgumentList.Add(argument);

        using Process? process = Process.Start(start);
        if (process == null)
        {
            Console.Error.WriteLine("Windows/.NET could not start PaRuX Server.");
            return 1;
        }

        Console.WriteLine($"Starting PaRuX Server (pid {process.Id})…");
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        ServerBootstrap? bootstrap = null;
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            bootstrap = ReadBootstrap(bootstrapPath);
            if (bootstrap?.ProcessId == process.Id
                && await IsHealthyAsync(bootstrap, cancellationToken).ConfigureAwait(false))
                break;
            bootstrap = null;
        }

        if (bootstrap == null)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            Console.Error.WriteLine(process.HasExited
                ? $"PaRuX Server exited during startup with code {process.ExitCode}."
                : "PaRuX Server did not become healthy within 20 seconds.");
            return 1;
        }

        Console.WriteLine($"PaRuX Server is healthy at {bootstrap.Endpoint}. Press Ctrl+C to stop this foreground Server.");
        PrintWebClientUrl(bootstrap);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                Console.WriteLine("Stopping PaRuX Server…");
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            return 0;
        }
    }

    internal static LaunchSpec ResolveServerLaunch(string baseDirectory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return SpecFor(configuredPath);

        string nestedExe = Path.Combine(baseDirectory, "PaRuX.Server", "PaRuX.Server.exe");
        if (File.Exists(nestedExe)) return SpecFor(nestedExe);
        string besideExe = Path.Combine(baseDirectory, "PaRuX.Server.exe");
        if (File.Exists(besideExe)) return SpecFor(besideExe);
        string nestedDll = Path.Combine(baseDirectory, "PaRuX.Server", "PaRuX.Server.dll");
        if (File.Exists(nestedDll)) return SpecFor(nestedDll);
        string besideDll = Path.Combine(baseDirectory, "PaRuX.Server.dll");
        if (File.Exists(besideDll)) return SpecFor(besideDll);

        // Official MSI layout: parux.exe is self-contained under Program Files\PaRuX\Cli while
        // Desktop and CLI intentionally share one sibling Program Files\PaRuX\PaRuX.Server process.
        string parent = Path.GetFullPath(Path.Combine(baseDirectory, ".."));
        string siblingExe = Path.Combine(parent, "PaRuX.Server", "PaRuX.Server.exe");
        if (File.Exists(siblingExe)) return SpecFor(siblingExe);
        string siblingDll = Path.Combine(parent, "PaRuX.Server", "PaRuX.Server.dll");
        if (File.Exists(siblingDll)) return SpecFor(siblingDll);

        DirectoryInfo? directory = new(baseDirectory);
        for (int depth = 0; depth < 9 && directory != null; depth++, directory = directory.Parent)
        {
            if (!directory.Name.Equals("src", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                string output = Path.Combine(directory.FullName, "PaRuX.Server", "bin", configuration, "net8.0");
                string exe = Path.Combine(output, "PaRuX.Server.exe");
                if (File.Exists(exe)) return SpecFor(exe);
                string dll = Path.Combine(output, "PaRuX.Server.dll");
                if (File.Exists(dll)) return SpecFor(dll);
            }
        }

        throw new InvalidOperationException(
            "PaRuX Server is missing from this installation. Repair/reinstall PaRuX, build the Server project, "
            + "or point PARUX_SERVER_PATH at PaRuX.Server.exe/PaRuX.Server.dll.");
    }

    private static LaunchSpec SpecFor(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string workingDirectory = Path.GetDirectoryName(fullPath)!;
        return fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new LaunchSpec("dotnet", new[] { fullPath }, workingDirectory)
            : new LaunchSpec(fullPath, Array.Empty<string>(), workingDirectory);
    }

    private static ServerBootstrap? ReadBootstrap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<ServerBootstrap>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prints the browser client's URL, token and all. This is the only place PaRuX ever puts the
    /// session token on a screen, and it is the right one: `parux serve` runs on the machine that
    /// owns the token, the URL is loopback, and without it a headless Core has no interface a person
    /// can use. The page strips the token out of the address bar as soon as it loads.
    /// </summary>
    private static void PrintWebClientUrl(ServerBootstrap bootstrap)
    {
        if (string.IsNullOrWhiteSpace(bootstrap.Token)
            || !PaRuXControlPlaneHttpClient.TryParseLocalEndpoint(bootstrap.Endpoint, out _)) return;
        Console.WriteLine("Open PaRuX in a browser on this machine:");
        Console.WriteLine("  " + bootstrap.Endpoint.TrimEnd('/') + "/app?token=" + Uri.EscapeDataString(bootstrap.Token));
    }

    private static async Task<bool> IsHealthyAsync(ServerBootstrap? bootstrap, CancellationToken cancellationToken)
    {
        if (bootstrap == null
            || !PaRuXControlPlaneHttpClient.TryParseLocalEndpoint(bootstrap.Endpoint, out _)) return false;
        try
        {
            using Process process = Process.GetProcessById(bootstrap.ProcessId);
            if (process.HasExited) return false;
            using HttpResponseMessage response = await HealthClient.GetAsync(
                bootstrap.Endpoint.TrimEnd('/') + "/api/v1/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
