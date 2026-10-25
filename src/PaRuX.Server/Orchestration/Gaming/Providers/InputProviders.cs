using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class NativeInputProvider : IInputProvider
{
    public string Id => "native";
    public string Name => "Native Controller Input";
    public bool IsInstalled => true;
    public string? ExecutablePath => null;
    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}

public sealed class XOutputInputProvider : IInputProvider
{
    private readonly object _processGate = new();
    private readonly Func<Process[]> _getRunningProcesses;
    private Process? _startedProcess;

    public XOutputInputProvider(Func<Process[]>? getRunningProcesses = null) =>
        _getRunningProcesses = getRunningProcesses ?? (() => Process.GetProcessesByName("XOutput"));

    public string Id => "xoutput";
    public string Name => "XOutput Controller Mapper";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates =
            {
                Path.Combine(localAppData, "XOutput", "XOutput.exe"),
                Path.Combine(programFiles, "XOutput", "XOutput.exe"),
                @"C:\XOutput\XOutput.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // Safety rule: Do not start XOutput if profile is remote and using Sunshine virtual input, unless explicitly set in settings
        if (string.Equals(profile.SessionType, "remote", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.InputProvider, "sunshine", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        string? exe = ExecutablePath;
        if (exe == null)
        {
            if (profile.InputSettings.TryGetValue("path", out string? customPath) && File.Exists(customPath))
            {
                exe = customPath;
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        try
        {
            Process[] existing = _getRunningProcesses();
            bool alreadyRunning = existing.Length > 0;
            foreach (Process process in existing) process.Dispose();
            if (alreadyRunning) return Task.FromResult(true);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            };
            if (profile.InputSettings.TryGetValue("args", out string? args) && !string.IsNullOrWhiteSpace(args))
            {
                psi.Arguments = args;
            }

            Process? started = Process.Start(psi);
            if (started == null) return Task.FromResult(false);
            lock (_processGate)
            {
                _startedProcess?.Dispose();
                _startedProcess = started;
            }
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        Process? ownedProcess;
        lock (_processGate)
        {
            ownedProcess = _startedProcess;
            _startedProcess = null;
        }

        if (ownedProcess == null) return Task.FromResult(true);

        try
        {
            using (ownedProcess)
            {
                if (ownedProcess.HasExited) return Task.FromResult(true);
                if (ownedProcess.CloseMainWindow()) ownedProcess.WaitForExit(2000);
                if (!ownedProcess.HasExited)
                    ownedProcess.Kill(true);
            }
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

public sealed class SunshineInputProvider : IInputProvider
{
    public string Id => "sunshine";
    public string Name => "Sunshine Virtual Controller Input";
    public bool IsInstalled => SunshineSessionTriggerProvider.IsInstalledOnMachine;
    public string? ExecutablePath => SunshineSessionTriggerProvider.ExecutablePath;
    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}

public sealed class CustomExecutableInputProvider : IInputProvider
{
    public string Id => "custom";
    public string Name => "Custom Input Utility";

    public bool IsInstalled => true;
    public string? ExecutablePath => null;

    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!profile.InputSettings.TryGetValue("path", out string? exePath) || string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return Task.FromResult(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };
            if (profile.InputSettings.TryGetValue("args", out string? args) && !string.IsNullOrWhiteSpace(args))
            {
                psi.Arguments = args;
            }

            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}
