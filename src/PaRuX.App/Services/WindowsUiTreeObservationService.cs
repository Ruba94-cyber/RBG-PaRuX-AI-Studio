using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only UI Automation evidence for an explicitly authorized desktop UI-verification
    /// task. The target is the physical foreground window's PID, obtained from Windows (never
    /// supplied by the language model). The optional WinApp CLI executable must have an
    /// absolute path explicitly configured in PARUX_WINAPP_CLI; no PATH resolution or install.
    ///
    /// This does not permit clicking, typing, process launch of the app under test, screenshot,
    /// network use or an independent grant. The calling worker must have Full Computer Use
    /// enabled before invoking it. It is supplemental evidence, not a PASS/FAIL verdict.
    /// </summary>
    internal static class WindowsUiTreeObservationService
    {
        private const int MaxReturnedCharacters = 8_000;
        private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(12);

        internal sealed record Observation(string Status, string Tree, int ProcessId = 0, long WindowHandle = 0)
        {
            public bool HasEvidence => Status == "observed" && Tree.Length > 0;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        internal static bool IsConfiguredExecutable(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);

        /// <summary>
        /// WinApp inspect --json reports a root object containing windows[], each
        /// with hwnd and elements[]. A syntactically valid error JSON is NOT UI
        /// evidence. Match the requested HWND to prevent an unrelated window tree
        /// from authorizing subsequent actions.
        /// </summary>
        internal static Observation ValidateJsonEvidence(string? raw, long expectedHwnd = 0)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new Observation("empty", string.Empty);
            if (raw.Length > MaxReturnedCharacters)
                return new Observation("too_large", string.Empty);
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(raw);
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !root.TryGetProperty("windows", out var windows)
                    || windows.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return new Observation("invalid_ui_tree", string.Empty);

                bool validWindow = false;
                foreach (System.Text.Json.JsonElement window in windows.EnumerateArray())
                {
                    if (window.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !window.TryGetProperty("hwnd", out var handle)
                        || !window.TryGetProperty("elements", out var elements)
                        || elements.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    long observed;
                    if (handle.ValueKind == System.Text.Json.JsonValueKind.Number
                        && handle.TryGetInt64(out observed))
                    {
                        // parsed as numeric HWND
                    }
                    else if (handle.ValueKind == System.Text.Json.JsonValueKind.String
                        && long.TryParse(handle.GetString(), out observed))
                    {
                        // tolerate numeric string in older WinApp versions
                    }
                    else continue;

                    if (observed <= 0 || (expectedHwnd > 0 && observed != expectedHwnd))
                        continue;
                    validWindow = true;
                    break;
                }
                return validWindow ? new Observation("observed", raw)
                    : new Observation("target_unverified", string.Empty);
            }
            catch (System.Text.Json.JsonException)
            {
                return new Observation("invalid_json", string.Empty);
            }
        }

        internal static (int Pid, long Hwnd) GetForegroundWindowIdentity()
        {
            if (!OperatingSystem.IsWindows()) return (0, 0);
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0
                || pid == 0 || pid > int.MaxValue) return (0, 0);
            return ((int)pid, window.ToInt64());
        }

        internal static int GetForegroundProcessId() => GetForegroundWindowIdentity().Pid;

        public static async Task<Observation> CaptureForegroundAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows()) return new Observation("not_windows", string.Empty);
            string? executable = Environment.GetEnvironmentVariable("PARUX_WINAPP_CLI");
            if (!IsConfiguredExecutable(executable))
                return new Observation("winapp_not_configured", string.Empty);

            (int pid, long hwnd) = GetForegroundWindowIdentity();
            if (pid == 0 || hwnd == 0)
                return new Observation("foreground_unavailable", string.Empty);

            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            // Single immutable, read-only UIA verb. All dynamic input is a numeric PID
            // read from the OS and not inferred from prompts or untrusted web content.
            foreach (string argument in new[] {
                "ui", "inspect", "-w", hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--interactive", "--json"
            })
                start.ArgumentList.Add(argument);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(MaxDuration);
            using var process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                if (!process.Start()) return new Observation("winapp_start_failed", string.Empty);
                started = true;

                // Stop immediately on unbounded output. Never dump raw screenshots or UIA
                // text into task logs, which may be visible on paired devices.
                Task<string> output = ReadBoundedAsync(process.StandardOutput, linked.Token);
                Task stderr = DrainBoundedAsync(process.StandardError, linked.Token);
                await Task.WhenAll(output, stderr, process.WaitForExitAsync(linked.Token))
                    .ConfigureAwait(false);
                if (process.ExitCode != 0) return new Observation("winapp_failed", string.Empty);
                return ValidateJsonEvidence(await output.ConfigureAwait(false), hwnd)
                    with { ProcessId = pid, WindowHandle = hwnd };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Observation("winapp_timeout", string.Empty);
            }
            catch (IOException)
            {
                return new Observation("winapp_io_error", string.Empty);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new Observation("winapp_start_failed", string.Empty);
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        /// <summary>
        /// Read-only final QA inspection of a window ALREADY observed and
        /// authorized through focus_window/ui_assert. This method does not
        /// focus a window or accept target identifiers from a model. It
        /// reattests the HWND/PID/executable before AND after WinApp inspect;
        /// the caller must separately check the live app allowlist, current
        /// task authority, STOP/Gaming Mode, and all expected controls.
        /// An inaccessible or replaced background window fails closed.
        /// </summary>
        internal static async Task<Observation> CaptureVerifiedWindowAsync(
            ComputerUseWindowFocusPolicy.Candidate? original,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
                return new Observation("not_windows", string.Empty);
            if (original == null
                || !ComputerUseWindowFocusPolicy.IsUnchanged(original,
                    DesktopAutomationService.CaptureWindowFocusIdentity(
                        new IntPtr(original.WindowHandle))))
                return new Observation("window_identity_changed", string.Empty);

            string? executable = Environment.GetEnvironmentVariable("PARUX_WINAPP_CLI");
            if (!IsConfiguredExecutable(executable))
                return new Observation("winapp_not_configured", string.Empty);

            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (string argument in new[] {
                "ui", "inspect", "-w",
                original.WindowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--interactive", "--json"
            })
                start.ArgumentList.Add(argument);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(MaxDuration);
            using var process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                if (!process.Start()) return new Observation("winapp_start_failed", string.Empty);
                started = true;
                Task<string> output = ReadBoundedAsync(process.StandardOutput, linked.Token);
                Task stderr = DrainBoundedAsync(process.StandardError, linked.Token);
                await Task.WhenAll(output, stderr, process.WaitForExitAsync(linked.Token))
                    .ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return new Observation("winapp_failed", string.Empty);
                if (!ComputerUseWindowFocusPolicy.IsUnchanged(original,
                    DesktopAutomationService.CaptureWindowFocusIdentity(
                        new IntPtr(original.WindowHandle))))
                    return new Observation("window_identity_changed", string.Empty);

                return ValidateJsonEvidence(await output.ConfigureAwait(false),
                    original.WindowHandle) with {
                        ProcessId = original.ProcessId,
                        WindowHandle = original.WindowHandle
                    };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Observation("winapp_timeout", string.Empty);
            }
            catch (IOException)
            {
                return new Observation("winapp_io_error", string.Empty);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new Observation("winapp_start_failed", string.Empty);
            }
            finally
            {
                if (started)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        private static async Task<string> ReadBoundedAsync(
            StreamReader reader, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            char[] chunk = new char[1_024];
            while (true)
            {
                int count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) return builder.ToString();
                if (builder.Length + count > MaxReturnedCharacters)
                    throw new IOException("UIA result exceeds safe prompt budget.");
                builder.Append(chunk, 0, count);
            }
        }

        private static async Task DrainBoundedAsync(
            StreamReader reader, CancellationToken cancellationToken)
        {
            char[] chunk = new char[1_024];
            int total = 0;
            while (total < 8_192)
            {
                int count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
        }
    }
}
