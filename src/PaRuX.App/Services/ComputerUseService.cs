using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class ComputerActionPlan
    {
        public string Name { get; set; } = string.Empty;
        public string Executable { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
    }

    public sealed class ComputerActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Windows-only allowlisted launcher. Chat text is never interpreted as a shell command:
    /// TryPlan resolves only built-in or explicitly configured applications, while ExecuteAsync
    /// executes an already-authorized serialized plan. Authorization is owned by ChatSession:
    /// Ask mode requires a one-task approval; Full Computer Use is the explicit persistent
    /// pre-authorization and therefore executes the same plan immediately.
    /// </summary>
    public static class ComputerUseService
    {
        private static readonly Regex OpenIntent = new Regex(
            @"\b(apri|avvia|lancia|esegui|open|launch|start|run|visita|visit)\b"
          + @"|\b(vai\s+su|naviga\s+(?:a|su)|go\s+to|browse\s+to|navigate\s+to)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly ComputerActionPlan[] BuiltIns =
        {
            new ComputerActionPlan { Name = "Notepad", Executable = "notepad.exe", BuiltIn = true },
            new ComputerActionPlan { Name = "Calculator", Executable = "calc.exe", BuiltIn = true },
            new ComputerActionPlan { Name = "File Explorer", Executable = "explorer.exe", BuiltIn = true },
            new ComputerActionPlan { Name = "Windows Settings", Executable = "ms-settings:", BuiltIn = true }
        };

        private static readonly Dictionary<string, string[]> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Notepad"] = new[] { "notepad", "blocco note", "blocco notes", "note", "notes" },
            ["Calculator"] = new[] { "calculator", "calcolatrice", "calc" },
            ["File Explorer"] = new[] { "file explorer", "esplora file", "explorer" },
            ["Windows Settings"] = new[] { "windows settings", "impostazioni windows", "settings" }
        };

        /// <summary>
        /// Actions that cannot be satisfied by merely launching an application. A simple launch is
        /// the correct fast path for "open Notepad", but "open Notepad AND type X" is a multi-step
        /// goal - matching only the launch half silently discarded the rest of the user's request
        /// (verified live: Notepad opened empty and the "type X" instruction vanished with no
        /// indication it had been dropped). When one of these appears, TryPlan declines so
        /// ChatSession falls through to the real Computer Use agent, which is the path actually
        /// designed for multi-step goals.
        /// </summary>
        private static readonly Regex FollowUpAction = new Regex(
            @"\b(type|write|enter|paste|search|click|press|save|rename|delete|close|"
            + @"scrivi|digita|inserisci|incolla|cerca|clicca|premi|salva|rinomina|elimina|chiudi)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static ComputerActionPlan? TryPlan(string? request)
        {
            if (string.IsNullOrWhiteSpace(request) || !OpenIntent.IsMatch(request)) return null;
            if (FollowUpAction.IsMatch(request)) return null;

            if (BrowserNavigationPlanner.TryFind(request, out BrowserNavigationTarget? browserTarget))
            {
                return new ComputerActionPlan
                {
                    Name = browserTarget!.DisplayName,
                    Executable = "parux-default-browser",
                    Arguments = browserTarget.AbsoluteUri,
                    BuiltIn = true
                };
            }

            foreach (ComputerActionPlan plan in BuiltIns)
            {
                if (BuiltInAliases[plan.Name].Any(alias => ContainsPhrase(request, alias))) return plan;
            }

            foreach (ComputerAppShortcut configured in SettingsStore.Current.Data.ComputerApps ?? new List<ComputerAppShortcut>())
            {
                IEnumerable<string> aliases = new[] { configured.Name, Path.GetFileNameWithoutExtension(configured.ExecutablePath) }
                    .Concat((configured.Aliases ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                if (aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Any(alias => ContainsPhrase(request, alias.Trim())))
                {
                    return new ComputerActionPlan
                    {
                        Name = configured.Name,
                        Executable = configured.ExecutablePath,
                        Arguments = configured.Arguments,
                        BuiltIn = false
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// The ONLY applications an already-approved agent may launch. A name
        /// (not an executable or arbitrary command) must exactly match one
        /// known built-in or an app the user explicitly configured in Settings.
        /// Ambiguous aliases do not select the first item.
        /// </summary>
        internal static ComputerActionPlan? TryPlanAllowedApp(string? exactName)
        {
            var plans = new List<ComputerActionPlan>();
            var candidates = new List<ComputerUseAppSelectionPolicy.Candidate>();

            foreach (ComputerActionPlan builtIn in BuiltIns)
            {
                plans.Add(builtIn);
                candidates.Add(new ComputerUseAppSelectionPolicy.Candidate(
                    builtIn.Name, BuiltInAliases[builtIn.Name]));
            }

            foreach (ComputerAppShortcut entry in SettingsStore.Current.Data.ComputerApps
                ?? new List<ComputerAppShortcut>())
            {
                if (!ComputerUseAppSelectionPolicy.IsValidName(entry.Name))
                    continue;

                string[] aliases = (entry.Aliases ?? string.Empty)
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(ComputerUseAppSelectionPolicy.IsValidName)
                    .ToArray();

                plans.Add(new ComputerActionPlan
                {
                    Name = entry.Name,
                    Executable = entry.ExecutablePath,
                    Arguments = entry.Arguments,
                    BuiltIn = false
                });
                candidates.Add(new ComputerUseAppSelectionPolicy.Candidate(
                    entry.Name, aliases));
            }

            int index = ComputerUseAppSelectionPolicy.FindUniqueIndex(candidates, exactName);
            if (index < 0) return null;
            ComputerActionPlan selected = plans[index];
            return new ComputerActionPlan
            {
                Name = selected.Name,
                Executable = selected.Executable,
                Arguments = selected.Arguments,
                BuiltIn = selected.BuiltIn
            };
        }

        /// <summary>
        /// Bounded labels for the approved Computer Use task prompt. No paths,
        /// arguments or inferred installed applications are exposed; configured
        /// app names with control characters are omitted.
        /// </summary>
        internal static IReadOnlyList<string> GetAllowedAppNames() =>
            BuiltIns.Select(p => p.Name)
                .Concat((SettingsStore.Current.Data.ComputerApps
                    ?? new List<ComputerAppShortcut>())
                    .Select(p => p.Name)
                    .Where(ComputerUseAppSelectionPolicy.IsValidName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray();

        public static string SerializePlan(ComputerActionPlan plan) => JsonSerializer.Serialize(plan);

        public static async Task<ComputerActionResult> ExecuteAsync(string payload)
        {
            if (GamingManager.Current.IsActive)
                return new ComputerActionResult { Message = "Gaming Mode is active. Computer Use is suspended." };

            ComputerActionPlan? plan;
            try { plan = JsonSerializer.Deserialize<ComputerActionPlan>(payload); }
            catch { return new ComputerActionResult { Message = "The saved computer action is unreadable." }; }
            if (plan == null || string.IsNullOrWhiteSpace(plan.Executable))
                return new ComputerActionResult { Message = "The saved computer action is incomplete." };

            if (plan.BuiltIn)
            {
                if (string.Equals(plan.Executable, "parux-default-browser", StringComparison.Ordinal))
                {
                    if (!BrowserNavigationPlanner.TryNormalize(plan.Arguments, out Uri? parsedUri))
                        return new ComputerActionResult { Message = "That browser address is not a valid HTTP or HTTPS URL." };
                    Uri uri = parsedUri!;
                    // This fast path hands the address to the shell, which opens it in the default browser outside
                    // PaRuX. It must honour the same Internet setting that gates PaRuX's own browser tools.
                    string? webRefusal = ComputerUseAuthorizationPolicy.WebOpenRefusal(await IsInternetEnabledAsync().ConfigureAwait(false));
                    if (webRefusal != null)
                        return new ComputerActionResult { Message = webRefusal };
                    plan = new ComputerActionPlan
                    {
                        Name = "Browser · " + uri.Host,
                        Executable = uri.AbsoluteUri,
                        BuiltIn = true
                    };
                }
                else
                {
                    ComputerActionPlan? builtIn = BuiltIns.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, plan.Name, StringComparison.Ordinal)
                        && string.Equals(candidate.Executable, plan.Executable, StringComparison.OrdinalIgnoreCase));
                    if (builtIn == null)
                        return new ComputerActionResult { Message = "That built-in action is not recognized." };
                    plan = builtIn;
                }
            }
            else
            {
                ComputerAppShortcut? allowed = (SettingsStore.Current.Data.ComputerApps ?? new List<ComputerAppShortcut>())
                    .FirstOrDefault(a => string.Equals(a.ExecutablePath, plan.Executable, StringComparison.OrdinalIgnoreCase));
                if (allowed == null || !File.Exists(allowed.ExecutablePath))
                    return new ComputerActionResult { Message = "That app is no longer in the Computer Use allowlist." };
                plan.Arguments = allowed.Arguments;
            }

            try
            {
                // plan.Executable/Arguments come only from the fixed BuiltIns list or the user's
                // SettingsStore.ComputerApps allowlist (both checked above), never from the
                // untrusted deserialized payload directly.
                var info = new ProcessStartInfo { FileName = plan.Executable, UseShellExecute = true }; // parux:shell-execute-reviewed
                if (!string.IsNullOrWhiteSpace(plan.Arguments)) info.Arguments = plan.Arguments;
                Process? process = Process.Start(info);
                if (process == null) return new ComputerActionResult { Message = plan.Name + " did not start." };
                await Task.Delay(900);
                bool alive;
                try { alive = !process.HasExited; } catch { alive = true; }
                return new ComputerActionResult
                {
                    Success = alive || plan.BuiltIn,
                    Message = alive ? plan.Name + " opened and its process was verified."
                        : plan.BuiltIn ? "Windows accepted the " + plan.Name + " launch request."
                        : plan.Name + " closed immediately."
                };
            }
            catch (Exception ex)
            {
                Log.Write("Computer action failed: " + ex);
                return new ComputerActionResult { Message = "Couldn't open " + plan.Name + ": " + ex.Message };
            }
        }

        private static bool ContainsPhrase(string text, string phrase) =>
            text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Reads the Internet toggle that PaRuX's browser tools also obey. A setting that cannot be read
        /// counts as off, so an unreachable Core never turns into an unrestricted web launch.</summary>
        private static async Task<bool> IsInternetEnabledAsync()
        {
            try
            {
                return (await InternetClient.Current.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).Enabled;
            }
            catch (Exception ex)
            {
                Log.Write("Computer Use could not read the Internet setting, so web opening stays off: " + ex.Message);
                return false;
            }
        }

    }
}
