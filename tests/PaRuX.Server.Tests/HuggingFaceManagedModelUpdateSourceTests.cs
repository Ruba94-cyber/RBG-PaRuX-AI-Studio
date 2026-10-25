using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class HuggingFaceManagedModelUpdateSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-model-update-" + Guid.NewGuid().ToString("N"));

    public HuggingFaceManagedModelUpdateSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Matching_lfs_sha_is_current_even_when_repo_revision_is_new()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("same model bytes");
        ManagedModelInfo model = WriteModel("model-Q4_K_M.gguf", bytes);
        string sha = Sha(bytes);
        var http = Client(Json("rev-new", model.FileName, sha));
        var revisions = new ManagedModelRevisionStore(Path.Combine(_root, "revisions.json"));
        var source = new HuggingFaceManagedModelUpdateSource(model, revisions, http);

        ManagedAiUpdateCheckResult result = await source.CheckAsync(CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        ManagedModelRevisionRecord? record = revisions.Find(model.FileName, model.RepoId!);
        Assert.NotNull(record);
        Assert.Equal("rev-new", record!.RepoRevision);
        Assert.Equal(sha, record.LocalSha256);
    }

    [Fact]
    public async Task Different_lfs_sha_creates_real_model_update_notification_evidence()
    {
        byte[] local = Encoding.UTF8.GetBytes("old model bytes");
        byte[] remote = Encoding.UTF8.GetBytes("new model bytes");
        ManagedModelInfo model = WriteModel("model-Q4_K_M.gguf", local);
        string upstreamSha = Sha(remote);
        var source = new HuggingFaceManagedModelUpdateSource(
            model,
            new ManagedModelRevisionStore(Path.Combine(_root, "revisions.json")),
            Client(Json("rev-b", model.FileName, upstreamSha)));

        ManagedAiUpdateCheckResult result = await source.CheckAsync(CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("rev-b", result.AvailableVersion);
        Assert.Equal(upstreamSha, result.ExpectedSha256);
        Assert.Contains("/resolve/rev-b/", result.SourceUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_same_basename_never_guesses_an_update()
    {
        byte[] local = Encoding.UTF8.GetBytes("model");
        ManagedModelInfo model = WriteModel("model.gguf", local);
        string sha = Sha(local);
        string json = "{\"sha\":\"rev-b\",\"siblings\":["
            + "{\"rfilename\":\"a/model.gguf\",\"lfs\":{\"sha256\":\"" + sha + "\"}},"
            + "{\"rfilename\":\"b/model.gguf\",\"lfs\":{\"sha256\":\"" + sha + "\"}}]}";
        var source = new HuggingFaceManagedModelUpdateSource(
            model,
            new ManagedModelRevisionStore(Path.Combine(_root, "revisions.json")),
            Client(json));

        ManagedAiUpdateCheckResult result = await source.CheckAsync(CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Contains("Multiple", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsafe_repo_id_is_rejected_before_network_use()
    {
        ManagedModelInfo model = WriteModel("model.gguf", Encoding.UTF8.GetBytes("x"));
        model.RepoId = "owner/../repo";

        Assert.Throws<InvalidDataException>(() => new HuggingFaceManagedModelUpdateSource(model));
    }

    private ManagedModelInfo WriteModel(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return new ManagedModelInfo
        {
            FileName = name,
            FilePath = path,
            SizeBytes = bytes.Length,
            RepoId = "owner/repo",
            Quantization = "Q4_K_M"
        };
    }

    private static HttpClient Client(string json) => new(new FakeHandler(json)) { Timeout = TimeSpan.FromSeconds(5) };

    private static string Json(string revision, string fileName, string sha) =>
        "{\"sha\":\"" + revision + "\",\"siblings\":[{\"rfilename\":\"" + fileName
        + "\",\"size\":123,\"lfs\":{\"sha256\":\"" + sha + "\",\"size\":123}}]}";

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _json;
        public FakeHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("huggingface.co", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
