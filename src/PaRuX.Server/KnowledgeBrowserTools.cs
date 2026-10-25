using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Native read-only tools over folders the user explicitly added to PaRuX RAG. This gives capable
/// local models an agentic, filesystem-style way to list/grep/read knowledge instead of relying on
/// one retrieval pass. It is NOT a shell: commands are parsed and executed by PaRuX itself, paths
/// are confined to enabled RAG source roots, reparse-point traversal is rejected, and output is
/// bounded before it can consume the model context window. The optional single pipeline is also
/// interpreted in-process and can only post-process already-bounded text with head/tail/wc.
/// </summary>
public sealed class KnowledgeBrowserTools
{
    public const string ExecToolName = "parux_kb_exec";
    public const string SearchToolName = "parux_kb_search";
    public const int MaximumOutputCharacters = 30_000;
    internal const int MaximumRegexPatternCharacters = 512;
    private const int MaximumEnumeratedEntries = 750;
    private const int MaximumGrepFiles = 300;
    private const int MaximumGrepMatches = 160;
    private const int MaximumRangeLines = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly EnumerationOptions RecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly RagStore _rag;

    public KnowledgeBrowserTools(RagStore rag) => _rag = rag ?? throw new ArgumentNullException(nameof(rag));

    public static bool IsBuiltInTool(string? name) =>
        string.Equals(name, ExecToolName, StringComparison.Ordinal)
        || string.Equals(name, SearchToolName, StringComparison.Ordinal);

    public JsonArray GetToolSchemas(IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        if (!HasEnabledSources()) return schemas;

        if (ContextEngineService.ToolNameAllowed(SearchToolName, allowedTools))
        {
            schemas.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = SearchToolName,
                    ["description"] = "Semantic/hybrid search over the user's enabled local PaRuX knowledge folders. Use this for concepts; use parux_kb_exec for listing, exact grep, line-range reads, or reading a specific file.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string" },
                            ["topK"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 12 }
                        },
                        ["required"] = new JsonArray("query")
                    }
                }
            });
        }

        if (ContextEngineService.ToolNameAllowed(ExecToolName, allowedTools))
        {
            schemas.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ExecToolName,
                    ["description"] = "Read-only filesystem-style access to folders already enabled as PaRuX knowledge sources. Commands: roots; ls/tree [sourceId/path]; cat sourceId/file; head/tail sourceId/file [N]; range sourceId/file START END; wc sourceId/file; grep \"text\" [sourceId/path]; grep -E \"safe-regex\" [sourceId/path]; find \"glob\" [sourceId/path]; stat sourceId/path. One optional in-process pipeline may end with `| head N`, `| tail N`, or `| wc`. This is not a shell and cannot write or execute programs.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["command"] = new JsonObject { ["type"] = "string", ["maxLength"] = 4_096 }
                        },
                        ["required"] = new JsonArray("command")
                    }
                }
            });
        }
        return schemas;
    }

    public async Task<string> CallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        if (!IsBuiltInTool(toolName)) throw new InvalidOperationException("Unknown PaRuX knowledge tool.");
        using JsonDocument args = ParseArguments(argumentsJson);

        if (toolName == SearchToolName)
        {
            string query = args.RootElement.TryGetProperty("query", out JsonElement q) && q.ValueKind == JsonValueKind.String
                ? q.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (query.Length == 0) return "query is required.";
            int topK = args.RootElement.TryGetProperty("topK", out JsonElement k) && k.TryGetInt32(out int parsed)
                ? Math.Clamp(parsed, 1, 12) : 6;
            List<RagChunkMatch> matches = await _rag.RetrieveAsync(query, topK, ct).ConfigureAwait(false);
            if (matches.Count == 0) return "No relevant indexed knowledge chunks were found.";
            var sb = new StringBuilder();
            for (int i = 0; i < matches.Count; i++)
            {
                RagChunkMatch match = matches[i];
                sb.Append("[").Append(i + 1).Append("] ").AppendLine(match.FilePath);
                sb.AppendLine(Truncate(match.Content, 4_000));
                sb.AppendLine();
            }
            return Truncate(sb.ToString().Trim(), MaximumOutputCharacters);
        }

        string command = args.RootElement.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()?.Trim() ?? string.Empty : string.Empty;
        return await ExecuteCommandAsync(command, ct).ConfigureAwait(false);
    }

    internal async Task<string> ExecuteCommandAsync(string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "A command is required. Start with `roots` to discover enabled knowledge sources.";
        if (command.Length > 4_096) return "Knowledge command exceeds the PaRuX command-length budget.";

        try
        {
            (string First, string? Second) = SplitSinglePipeline(command);
            string output = await ExecuteBaseCommandAsync(First, ct).ConfigureAwait(false);
            if (Second == null) return output;
            return ApplyPipeline(output, Second);
        }
        catch (OperationCanceledException) { throw; }
        catch (RegexMatchTimeoutException)
        {
            return "Knowledge command failed: regex exceeded the PaRuX evaluation timeout.";
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return "Knowledge command failed: " + ex.Message;
        }
    }

    private async Task<string> ExecuteBaseCommandAsync(string command, CancellationToken ct)
    {
        List<string> tokens = TokenizeCommand(command);
        if (tokens.Count == 0) return "A command is required.";
        string verb = tokens[0].ToLowerInvariant();
        return verb switch
        {
            "roots" => RenderRoots(),
            "ls" => RenderList(tokens.Count > 1 ? tokens[1] : string.Empty),
            "tree" => RenderTree(tokens.Count > 1 ? tokens[1] : string.Empty, ct),
            "cat" => await ReadFileAsync(RequireArg(tokens, 1, "cat requires a sourceId/file path."), null, ct).ConfigureAwait(false),
            "head" => await ReadFileAsync(RequireArg(tokens, 1, "head requires a sourceId/file path."), ParseLineCount(tokens, 2, 40), ct, tail: false).ConfigureAwait(false),
            "tail" => await ReadFileAsync(RequireArg(tokens, 1, "tail requires a sourceId/file path."), ParseLineCount(tokens, 2, 40), ct, tail: true).ConfigureAwait(false),
            "range" => await ReadRangeAsync(
                RequireArg(tokens, 1, "range requires a sourceId/file path."),
                ParseRequiredPositiveInt(tokens, 2, "range requires START and END line numbers."),
                ParseRequiredPositiveInt(tokens, 3, "range requires START and END line numbers."),
                ct).ConfigureAwait(false),
            "wc" => await WordCountFileAsync(RequireArg(tokens, 1, "wc requires a sourceId/file path."), ct).ConfigureAwait(false),
            "grep" => await ExecuteGrepAsync(tokens, ct).ConfigureAwait(false),
            "find" => Find(RequireArg(tokens, 1, "find requires a file-name glob."), tokens.Count > 2 ? tokens[2] : string.Empty, ct),
            "stat" => Stat(RequireArg(tokens, 1, "stat requires a sourceId/path.")),
            _ => "Unsupported read-only knowledge command. Use roots, ls, tree, cat, head, tail, range, wc, grep, find, or stat."
        };
    }

    private async Task<string> ExecuteGrepAsync(List<string> tokens, CancellationToken ct)
    {
        bool regex = tokens.Count > 1 && (tokens[1] == "-E" || tokens[1] == "--regex");
        int patternIndex = regex ? 2 : 1;
        string pattern = RequireArg(tokens, patternIndex, regex ? "grep -E requires a regex pattern." : "grep requires literal text.");
        string scope = tokens.Count > patternIndex + 1 ? tokens[patternIndex + 1] : string.Empty;
        return await GrepAsync(pattern, scope, regex, ct).ConfigureAwait(false);
    }

    private bool HasEnabledSources() =>
        _rag.ListSources().Any(source => source.Enabled && Directory.Exists(source.FolderPath));

    private List<RagSourceDto> EnabledSources() => _rag.ListSources()
        .Where(source => source.Enabled && Directory.Exists(source.FolderPath))
        .ToList();

    private string RenderRoots()
    {
        List<RagSourceDto> sources = EnabledSources();
        if (sources.Count == 0) return "No enabled local knowledge folders are available.";
        return string.Join("\n", sources.Select(source =>
            source.Id + "/  ->  " + Path.GetFileName(Path.TrimEndingDirectorySeparator(source.FolderPath))));
    }

    private string RenderList(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return RenderRoots();
        (RagSourceDto source, string full, string relative) = Resolve(virtualPath, requireExists: true);
        if (File.Exists(full)) return source.Id + "/" + relative;
        var lines = Directory.EnumerateFileSystemEntries(full)
            .Where(path => !IsReparsePoint(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumEnumeratedEntries)
            .Select(path => source.Id + "/" + NormalizeRelative(source.FolderPath, path) + (Directory.Exists(path) ? "/" : string.Empty));
        return Truncate(string.Join("\n", lines), MaximumOutputCharacters);
    }

    private string RenderTree(string virtualPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return RenderRoots();
        (RagSourceDto source, string full, string relative) = Resolve(virtualPath, requireExists: true);
        if (File.Exists(full)) return source.Id + "/" + relative;
        var sb = new StringBuilder();
        int count = 0;
        void Walk(string directory, int depth)
        {
            if (depth > 6 || count >= MaximumEnumeratedEntries) return;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (count++ >= MaximumEnumeratedEntries) break;
                if (IsReparsePoint(entry)) continue;
                sb.Append(' ', depth * 2).Append(Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    sb.Append('/').AppendLine();
                    Walk(entry, depth + 1);
                }
                else sb.AppendLine();
            }
        }
        sb.Append(source.Id).Append('/').Append(relative).Append(relative.Length == 0 ? string.Empty : "/").AppendLine();
        Walk(full, 1);
        if (count >= MaximumEnumeratedEntries) sb.AppendLine("[tree truncated by PaRuX entry budget]");
        return Truncate(sb.ToString().TrimEnd(), MaximumOutputCharacters);
    }

    private async Task<string> ReadFileAsync(string virtualPath, int? lines, CancellationToken ct, bool tail = false)
    {
        (_, string full, _) = Resolve(virtualPath, requireExists: true);
        if (!File.Exists(full)) throw new FileNotFoundException("The knowledge path is not a file.");
        if (!DocumentTextExtractor.CanExtract(full))
            throw new InvalidDataException("This indexed-folder file type is not supported for text extraction.");
        string text = await DocumentTextExtractor.ExtractAsync(full, ct).ConfigureAwait(false);
        if (lines.HasValue)
        {
            string[] split = NormalizeLines(text);
            IEnumerable<string> slice = tail ? split.TakeLast(lines.Value) : split.Take(lines.Value);
            text = string.Join("\n", slice);
        }
        return Truncate(text, MaximumOutputCharacters);
    }

    private async Task<string> ReadRangeAsync(string virtualPath, int startLine, int endLine, CancellationToken ct)
    {
        if (endLine < startLine) throw new InvalidDataException("range END must be greater than or equal to START.");
        if (endLine - startLine + 1 > MaximumRangeLines)
            throw new InvalidDataException("range may read at most " + MaximumRangeLines + " lines per command.");
        (_, string full, _) = Resolve(virtualPath, requireExists: true);
        if (!File.Exists(full)) throw new FileNotFoundException("The knowledge path is not a file.");
        if (!DocumentTextExtractor.CanExtract(full))
            throw new InvalidDataException("This indexed-folder file type is not supported for text extraction.");
        string[] lines = NormalizeLines(await DocumentTextExtractor.ExtractAsync(full, ct).ConfigureAwait(false));
        if (startLine > lines.Length) return "Requested range starts after end of file (" + lines.Length + " lines).";
        int take = Math.Min(lines.Length, endLine) - startLine + 1;
        var sb = new StringBuilder();
        for (int i = 0; i < take; i++)
            sb.Append(startLine + i).Append(": ").AppendLine(lines[startLine - 1 + i]);
        return Truncate(sb.ToString().TrimEnd(), MaximumOutputCharacters);
    }

    private async Task<string> WordCountFileAsync(string virtualPath, CancellationToken ct)
    {
        (_, string full, _) = Resolve(virtualPath, requireExists: true);
        if (!File.Exists(full)) throw new FileNotFoundException("The knowledge path is not a file.");
        if (!DocumentTextExtractor.CanExtract(full))
            throw new InvalidDataException("This indexed-folder file type is not supported for text extraction.");
        string text = await DocumentTextExtractor.ExtractAsync(full, ct).ConfigureAwait(false);
        FileInfo info = new(full);
        return RenderWordCount(text, info.Length);
    }

    private async Task<string> GrepAsync(string pattern, string virtualPath, bool regex, CancellationToken ct)
    {
        if (pattern.Length == 0) return "grep pattern may not be empty.";
        Regex? compiled = null;
        if (regex)
        {
            if (pattern.Length > MaximumRegexPatternCharacters)
                throw new InvalidDataException("Regex pattern exceeds the PaRuX " + MaximumRegexPatternCharacters + " character limit.");
            compiled = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                RegexTimeout);
        }

        List<(RagSourceDto Source, string Root)> scopes = ResolveScopes(virtualPath);
        var output = new StringBuilder();
        int files = 0;
        int matches = 0;
        foreach ((RagSourceDto source, string root) in scopes)
        {
            IEnumerable<string> candidates = File.Exists(root) ? new[] { root } : Directory.EnumerateFiles(root, "*", RecursiveEnumeration);
            foreach (string file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (files++ >= MaximumGrepFiles || matches >= MaximumGrepMatches) break;
                if (IsReparsePoint(file) || !DocumentTextExtractor.IsTextDocument(file)) continue;
                string[] lines;
                try { lines = NormalizeLines(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)); }
                catch { continue; }
                for (int line = 0; line < lines.Length && matches < MaximumGrepMatches; line++)
                {
                    bool hit = compiled != null
                        ? compiled.IsMatch(lines[line])
                        : lines[line].Contains(pattern, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                    output.Append(source.Id).Append('/').Append(NormalizeRelative(source.FolderPath, file))
                        .Append(':').Append(line + 1).Append(": ").AppendLine(Truncate(lines[line].Trim(), 500));
                    matches++;
                }
            }
        }
        if (matches == 0) return regex
            ? "No regex matches found in supported text knowledge files."
            : "No literal matches found in supported text knowledge files.";
        if (files >= MaximumGrepFiles || matches >= MaximumGrepMatches) output.AppendLine("[grep truncated by PaRuX scan budget]");
        return Truncate(output.ToString().TrimEnd(), MaximumOutputCharacters);
    }

    private string Find(string glob, string virtualPath, CancellationToken ct)
    {
        List<(RagSourceDto Source, string Root)> scopes = ResolveScopes(virtualPath);
        var output = new List<string>();
        foreach ((RagSourceDto source, string root) in scopes)
        {
            IEnumerable<string> candidates = File.Exists(root) ? new[] { root } : Directory.EnumerateFiles(root, "*", RecursiveEnumeration);
            foreach (string file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (output.Count >= MaximumEnumeratedEntries) break;
                if (IsReparsePoint(file)) continue;
                if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), ignoreCase: true)) continue;
                output.Add(source.Id + "/" + NormalizeRelative(source.FolderPath, file));
            }
        }
        return output.Count == 0 ? "No matching knowledge files found." : Truncate(string.Join("\n", output), MaximumOutputCharacters);
    }

    private string Stat(string virtualPath)
    {
        (RagSourceDto source, string full, string relative) = Resolve(virtualPath, requireExists: true);
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            return $"path: {source.Id}/{relative}\ntype: file\nsize: {info.Length}\nmodifiedUtc: {info.LastWriteTimeUtc:O}\nextractable: {DocumentTextExtractor.CanExtract(full)}";
        }
        var directory = new DirectoryInfo(full);
        return $"path: {source.Id}/{relative}\ntype: directory\nmodifiedUtc: {directory.LastWriteTimeUtc:O}";
    }

    private List<(RagSourceDto Source, string Root)> ResolveScopes(string virtualPath)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath))
        {
            (RagSourceDto source, string full, _) = Resolve(virtualPath, requireExists: true);
            return new List<(RagSourceDto, string)> { (source, full) };
        }
        return EnabledSources().Select(source =>
        {
            EnsureSourceRootSafe(source.FolderPath);
            return (source, Path.GetFullPath(source.FolderPath));
        }).ToList();
    }

    private (RagSourceDto Source, string FullPath, string RelativePath) Resolve(string virtualPath, bool requireExists)
    {
        List<RagSourceDto> sources = EnabledSources();
        if (sources.Count == 0) throw new InvalidDataException("No enabled knowledge sources are available.");
        string normalized = (virtualPath ?? string.Empty).Replace('\\', '/').Trim('/');
        string[] parts = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);

        RagSourceDto? source = null;
        string relative = normalized;
        if (parts.Length > 0)
        {
            List<RagSourceDto> idMatches = sources.Where(candidate =>
                candidate.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                || candidate.Id.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (idMatches.Count == 1)
            {
                source = idMatches[0];
                relative = parts.Length > 1 ? parts[1] : string.Empty;
            }
        }
        if (source == null && sources.Count == 1) source = sources[0];
        if (source == null)
            throw new InvalidDataException("Knowledge path must begin with one unambiguous source id. Run `roots` first.");

        string root = Path.GetFullPath(source.FolderPath);
        EnsureSourceRootSafe(root);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Knowledge path escapes its enabled source root.");
        EnsureNoReparseTraversal(root, full);
        if (requireExists && !File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Knowledge path does not exist.");
        return (source, full, NormalizeRelative(root, full));
    }

    private static void EnsureSourceRootSafe(string root)
    {
        if (IsReparsePoint(root))
            throw new UnauthorizedAccessException("Knowledge source roots that are reparse points are not browsed by the agentic reader.");
    }

    private static void EnsureNoReparseTraversal(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full);
        if (relative == ".") return;
        string current = root;
        foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new UnauthorizedAccessException("Knowledge browsing does not follow symbolic links or junctions.");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static string NormalizeRelative(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    private static (string First, string? Second) SplitSinglePipeline(string command)
    {
        int pipe = -1;
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char current = command[i];
            if ((current == '\'' || current == '"'))
            {
                if (quote == '\0') quote = current;
                else if (quote == current) quote = '\0';
                continue;
            }
            if (current != '|' || quote != '\0') continue;
            if (pipe >= 0) throw new InvalidDataException("Knowledge pipelines allow exactly one `|` stage.");
            pipe = i;
        }
        if (quote != '\0') throw new InvalidDataException("Knowledge command contains an unterminated quote.");
        if (pipe < 0) return (command.Trim(), null);
        string first = command[..pipe].Trim();
        string second = command[(pipe + 1)..].Trim();
        if (first.Length == 0 || second.Length == 0)
            throw new InvalidDataException("Both sides of the bounded knowledge pipeline are required.");
        return (first, second);
    }

    private static string ApplyPipeline(string input, string stage)
    {
        List<string> tokens = TokenizeCommand(stage);
        if (tokens.Count == 0) throw new InvalidDataException("Pipeline stage is empty.");
        string verb = tokens[0].ToLowerInvariant();
        string[] lines = NormalizeLines(input);
        return verb switch
        {
            "head" => Truncate(string.Join("\n", lines.Take(ParseLineCount(tokens, 1, 40))), MaximumOutputCharacters),
            "tail" => Truncate(string.Join("\n", lines.TakeLast(ParseLineCount(tokens, 1, 40))), MaximumOutputCharacters),
            "wc" when tokens.Count == 1 => RenderWordCount(input, Encoding.UTF8.GetByteCount(input)),
            _ => throw new InvalidDataException("Pipeline post-processing is limited to head [N], tail [N], or wc; no shell/program execution exists.")
        };
    }

    private static string RenderWordCount(string text, long bytes)
    {
        string[] lines = NormalizeLines(text);
        int words = Regex.Matches(text, @"\S+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout).Count;
        return $"lines: {lines.Length}\nwords: {words}\ncharacters: {text.Length}\nbytes: {bytes}";
    }

    private static string[] NormalizeLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] split = normalized.Split('\n');
        // A single trailing newline is the file's line terminator, not an extra blank final line
        // (otherwise "tail 1" on any newline-terminated file returns "" instead of the last line).
        if (split.Length > 1 && split[^1].Length == 0)
            return split[..^1];
        return split;
    }

    private static int ParseLineCount(List<string> tokens, int index, int fallback)
    {
        if (tokens.Count <= index || !int.TryParse(tokens[index], out int count)) return fallback;
        return Math.Clamp(count, 1, 500);
    }

    private static int ParseRequiredPositiveInt(List<string> tokens, int index, string message)
    {
        if (tokens.Count <= index || !int.TryParse(tokens[index], out int value) || value < 1)
            throw new InvalidDataException(message);
        return value;
    }

    private static string RequireArg(List<string> tokens, int index, string message) =>
        tokens.Count > index && tokens[index].Length > 0 ? tokens[index] : throw new InvalidDataException(message);

    private static JsonDocument ParseArguments(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException ex) { throw new InvalidDataException("Knowledge tool arguments are not valid JSON.", ex); }
    }

    private static List<string> TokenizeCommand(string command)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(command, "(?:\\\"(?<q>[^\\\"]*)\\\"|'(?<s>[^']*)'|(?<u>\\S+))"))
        {
            string value = match.Groups["q"].Success ? match.Groups["q"].Value
                : match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["u"].Value;
            if (value.Length > 0) result.Add(value);
        }
        return result;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Length <= max ? text : text[..max] + "\n[output truncated by PaRuX knowledge budget]";
    }
}
