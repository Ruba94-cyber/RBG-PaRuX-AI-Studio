using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// UIA-pattern-first desktop actions using an explicitly installed WinApp CLI.
    /// Called ONLY from an already-approved Computer Use agent task. Every action
    /// is pinned to the exact foreground PID captured immediately before inference;
    /// any foreground switch refuses the action. No shell, free-form commands, UAC,
    /// process spawning of the target app, or network. The WinApp executable itself
    /// must be explicitly configured through PARUX_WINAPP_CLI.
    /// </summary>
    internal static class WindowsUiActionService
    {
        private const int MaxValueChars = 512;
        private const int MaxOutputChars = 4_096;
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(12);

        internal sealed record Result(bool Success, string Status);

        internal static bool IsSafeSelector(string? value) =>
            !string.IsNullOrEmpty(value)
            && value.Length <= 128
            && IsAsciiLetterOrDigit(value[0])
            && value.All(ch => IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_');

        private static bool IsAsciiLetterOrDigit(char ch) =>
            ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

        /// <summary>
        /// Enforce the actual observation boundary: a syntactically safe but invented
        /// selector is not actionable. WinApp inspect --json exposes nested UIA
        /// elements; a selector must appear as an exact JSON string value, never
        /// merely as a substring of a description or command in untrusted UI text.
        /// </summary>
        /// <summary>
        /// Only a selector field on an actual element under windows[].elements[]
        /// (including recursive children[]) is usable. Reject arbitrary selector
        /// fields in errors, metadata, control labels or unrelated JSON.
        /// </summary>
        internal static bool IsSelectorObserved(string? tree, string? selector, long expectedHwnd = 0)
        {
            if (!IsSafeSelector(selector) || string.IsNullOrWhiteSpace(tree)
                || tree.Length > 8_000) return false;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(tree);
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !root.TryGetProperty("windows", out var windows)
                    || windows.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return false;
                int matches = 0;
                foreach (var window in windows.EnumerateArray())
                {
                    if (window.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !window.TryGetProperty("elements", out var elements)
                        || elements.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    if (expectedHwnd > 0
                        && (!window.TryGetProperty("hwnd", out var observedHandle)
                            || !MatchesWindow(observedHandle, expectedHwnd)))
                        continue;
                    if (!CountMatchingSelectors(elements, selector!, ref matches))
                        return false;
                    if (matches > 1) return false;
                }
                return matches == 1;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>A multi-window WinApp response must never authorize a selector
        /// from a different HWND. Accept numeric or decimal-string handles.</summary>
        private static bool MatchesWindow(System.Text.Json.JsonElement handle, long expectedHwnd) =>
            handle.ValueKind == System.Text.Json.JsonValueKind.Number
                && handle.TryGetInt64(out long number) && number == expectedHwnd
            || handle.ValueKind == System.Text.Json.JsonValueKind.String
                && long.TryParse(handle.GetString(), out long parsed) && parsed == expectedHwnd;

        /// <summary>Require one exact selector within the scoped UIA window.
        /// Ambiguous duplicates are unsafe to invoke, even when all look alike.</summary>
        private static bool CountMatchingSelectors(
            System.Text.Json.JsonElement elements, string selector, ref int matches, int depth = 0)
        {
            if (depth > 24 || elements.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;
            foreach (var element in elements.EnumerateArray())
            {
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return false;
                if (element.TryGetProperty("selector", out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(value.GetString(), selector, StringComparison.Ordinal))
                {
                    matches++;
                    if (matches > 1) return true;
                }
                if (element.TryGetProperty("children", out var children)
                    && !CountMatchingSelectors(children, selector, ref matches, depth + 1))
                    return false;
            }
            return true;
        }

        internal static bool IsValidAction(
            string? action, string? selector, string? text, int observedPid)
        {
            if (!IsSafeSelector(selector) || observedPid <= 0) return false;
            return action switch
            {
                "ui_invoke" or "ui_wait_for" => true,
                "ui_set_value" => text != null && text.Length <= MaxValueChars
                    && !text.Contains('\0'),
                _ => false
            };
        }

        public static async Task<Result> ExecuteAsync(
            string? action, string? selector, string? text,
            int observedPid, long observedHwnd, CancellationToken cancellationToken,
            Func<bool>? mayContinue = null, Func<bool>? mayStart = null)
        {
            if (!OperatingSystem.IsWindows())
                return new Result(false, "windows_required");
            if (!IsValidAction(action, selector, text, observedPid))
                return new Result(false, "invalid_action");
            if ((mayStart != null && !mayStart()) || (mayContinue != null && !mayContinue()) || observedHwnd == 0 ||
                WindowsUiTreeObservationService.GetForegroundWindowIdentity()
                    != (observedPid, observedHwnd))
                return new Result(false, "foreground_changed");

            string? executable = Environment.GetEnvironmentVariable("PARUX_WINAPP_CLI");
            if (!WindowsUiTreeObservationService.IsConfiguredExecutable(executable))
                return new Result(false, "winapp_not_configured");

            string verb = action == "ui_invoke" ? "invoke"
                : action == "ui_set_value" ? "set-value" : "wait-for";
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            start.ArgumentList.Add("ui");
            start.ArgumentList.Add(verb);
            start.ArgumentList.Add(selector!);
            if (verb == "set-value")
                start.ArgumentList.Add(text!);
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add(observedHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (verb == "wait-for")
            {
                start.ArgumentList.Add("--timeout");
                start.ArgumentList.Add("3000");
            }
            start.ArgumentList.Add("--json");

            // Protect against a window switch that races argument construction.
            if ((mayStart != null && !mayStart()) || (mayContinue != null && !mayContinue()) || observedHwnd == 0 ||
                WindowsUiTreeObservationService.GetForegroundWindowIdentity()
                    != (observedPid, observedHwnd))
                return new Result(false, "foreground_changed");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(ActionTimeout);
            using var process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                // Accepted residual risk, not a missing check. The foreground is re-read above, but not between this start and the
                // action. PaRuX invokes the CLI as one action command, so there is no point between Start() and the action where PaRuX
                // can re-verify the foreground, and a check after Start() would race the CLI. The observed handle is passed with -w,
                // which is how the action is addressed to that window.
                if (!process.Start()) return new Result(false, "winapp_start_failed");
                started = true;
                Task<string> stdout = ReadBoundedAsync(process.StandardOutput, linked.Token);
                Task<string> stderr = ReadBoundedAsync(process.StandardError, linked.Token);
                Task exit = process.WaitForExitAsync(linked.Token);
                if (!await WaitWhileAuthorizedAsync(exit, mayContinue, linked.Token).ConfigureAwait(false))
                {
                    linked.Cancel();
                    return new Result(false, "authorization_revoked");
                }
                await Task.WhenAll(stdout, stderr, exit).ConfigureAwait(false);
                if (mayContinue != null && !mayContinue())
                    return new Result(false, "authorization_revoked");
                if (process.ExitCode != 0) return new Result(false, "uia_action_failed");
                string envelope = await stdout.ConfigureAwait(false);
                if (!WindowsUiActionResultPolicy.IsDelivered(action, envelope))
                    return new Result(false, "uia_action_response_invalid");
                // Success is only successful delivery by WinApp CLI, or a positive
                // wait-for observation. The next UI observation and independent
                // verifier must still prove the requested end-to-end result.
                return new Result(true, action == "ui_wait_for"
                    ? "uia_control_observed_unverified_goal"
                    : "action_delivered_unverified");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Result(false, "uia_timeout");
            }
            catch (IOException)
            {
                return new Result(false, "uia_output_invalid");
            }
            catch (Win32Exception)
            {
                return new Result(false, "winapp_start_failed");
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }

        internal static async Task<bool> WaitWhileAuthorizedAsync(
            Task exit, Func<bool>? mayContinue, CancellationToken cancellationToken)
        {
            while (!exit.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mayContinue != null && !mayContinue()) return false;
                await Task.WhenAny(exit, Task.Delay(50, cancellationToken)).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return mayContinue == null || mayContinue();
        }

        private static async Task<string> ReadBoundedAsync(
            StreamReader reader, CancellationToken cancellationToken)
        {
            var result = new StringBuilder();
            char[] chunk = new char[512];
            while (true)
            {
                int n = await reader.ReadAsync(chunk.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0) return result.ToString();
                if (result.Length + n > MaxOutputChars)
                    throw new IOException("WinApp output exceeds the permitted limit.");
                result.Append(chunk, 0, n);
            }
        }
    }
}
