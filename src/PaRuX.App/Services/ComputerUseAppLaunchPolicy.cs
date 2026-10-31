using System;
using System.Collections.Generic;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// The multi-step desktop agent may only start registered GUI application
    /// binaries. This is a defense-in-depth guard *in addition to* the existing
    /// user-configured allowlist, task approval, execution revalidation and
    /// Windows process policy. It is not an OS sandbox: a renamed executable or
    /// a GUI app with dangerous arguments still requires stronger task grants.
    ///
    /// Scripts, shell protocol handlers and common interpreters must go through
    /// the separate sandboxed coding/command path, not open_app.
    /// </summary>
    internal static class ComputerUseAppLaunchPolicy
    {
        private static readonly HashSet<string> CommandRunners =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "cmd.exe", "command.com", "powershell.exe", "pwsh.exe",
                "wscript.exe", "cscript.exe", "mshta.exe",
                "rundll32.exe", "regsvr32.exe", "msiexec.exe",
                "bash.exe", "sh.exe", "wsl.exe",
                "python.exe", "pythonw.exe", "py.exe", "node.exe",
                "perl.exe", "ruby.exe", "java.exe",
                "git.exe", "curl.exe", "wget.exe", "certutil.exe",
                "schtasks.exe", "reg.exe"
            };

        internal static bool IsAllowedConfiguredExecutable(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.IsPathFullyQualified(executablePath)
                || !string.Equals(Path.GetExtension(executablePath), ".exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return !CommandRunners.Contains(Path.GetFileName(executablePath));
        }
    }
}
