using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class KnowledgeBrowserToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-kb-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _knowledge;
    private readonly string _db;
    private readonly string _embedding;
    private readonly RagStore _rag;
    private readonly KnowledgeBrowserTools _tools;
    private readonly RagSourceDto _source;

    public KnowledgeBrowserToolsTests()
    {
        _knowledge = Path.Combine(_root, "knowledge");
        _db = Path.Combine(_root, "db");
        _embedding = Path.Combine(_root, "embedding-not-installed");
        Directory.CreateDirectory(_knowledge);
        Directory.CreateDirectory(Path.Combine(_knowledge, "nested"));
        File.WriteAllText(Path.Combine(_knowledge, "notes.txt"),
            "alpha first line\nThe deploy script lives at scripts/deploy.\nomega last line\n");
        File.WriteAllText(Path.Combine(_knowledge, "nested", "readme.md"),
            "# Knowledge\nNeedle Phrase appears here.\nNeedle second hit.\n");
        File.WriteAllText(Path.Combine(_knowledge, ".hidden.md"),
            "hidden file names must remain exact in the virtual knowledge namespace.\n");
        File.WriteAllBytes(Path.Combine(_knowledge, "opaque.bin"), new byte[] { 0, 1, 2, 3 });

        var embeddings = new EmbeddingService(_embedding);
        _rag = new RagStore(embeddings, null, _db);
        _source = _rag.AddSource(_knowledge);
        _tools = new KnowledgeBrowserTools(_rag);
    }

    public void Dispose()
    {
        try { _rag.RemoveSource(_source.Id); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Tool_schemas_exist_only_for_enabled_knowledge_and_respect_skill_allowlists()
    {
        var all = _tools.GetToolSchemas();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, schema => schema?["function"]?["name"]?.GetValue<string>() == KnowledgeBrowserTools.ExecToolName);
        Assert.Contains(all, schema => schema?["function"]?["name"]?.GetValue<string>() == KnowledgeBrowserTools.SearchToolName);

        var execOnly = _tools.GetToolSchemas(new[] { KnowledgeBrowserTools.ExecToolName });
        Assert.Single(execOnly);
        Assert.Equal(KnowledgeBrowserTools.ExecToolName,
            execOnly[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Roots_list_tree_and_stat_expose_only_the_enabled_source_namespace()
    {
        string roots = await _tools.ExecuteCommandAsync("roots", CancellationToken.None);
        Assert.Contains(_source.Id, roots);
        Assert.DoesNotContain(_knowledge, roots); // do not disclose the absolute host path to the model

        string list = await _tools.ExecuteCommandAsync("ls " + _source.Id, CancellationToken.None);
        Assert.Contains(_source.Id + "/notes.txt", list);
        Assert.Contains(_source.Id + "/nested/", list);
        Assert.Contains(_source.Id + "/.hidden.md", list);

        string tree = await _tools.ExecuteCommandAsync("tree " + _source.Id, CancellationToken.None);
        Assert.Contains("readme.md", tree);

        string stat = await _tools.ExecuteCommandAsync("stat " + _source.Id + "/notes.txt", CancellationToken.None);
        Assert.Contains("type: file", stat);
        Assert.Contains("extractable: True", stat);
    }

    [Fact]
    public async Task Cat_head_tail_and_quoted_grep_return_real_file_evidence()
    {
        string virtualFile = _source.Id + "/notes.txt";

        string cat = await _tools.ExecuteCommandAsync("cat " + virtualFile, CancellationToken.None);
        Assert.Contains("scripts/deploy", cat);

        string head = await _tools.ExecuteCommandAsync("head " + virtualFile + " 1", CancellationToken.None);
        Assert.Contains("alpha first line", head);
        Assert.DoesNotContain("omega last line", head);

        string tail = await _tools.ExecuteCommandAsync("tail " + virtualFile + " 1", CancellationToken.None);
        Assert.Contains("omega last line", tail);
        Assert.DoesNotContain("alpha first line", tail);

        string grep = await _tools.ExecuteCommandAsync(
            "grep \"Needle Phrase\" " + _source.Id + "/nested", CancellationToken.None);
        Assert.Contains("readme.md:2", grep);
        Assert.Contains("Needle Phrase", grep);
    }

    [Fact]
    public async Task Range_reads_exact_one_based_lines_and_enforces_the_line_budget()
    {
        string virtualFile = _source.Id + "/notes.txt";

        string range = await _tools.ExecuteCommandAsync("range " + virtualFile + " 2 3", CancellationToken.None);
        Assert.Contains("2: The deploy script lives at scripts/deploy.", range);
        Assert.Contains("3: omega last line", range);
        Assert.DoesNotContain("alpha first line", range);

        string rejected = await _tools.ExecuteCommandAsync("range " + virtualFile + " 1 501", CancellationToken.None);
        Assert.Contains("at most 500 lines", rejected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wc_reports_bounded_file_and_pipeline_counts()
    {
        string virtualFile = _source.Id + "/notes.txt";

        string fileCount = await _tools.ExecuteCommandAsync("wc " + virtualFile, CancellationToken.None);
        Assert.Contains("lines:", fileCount);
        Assert.Contains("words:", fileCount);
        Assert.Contains("characters:", fileCount);
        Assert.Contains("bytes:", fileCount);

        string pipelineCount = await _tools.ExecuteCommandAsync("head " + virtualFile + " 2 | wc", CancellationToken.None);
        Assert.Contains("lines: 2", pipelineCount);
        Assert.Contains("words:", pipelineCount);
    }

    [Fact]
    public async Task Regex_grep_is_bounded_and_literal_grep_remains_literal()
    {
        string scope = _source.Id + "/nested";

        string regex = await _tools.ExecuteCommandAsync(
            "grep -E \"Needle.*(Phrase|second)\" " + scope,
            CancellationToken.None);
        Assert.Contains("readme.md:2", regex);
        Assert.Contains("readme.md:3", regex);

        string literal = await _tools.ExecuteCommandAsync(
            "grep \"Needle.*Phrase\" " + scope,
            CancellationToken.None);
        Assert.Contains("No literal matches", literal, StringComparison.OrdinalIgnoreCase);

        string oversized = new('a', 513);
        string rejected = await _tools.ExecuteCommandAsync(
            "grep -E \"" + oversized + "\" " + scope,
            CancellationToken.None);
        Assert.Contains("512 character limit", rejected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_single_in_process_pipeline_is_supported_but_shell_chaining_is_rejected()
    {
        string scope = _source.Id + "/nested";

        string head = await _tools.ExecuteCommandAsync(
            "grep \"Needle\" " + scope + " | head 1",
            CancellationToken.None);
        Assert.Contains("readme.md:2", head);
        Assert.DoesNotContain("readme.md:3", head);

        string multiple = await _tools.ExecuteCommandAsync(
            "grep \"Needle\" " + scope + " | head 1 | tail 1",
            CancellationToken.None);
        Assert.Contains("exactly one", multiple, StringComparison.OrdinalIgnoreCase);

        string unsupported = await _tools.ExecuteCommandAsync(
            "grep \"Needle\" " + scope + " | sort",
            CancellationToken.None);
        Assert.Contains("limited to head", unsupported, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Find_supports_simple_globs_without_becoming_a_shell_or_mangling_dotfiles()
    {
        string found = await _tools.ExecuteCommandAsync(
            "find \"*.md\" " + _source.Id, CancellationToken.None);
        Assert.Contains(_source.Id + "/nested/readme.md", found);
        Assert.Contains(_source.Id + "/.hidden.md", found);
        Assert.DoesNotContain("notes.txt", found);

        string rejected = await _tools.ExecuteCommandAsync("rm -rf /", CancellationToken.None);
        Assert.Contains("Unsupported read-only knowledge command", rejected);
    }

    [Fact]
    public async Task Path_traversal_outside_the_enabled_source_is_rejected()
    {
        string result = await _tools.ExecuteCommandAsync(
            "cat " + _source.Id + "/../outside.txt", CancellationToken.None);

        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("escapes", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_binary_files_are_not_interpreted_as_text()
    {
        string result = await _tools.ExecuteCommandAsync(
            "cat " + _source.Id + "/opaque.bin", CancellationToken.None);

        Assert.Contains("not supported", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Native_tool_call_parses_json_arguments_and_executes_the_same_bounded_reader()
    {
        string result = await _tools.CallAsync(
            KnowledgeBrowserTools.ExecToolName,
            "{\"command\":\"head " + _source.Id + "/notes.txt 1\"}",
            CancellationToken.None);

        Assert.Contains("alpha first line", result);
        Assert.DoesNotContain("omega last line", result);
    }
}
