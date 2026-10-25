using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Services.Integrations
{
    /// <summary>Where each client keeps its configuration; replaceable so tests never touch the real profile.</summary>
    public sealed record ClientIntegrationEnvironment(string UserHome, string CodexHome, string OpenCodeConfigDirectory)
    {
        public static ClientIntegrationEnvironment Current()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            return new ClientIntegrationEnvironment(
                home,
                string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(home, ".codex") : codexHome,
                Path.Combine(home, ".config", "opencode"));
        }
    }

    /// <summary>
    /// One external agent client. Implementations know the client's own configuration format and
    /// never modify anything that PaRuX did not create (Disconnect is surgical; Restore uses the
    /// pre-connect backup recorded in the manifest).
    /// </summary>
    public interface IClientIntegration
    {
        string ClientId { get; }
        string DisplayName { get; }
        string Protocol { get; }
        /// <summary>Newest client version verified end-to-end with the PaRuX gateway.</summary>
        string TestedVersion { get; }
        string TestFileName { get; }
        string TestMarker { get; }

        IReadOnlyList<string> Locate();
        ClientIntegrationStatus ReadConfiguration(IntegrationManifest? manifest, out string detail);
        IntegrationOutcome Apply(GatewayEndpoint endpoint, IntegrationManifest manifest, IntegrationStore store);
        IntegrationOutcome Remove(IntegrationManifest manifest, IntegrationStore store);
        ProcessStartInfo CreateLaunch(string executable, GatewayEndpoint endpoint, string workingDirectory, IntegrationStore store);
        ProcessStartInfo CreateTest(string executable, GatewayEndpoint endpoint, string workingDirectory, string prompt, IntegrationStore store);
    }

    internal static class IntegrationProcess
    {
        /// <summary>
        /// The only environment a third-party client inherits. Everything else is dropped, so cloud keys, tokens and
        /// other secrets in PaRuX's or the user's session never reach the client's shell tool (CF-07). A client's own
        /// gateway credential is added by that client's launch code, never inherited.
        /// </summary>
        private static readonly string[] InheritedEnvironmentNames =
        {
            "PATH", "PATHEXT", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "TEMP", "TMP",
            "USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA",
            "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "COMMONPROGRAMFILES", "COMPUTERNAME",
            "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "LANG",
            "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "REQUESTS_CA_BUNDLE",
        };

        /// <summary>
        /// Replaces the inherited environment with the allowlist. The project folder is untrusted, so cmd.exe must
        /// also never resolve a program name from it.
        /// </summary>
        internal static void ScrubEnvironment(ProcessStartInfo info)
        {
            var kept = new List<KeyValuePair<string, string>>();
            foreach (string name in InheritedEnvironmentNames)
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) kept.Add(new KeyValuePair<string, string>(name, value));
            }
            info.Environment.Clear();
            foreach (KeyValuePair<string, string> pair in kept) info.Environment[pair.Key] = pair.Value;
            info.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        }

        /// <summary>Headless process with redirected output; .cmd/.bat npm shims run through cmd.exe.</summary>
        public static ProcessStartInfo Headless(string executable, string workingDirectory, IEnumerable<string> arguments)
        {
            bool shim = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            var info = new ProcessStartInfo
            {
                FileName = shim ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            // A project folder is untrusted: cmd.exe and npm shims must never pick up a node/cmd planted there.
            ScrubEnvironment(info);
            if (shim)
            {
                // cmd /s /c strips exactly one outer pair of quotes, so the whole command is wrapped once more.
                info.Arguments = "/d /s /c \"" + CommandLine(executable, arguments) + "\"";
                return info;
            }
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            return info;
        }

        /// <summary>
        /// A visible console that stays open after the client exits. Arguments are fixed PaRuX
        /// values (model ids, tool names); they are validated so cmd.exe never sees metacharacters.
        /// </summary>
        public static ProcessStartInfo Console(string executable, string workingDirectory, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /s /k \"" + CommandLine(executable, arguments) + "\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            ScrubEnvironment(info);
            return info;
        }

        /// <summary>A cmd.exe command line from fixed PaRuX values; anything cmd would interpret is refused.</summary>
        internal static string CommandLine(string executable, IEnumerable<string> arguments)
        {
            var parts = new List<string>();
            foreach (string part in arguments.Prepend(executable))
            {
                if (part.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', '!', '\r', '\n' }) >= 0)
                    throw new ArgumentException("Unsafe command-line value: " + part);
                parts.Add(part.Length == 0 || part.Contains(' ') || part == executable ? "\"" + part + "\"" : part);
            }
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Claude Code (Anthropic Messages). PaRuX never edits ~/.claude: it owns an isolated profile
    /// (CLAUDE_CONFIG_DIR) under the PaRuX data root and launches Claude Code with it. The key is
    /// injected as ANTHROPIC_AUTH_TOKEN at launch and is not written to disk in clear text.
    /// </summary>
    public sealed class ClaudeCodeIntegration : IClientIntegration
    {
        /// <summary>Tools whose schemas add ~9k tokens and have no local use; dropped so requests fit a 16k local context.</summary>
        public const string LeanDisallowedTools =
            "SendMessage,Workflow,ScheduleWakeup,CronCreate,CronDelete,CronList,EnterWorktree,ExitWorktree,ReportFindings,ListAgents,Skill,TaskStop,WebSearch,WebFetch,NotebookEdit";

        private readonly ClientIntegrationEnvironment _environment;
        private readonly Func<IReadOnlyList<string>> _locate;

        public ClaudeCodeIntegration(ClientIntegrationEnvironment environment, Func<IReadOnlyList<string>>? locate = null)
        {
            _environment = environment;
            _locate = locate ?? (() => ClientLocator.CandidatePaths("claude"));
        }

        public string ClientId => "claude-code";
        public string DisplayName => "Claude Code";
        public string Protocol => "Anthropic Messages (/v1/messages)";
        public string TestedVersion => "2.1.283";
        public string TestFileName => "parux_claude_test.txt";
        public string TestMarker => "PARUX_CLAUDE_OK";

        public IReadOnlyList<string> Locate() => _locate();

        public static string ProfileDirectory(IntegrationStore store) => Path.Combine(store.ClientDirectory("claude-code"), "config");

        public ClientIntegrationStatus ReadConfiguration(IntegrationManifest? manifest, out string detail)
        {
            if (manifest != null)
            {
                ManagedFileChange? settings = manifest.Files.FirstOrDefault(f => f.Kind == "claude-settings");
                if (settings == null || !File.Exists(settings.Path))
                {
                    detail = "The PaRuX profile for Claude Code is missing; reconnect to recreate it.";
                    return ClientIntegrationStatus.ConfigurationError;
                }
                detail = "Isolated PaRuX profile; your normal Claude Code login and settings are untouched.";
                return ClientIntegrationStatus.Connected;
            }
            string? foreign = ForeignBaseUrl();
            if (foreign != null)
            {
                detail = "Claude Code is pointed at " + foreign + ".";
                return ClientIntegrationStatus.UsingAnotherProvider;
            }
            detail = "Uses Anthropic by default. Connect adds a separate PaRuX profile.";
            return ClientIntegrationStatus.Available;
        }

        private string? ForeignBaseUrl()
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
            try
            {
                string settings = Path.Combine(_environment.UserHome, ".claude", "settings.json");
                if (!File.Exists(settings)) return null;
                return (JsonNode.Parse(File.ReadAllText(settings)) as JsonObject)?["env"]?["ANTHROPIC_BASE_URL"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        public IntegrationOutcome Apply(GatewayEndpoint endpoint, IntegrationManifest manifest, IntegrationStore store)
        {
            string profile = ProfileDirectory(store);
            string settingsPath = Path.Combine(profile, "settings.json");
            var env = new JsonObject
            {
                ["ANTHROPIC_BASE_URL"] = endpoint.BaseUrl.TrimEnd('/'),
                ["ANTHROPIC_MODEL"] = endpoint.Model,
                ["ANTHROPIC_SMALL_FAST_MODEL"] = "parux-fast",
                ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "parux-fast",
                ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = endpoint.Model,
                ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = endpoint.Model,
                ["DISABLE_TELEMETRY"] = "1",
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
                ["PARUX_MANAGED"] = "1"
            };
            IntegrationStore.WriteAtomically(settingsPath, new JsonObject { ["env"] = env }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            // Skips the first-run theme/login wizard in the isolated profile; the gateway key is the credential.
            string statePath = Path.Combine(profile, ".claude.json");
            if (!File.Exists(statePath))
                IntegrationStore.WriteAtomically(statePath, "{\n  \"hasCompletedOnboarding\": true\n}\n");
            manifest.Files.RemoveAll(f => f.Kind is "claude-settings" or "claude-profile");
            manifest.Files.Add(new ManagedFileChange { Path = settingsPath, CreatedByPaRuX = true, Kind = "claude-settings" });
            manifest.Files.Add(new ManagedFileChange { Path = profile, CreatedByPaRuX = true, Kind = "claude-profile" });
            return new IntegrationOutcome(true, ClientIntegrationStatus.Connected, "PaRuX profile created at " + profile + ".");
        }

        public IntegrationOutcome Remove(IntegrationManifest manifest, IntegrationStore store)
        {
            string profile = ProfileDirectory(store);
            string settings = Path.Combine(profile, "settings.json");
            // The gateway configuration is what makes the profile PaRuX's; it must go. Session files a
            // still-open Claude Code window holds are only history and are cleaned up best-effort.
            try
            {
                if (File.Exists(settings)) File.Delete(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, "Could not remove the PaRuX Claude profile: " + ex.Message);
            }
            try
            {
                if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new IntegrationOutcome(true, ClientIntegrationStatus.Available,
                    "PaRuX profile removed; some session files are still in use by an open Claude Code window and stay in " + profile + ".");
            }
            return new IntegrationOutcome(true, ClientIntegrationStatus.Available, "PaRuX profile removed. Your own Claude Code settings were never changed.");
        }

        private static void AddEnvironment(ProcessStartInfo info, GatewayEndpoint endpoint, string profile)
        {
            info.Environment["CLAUDE_CONFIG_DIR"] = profile;
            info.Environment["ANTHROPIC_BASE_URL"] = endpoint.BaseUrl.TrimEnd('/');
            info.Environment["ANTHROPIC_AUTH_TOKEN"] = endpoint.ApiKey;
            info.Environment.Remove("ANTHROPIC_API_KEY");
            info.Environment["ANTHROPIC_MODEL"] = endpoint.Model;
            info.Environment["ANTHROPIC_SMALL_FAST_MODEL"] = "parux-fast";
            info.Environment["DISABLE_TELEMETRY"] = "1";
            info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        }

        public ProcessStartInfo CreateLaunch(string executable, GatewayEndpoint endpoint, string workingDirectory, IntegrationStore store)
        {
            ProcessStartInfo info = IntegrationProcess.Console(executable, workingDirectory,
                new[] { "--model", endpoint.Model, "--disallowedTools", LeanDisallowedTools });
            AddEnvironment(info, endpoint, ProfileDirectory(store));
            return info;
        }

        public ProcessStartInfo CreateTest(string executable, GatewayEndpoint endpoint, string workingDirectory, string prompt, IntegrationStore store)
        {
            ProcessStartInfo info = IntegrationProcess.Headless(executable, workingDirectory, new[]
            {
                "-p", prompt, "--permission-mode", "acceptEdits", "--output-format", "json",
                "--model", endpoint.Model, "--disallowedTools", LeanDisallowedTools
            });
            AddEnvironment(info, endpoint, ProfileDirectory(store));
            return info;
        }
    }

    /// <summary>
    /// Codex CLI (OpenAI Responses). PaRuX writes only CODEX_HOME\parux.config.toml and launches
    /// "codex --profile parux"; config.toml, the ChatGPT login and Codex Desktop stay as they are.
    /// </summary>
    public sealed class CodexIntegration : IClientIntegration
    {
        private readonly ClientIntegrationEnvironment _environment;
        private readonly Func<IReadOnlyList<string>> _locate;

        public CodexIntegration(ClientIntegrationEnvironment environment, Func<IReadOnlyList<string>>? locate = null)
        {
            _environment = environment;
            _locate = locate ?? (() => ClientLocator.CandidatePaths("codex").Concat(ClientLocator.CodexDesktopCliCandidates()).ToList());
        }

        public string ClientId => "codex";
        public string DisplayName => "Codex";
        public string Protocol => "OpenAI Responses (/v1/responses)";
        public string TestedVersion => "0.158.0";
        public string TestFileName => "parux_codex_test.txt";
        public string TestMarker => "PARUX_CODEX_OK";

        public IReadOnlyList<string> Locate() => _locate();

        public string ProfilePath => Path.Combine(_environment.CodexHome, CodexConfigEditor.ProfileFileName);
        private string MainConfigPath => Path.Combine(_environment.CodexHome, "config.toml");

        public ClientIntegrationStatus ReadConfiguration(IntegrationManifest? manifest, out string detail)
        {
            string? profile = TryRead(ProfilePath);
            if (manifest != null)
            {
                if (!CodexConfigEditor.IsManaged(profile))
                {
                    detail = CodexConfigEditor.ProfileFileName + " is missing or was changed outside PaRuX; reconnect to repair it.";
                    return ClientIntegrationStatus.ConfigurationError;
                }
                detail = "Profile \"parux\" in " + _environment.CodexHome + ". Your default Codex setup is untouched.";
                return ClientIntegrationStatus.Connected;
            }
            if (profile != null && !CodexConfigEditor.IsManaged(profile))
            {
                detail = "A \"parux\" profile exists that PaRuX did not create.";
                return ClientIntegrationStatus.ConfigurationError;
            }
            string? provider = MainProvider(TryRead(MainConfigPath));
            if (provider != null && provider != "openai" && provider != "parux")
            {
                detail = "Codex default provider is \"" + provider + "\".";
                return ClientIntegrationStatus.UsingAnotherProvider;
            }
            detail = "Uses OpenAI by default. Connect adds a separate \"parux\" profile.";
            return ClientIntegrationStatus.Available;
        }

        /// <summary>The top-level model_provider of config.toml, or null.</summary>
        internal static string? MainProvider(string? config)
        {
            if (config == null) return null;
            foreach (string raw in config.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) break;
                if (!line.StartsWith("model_provider", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals > 0) return line[(equals + 1)..].Trim().Trim('"', '\'');
            }
            return null;
        }

        public IntegrationOutcome Apply(GatewayEndpoint endpoint, IntegrationManifest manifest, IntegrationStore store)
        {
            string? text = CodexConfigEditor.Apply(TryRead(ProfilePath), TryRead(MainConfigPath), endpoint, out string? error);
            if (text == null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, error ?? "Codex profile rejected.");
            IntegrationStore.WriteAtomically(ProfilePath, text);
            if (!manifest.Files.Any(f => f.Kind == "codex-profile"))
                manifest.Files.Add(new ManagedFileChange { Path = ProfilePath, CreatedByPaRuX = true, Kind = "codex-profile" });
            return new IntegrationOutcome(true, ClientIntegrationStatus.Connected, "Profile written to " + ProfilePath + ".");
        }

        public IntegrationOutcome Remove(IntegrationManifest manifest, IntegrationStore store)
        {
            string? existing = TryRead(ProfilePath);
            if (existing == null) return new IntegrationOutcome(true, ClientIntegrationStatus.Available, "No PaRuX profile to remove.");
            if (!CodexConfigEditor.IsManaged(existing))
                return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, CodexConfigEditor.ProfileFileName + " is not PaRuX's; it was left in place.");
            try { File.Delete(ProfilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, "Could not remove the Codex profile: " + ex.Message);
            }
            return new IntegrationOutcome(true, ClientIntegrationStatus.Available, "Removed " + CodexConfigEditor.ProfileFileName + ". config.toml was never changed.");
        }

        public ProcessStartInfo CreateLaunch(string executable, GatewayEndpoint endpoint, string workingDirectory, IntegrationStore store) =>
            WithHome(IntegrationProcess.Console(executable, workingDirectory, new[] { "--profile", CodexConfigEditor.ProfileName }));

        public ProcessStartInfo CreateTest(string executable, GatewayEndpoint endpoint, string workingDirectory, string prompt, IntegrationStore store) =>
            WithHome(IntegrationProcess.Headless(executable, workingDirectory, new[]
            {
                "exec", "--profile", CodexConfigEditor.ProfileName, "--skip-git-repo-check", "--sandbox", "workspace-write", prompt
            }));

        private ProcessStartInfo WithHome(ProcessStartInfo info)
        {
            info.Environment["CODEX_HOME"] = _environment.CodexHome;
            return info;
        }

        internal static string? TryRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// OpenCode (OpenAI-compatible chat completions via @ai-sdk/openai-compatible). PaRuX adds
    /// provider "parux" to the global opencode.json after backing it up; the user's default model
    /// is kept, and Launch selects parux/&lt;model&gt; explicitly.
    /// </summary>
    public sealed class OpenCodeIntegration : IClientIntegration
    {
        private readonly ClientIntegrationEnvironment _environment;
        private readonly Func<IReadOnlyList<string>> _locate;

        public OpenCodeIntegration(ClientIntegrationEnvironment environment, Func<IReadOnlyList<string>>? locate = null)
        {
            _environment = environment;
            _locate = locate ?? (() => ClientLocator.CandidatePaths("opencode"));
        }

        public string ClientId => "opencode";
        public string DisplayName => "OpenCode";
        public string Protocol => "OpenAI Chat Completions (/v1/chat/completions)";
        public string TestedVersion => "1.18.22";
        public string TestFileName => "parux_opencode_test.txt";
        public string TestMarker => "PARUX_OPENCODE_OK";

        public IReadOnlyList<string> Locate() => _locate();

        public string ConfigPath
        {
            get
            {
                string json = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.json");
                string jsonc = Path.Combine(_environment.OpenCodeConfigDirectory, "opencode.jsonc");
                string path = !File.Exists(json) && File.Exists(jsonc) ? jsonc : json;
                // A dotfiles-style symlink stays a link: PaRuX reads, backs up and writes its target.
                try
                {
                    if (File.Exists(path) && File.ResolveLinkTarget(path, returnFinalTarget: true) is FileSystemInfo target)
                        return target.FullName;
                }
                catch (IOException) { }
                return path;
            }
        }

        public ClientIntegrationStatus ReadConfiguration(IntegrationManifest? manifest, out string detail)
        {
            string? text = CodexIntegration.TryRead(ConfigPath);
            string? defaultProvider = OpenCodeConfigEditor.DefaultProvider(text);
            if (manifest != null)
            {
                if (!HasManagedProvider(text))
                {
                    detail = "Provider \"parux\" is missing from " + ConfigPath + "; reconnect to repair it.";
                    return ClientIntegrationStatus.ConfigurationError;
                }
                detail = defaultProvider != null && defaultProvider != OpenCodeConfigEditor.ProviderId
                    ? "Provider \"parux\" added; your default stays \"" + defaultProvider + "\" (Launch uses PaRuX)."
                    : "Provider \"parux\" added to " + ConfigPath + ".";
                return ClientIntegrationStatus.Connected;
            }
            if (defaultProvider != null && defaultProvider != OpenCodeConfigEditor.ProviderId)
            {
                detail = "OpenCode default provider is \"" + defaultProvider + "\".";
                return ClientIntegrationStatus.UsingAnotherProvider;
            }
            detail = "Connect adds a \"parux\" provider; other providers are kept.";
            return ClientIntegrationStatus.Available;
        }

        private static bool HasManagedProvider(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || OpenCodeConfigEditor.HasComments(text)) return false;
            try
            {
                return (JsonNode.Parse(text) as JsonObject)?["provider"]?[OpenCodeConfigEditor.ProviderId] is JsonObject provider
                    && OpenCodeConfigEditor.IsManaged(provider);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
        }

        public IntegrationOutcome Apply(GatewayEndpoint endpoint, IntegrationManifest manifest, IntegrationStore store)
        {
            string path = ConfigPath;
            string? existing = CodexIntegration.TryRead(path);
            string? text = OpenCodeConfigEditor.Apply(existing, endpoint, out string? error);
            if (text == null) return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, error ?? "opencode.json rejected.");
            // First connect only: the backup is the user's file as it was before PaRuX touched it.
            if (!manifest.Files.Any(f => f.Kind == "opencode-config"))
            {
                string? backup = store.Backup(ClientId, path);
                manifest.Files.Add(new ManagedFileChange { Path = path, BackupPath = backup, CreatedByPaRuX = existing == null, Kind = "opencode-config" });
            }
            IntegrationStore.WriteAtomically(path, text);
            return new IntegrationOutcome(true, ClientIntegrationStatus.Connected, "Provider \"parux\" added to " + path + ".");
        }

        public IntegrationOutcome Remove(IntegrationManifest manifest, IntegrationStore store)
        {
            string path = ConfigPath;
            string? current = CodexIntegration.TryRead(path);
            if (current != null && OpenCodeConfigEditor.HasComments(current)
                && current.Contains("\"" + OpenCodeConfigEditor.ProviderId + "\"", StringComparison.Ordinal))
                return new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError,
                    path + " now contains comments, so PaRuX cannot remove its provider safely. Remove the \"parux\" provider by hand or use Restore.");
            string? updated = OpenCodeConfigEditor.Remove(current, out bool changed);
            if (!changed || updated == null) return new IntegrationOutcome(true, ClientIntegrationStatus.Available, "No PaRuX provider to remove.");
            ManagedFileChange? record = manifest.Files.FirstOrDefault(f => f.Kind == "opencode-config");
            // A file PaRuX created that now holds nothing else is removed instead of left behind.
            if (record?.CreatedByPaRuX == true && IsEmptyConfig(updated))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { IntegrationStore.WriteAtomically(path, updated); }
            }
            else
            {
                IntegrationStore.WriteAtomically(path, updated);
            }
            return new IntegrationOutcome(true, ClientIntegrationStatus.Available, "Provider \"parux\" removed; your other providers were kept.");
        }

        private static bool IsEmptyConfig(string text)
        {
            try { return JsonNode.Parse(text) is JsonObject root && root.All(p => p.Key == "$schema"); }
            catch (JsonException) { return false; }
        }

        public ProcessStartInfo CreateLaunch(string executable, GatewayEndpoint endpoint, string workingDirectory, IntegrationStore store) =>
            IntegrationProcess.Console(executable, workingDirectory, new[] { "--model", OpenCodeConfigEditor.ProviderId + "/" + endpoint.Model });

        /// <summary>Test-only permissions: the model may edit files in the scratch folder but not run commands or fetch.</summary>
        internal const string TestPermissions = "{\"permission\":{\"bash\":\"deny\",\"webfetch\":\"deny\"}}";

        public ProcessStartInfo CreateTest(string executable, GatewayEndpoint endpoint, string workingDirectory, string prompt, IntegrationStore store)
        {
            ProcessStartInfo info = IntegrationProcess.Headless(executable, workingDirectory, new[] { "run", "--model", OpenCodeConfigEditor.ProviderId + "/" + endpoint.Model, prompt });
            info.Environment["OPENCODE_CONFIG_CONTENT"] = TestPermissions;
            return info;
        }
    }
}
