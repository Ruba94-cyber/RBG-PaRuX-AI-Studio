using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class WindowsNativeSuspendProvider : IGameSuspendProvider
{
    public string Id => "win32native";
    public string Name => "Windows Native Suspend (RAM)";
    public bool IsAvailable => OperatingSystem.IsWindows();

    [DllImport("ntdll.dll", PreserveSig = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", PreserveSig = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint ProcessSuspendResume = 0x0800;

    public Task<bool> SuspendProcessAsync(int processId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return Task.FromResult(false);

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessSuspendResume, false, processId);
            if (handle == IntPtr.Zero) return Task.FromResult(false);

            int status = NtSuspendProcess(handle);
            return Task.FromResult(status == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public Task<bool> ResumeProcessAsync(int processId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return Task.FromResult(false);

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessSuspendResume, false, processId);
            if (handle == IntPtr.Zero) return Task.FromResult(false);

            int status = NtResumeProcess(handle);
            return Task.FromResult(status == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }
}

public sealed class NyrnaGameSuspendProvider : IGameSuspendProvider
{
    public string Id => "nyrna";
    public string Name => "Nyrna Suspend Utility";

    public bool IsAvailable => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return FindExecutable(localAppData, programFiles, FindStorePackageRoots());
        }
    }

    internal static string? FindExecutable(string localAppData, string programFiles, IEnumerable<string>? storePackageRoots = null)
    {
        string[] candidates =
        {
            Path.Combine(localAppData, "Nyrna", "Nyrna.exe"),
            Path.Combine(programFiles, "Nyrna", "Nyrna.exe"),
            @"C:\Nyrna\Nyrna.exe"
        };

        string? unpackaged = candidates.FirstOrDefault(File.Exists);
        if (unpackaged != null) return unpackaged;

        // WindowsApps does not allow listing its parent on normal user accounts. Resolve this
        // user's Microsoft Store package root from AppModel's per-user registry entry instead.
        return (storePackageRoots ?? FindStorePackageRoots())
            .Select(directory => Path.Combine(directory, "nyrna.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<string> FindStorePackageRoots()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        const string packagesRegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using RegistryKey? packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(packagesRegistryPath, writable: false);
            if (packages == null) return Array.Empty<string>();

            var roots = new System.Collections.Generic.List<string>();
            foreach (string packageName in packages.GetSubKeyNames().Where(name =>
                name.StartsWith("33694MerrittCodes.Nyrna_", StringComparison.OrdinalIgnoreCase)))
            {
                using RegistryKey? package = packages.OpenSubKey(packageName, writable: false);
                if (package?.GetValue("PackageRootFolder") is string root && !string.IsNullOrWhiteSpace(root))
                    roots.Add(root);
            }
            return roots;
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<bool> SuspendProcessAsync(int processId, CancellationToken ct)
    {
        string? exe = ExecutablePath;
        if (exe == null)
        {
            // Fallback to Win32 native suspend if Nyrna isn't present
            var native = new WindowsNativeSuspendProvider();
            return await native.SuspendProcessAsync(processId, ct).ConfigureAwait(false);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--suspend {processId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ResumeProcessAsync(int processId, CancellationToken ct)
    {
        string? exe = ExecutablePath;
        if (exe == null)
        {
            // Fallback to Win32 native resume if Nyrna isn't present
            var native = new WindowsNativeSuspendProvider();
            return await native.ResumeProcessAsync(processId, ct).ConfigureAwait(false);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--resume {processId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class NoneGameSuspendProvider : IGameSuspendProvider
{
    public string Id => "none";
    public string Name => "No Suspension (Manage Game Lifecycle Directly)";
    public bool IsAvailable => true;
    public Task<bool> SuspendProcessAsync(int processId, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ResumeProcessAsync(int processId, CancellationToken ct) => Task.FromResult(true);
}
