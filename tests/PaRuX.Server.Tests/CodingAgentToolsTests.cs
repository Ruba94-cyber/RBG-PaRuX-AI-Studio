using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Coding Runtime upgrade (roadmap P1/C1) - CodingAgentTools is linked into this test project
/// from PaRuX.App (see the .csproj Compile Include) rather than duplicated, so these tests cover
/// the exact same code CodingAgentService dispatches to.
/// </summary>
public sealed class CodingAgentToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-codingtools-tests-" + Guid.NewGuid().ToString("N"));

    public CodingAgentToolsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- TryReplaceRange: the stale-content-checked precise edit ----

    [Fact]
    public void TryReplaceRange_applies_a_unique_match()
    {
        (bool success, string newContent, string? error) = CodingAgentTools.TryReplaceRange(
            "line1\nline2\nline3", "line2", "REPLACED");

        Assert.True(success);
        Assert.Equal("line1\nREPLACED\nline3", newContent);
        Assert.Null(error);
    }

    [Fact]
    public void TryReplaceRange_fails_when_oldText_is_not_found()
    {
        (bool success, string newContent, string? error) = CodingAgentTools.TryReplaceRange(
            "line1\nline2\nline3", "does not exist", "REPLACED");

        Assert.False(success);
        Assert.Equal("line1\nline2\nline3", newContent);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void TryReplaceRange_fails_when_oldText_matches_more_than_once()
    {
        (bool success, string newContent, string? error) = CodingAgentTools.TryReplaceRange(
            "foo\nbar\nfoo\nbaz", "foo", "REPLACED");

        Assert.False(success);
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void TryReplaceRange_rejects_an_empty_oldText()
    {
        (bool success, string newContent, string? error) = CodingAgentTools.TryReplaceRange(
            "content", "", "new");

        Assert.False(success);
        Assert.Contains("create_file", error);
    }

    [Fact]
    public void TryReplaceRange_can_delete_text_by_replacing_with_empty_string()
    {
        (bool success, string newContent, string? error) = CodingAgentTools.TryReplaceRange(
            "before-MARKER-after", "-MARKER-", "");

        Assert.True(success);
        Assert.Equal("beforeafter", newContent);
    }

    [Fact]
    public async Task ApplyReplaceRangeFile_does_not_write_or_report_mutation_when_splice_is_identical()
    {
        string path = Path.Combine(_tempDir, "unchanged.cs");
        const string source = "if (quantity >= 10) return total * 90 / 100;\r\n";
        await File.WriteAllTextAsync(path, source, new System.Text.UTF8Encoding(false));

        // A read-only handle that permits concurrent reads but denies writes proves the helper
        // does not reopen/truncate an identity replacement as a successful edit.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (bool success, bool changed, string? error, string? note) =
                await CodingAgentTools.TryApplyReplaceRangeFileAsync(
                    path, "quantity >= 10", "quantity >= 10", CancellationToken.None);

            Assert.True(success, error);
            Assert.False(changed);
            Assert.Null(error);
            Assert.Contains("no content change", note, StringComparison.Ordinal);
        }

        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyReplaceRangeFile_writes_genuine_change_and_preserves_tolerant_line_ending_match()
    {
        string path = Path.Combine(_tempDir, "changed.cs");
        string source = "class Sample\r\n{\r\n    public int Compute(int value)\r\n    {\r\n        return value * 2;\r\n    }\r\n}\r\n";
        await File.WriteAllTextAsync(path, source, new System.Text.UTF8Encoding(false));

        (bool success, bool changed, string? error, string? note) =
            await CodingAgentTools.TryApplyReplaceRangeFileAsync(path,
                "        return value * 2;\n    }", "        return value * 4;\n    }", CancellationToken.None);

        Assert.True(success, error);
        Assert.True(changed);
        Assert.Equal("matched after normalizing line endings", note);
        string updated = await File.ReadAllTextAsync(path);
        Assert.Contains("        return value * 4;\r\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("value * 2", updated, StringComparison.Ordinal);
        Assert.Equal(updated.Split('\n').Length - 1, updated.Split("\r\n").Length - 1);
    }

    [Fact]
    public async Task ApplyReplaceRangeFile_preserves_failure_for_missing_old_text()
    {
        string path = Path.Combine(_tempDir, "missing.cs");
        const string source = "int value = 1;";
        await File.WriteAllTextAsync(path, source);

        (bool success, bool changed, string? error, _) =
            await CodingAgentTools.TryApplyReplaceRangeFileAsync(path, "int value = 2;", "int value = 3;",
                CancellationToken.None);

        Assert.False(success);
        Assert.False(changed);
        Assert.Contains("not found", error, StringComparison.Ordinal);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    // ---- Command safety policy ----

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("npm test")]
    [InlineData("del myfile.txt")]
    public void DenyReasonFor_allows_ordinary_dev_commands(string command) =>
        Assert.Null(CodingAgentTools.DenyReasonFor(command));

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff -- src/Widget.cs")]
    public void DenyReasonFor_refuses_git_so_the_dedicated_git_actions_are_used(string command) =>
        Assert.Contains("Git commands cannot run", CodingAgentTools.DenyReasonFor(command) ?? string.Empty);

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf ~")]
    [InlineData("DEL /F /S /Q C:\\")]
    [InlineData("shutdown /s /t 0")]
    [InlineData("diskpart")]
    public void DenyReasonFor_blocks_catastrophic_commands(string command) =>
        Assert.NotNull(CodingAgentTools.DenyReasonFor(command));

    [Fact]
    public void DenyReasonFor_allows_a_null_or_empty_command()
    {
        Assert.Null(CodingAgentTools.DenyReasonFor(null));
        Assert.Null(CodingAgentTools.DenyReasonFor(""));
    }

    // ---- Grep ----

    [Fact]
    public void Grep_finds_a_plain_text_match_with_file_and_line_number()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "hello world\nfoo bar\nHELLO again");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "hello", useRegex: false, CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.RelativePath == "a.txt" && m.LineNumber == 1);
        Assert.Contains(matches, m => m.RelativePath == "a.txt" && m.LineNumber == 3);
    }

    [Fact]
    public void Grep_supports_regex_patterns()
    {
        File.WriteAllText(Path.Combine(_tempDir, "code.cs"), "public void Foo() {}\npublic void Bar() {}\nprivate void Baz() {}");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, @"public void \w+\(\)", useRegex: true, CancellationToken.None);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Grep_excludes_dotgit_and_build_output_directories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".git", "HEAD"), "needle");
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        File.WriteAllText(Path.Combine(_tempDir, "bin", "output.txt"), "needle");
        File.WriteAllText(Path.Combine(_tempDir, "real.txt"), "needle");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "needle", useRegex: false, CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal("real.txt", matches[0].RelativePath);
    }

    [Fact]
    public void Grep_respects_the_maxResults_bound()
    {
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_tempDir, "f" + i + ".txt"), "needle");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "needle", useRegex: false, CancellationToken.None, maxResults: 3);

        Assert.Equal(3, matches.Count);
    }

    [Fact]
    public void Grep_does_not_hang_or_throw_on_a_pathological_regex()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), new string('a', 40) + "!");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "(a+)+$", useRegex: true, CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public void Grep_invalid_regex_fails_soft_instead_of_aborting_the_agent()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "needle");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "([", useRegex: true, CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public void Grep_skips_oversized_files_instead_of_loading_them_into_memory()
    {
        string large = Path.Combine(_tempDir, "large.txt");
        using (var stream = new FileStream(large, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] prefix = System.Text.Encoding.UTF8.GetBytes("needle\n");
            stream.Write(prefix, 0, prefix.Length);
            stream.SetLength(CodingAgentTools.MaximumSearchableFileBytes + 1);
        }
        File.WriteAllText(Path.Combine(_tempDir, "small.txt"), "needle");

        List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(_tempDir, "needle", useRegex: false, CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal("small.txt", matches[0].RelativePath);
    }

    [Fact]
    public void Grep_and_glob_do_not_traverse_a_directory_symlink_outside_workspace_when_supported()
    {
        string outside = Path.Combine(Path.GetTempPath(), "parux-codingtools-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-needle");
        string link = Path.Combine(_tempDir, "linked-outside");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return; // Host cannot create a test symlink; production code still fail-closes on reparse attributes.
            }

            Assert.Empty(CodingAgentTools.Grep(_tempDir, "outside-needle", useRegex: false, CancellationToken.None));
            Assert.DoesNotContain("linked-outside/secret.txt", CodingAgentTools.Glob(_tempDir, "**/*.txt", CancellationToken.None));
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    // ---- Glob ----

    [Fact]
    public void Glob_matches_a_simple_extension_pattern()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "");

        List<string> files = CodingAgentTools.Glob(_tempDir, "*.cs", CancellationToken.None);

        Assert.Single(files);
        Assert.Equal("a.cs", files[0]);
    }

    [Fact]
    public void Glob_double_star_matches_any_depth()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "src", "nested"));
        File.WriteAllText(Path.Combine(_tempDir, "top.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "src", "mid.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "src", "nested", "deep.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "src", "nested", "deep.txt"), "");

        List<string> files = CodingAgentTools.Glob(_tempDir, "**/*.cs", CancellationToken.None);

        Assert.Equal(3, files.Count);
        Assert.Contains("top.cs", files);
        Assert.Contains("src/mid.cs", files);
        Assert.Contains("src/nested/deep.cs", files);
    }

    [Fact]
    public void Glob_supports_a_directory_prefix_before_double_star()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "src", "a"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "other"));
        File.WriteAllText(Path.Combine(_tempDir, "src", "a", "Foo.ts"), "");
        File.WriteAllText(Path.Combine(_tempDir, "other", "Bar.ts"), "");

        List<string> files = CodingAgentTools.Glob(_tempDir, "src/**/*.ts", CancellationToken.None);

        Assert.Single(files);
        Assert.Equal("src/a/Foo.ts", files[0]);
    }

    [Fact]
    public void Glob_returns_empty_for_a_blank_pattern() =>
        Assert.Empty(CodingAgentTools.Glob(_tempDir, "", CancellationToken.None));

    // ---- Real git subprocess tool ----

    private static async Task RunGitSetupAsync(string dir, params string[] args)
    {
        (int exit, string output) = await CodingAgentTools.RunGitAsync(dir, CancellationToken.None, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({exit}): {output}");
    }

    [Fact]
    public async Task RunGitAsync_reports_a_real_diff_against_a_real_repository()
    {
        await RunGitSetupAsync(_tempDir, "init", "-b", "main");
        await RunGitSetupAsync(_tempDir, "config", "user.email", "test@parux.local");
        await RunGitSetupAsync(_tempDir, "config", "user.name", "PaRuX Test");
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "original\n");
        await RunGitSetupAsync(_tempDir, "add", ".");
        await RunGitSetupAsync(_tempDir, "commit", "-m", "initial");

        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "modified\n");
        (int exit, string diff) = await CodingAgentTools.RunGitAsync(_tempDir, CancellationToken.None, "diff");

        Assert.Equal(0, exit);
        Assert.Contains("-original", diff);
        Assert.Contains("+modified", diff);
    }

    [Fact]
    public async Task RunGitAsync_returns_a_nonzero_exit_for_a_non_repository()
    {
        (int exit, string output) = await CodingAgentTools.RunGitAsync(_tempDir, CancellationToken.None, "rev-parse", "HEAD");
        Assert.NotEqual(0, exit);
    }
}
