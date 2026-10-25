using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Runs a package.json script the way `npm test` / `npm run &lt;name&gt;` would, without starting npm.
    ///
    /// Live UI check: inside the no-network AppContainer npm cannot start on a default Windows host. Its
    /// JavaScript real-path walk lstat()s the drive root, which grants app containers no read-attributes
    /// right (EPERM: lstat 'C:\'); with --preserve-symlinks it hung instead. Changing the drive-root ACL
    /// needs administrator rights on a system object, so the sandbox executes the script itself with
    /// npm's documented run-script semantics: pre/post hooks, node_modules\.bin of the package and its
    /// ancestors on PATH, npm_lifecycle_event, arguments after "--" appended to the main script. Each
    /// script body is written verbatim to its own .cmd file and run in a child `cmd /d /s /c`, like npm
    /// does on Windows, so quotes, parentheses or `exit` inside a script cannot break the outer line.
    /// </summary>
    public static class CodingNpmScriptRunner
    {
        private static readonly Regex NpmScriptCommand = new(
            @"^npm(?:\.cmd)?\s+(?:(?<verb>test|t|tst)|(?:run|run-script|rum|urn)\s+(?<name>[A-Za-z0-9:_.\-]+))(?:\s+--\s+(?<args>.*))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed record Rewrite(string CommandLine, IReadOnlyList<string> ScriptFiles);

        /// <summary>The package.json script name an npm invocation runs, or null when it is not a plain script run.</summary>
        public static string? ScriptName(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            Match match = NpmScriptCommand.Match(command.Trim());
            if (!match.Success) return null;
            return match.Groups["verb"].Success ? "test" : match.Groups["name"].Value;
        }

        /// <summary>
        /// Writes one .cmd file per lifecycle script into <paramref name="scriptDirectory"/> and returns the
        /// cmd.exe line that runs them in order, or null to run the command unchanged.
        /// </summary>
        public static Rewrite? TryRewrite(string workspaceRoot, string command, string scriptDirectory, string filePrefix)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(scriptDirectory)) return null;
            string? name = ScriptName(command);
            if (name == null) return null;
            string root = Path.GetFullPath(workspaceRoot);
            Dictionary<string, string>? scripts = ReadScripts(File.Exists(Path.Combine(root, "package.json"))
                ? SafeRead(Path.Combine(root, "package.json")) : null);
            if (scripts == null || !scripts.TryGetValue(name, out string? body) || string.IsNullOrWhiteSpace(body)) return null;

            Match match = NpmScriptCommand.Match(command.Trim());
            string extraArgs = match.Groups["args"].Success ? match.Groups["args"].Value.Trim() : string.Empty;
            var lifecycle = new List<(string Event, string Body)>();
            if (scripts.TryGetValue("pre" + name, out string? pre) && !string.IsNullOrWhiteSpace(pre)) lifecycle.Add(("pre" + name, pre));
            lifecycle.Add((name, extraArgs.Length == 0 ? body : body + " " + extraArgs));
            if (scripts.TryGetValue("post" + name, out string? post) && !string.IsNullOrWhiteSpace(post)) lifecycle.Add(("post" + name, post));
            if (lifecycle.Any(step => step.Body.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) return null;

            Directory.CreateDirectory(scriptDirectory);
            var files = new List<string>();
            var steps = new List<string>();
            for (int i = 0; i < lifecycle.Count; i++)
            {
                (string eventName, string scriptBody) = lifecycle[i];
                string file = Path.Combine(scriptDirectory, filePrefix + "-npm-" + i + ".cmd");
                File.WriteAllText(file, "@echo off\r\nset \"npm_lifecycle_event=" + eventName + "\"\r\n" + scriptBody + "\r\n",
                    new UTF8Encoding(false));
                files.Add(file);
                steps.Add("(echo. & echo ^> " + EscapeForEcho(eventName) + " & cmd /d /s /c \"\"" + file + "\"\")");
            }

            string binPath = string.Join(";", BinDirectories(root));
            string line = "echo [PaRuX] npm cannot start inside the offline AppContainer (drive-root access); running package.json script \""
                + name + "\" directly with npm run-script semantics."
                + " & set \"PATH=" + binPath.Replace("%", "%%", StringComparison.Ordinal) + ";%PATH%\""
                + " & " + string.Join(" && ", steps);
            return new Rewrite(line, files);
        }

        /// <summary>
        /// The lifecycle bodies (pre, main, post) a script run executes, from package.json text. Used to refuse
        /// counting `npm test` as verification when the agent changed what it runs (review finding: editing
        /// "test" to "exit 0" would otherwise pass as verified).
        /// </summary>
        public static string? LifecycleFingerprint(string? packageJsonText, string scriptName)
        {
            Dictionary<string, string>? scripts = ReadScripts(packageJsonText);
            if (scripts == null) return null;
            string Get(string key) => scripts.TryGetValue(key, out string? value) ? value : "\u0000";
            return Get("pre" + scriptName) + "\u0001" + Get(scriptName) + "\u0001" + Get("post" + scriptName);
        }

        private static IEnumerable<string> BinDirectories(string root)
        {
            for (DirectoryInfo? directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
                yield return Path.Combine(directory.FullName, "node_modules", ".bin");
        }

        private static string EscapeForEcho(string value) =>
            new StringBuilder(value).Replace("^", "^^").Replace("&", "^&").Replace("|", "^|").Replace("<", "^<").Replace(">", "^>").ToString();

        private static string? SafeRead(string path)
        {
            try { return new FileInfo(path).Length > 1_000_000 ? null : File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static Dictionary<string, string>? ReadScripts(string? packageJsonText)
        {
            if (string.IsNullOrWhiteSpace(packageJsonText)) return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(packageJsonText);
                if (!document.RootElement.TryGetProperty("scripts", out JsonElement scripts) || scripts.ValueKind != JsonValueKind.Object) return null;
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty property in scripts.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) result[property.Name] = property.Value.GetString() ?? string.Empty;
                return result;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>True when the command starts a JavaScript package manager (npm, npx, pnpm, yarn, corepack).</summary>
        public static bool StartsPackageManager(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            string first = command.TrimStart().Split(new[] { ' ', '\t' }, 2)[0].Trim('"');
            string name = Path.GetFileNameWithoutExtension(first).ToLowerInvariant();
            return name is "npm" or "npx" or "pnpm" or "yarn" or "corepack";
        }
    }
}
