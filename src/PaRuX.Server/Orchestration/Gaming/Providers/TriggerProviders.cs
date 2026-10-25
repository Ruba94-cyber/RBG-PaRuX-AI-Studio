using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class ManualTriggerProvider : IGamingTriggerProvider
{
    public string Id => "manual";
    public string Name => "Manual Trigger";
    public bool IsDetected => true;

    private bool _manualState;

    public void SetTriggered(bool active) => _manualState = active;

    public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct) =>
        Task.FromResult(_manualState);
}

/// <summary>
/// Composite display trigger. Combines display presence (e.g. EDID detection) with user activity
/// or controller interaction to avoid false-positives when a TV/monitor is powered off but EDID remains attached.
/// </summary>
public sealed class WindowsDisplayTriggerProvider : IGamingTriggerProvider
{
    public string Id => "display";
    public string Name => "Windows Display / Composite Trigger";
    public bool IsDetected => OperatingSystem.IsWindows();

    public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        try
        {
            // 1. Check if multiple displays or external display is active
            int displayCount = GetDisplayCount();
            if (displayCount <= 1) return Task.FromResult(false);

            // 2. Check composite setting: if "require_controller" is enabled in settings, verify controller presence
            if (profile.TriggerSettings.TryGetValue("require_controller", out string? reqCtrl)
                && bool.TryParse(reqCtrl, out bool requireCtrl) && requireCtrl)
            {
                bool controllerConnected = IsGameControllerConnected();
                return Task.FromResult(controllerConnected);
            }

            // Default: display presence + active user session
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static int GetDisplayCount()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int count = GetSystemMetrics(80); // SM_CMONITORS
                return count > 0 ? count : 1;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    private static bool IsGameControllerConnected()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // Check if XInput or XOutput or Sunshine virtual input is active
            Process[] processes = Process.GetProcesses();
            string[] controllerProcesses = { "xoutput", "inputmapper", "ds4windows", "steam" };
            return processes.Any(p => controllerProcesses.Any(cp => p.ProcessName.Equals(cp, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }
}

/// <summary>
/// Detects Sunshine remote streaming sessions via Sunshine status API / process check / state files.
/// </summary>
public sealed class SunshineSessionTriggerProvider : IGamingTriggerProvider
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string[] SunshineProcessNames = { "sunshine", "sunshine.exe" };

    public string Id => "sunshine";
    public string Name => "Sunshine Remote Stream Trigger";

    public bool IsDetected
    {
        get => IsInstalledOnMachine;
    }

    internal static bool IsInstalledOnMachine => ExecutablePath != null || IsProcessRunning();

    internal static string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return FindExecutable(localAppData, programFiles);
        }
    }

    internal static string? FindExecutable(string localAppData, string programFiles)
    {
        string[] candidates =
        {
            Path.Combine(programFiles, "Sunshine", "sunshine.exe"),
            Path.Combine(localAppData, "Programs", "Sunshine", "sunshine.exe"),
            @"C:\Sunshine\sunshine.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!IsProcessRunning()) return false;

        // Try Sunshine HTTP status endpoint if available
        try
        {
            string url = profile.TriggerSettings.TryGetValue("sunshine_url", out string? customUrl) && !string.IsNullOrWhiteSpace(customUrl)
                ? customUrl
                : "http://127.0.0.1:47989/api/config"; // Default Sunshine API port

            using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (json.Contains("\"session_active\":true", StringComparison.OrdinalIgnoreCase)
                    || json.Contains("\"status\":\"streaming\"", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        // Fallback: Check if Sunshine child streaming process (e.g. steam, playnite, or game app under Sunshine) is active
        try
        {
            Process[] processes = Process.GetProcessesByName("sunshine");
            if (processes.Length > 0)
            {
                // If Sunshine is running and actively using GPU/network encoder stream port 47998/47999
                bool isPort47998Active = IsPortInUse(47998) || IsPortInUse(47999) || IsPortInUse(47989);
                return isPort47998Active;
            }
        }
        catch { }

        return false;
    }

    /// <summary>The orchestrator polls this once per second; a full process-table scan each time (with undisposed Process objects)
    /// was the largest idle CPU cost in Core. The answer is reused for a few seconds, which only delays noticing a Sunshine
    /// start by that long.</summary>
    internal static readonly TtlValue<bool> ProcessRunning = new(TimeSpan.FromSeconds(5), ScanForSunshineProcess);

    private static bool IsProcessRunning() => ProcessRunning.Get();

    private static bool ScanForSunshineProcess()
    {
        try
        {
            Process[] found = Process.GetProcessesByName("sunshine");
            bool any = found.Length > 0;
            foreach (Process process in found) process.Dispose();
            return any;
        }
        catch { return false; }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(p => p.Port == port);
        }
        catch { return false; }
    }
}
