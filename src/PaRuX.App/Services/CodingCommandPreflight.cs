using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Deterministic defense-in-depth review for model-requested coding shell commands.
    ///
    /// This is deliberately not advertised as a complete shell AST or as the security sandbox:
    /// WindowsCodingSandbox remains the authoritative OS boundary and still runs every accepted
    /// command in a no-network AppContainer. The preflight's job is narrower and fail-closed for
    /// operations PaRuX can recognize mechanically as host/global/destructive or as network/package
    /// acquisition that belongs in the explicit restore_packages path. Unknown ordinary developer
    /// commands remain AppContainer-contained rather than being falsely labelled safe.
    /// </summary>
    public static class CodingCommandPreflight
    {
        private const string GitCommandRefusal = "Git commands cannot run inside the AppContainer sandbox. Use the dedicated git_status, git_diff, or git_log actions instead.";

        public enum Classification
        {
            SafeReadOnly,
            WorkspaceMutation,
            ContainedUnknown,
            Denied
        }

        public sealed record Review(
            Classification Kind,
            string Reason,
            IReadOnlyList<string> Capabilities);

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex PowerShellEncoded = new(
            @"\b(?:powershell|pwsh)(?:\.exe)?\b[^\r\n]*(?:-e\b|-enc\b|-encodedcommand\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex EnvironmentMutation = new(
            @"(?:^|[&|]\s*)(?:setx\b|set\s+(?:path|comspec|pathext|git_[a-z0-9_]+|http_proxy|https_proxy)\s*=|(?:powershell|pwsh)(?:\.exe)?\b[^\r\n]*\$env:(?:path|comspec|pathext|git_[a-z0-9_]+|http_proxy|https_proxy)\s*=)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex AbsoluteSystemRedirection = new(
            @"(?:>|>>|2>|2>>|1>|1>>)\s*(?:[a-z]:\\(?:windows|program files|programdata)\\|\\\\[^\\]+\\[^\\]+\\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        private static readonly string[] HardDeniedFragments =
        {
            "rm -rf /", "rm -rf ~", "rm -rf *", "format ", "mkfs", "dd if=",
            "del /s /q c:\\", "del /f /s /q c:\\", "rmdir /s /q c:\\", "rd /s /q c:\\",
            "shutdown", "diskpart", ":(){ :|:& };:", ":(){:|:&};:",
            "bcdedit", "vssadmin delete", "wbadmin delete", "cipher /w", "manage-bde",
            "net user", "net localgroup", "sc create", "sc delete", "schtasks /create", "schtasks /delete",
            "reg add", "reg delete", "reg import", "reg restore", "runas ", "-verb runas",
            "git reset --hard", "git clean -f", "git clean -d", "git checkout -- .", "git checkout .",
            "git restore .", "git restore --worktree .", "git push", "git credential", "git config --global",
            "git config --system"
        };

        private static readonly (string Prefix, string Guidance)[] PackageAcquisitionPrefixes =
        {
            ("dotnet restore", "dotnet"),
            ("nuget restore", "dotnet"),
            ("npm install", "npm"),
            ("npm i ", "npm"),
            ("npm ci", "npm"),
            ("pnpm install", "pnpm"),
            ("pnpm i", "pnpm"),
            ("yarn install", "yarn"),
            ("yarn add", "yarn"),
            ("pip install", "uv/pip"),
            ("python -m pip install", "uv/pip"),
            ("python3 -m pip install", "uv/pip"),
            ("uv sync", "uv"),
            ("uv pip install", "uv"),
            ("cargo fetch", "cargo"),
            ("cargo install", "cargo"),
            ("go get", "go"),
            ("go mod download", "go"),
            ("mvn dependency:", "maven"),
            ("gradle dependencies", "gradle"),
            ("gradlew dependencies", "gradle")
        };

        private static readonly string[] NetworkToolPrefixes =
        {
            "curl ", "curl.exe ", "wget ", "wget.exe ", "aria2c ", "bitsadmin ",
            "certutil -urlcache", "invoke-webrequest ", "invoke-restmethod ", "iwr ", "irm ",
            "ssh ", "scp ", "sftp ", "ftp ", "telnet ", "nc ", "ncat "
        };

        private static readonly string[] SafeReadOnlyPrefixes =
        {
            "git status", "git diff", "git log", "git show", "git rev-parse", "git branch --show-current",
            "dotnet --info", "dotnet --version", "node --version", "npm --version", "pnpm --version",
            "yarn --version", "python --version", "python3 --version", "git --version", "cargo --version",
            "go version", "java -version", "javac -version", "where ", "where.exe ", "type ", "dir ", "echo "
        };

        private static readonly string[] WorkspaceMutationPrefixes =
        {
            "dotnet build", "dotnet test", "dotnet publish", "npm test", "npm run ", "pnpm test", "pnpm run ",
            "yarn test", "yarn run ", "cargo build", "cargo test", "go build", "go test", "mvn test", "mvn verify",
            "mvn package", "gradle test", "gradlew test", "cmake ", "ninja ", "make ", "msbuild ", "git add ", "git commit"
        };

        public static Review Analyze(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return new Review(Classification.SafeReadOnly, "Empty command.", Array.Empty<string>());

            string normalized = Normalize(command);

            foreach (string denied in HardDeniedFragments)
            {
                if (normalized.Contains(denied, StringComparison.OrdinalIgnoreCase))
                    return Denied("Command contains a mechanically denied destructive/host-global operation: " + denied.Trim() + ".");
            }

            if (SafeIsMatch(PowerShellEncoded, command))
                return Denied("Encoded PowerShell is not accepted from a model-requested shell action; use an inspectable command or a first-class PaRuX tool.");

            if (SafeIsMatch(EnvironmentMutation, command))
                return Denied("The command attempts to replace a security-relevant process environment variable. PaRuX supplies a sanitized sandbox environment instead.");

            if (SafeIsMatch(AbsoluteSystemRedirection, command))
                return Denied("Redirection to an absolute system/network location is not accepted by the coding shell. Write only through the workspace-scoped file tools.");

            foreach ((string prefix, string ecosystem) in PackageAcquisitionPrefixes)
            {
                if (StartsWithCommand(normalized, prefix))
                    return Denied("Dependency acquisition is a separate capability. Use restore_packages for " + ecosystem + " so Core can apply the explicit network grant and deterministic restore policy.");
            }

            foreach (string prefix in NetworkToolPrefixes)
            {
                if (StartsWithCommand(normalized, prefix))
                    return Denied("General network clients are not available through run_command. Use an authorized PaRuX web/research tool or restore_packages instead.");
            }

            if (IsGitCommandInvocation(command))
                return Denied(GitCommandRefusal);

            if (ContainsNestedShell(normalized))
                return new Review(
                    Classification.ContainedUnknown,
                    "Nested shell invocation is not mechanically promoted as safe; it remains confined to the no-network AppContainer.",
                    new[] { "process", "workspace" });

            foreach (string prefix in SafeReadOnlyPrefixes)
            {
                if (StartsWithCommand(normalized, prefix))
                    return new Review(Classification.SafeReadOnly, "Known read-only developer command.", new[] { "read-workspace", "process" });
            }

            foreach (string prefix in WorkspaceMutationPrefixes)
            {
                if (StartsWithCommand(normalized, prefix))
                    return new Review(Classification.WorkspaceMutation, "Known build/test/version-control command; writes remain AppContainer/workspace confined.", new[] { "read-workspace", "write-workspace", "process" });
            }

            return new Review(
                Classification.ContainedUnknown,
                "Command is not on the deterministic safe list. It is not called safe; execution, if allowed, remains in the no-network AppContainer with sanitized credentials/environment.",
                new[] { "process", "workspace" });
        }

        public static string? DenyReasonFor(string? command)
        {
            Review review = Analyze(command);
            if (review.Kind != Classification.Denied) return null;
            return string.Equals(review.Reason, GitCommandRefusal, StringComparison.Ordinal)
                ? review.Reason
                : review.Reason + " The command was not run.";
        }

        /// <summary>
        /// Plan mode admits only mechanically recognized read-only commands. Reject shell
        /// composition and redirection before consulting the conservative command allowlist.
        /// This is an admission policy; the AppContainer remains the execution boundary.
        /// </summary>
        public static bool IsReadOnlyPlanCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            // Probe the normalized text: the shell treats a tab after "git" as a separator too.
            string gitProbe = Normalize(command);
            // Git's dedicated first-class actions are the only supported Git surface. The command
            // executor refuses shell Git even when it is read-only, so Plan uses the same boundary.
            if (gitProbe.StartsWith("git ", StringComparison.OrdinalIgnoreCase)
                || gitProbe.StartsWith("git.exe ", StringComparison.OrdinalIgnoreCase))
                return false;
            if (command.IndexOfAny(new[] { ';', '\r', '\n', '|', '&', '>', '<', '`', '$', '%', '!', '(', ')' }) >= 0
                || command.Contains("$(", StringComparison.Ordinal)
                || command.Contains("${", StringComparison.Ordinal))
                return false;

            return Analyze(command).Kind == Classification.SafeReadOnly;
        }

        private static Review Denied(string reason) =>
            new(Classification.Denied, reason, Array.Empty<string>());

        private static bool SafeIsMatch(Regex regex, string input)
        {
            try { return regex.IsMatch(input); }
            catch (RegexMatchTimeoutException) { return true; }
        }

        private static string Normalize(string command)
        {
            string value = command.Trim().Replace('\t', ' ');
            try { value = Whitespace.Replace(value, " "); }
            catch (RegexMatchTimeoutException) { }
            return value;
        }

        private static readonly char[] GitTokenSeparators = { ' ', '\t', '\r', '\n', '&', '|', ';', '(', ')', '<', '>' };
        private static readonly char[] GitTokenEscapes = { '"', '\'', '^', '`', '@' };
        private static readonly HashSet<string> GitProgramNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "git", "git.exe", "git.cmd", "git.bat", "git.com"
        };

        /// <summary>
        /// Refuses git when any command-position token names git, not only the leading token.
        /// cmd.exe runs the command text as a script and resolves program names after removing quote,
        /// caret, backtick and at-sign escapes, so chaining, call/start, nested shells and FOR loops
        /// all reach git. Scanning every token over-refuses a bare "git" argument; that is the
        /// accepted trade-off for a refusal policy.
        /// </summary>
        private static bool IsGitCommandInvocation(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            foreach (string raw in command.Split(GitTokenSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = new string(raw.Where(c => Array.IndexOf(GitTokenEscapes, c) < 0).ToArray()).TrimEnd('.');
                int separator = Math.Max(token.LastIndexOf('\\'), token.LastIndexOf('/'));
                if (GitProgramNames.Contains(token[(separator + 1)..]))
                    return true;
            }
            return false;
        }

        private static bool StartsWithCommand(string command, string prefix)
        {
            if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string separator in new[] { "&&", "||", "&", "|" })
            {
                int offset = 0;
                while (true)
                {
                    int index = command.IndexOf(separator, offset, StringComparison.Ordinal);
                    if (index < 0) break;
                    string tail = command[(index + separator.Length)..].TrimStart();
                    if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    offset = index + separator.Length;
                }
            }
            return false;
        }

        private static bool ContainsNestedShell(string command)
        {
            string lower = command.ToLowerInvariant();
            return lower.StartsWith("cmd /c ", StringComparison.Ordinal)
                || lower.StartsWith("cmd.exe /c ", StringComparison.Ordinal)
                || lower.StartsWith("powershell ", StringComparison.Ordinal)
                || lower.StartsWith("powershell.exe ", StringComparison.Ordinal)
                || lower.StartsWith("pwsh ", StringComparison.Ordinal)
                || lower.StartsWith("pwsh.exe ", StringComparison.Ordinal)
                || lower.StartsWith("bash -c ", StringComparison.Ordinal)
                || lower.StartsWith("sh -c ", StringComparison.Ordinal);
        }
    }
}
