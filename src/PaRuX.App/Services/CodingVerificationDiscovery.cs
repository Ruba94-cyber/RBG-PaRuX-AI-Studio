using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Repository-shape based build/test discovery for the local coding agent. Commands are derived
    /// only from files that actually exist. Verification runs inside the no-network AppContainer,
    /// so discovery prefers already-restored/offline-safe forms whenever repository evidence proves
    /// they are usable and never invents a package-install step.
    ///
    /// A PaRuX multi-repository coding workspace is an ordinary parent directory whose direct
    /// children are real Git worktrees. For that exact shape, discovery returns ONE composite
    /// verify-all command that executes every discovered check in every child repository. This is
    /// deliberately stricter than accepting a successful check from only one repository: the
    /// coding completion gate cannot be cleared by testing backend while frontend remains broken.
    /// </summary>
    public static class CodingVerificationDiscovery
    {
        private const int MaximumEnumeratedFiles = 10_000;
        private const int MaximumVisitedDirectories = 20_000;

        public sealed class VerificationCommand
        {
            public required string Kind { get; set; }
            public required string Command { get; set; }
            public required string Reason { get; set; }
        }

        /// <summary>
        /// Names the exact accepted commands: a small context budget can drop the verification
        /// section from the prompt, and a model told only "run a check" keeps repeating commands
        /// such as <c>dotnet run</c> that never count as verification.
        /// </summary>
        public static string BuildUnverifiedCompletionRejection(string workspaceRoot)
        {
            List<string> accepted = Discover(workspaceRoot).Select(candidate => candidate.Command).Take(4).ToList();
            return "Completion was rejected: executable/testable code changed after the last successful verification. "
                + (accepted.Count == 0
                    ? "Run one of the discovered build/test/check commands and observe a successful exit before done. "
                        + "For changed JavaScript with no project build, run exactly: node --check <changed file>."
                    // One quoted command per line: a small model copies a joined line verbatim, and a
                    // separator such as "|" or "&&" turns it into a shell pipeline that never matches.
                    : "Run exactly ONE of these commands by itself, unchanged and not combined with any other command, "
                        + "and observe a successful exit before done. Do not add --filter or other arguments, because a "
                        + "filtered run can skip failing tests. Other commands do not satisfy verification:\n"
                        + string.Join("\n", accepted.Select(command => "- \"" + command + "\""))
                        + "\nIn a larger repository, running the existing, unmodified project or verifier that covers your change "
                        + "also counts, for example dotnet run --project <that project> or python/node <its verifier script>."
                        + (accepted.Any(command => command.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase))
                            ? "\nIf a .NET command fails with MSB1025 or a named-pipe access error, run the same command with -m:1 appended."
                            : string.Empty));
        }

        /// <summary>
        /// Scope-aware verification for repositories where the root check does not fit the change
        /// (a live monorepo run: the task's own fixture verifier passed, but only a whole-solution
        /// `dotnet test` at the root counted, which cannot run offline, and the agent spiralled).
        /// Accepts, besides the root-discovered commands, a single uncomposed command that runs a
        /// project or verifier script covering the changed files:
        ///   dotnet build|test|run on a tracked, unmodified project whose directory contains a change
        ///   (never with --filter or --no-build); python/node running a tracked, unmodified script
        ///   below the repository root whose directory contains a change.
        /// The script or project must be unmodified so the agent cannot author its own "verifier".
        /// </summary>
        public static bool IsScopedVerificationCommand(
            string workspaceRoot,
            string command,
            IReadOnlyCollection<string> changedRelativePaths,
            Func<string, bool> isTrackedPath)
        {
            if (string.IsNullOrWhiteSpace(command) || changedRelativePaths == null || changedRelativePaths.Count == 0) return false;
            string root = Path.GetFullPath(workspaceRoot);
            string baseDirectory = root;
            // Models commonly write "cd <dir> && <command>". Accept exactly one leading cd to a
            // directory inside the workspace followed by one command, evaluated from that directory.
            System.Text.RegularExpressions.Match cd = System.Text.RegularExpressions.Regex.Match(command.Trim(),
                @"^cd\s+(?:/d\s+)?""?(?<dir>[^""&|;<>]+?)""?\s*&&\s*(?<rest>[^&].*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (cd.Success)
            {
                string target = Path.GetFullPath(Path.Combine(root, cd.Groups["dir"].Value.Trim()));
                string relativeTarget = Path.GetRelativePath(root, target);
                if (relativeTarget.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeTarget) || !Directory.Exists(target))
                    return false;
                baseDirectory = target;
                command = cd.Groups["rest"].Value;
            }
            if (command.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\r', '\n' }) >= 0 || command.Contains("$(", StringComparison.Ordinal)) return false;
            List<string> tokens = Tokenize(command);
            if (tokens.Count < 2) return false;
            // Help/version requests exit 0 without checking anything.
            if (tokens.Skip(1).Any(t => HelpOrVersionArguments.Contains(t))) return false;
            var changed = changedRelativePaths
                .Select(p => p.Replace('\\', '/').TrimStart('/'))
                .Where(p => p.Length > 0)
                .ToList();

            bool Covers(string scopeRelative) =>
                changed.Any(c => scopeRelative.Length == 0 || c.StartsWith(scopeRelative + "/", StringComparison.OrdinalIgnoreCase));
            bool Unmodified(string relative) =>
                !changed.Any(c => string.Equals(c, relative, StringComparison.OrdinalIgnoreCase)) && isTrackedPath(relative);
            string? RelativeInside(string value)
            {
                string full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
            }

            string exe = Path.GetFileNameWithoutExtension(tokens[0]).ToLowerInvariant();
            if (exe == "dotnet")
            {
                string verb = tokens[1].ToLowerInvariant();
                if (verb is not ("build" or "test" or "run")) return false;
                if (tokens.Any(t => t.Equals("--filter", StringComparison.OrdinalIgnoreCase) || t.Equals("--no-build", StringComparison.OrdinalIgnoreCase))) return false;
                int separator = tokens.IndexOf("--");
                List<string> options = separator < 0 ? tokens.Skip(2).ToList() : tokens.Skip(2).Take(separator - 2).ToList();
                string? target = null;
                int projectFlag = options.FindIndex(t => t.Equals("--project", StringComparison.OrdinalIgnoreCase) || t.Equals("-p", StringComparison.Ordinal));
                if (verb == "run" && projectFlag >= 0 && projectFlag + 1 < options.Count) target = options[projectFlag + 1];
                else if (verb != "run") target = options.FirstOrDefault(t => !t.StartsWith('-'));
                // No explicit project: dotnet uses the project in the working directory.
                target ??= projectFlag < 0 ? "." : null;
                if (target == null) return false;
                string? relative = RelativeInside(target.Trim('"'));
                if (relative == null) return false;
                string full = Path.Combine(root, relative);
                string? projectRelative = null;
                if (File.Exists(full) && IsProjectFile(full)) projectRelative = relative;
                else if (Directory.Exists(full))
                {
                    string[] projects = SafeEnumerate(full, "*.*proj", SearchOption.TopDirectoryOnly).Where(IsProjectFile).ToArray();
                    if (projects.Length == 1) projectRelative = RelativeInside(projects[0]);
                }
                if (projectRelative == null || !Unmodified(projectRelative)) return false;
                string scope = Path.GetDirectoryName(projectRelative)?.Replace('\\', '/') ?? string.Empty;
                return scope.Length > 0 && Covers(scope)
                    && !HasUntrustedBuildOverride(root, scope, changed, isTrackedPath);
            }

            if (exe is "python" or "python3" or "py" or "node")
            {
                string script = tokens[1].Trim('"');
                string extension = Path.GetExtension(script).ToLowerInvariant();
                bool isScript = exe == "node" ? extension is ".js" or ".mjs" or ".cjs" : extension == ".py";
                if (!isScript) return false;
                string? relative = RelativeInside(script);
                if (relative == null || !File.Exists(Path.Combine(root, relative)) || !Unmodified(relative)) return false;
                // Only a script that is a verifier by name: running an arbitrary program in the
                // changed directory proves nothing.
                if (!VerifierScriptName.IsMatch(Path.GetFileNameWithoutExtension(relative))) return false;
                string scope = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
                return scope.Length > 0 && Covers(scope)
                    && !(exe == "node"
                        ? HasUntrustedNodeResolution(root, scope, changed, isTrackedPath)
                        : HasUntrustedPythonResolution(root, scope, isTrackedPath));
            }
            return false;
        }

        /// <summary>JavaScript files that <c>node --check</c> can validate.</summary>
        public static bool IsScriptSyntaxCheckable(string? relativePath) =>
            Path.GetExtension(relativePath ?? string.Empty).ToLowerInvariant() is ".js" or ".mjs" or ".cjs";

        /// <summary>
        /// Exactly <c>node --check &lt;changed script&gt;</c> (or <c>-c</c>): one command, no operators,
        /// naming a changed JavaScript file inside the workspace. Used when a repository has no
        /// discoverable build/test command so changed JavaScript still has to parse before done.
        /// </summary>
        public static bool IsNodeSyntaxCheck(string? command, IReadOnlyCollection<string> changedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(command) || changedRelativePaths == null || changedRelativePaths.Count == 0) return false;
            if (command.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\r', '\n' }) >= 0 || command.Contains("$(", StringComparison.Ordinal)) return false;
            List<string> tokens = Tokenize(command);
            if (tokens.Count != 3) return false;
            if (!string.Equals(Path.GetFileNameWithoutExtension(tokens[0]), "node", StringComparison.OrdinalIgnoreCase)) return false;
            if (tokens[1] is not ("--check" or "-c")) return false;
            string file = tokens[2].Trim('"').Replace('\\', '/').TrimStart('/');
            if (file.StartsWith("./", StringComparison.Ordinal)) file = file[2..];
            if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file) || !IsScriptSyntaxCheckable(file)) return false;
            return changedRelativePaths.Any(changed => string.Equals(changed.Replace('\\', '/').TrimStart('/'), file, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly HashSet<string> HelpOrVersionArguments = new(StringComparer.OrdinalIgnoreCase)
        {
            "--help", "-h", "-?", "/?", "/h", "/help", "--version", "-V", "--info", "--usage"
        };

        private static readonly Regex VerifierScriptName = new(
            @"^(?:verif(?:y|ier|ication)|check|test)(?:[._-][\w.-]*)?$|^[\w.-]*[._-](?:test|tests|spec|check|verify)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] BuildOverrideFiles =
        {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
            "Directory.Solution.props", "Directory.Solution.targets", "global.json", "NuGet.config"
        };

        /// <summary>
        /// Source files plus the non-source files that change what a build or test run compiles, resolves
        /// or executes: project and build properties, manifests and lockfiles, XAML and resources, and build
        /// scripts. Editing one of these requires a deterministic verification run before the coding task can
        /// complete, because a model verdict cannot show that the build still works. This is the one predicate
        /// that decides which edits are "testable"; the coding completion gate and the change fingerprint use it.
        /// </summary>
        internal static bool IsBuildOrTestableCodePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string name = Path.GetFileName(path);
            string extension = Path.GetExtension(name);
            return TestableCodeExtensions.Contains(extension)
                || BuildAffectingExtensions.Contains(extension)
                || TestableCodeFileNames.Contains(name)
                || Array.Exists(BuildOverrideFiles, file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase))
                || CodingVerificationManifestPolicy.IsVerifierManifest(name);
        }

        private static readonly HashSet<string> TestableCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".sln",
            ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx",
            ".py", ".pyi", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
            ".java", ".kt", ".kts", ".rs", ".go", ".swift", ".dart", ".rb", ".php",
            ".gradle", ".props", ".targets"
        };

        /// <summary>
        /// Non-source files that a build reads: solution and response files, XAML and resources, run settings,
        /// lockfiles and build or verification scripts. Each one can change the compiled result. JSON and XML are
        /// not matched by extension, because most files with those extensions are data or templates. The build
        /// inputs among them are listed by name in TestableCodeFileNames.
        /// </summary>
        private static readonly HashSet<string> BuildAffectingExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".slnx", ".rsp", ".xaml", ".resx", ".runsettings", ".lock", ".ps1", ".sh", ".cmd", ".bat", ".mk", ".cmake"
        };

        private static readonly HashSet<string> TestableCodeFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "pyproject.toml", "requirements.txt", "Cargo.toml", "go.mod", "go.sum",
            "CMakeLists.txt", "Makefile", "Dockerfile", "Directory.Build.props", "Directory.Build.targets",
            "Pipfile", "pnpm-lock.yaml", "gradle.lockfile",
            // JSON build inputs: manifests, lockfiles and compiler configuration. Other JSON files are data.
            "package-lock.json", "npm-shrinkwrap.json", "packages.lock.json", "tsconfig.json", "jsconfig.json"
        };

        /// <summary>
        /// MSBuild imports Directory.Build.* (and honours global.json / NuGet.config) from the project
        /// directory and every ancestor, so an agent-authored override there can replace the build or
        /// test target of an otherwise unmodified project. Such a file must be tracked and unchanged
        /// for the scoped run to count.
        /// </summary>
        private static bool HasUntrustedBuildOverride(string root, string scope, IReadOnlyCollection<string> changed, Func<string, bool> isTrackedPath)
        {
            foreach (string directory in ScopeAndAncestors(scope))
            {
                foreach (string name in BuildOverrideFiles)
                {
                    string relative = directory.Length == 0 ? name : directory + "/" + name;
                    if (!File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))) continue;
                    if (!isTrackedPath(relative) || changed.Any(c => string.Equals(c, relative, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Python puts the script's directory first on sys.path, so an untracked module there (for
        /// example unittest.py or a sourceless .pyc) shadows the standard library the verifier
        /// imports; sitecustomize/usercustomize run at interpreter start. Checked on the file system
        /// rather than git status so ignored files cannot hide.
        /// </summary>
        private static bool HasUntrustedPythonResolution(string root, string scope, Func<string, bool> isTrackedPath)
        {
            string directory = Path.Combine(root, scope.Replace('/', Path.DirectorySeparatorChar));
            foreach (string file in SafeEnumerate(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                string extension = Path.GetExtension(name).ToLowerInvariant();
                bool importable = extension is ".py" or ".pyc" or ".pyo" or ".pyd" or ".pyw" or ".pth" or ".so" or ".zip" or ".egg";
                if (importable && !isTrackedPath(scope + "/" + name)) return true;
            }
            try
            {
                foreach (string package in Directory.EnumerateDirectories(directory).Take(MaximumEnumeratedFiles))
                {
                    string init = scope + "/" + Path.GetFileName(package) + "/__init__.py";
                    if (File.Exists(Path.Combine(package, "__init__.py")) && !isTrackedPath(init)) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
            foreach (string ancestor in ScopeAndAncestors(scope))
            {
                foreach (string name in new[] { "sitecustomize.py", "usercustomize.py", "pyvenv.cfg" })
                {
                    string relative = ancestor.Length == 0 ? name : ancestor + "/" + name;
                    if (File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))) && !isTrackedPath(relative)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Node resolves bare specifiers through node_modules in the script directory and every
        /// ancestor, and package.json can remap imports. A node_modules directory there (normally
        /// ignored, so invisible to git status) or a changed/untracked package.json can substitute
        /// the modules the verifier loads.
        /// </summary>
        private static bool HasUntrustedNodeResolution(string root, string scope, IReadOnlyCollection<string> changed, Func<string, bool> isTrackedPath)
        {
            foreach (string ancestor in ScopeAndAncestors(scope))
            {
                string directory = Path.Combine(root, ancestor.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(Path.Combine(directory, "node_modules"))) return true;
                string manifest = ancestor.Length == 0 ? "package.json" : ancestor + "/package.json";
                if (File.Exists(Path.Combine(root, manifest.Replace('/', Path.DirectorySeparatorChar)))
                    && (!isTrackedPath(manifest) || changed.Any(c => string.Equals(c, manifest, StringComparison.OrdinalIgnoreCase))))
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> ScopeAndAncestors(string scope)
        {
            string? current = scope.Replace('\\', '/').Trim('/');
            while (!string.IsNullOrEmpty(current))
            {
                yield return current;
                int slash = current.LastIndexOf('/');
                current = slash < 0 ? null : current[..slash];
            }
            yield return string.Empty;
        }

        /// <summary>
        /// cmd.exe resolves a bare program name (dotnet, python) through the working directory and
        /// PATHEXT. The sandbox sets NoDefaultCurrentDirectoryInExePath so that lookup is disabled and
        /// repository wrappers are discovered with an explicit ".\" path; this check is defence in
        /// depth. Every program file the command could resolve inside the workspace - bare-name
        /// candidates in the working directory and an explicit relative path - must be a tracked,
        /// unchanged repository file both before and after the run (an agent-written dotnet.cmd can
        /// delete itself after exiting 0). Explicit paths into ignored dependency folders such as
        /// vendor\bin are not git-visible and are accepted as before.
        /// </summary>
        public static bool UsesUntrustedWorkingDirectoryProgram(
            string workspaceRoot,
            string command,
            IReadOnlyCollection<string> changedRelativePaths,
            Func<string, bool> isTrackedPath,
            IReadOnlyCollection<string>? candidatesBeforeRun = null)
        {
            (List<string> Bare, string? Explicit) now = ProgramCandidates(workspaceRoot, command);
            var changed = new HashSet<string>((changedRelativePaths ?? Array.Empty<string>()).Select(c => c.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            foreach (string relative in now.Bare.Concat(candidatesBeforeRun ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return true;
                if (!File.Exists(Path.Combine(Path.GetFullPath(workspaceRoot), relative.Replace('/', Path.DirectorySeparatorChar)))
                    || !isTrackedPath(relative) || changed.Contains(relative))
                    return true;
            }
            return now.Explicit != null && changed.Contains(now.Explicit);
        }

        /// <summary>Workspace-relative program files the command's first token could resolve to right now.</summary>
        public static IReadOnlyList<string> WorkingDirectoryProgramCandidates(string workspaceRoot, string command) =>
            ProgramCandidates(workspaceRoot, command).Bare;

        // cmd.exe's default PATHEXT: a planted dotnet.js or python.vbs runs through Windows Script Host.
        private static readonly string[] PathExtensions =
            { ".com", ".exe", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc" };

        private static (List<string> Bare, string? Explicit) ProgramCandidates(string workspaceRoot, string command)
        {
            var bare = new List<string>();
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(workspaceRoot)) return (bare, null);
            string root = Path.GetFullPath(workspaceRoot);
            string baseDirectory = root;
            string rest = command.Trim();
            Match cd = Regex.Match(rest, @"^cd\s+(?:/d\s+)?""?(?<dir>[^""&|;<>]+?)""?\s*&&\s*(?<rest>[^&].*)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (cd.Success)
            {
                baseDirectory = Path.GetFullPath(Path.Combine(root, cd.Groups["dir"].Value.Trim()));
                rest = cd.Groups["rest"].Value;
            }
            List<string> tokens = Tokenize(rest);
            if (tokens.Count == 0) return (bare, null);
            string program = tokens[0];
            string Relative(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
            if (program.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            {
                string full = Path.GetFullPath(Path.IsPathRooted(program) ? program : Path.Combine(baseDirectory, program));
                string relative = Relative(full);
                return (bare, relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative);
            }
            IEnumerable<string> names = new[] { program }.Concat(PathExtensions.Select(extension => program + extension));
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(baseDirectory, name);
                if (File.Exists(candidate)) bare.Add(Relative(candidate));
            }
            return (bare, null);
        }

        /// <summary>
        /// Concrete, cheap verification commands for the current change: the nearest project of
        /// each changed file and verifier scripts in the change's directories (never the root).
        /// In a monorepo the root-discovered whole-solution commands took 185-231 s each in the
        /// sandbox and failed offline, while the covering project verified in seconds.
        /// </summary>
        public static IReadOnlyList<string> SuggestScopedCommands(string workspaceRoot, IEnumerable<string> changedRelativePaths)
        {
            string root = Path.GetFullPath(workspaceRoot);
            var suggestions = new List<string>();
            foreach (string changed in changedRelativePaths ?? Array.Empty<string>())
            {
                string relative = changed.Replace('\\', '/').Trim('/');
                string? directory = Path.GetDirectoryName(relative)?.Replace('\\', '/');
                bool projectFound = false;
                while (!string.IsNullOrEmpty(directory))
                {
                    string full = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(full))
                    {
                        foreach (string script in SafeEnumerate(full, "verify*.*", SearchOption.TopDirectoryOnly))
                        {
                            string extension = Path.GetExtension(script).ToLowerInvariant();
                            string runner = extension == ".py" ? "python" : extension is ".mjs" or ".js" or ".cjs" ? "node" : string.Empty;
                            if (runner.Length > 0)
                                AddSuggestion(suggestions, runner + " " + directory + "/" + Path.GetFileName(script) + "  (add the case argument its usage requires)");
                        }
                        if (!projectFound)
                        {
                            string[] projects = SafeEnumerate(full, "*.*proj", SearchOption.TopDirectoryOnly).Where(IsProjectFile).ToArray();
                            if (projects.Length == 1)
                            {
                                projectFound = true;
                                string project = directory + "/" + Path.GetFileName(projects[0]);
                                string text = SafeReadHead(projects[0]);
                                if (text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                                    || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase))
                                    AddSuggestion(suggestions, "dotnet test " + project);
                                else if (text.Contains("<OutputType>Exe", StringComparison.OrdinalIgnoreCase))
                                    AddSuggestion(suggestions, "dotnet run --project " + project + "  (add -- <arguments> if the program needs them)");
                                else
                                    AddSuggestion(suggestions, "dotnet build " + project);
                            }
                        }
                    }
                    int slash = directory.LastIndexOf('/');
                    directory = slash < 0 ? null : directory[..slash];
                }
                if (suggestions.Count >= 4) break;
            }
            return suggestions.Take(4).ToList();
        }

        private static void AddSuggestion(List<string> suggestions, string command)
        {
            if (!suggestions.Contains(command, StringComparer.OrdinalIgnoreCase)) suggestions.Add(command);
        }

        private static string SafeReadHead(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                char[] buffer = new char[16_384];
                int read = reader.Read(buffer, 0, buffer.Length);
                return new string(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
        }

        private static bool IsProjectFile(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx";

        private static List<string> Tokenize(string command)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char ch in command.Trim())
            {
                if (ch == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(ch) && !quoted)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        public static IReadOnlyList<VerificationCommand> Discover(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return Array.Empty<VerificationCommand>();

            string root = Path.GetFullPath(workspaceRoot);
            List<VerificationCommand> rootCommands = DiscoverSingleRoot(root);

            // A normal repository may contain nested Git submodules.
            // However the parent of an aggregate PaRuX workspace is NOT a
            // Git repository. A model-created package.json/Makefile there
            // cannot supersede two already authorized child worktrees.
            string[] discoveredChildGit = SafeDirectRepositories(root).ToArray();
            bool rootHasGitMarker = File.Exists(Path.Combine(root, ".git"))
                || Directory.Exists(Path.Combine(root, ".git"));
            if (rootCommands.Count > 0)
                return !rootHasGitMarker && discoveredChildGit.Length >= 2
                    ? Array.Empty<VerificationCommand>()
                    : rootCommands;

            // For a true aggregate, every direct authority must participate.
            // The old implementation silently skipped an unsupported third
            // repository, and could return a "verify-all" PASS that never
            // inspected it. Never drop that missing evidence.
            string[] allDirect;
            try { allDirect = Directory.GetDirectories(root); }
            catch { return Array.Empty<VerificationCommand>(); }
            if (allDirect.Length < 2) return rootCommands;
            if (allDirect.Length > 16) return Array.Empty<VerificationCommand>();
            string[] gitChildren = discoveredChildGit;
            if (gitChildren.Length != allDirect.Length || gitChildren.Length < 2)
                return rootCommands;

            var childRepositories = new List<(string Alias, List<VerificationCommand> Commands)>();
            foreach (string child in gitChildren)
            {
                string alias = Path.GetFileName(child);
                if (!IsSafeMultiRepositoryAlias(alias)) return Array.Empty<VerificationCommand>();
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        return Array.Empty<VerificationCommand>();
                }
                catch { return Array.Empty<VerificationCommand>(); }
                List<VerificationCommand> childCommands = DiscoverSingleRoot(child);
                if (childCommands.Count == 0)
                    return Array.Empty<VerificationCommand>(); // missing authority proof
                childRepositories.Add((alias, childCommands));
            }

            var segments = new List<string>();
            var reasons = new List<string>();
            foreach ((string aliasRaw, List<VerificationCommand> commands) in childRepositories)
            {
                string alias = aliasRaw;
                // Already validated when discovering; no lossy shell escaping
                // of arbitrary filesystem names may authorize verification.
                string inner = string.Join(" && ", commands.Select(command => command.Command));
                if (inner.Length == 0) continue;
                // WindowsCodingSandbox launches through cmd.exe. pushd/popd keeps each repository's
                // relative tooling paths correct while allowing the next repository to start again
                // from the aggregate workspace root.
                segments.Add("pushd \"" + alias + "\" && " + inner + " && popd");
                reasons.Add("'" + alias + "': " + string.Join(", ", commands.Select(command => command.Kind).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            if (segments.Count == 0) return Array.Empty<VerificationCommand>();

            return new[]
            {
                new VerificationCommand
                {
                    Kind = "verify-all",
                    Command = string.Join(" && ", segments),
                    Reason = "Multi-repository completion gate; all discovered offline-safe checks must pass in one sandboxed command. "
                        + string.Join("; ", reasons)
                }
            };
        }

        private static bool IsSafeMultiRepositoryAlias(string? alias) =>
            CodingRepositoryAliasPolicy.IsValid(alias);

        private static List<VerificationCommand> DiscoverSingleRoot(string root)
        {
            var commands = new List<VerificationCommand>();
            DiscoverDotNet(root, commands);
            DiscoverNode(root, commands);
            DiscoverPython(root, commands);
            DiscoverRust(root, commands);
            DiscoverGo(root, commands);
            DiscoverGradle(root, commands);
            DiscoverMaven(root, commands);
            DiscoverCMakeMesonMake(root, commands);
            DiscoverDartFlutter(root, commands);
            DiscoverRuby(root, commands);
            DiscoverPhp(root, commands);
            return commands;
        }

        private static void DiscoverDotNet(string root, List<VerificationCommand> commands)
        {
            bool hasSolution = SafeEnumerate(root, "*.sln", SearchOption.TopDirectoryOnly).Any()
                || SafeEnumerate(root, "*.slnx", SearchOption.TopDirectoryOnly).Any();
            bool hasProject = SafeEnumerate(root, "*.csproj", SearchOption.TopDirectoryOnly).Any()
                || SafeEnumerate(root, "*.fsproj", SearchOption.TopDirectoryOnly).Any()
                || SafeEnumerate(root, "*.vbproj", SearchOption.TopDirectoryOnly).Any();
            if (!hasSolution && !hasProject) return;

            // obj is intentionally not a globally-pruned directory because project.assets.json is
            // authoritative evidence that --no-restore is usable. Other expensive generated trees
            // are pruned before traversal by SafeEnumerateRecursive.
            bool restored = SafeEnumerate(root, "project.assets.json", SearchOption.AllDirectories)
                .Any(path => Normalize(path).Contains("/obj/", StringComparison.OrdinalIgnoreCase));
            string suffix = restored ? " --no-restore" : string.Empty;
            AddUnique(commands, "build", "dotnet build" + suffix,
                restored
                    ? "A .NET solution/project and an existing obj/project.assets.json were found, so verification can stay offline with --no-restore."
                    : "A .NET solution/project exists; no restored assets were found, so dotnet may use only already-cached packages inside the network-denied sandbox.");
            AddUnique(commands, "test", "dotnet test" + suffix,
                "A .NET repository exists; dotnet test discovers referenced test projects." + (restored ? " Existing restore state allows --no-restore." : string.Empty));
        }

        private static void DiscoverNode(string root, List<VerificationCommand> commands)
        {
            string packageJson = Path.Combine(root, "package.json");
            if (!File.Exists(packageJson)) return;
            HashSet<string> scripts = ReadNpmScripts(packageJson);
            if (scripts.Contains("build")) AddUnique(commands, "build", "npm run build", "package.json defines a build script.");
            if (scripts.Contains("test")) AddUnique(commands, "test", "npm test", "package.json defines a test script.");
            if (scripts.Contains("lint")) AddUnique(commands, "lint", "npm run lint", "package.json defines a lint script.");
            if (scripts.Contains("typecheck")) AddUnique(commands, "typecheck", "npm run typecheck", "package.json defines a typecheck script.");
            if (scripts.Contains("check")) AddUnique(commands, "check", "npm run check", "package.json defines a check script.");
        }

        private static void DiscoverPython(string root, List<VerificationCommand> commands)
        {
            bool hasPyProject = File.Exists(Path.Combine(root, "pyproject.toml"));
            bool hasPytestConfig = File.Exists(Path.Combine(root, "pytest.ini"))
                || File.Exists(Path.Combine(root, "tox.ini"))
                || File.Exists(Path.Combine(root, "setup.cfg"));
            bool hasPythonTests = Directory.Exists(Path.Combine(root, "tests"))
                && SafeEnumerate(Path.Combine(root, "tests"), "test_*.py", SearchOption.AllDirectories).Any();
            if (hasPyProject || hasPytestConfig || hasPythonTests)
                AddUnique(commands, "test", "python -m pytest", "Python/pytest project markers were found in the workspace.");

            string pyproject = Path.Combine(root, "pyproject.toml");
            if (File.Exists(pyproject))
            {
                string text = SafeRead(pyproject);
                if (text.Contains("[tool.ruff", StringComparison.OrdinalIgnoreCase))
                    AddUnique(commands, "lint", "python -m ruff check .", "pyproject.toml configures Ruff.");
                if (text.Contains("[tool.mypy", StringComparison.OrdinalIgnoreCase))
                    AddUnique(commands, "typecheck", "python -m mypy .", "pyproject.toml configures mypy.");
            }
        }

        private static void DiscoverRust(string root, List<VerificationCommand> commands)
        {
            if (!File.Exists(Path.Combine(root, "Cargo.toml"))) return;
            AddUnique(commands, "build", "cargo build --offline", "Cargo.toml exists; --offline forbids an accidental registry/network fetch.");
            AddUnique(commands, "test", "cargo test --offline", "Cargo.toml exists; tests run with Cargo offline mode.");
            AddUnique(commands, "lint", "cargo clippy --offline --all-targets --all-features -- -D warnings", "Cargo.toml exists; clippy is the standard Rust lint path when installed.");
        }

        private static void DiscoverGo(string root, List<VerificationCommand> commands)
        {
            if (!File.Exists(Path.Combine(root, "go.mod"))) return;
            AddUnique(commands, "build", "go build ./...", "go.mod exists at workspace root.");
            AddUnique(commands, "test", "go test ./...", "go.mod exists at workspace root.");
            AddUnique(commands, "lint", "go vet ./...", "go.mod exists; go vet requires no invented third-party linter.");
        }

        private static void DiscoverGradle(string root, List<VerificationCommand> commands)
        {
            string gradlewBat = Path.Combine(root, "gradlew.bat");
            string gradlew = Path.Combine(root, "gradlew");
            if (!File.Exists(gradlewBat) && !File.Exists(gradlew)) return;
            string wrapper = File.Exists(gradlewBat) ? ".\\gradlew.bat" : "./gradlew";
            AddUnique(commands, "build", wrapper + " --offline build", "A Gradle wrapper exists; --offline prevents dependency download from the coding sandbox.");
            AddUnique(commands, "test", wrapper + " --offline test", "A Gradle wrapper exists; tests run in offline mode.");
            if (ContainsGradleTaskHint(root, "lint"))
                AddUnique(commands, "lint", wrapper + " --offline lint", "Gradle files contain a lint task/plugin hint.");
        }

        private static void DiscoverMaven(string root, List<VerificationCommand> commands)
        {
            if (!File.Exists(Path.Combine(root, "pom.xml"))) return;
            string wrapper = File.Exists(Path.Combine(root, "mvnw.cmd")) ? ".\\mvnw.cmd"
                : File.Exists(Path.Combine(root, "mvnw")) ? "./mvnw"
                : "mvn";
            AddUnique(commands, "build", wrapper + " -o -DskipTests package", "pom.xml exists; Maven offline mode prevents network fetches.");
            AddUnique(commands, "test", wrapper + " -o test", "pom.xml exists; Maven tests run offline.");
            AddUnique(commands, "verify", wrapper + " -o verify", "pom.xml exists; Maven verify covers configured integration/quality phases.");
        }

        private static void DiscoverCMakeMesonMake(string root, List<VerificationCommand> commands)
        {
            bool hasCmake = File.Exists(Path.Combine(root, "CMakeLists.txt"));
            string? cmakeBuild = new[] { "build", "out", "cmake-build-debug", "cmake-build-release" }
                .Select(name => Path.Combine(root, name))
                .FirstOrDefault(dir => Directory.Exists(dir) && File.Exists(Path.Combine(dir, "CMakeCache.txt")));
            if (hasCmake && cmakeBuild != null)
            {
                string relative = Normalize(Path.GetRelativePath(root, cmakeBuild));
                AddUnique(commands, "build", "cmake --build \"" + relative + "\"", "An existing configured CMake build directory was found; no configure/download is needed.");
                AddUnique(commands, "test", "ctest --test-dir \"" + relative + "\" --output-on-failure", "An existing CMake build directory allows CTest discovery.");
            }

            bool hasMeson = File.Exists(Path.Combine(root, "meson.build"));
            string mesonBuild = Path.Combine(root, "build");
            if (hasMeson && Directory.Exists(Path.Combine(mesonBuild, "meson-private")))
            {
                AddUnique(commands, "build", "meson compile -C build", "meson.build and an existing configured build directory were found.");
                AddUnique(commands, "test", "meson test -C build", "Existing Meson build metadata allows test discovery without reconfiguration.");
            }

            string makefile = new[] { "Makefile", "makefile", "GNUmakefile" }
                .Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists) ?? string.Empty;
            if (makefile.Length > 0)
            {
                AddUnique(commands, "build", "make", "A Makefile exists at workspace root.");
                string text = SafeRead(makefile);
                if (Regex.IsMatch(text, @"(?m)^\s*test\s*:", RegexOptions.IgnoreCase))
                    AddUnique(commands, "test", "make test", "The Makefile defines a test target.");
                if (Regex.IsMatch(text, @"(?m)^\s*(lint|check)\s*:", RegexOptions.IgnoreCase))
                    AddUnique(commands, "check", Regex.IsMatch(text, @"(?m)^\s*check\s*:", RegexOptions.IgnoreCase) ? "make check" : "make lint", "The Makefile defines a lint/check target.");
            }
        }

        private static void DiscoverDartFlutter(string root, List<VerificationCommand> commands)
        {
            string pubspec = Path.Combine(root, "pubspec.yaml");
            if (!File.Exists(pubspec)) return;
            string text = SafeRead(pubspec);
            bool flutter = text.Contains("flutter:", StringComparison.OrdinalIgnoreCase)
                || text.Contains("sdk: flutter", StringComparison.OrdinalIgnoreCase);
            if (flutter)
            {
                AddUnique(commands, "test", "flutter test", "pubspec.yaml identifies a Flutter project.");
                AddUnique(commands, "lint", "flutter analyze", "Flutter analyze is available without inventing a linter configuration.");
            }
            else
            {
                AddUnique(commands, "test", "dart test", "pubspec.yaml identifies a Dart project.");
                AddUnique(commands, "lint", "dart analyze", "Dart analyze is the built-in static analysis path.");
            }
        }

        private static void DiscoverRuby(string root, List<VerificationCommand> commands)
        {
            if (!File.Exists(Path.Combine(root, "Gemfile"))) return;
            if (File.Exists(Path.Combine(root, "Rakefile")))
                AddUnique(commands, "test", "bundle exec rake test", "Gemfile and Rakefile exist; use the repository's bundled Rake test target.");
            if (SafeEnumerate(root, "*_spec.rb", SearchOption.AllDirectories).Any())
                AddUnique(commands, "test", "bundle exec rspec", "RSpec files were found outside pruned dependency/build trees.");
        }

        private static void DiscoverPhp(string root, List<VerificationCommand> commands)
        {
            if (!File.Exists(Path.Combine(root, "composer.json"))) return;
            if (File.Exists(Path.Combine(root, "vendor", "bin", "phpunit")) || File.Exists(Path.Combine(root, "vendor", "bin", "phpunit.bat")))
                AddUnique(commands, "test", OperatingSystem.IsWindows() ? "vendor\\bin\\phpunit.bat" : "vendor/bin/phpunit", "Composer vendor directory contains PHPUnit; no package install is needed.");
        }

        private static bool ContainsGradleTaskHint(string root, string hint)
        {
            foreach (string name in new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" })
            {
                string path = Path.Combine(root, name);
                if (File.Exists(path) && SafeRead(path).Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern, SearchOption option)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            if (option == SearchOption.TopDirectoryOnly)
            {
                try
                {
                    return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                        .Take(MaximumEnumeratedFiles)
                        .ToArray();
                }
                catch { return Array.Empty<string>(); }
            }
            return SafeEnumerateRecursive(root, pattern);
        }

        private static IReadOnlyList<string> SafeEnumerateRecursive(string root, string pattern)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            int visitedDirectories = 0;

            while (pending.Count > 0
                && found.Count < MaximumEnumeratedFiles
                && visitedDirectories < MaximumVisitedDirectories)
            {
                string current = pending.Pop();
                visitedDirectories++;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly))
                    {
                        found.Add(file);
                        if (found.Count >= MaximumEnumeratedFiles) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

                if (found.Count >= MaximumEnumeratedFiles) break;
                try
                {
                    foreach (string directory in Directory.EnumerateDirectories(current))
                    {
                        string name = Path.GetFileName(directory);
                        if (IsRecursiveNoiseDirectoryName(name)) continue;
                        pending.Push(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
            }
            return found;
        }

        private static IEnumerable<string> SafeDirectRepositories(string root)
        {
            try
            {
                return Directory.EnumerateDirectories(root)
                    .Where(path => File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")))
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static bool IsRecursiveNoiseDirectoryName(string name) =>
            name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".parux-agents", StringComparison.OrdinalIgnoreCase)
            || name.Equals("target", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vendor", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".gradle", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".venv", StringComparison.OrdinalIgnoreCase)
            || name.Equals("venv", StringComparison.OrdinalIgnoreCase)
            || name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dist", StringComparison.OrdinalIgnoreCase);

        private static HashSet<string> ReadNpmScripts(string packageJson)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (!doc.RootElement.TryGetProperty("scripts", out JsonElement scripts) || scripts.ValueKind != JsonValueKind.Object)
                    return result;
                foreach (JsonProperty property in scripts.EnumerateObject()) result.Add(property.Name);
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return string.Empty; }
        }

        private static string Normalize(string path) => path.Replace('\\', '/');

        private static void AddUnique(List<VerificationCommand> commands, string kind, string command, string reason)
        {
            if (commands.Any(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase))) return;
            commands.Add(new VerificationCommand { Kind = kind, Command = command, Reason = reason });
        }
    }
}
