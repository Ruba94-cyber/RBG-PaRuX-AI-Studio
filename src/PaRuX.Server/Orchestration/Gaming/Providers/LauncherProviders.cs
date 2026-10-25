using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class PlayniteLauncherProvider : IGameLauncherProvider
{
    private readonly object _processGate = new();
    private readonly Func<Process[]> _getRunningProcesses;
    private Process? _startedProcess;

    public PlayniteLauncherProvider(Func<Process[]>? getRunningProcesses = null) =>
        _getRunningProcesses = getRunningProcesses ?? (() => Process.GetProcessesByName("Playnite.FullscreenApp")
            .Concat(Process.GetProcessesByName("Playnite.DesktopApp")).ToArray());

    public string Id => "playnite";
    public string Name => "Playnite Launcher (Fullscreen)";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] candidates =
            {
                Path.Combine(localAppData, "Playnite", "Playnite.FullscreenApp.exe"),
                Path.Combine(localAppData, "Playnite", "Playnite.DesktopApp.exe"),
                Path.Combine(programFiles, "Playnite", "Playnite.FullscreenApp.exe"),
                Path.Combine(programFiles, "Playnite", "Playnite.DesktopApp.exe"),
                Path.Combine(programFilesX86, "Playnite", "Playnite.FullscreenApp.exe")
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        string? exe = ExecutablePath;
        if (exe == null)
        {
            // If explicit path is passed in settings, try it
            if (profile.LauncherSettings.TryGetValue("path", out string? customPath) && File.Exists(customPath))
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
            // Check if Playnite is already running
            Process[] existing = _getRunningProcesses();
            bool alreadyRunning = existing.Length > 0;
            foreach (Process process in existing) process.Dispose();
            if (alreadyRunning) return Task.FromResult(true);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            };

            if (!exe.EndsWith("Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("--fullscreen");
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

public sealed class SteamBigPictureLauncherProvider : IGameLauncherProvider
{
    public string Id => "steam";
    public string Name => "Steam Big Picture";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string candidate = Path.Combine(programFilesX86, "Steam", "steam.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "steam://open/bigpicture",
                UseShellExecute = true
            };
            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        return Task.FromResult(true);
    }
}

public sealed class CustomExecutableLauncherProvider : IGameLauncherProvider
{
    public string Id => "custom";
    public string Name => "Custom Executable Launcher";

    public bool IsInstalled => true;

    public string? ExecutablePath => null;

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!profile.LauncherSettings.TryGetValue("path", out string? exePath) || string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return Task.FromResult(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };

            if (profile.LauncherSettings.TryGetValue("args", out string? args) && !string.IsNullOrWhiteSpace(args))
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

public sealed class NoneLauncherProvider : IGameLauncherProvider
{
    public string Id => "none";
    public string Name => "No Launcher (Manage Game Directly)";
    public bool IsInstalled => true;
    public string? ExecutablePath => null;
    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}
