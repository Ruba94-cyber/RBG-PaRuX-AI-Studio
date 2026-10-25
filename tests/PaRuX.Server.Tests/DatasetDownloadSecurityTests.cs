using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DatasetDownloadSecurityTests
{
    [Theory]
    [InlineData("org/dataset", "data/train.jsonl")]
    [InlineData("dataset", "train.parquet")]
    [InlineData("some.org/data-set_1", "nested/folder/file.csv")]
    public void Valid_hugging_face_identity_builds_canonical_https_url(string repo, string file)
    {
        Uri uri = DatasetDownloadService.BuildDatasetDownloadUri(repo, file);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("huggingface.co", uri.Host);
        Assert.True(DatasetDownloadService.MatchesAuthoritativeDownloadUri(uri.AbsoluteUri, uri));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolute")]
    [InlineData("folder/../../secret")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("folder//file.jsonl")]
    public void Dataset_file_path_traversal_is_rejected(string file)
    {
        Assert.False(DatasetDownloadService.TryNormalizeRelativeFileName(file, out _));
    }

    [Theory]
    [InlineData("../repo")]
    [InlineData("org/../repo")]
    [InlineData("org/repo/extra")]
    [InlineData("https://evil.test/repo")]
    [InlineData("org\\repo")]
    public void Malformed_repo_ids_are_rejected(string repo)
    {
        Assert.False(DatasetDownloadService.TryNormalizeRepoId(repo, out _));
    }

    [Theory]
    [InlineData("https://evil.example/datasets/org/repo/resolve/main/train.jsonl")]
    [InlineData("http://huggingface.co/datasets/org/repo/resolve/main/train.jsonl")]
    [InlineData("https://huggingface.co/datasets/other/repo/resolve/main/train.jsonl")]
    [InlineData("https://huggingface.co/datasets/org/repo/resolve/main/other.jsonl")]
    [InlineData("https://huggingface.co/datasets/org/repo/resolve/main/train.jsonl?token=attacker")]
    [InlineData("https://user@huggingface.co/datasets/org/repo/resolve/main/train.jsonl")]
    public void Arbitrary_or_mismatched_download_urls_are_rejected(string candidate)
    {
        Uri expected = DatasetDownloadService.BuildDatasetDownloadUri("org/repo", "train.jsonl");
        Assert.False(DatasetDownloadService.MatchesAuthoritativeDownloadUri(candidate, expected));
    }

    [Fact]
    public void Safe_nested_file_resolves_below_repo_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-dataset-security", Guid.NewGuid().ToString("N"));
        string resolved = DatasetDownloadService.ResolveSafeDatasetPath(root, "nested/train.jsonl");
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(canonicalRoot, resolved, StringComparison.OrdinalIgnoreCase);
    }
}
