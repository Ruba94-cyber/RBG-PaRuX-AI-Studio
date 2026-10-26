using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentArtifactStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-artifacts-" + Guid.NewGuid().ToString("N"));

    public CodingAgentArtifactStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Edited_artifact_text_is_refused_by_read_and_search_instead_of_served_as_evidence()
    {
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "command-output", "dotnet test", "build ok\nerror CS0103: name does not exist\n");
        File.WriteAllText(Path.Combine(_dir, saved.Id + ".txt"), "build ok\nno errors\n");

        Assert.Null(CodingAgentArtifactStore.ReadFromDirectory(_dir, saved.Id));
        Assert.Null(CodingAgentArtifactStore.SearchFromDirectory(_dir, saved.Id, "error"));
    }

    [Fact]
    public void Torn_artifact_text_is_refused_even_when_its_prefix_is_still_readable()
    {
        string text = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line-{i:D4}"));
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "git-diff", "git diff", text);
        File.WriteAllText(Path.Combine(_dir, saved.Id + ".txt"), text[..(text.Length / 2)]);

        Assert.Null(CodingAgentArtifactStore.ReadFromDirectory(_dir, saved.Id, 0, 100));
    }

    [Fact]
    public void Artifact_without_a_recorded_digest_is_refused_rather_than_trusted()
    {
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "kind", "source", "payload");
        string metaPath = Path.Combine(_dir, saved.Id + ".json");
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        CodingAgentArtifactStore.ArtifactMetadata edited = System.Text.Json.JsonSerializer.Deserialize<CodingAgentArtifactStore.ArtifactMetadata>(
            File.ReadAllText(metaPath), options)!;
        edited.Sha256 = string.Empty;
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(edited, options));

        Assert.Null(CodingAgentArtifactStore.ReadFromDirectory(_dir, saved.Id));
    }

    [Fact]
    public void Save_list_and_page_preserve_full_evidence()
    {
        string text = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line-{i:D4}-" + new string('x', 20)));
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "command-output", "dotnet test", text);

        Assert.Equal(text.Length, saved.Characters);
        Assert.Equal(500, saved.Lines);
        Assert.Equal(64, saved.Sha256.Length);

        CodingAgentArtifactStore.ReadResult? first = CodingAgentArtifactStore.ReadFromDirectory(_dir, saved.Id, 0, 300);
        CodingAgentArtifactStore.ReadResult? second = CodingAgentArtifactStore.ReadFromDirectory(_dir, saved.Id, 300, 300);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first!.HasMore);
        Assert.Equal(text[..300], first.Text);
        Assert.Equal(text.Substring(300, 300), second!.Text);
        Assert.Contains(CodingAgentArtifactStore.ListFromDirectory(_dir), item => item.Id == saved.Id);
    }

    [Fact]
    public void Search_returns_line_numbers_without_loading_evidence_into_prompt()
    {
        string text = "start\nerror CS1001 alpha\nok\nERROR CS2002 beta\nend";
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "build-log", "dotnet build", text);

        CodingAgentArtifactStore.SearchResult? result = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "error cs", regex: false, maxMatches: 10);

        Assert.NotNull(result);
        Assert.Collection(result!.Matches,
            first => Assert.Equal(2, first.LineNumber),
            second => Assert.Equal(4, second.LineNumber));
    }

    [Fact]
    public void Regex_search_is_bounded_and_invalid_regex_fails_soft()
    {
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "log", "test", string.Join('\n', Enumerable.Range(0, 200).Select(i => "ERR" + i)));

        CodingAgentArtifactStore.SearchResult? bounded = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "^ERR", regex: true, maxMatches: 7);
        CodingAgentArtifactStore.SearchResult? invalid = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "[", regex: true, maxMatches: 7);

        Assert.NotNull(bounded);
        Assert.Equal(7, bounded!.Matches.Count);
        Assert.NotNull(invalid);
        Assert.Empty(invalid!.Matches);
    }

    [Fact]
    public void Artifact_ids_and_session_paths_are_path_safe()
    {
        string safe = CodingAgentArtifactStore.SanitizeSessionId("../../task:abc\\danger");
        Assert.DoesNotContain("/", safe);
        Assert.DoesNotContain("\\", safe);
        Assert.DoesNotContain(":", safe);
        Assert.Null(CodingAgentArtifactStore.ReadFromDirectory(_dir, "../escape", 0, 100));
    }

    [Fact]
    public void Storage_truncation_is_explicit_not_silent()
    {
        string text = new string('z', CodingAgentArtifactStore.MaxArtifactCharacters + 123);
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "huge", "fixture", text);

        Assert.True(saved.WasTruncatedAtStorage);
        Assert.Equal(CodingAgentArtifactStore.MaxArtifactCharacters, saved.Characters);
    }

    [Fact]
    public void Literal_OR_fallback_returns_each_matching_line_once_when_no_exact_match()
    {
        // No line contains the complete literal query "foo|bar|baz".
        string text = string.Join('\n', new[]
        {
            "alpha foo delta",
            "gamma beta zeta",
            "epsilon foo bar theta",
            "no match here",
            "kappa baz lambda"
        });
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "or-fallback", "test", text);

        // With no complete literal occurrence, the query falls back to OR terms.
        CodingAgentArtifactStore.SearchResult? result = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "foo|bar|baz", regex: false, maxMatches: 10);

        Assert.NotNull(result);
        // Lines 1, 3, and 5 match. Line 3 contains two terms but appears once.
        Assert.Equal(3, result!.Matches.Count);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(3, result.Matches[1].LineNumber);
        Assert.Equal(5, result.Matches[2].LineNumber);
    }

    [Fact]
    public void Exact_literal_match_anywhere_in_artifact_suppresses_OR_fallback()
    {
        // One line contains the complete query; the other lines match individual OR terms.
        string text = string.Join('\n', new[]
        {
            "alpha foo delta",   // matches the "foo" term
            "FOO|BAR",           // complete case-insensitive literal query
            "gamma bar zeta"     // matches the "bar" term
        });
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "or-suppress", "test", text);

        CodingAgentArtifactStore.SearchResult? result = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "foo|bar", regex: false, maxMatches: 10);

        Assert.NotNull(result);
        // The complete literal on line 2 suppresses OR fallback; only line 2 is returned.
        Assert.Equal(1, result!.Matches.Count);
        Assert.Equal(2, result.Matches[0].LineNumber);
    }

    [Fact]
    public void Literal_OR_fallback_ignores_empty_terms_and_keeps_a_single_term()
    {
        CodingAgentArtifactStore.ArtifactMetadata saved = CodingAgentArtifactStore.SaveTextToDirectory(
            _dir, "or-empty-terms", "test", "alpha foo delta\nno match here");

        CodingAgentArtifactStore.SearchResult? result = CodingAgentArtifactStore.SearchFromDirectory(
            _dir, saved.Id, "foo||", regex: false, maxMatches: 10);

        Assert.NotNull(result);
        Assert.Single(result!.Matches);
        Assert.Equal(1, result.Matches[0].LineNumber);
    }
}
