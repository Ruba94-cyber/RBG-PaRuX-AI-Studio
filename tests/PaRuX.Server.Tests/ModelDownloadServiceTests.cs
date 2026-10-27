using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the resume-decision logic and checksum verification added to ModelDownloadService,
/// against a mocked HttpMessageHandler - no real network calls. The Range/206 behavior and the
/// "X-Linked-ETag" header this exercises were both verified live against a real Hugging Face
/// resolve URL before writing this service (see ModelDownloadService.ExtractSha256FromHeaders'
/// doc comment for the exact response that confirmed it).
///
/// ModelsDirectory itself is not injectable (it's a fixed %LOCALAPPDATA%\PaRuX\models\ path
/// shared with the real app), so these tests write real files there under unique GUID-based
/// names and delete everything they create in Dispose(), per the "no orphaned partial files"
/// requirement.
/// </summary>
public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly List<string> _createdPaths = new();
    private readonly List<string> _createdExternalPaths = new();

    private static string ModelsDir => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "models");

    public void Dispose()
    {
        foreach (string path in _createdPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        foreach (string path in _createdExternalPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        // A successful hermetic download test (RequestFor always sets RepoId="test/repo") now
        // writes a real entry into the shared .repo-ids.json sidecar for its own GUID-named file -
        // clean those up too, the same "no orphaned sidecar entries from a test run" discipline
        // DeleteManagedModel_also_clears_the_files_alias_so_it_never_orphans already established
        // for .aliases.json, just applied here directly since these tests bypass DeleteManagedModel.
        try
        {
            var testFileNames = _createdPaths.Select(p => Path.GetFileName(p.EndsWith(".partial") ? p[..^".partial".Length] : p)).ToHashSet();
            string repoIdsPath = Path.Combine(ModelsDir, ".repo-ids.json");
            if (File.Exists(repoIdsPath))
            {
                var repoIds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(repoIdsPath)) ?? new();
                bool changed = false;
                foreach (string name in testFileNames) changed |= repoIds.Remove(name);
                if (changed) File.WriteAllText(repoIdsPath, System.Text.Json.JsonSerializer.Serialize(repoIds));
            }
            foreach (string name in testFileNames) ModelDownloadProvenanceManifestStore.Remove(ModelsDir, name);
        }
        catch { /* best-effort cleanup */ }
    }

    private string NewTrackedFileName()
    {
        Directory.CreateDirectory(ModelsDir);
        string name = "parux-test-" + Guid.NewGuid().ToString("N") + ".gguf";
        _createdPaths.Add(Path.Combine(ModelsDir, name));
        _createdPaths.Add(Path.Combine(ModelsDir, name) + ".partial");
        _createdPaths.Add(Path.Combine(ModelsDir, name) + ".partial.source.json");
        _createdPaths.Add(Path.Combine(ModelsDir, name) + ".partial.source.json.tmp");
        return name;
    }

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status != null && status.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Download did not reach a terminal state within " + timeout);
    }

    private static ModelDownloadRequest RequestFor(string fileName) => new()
    {
        RepoId = "test/repo",
        FileName = fileName,
        DownloadUrl = "https://huggingface.co/test/repo/resolve/main/" + fileName
    };

    private static ModelDiscoveryCandidate CandidateFor(string fileName, DateTime observedUtc, long declaredSize)
    {
        const string repoId = "test/candidate-repo";
        string revision = new('a', 40);
        string path = "nested/" + fileName;
        return new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, path),
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = path,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, path),
            DeclaredLicense = "MIT",
            DeclaredSizeBytes = declaredSize,
            FirstSeenUtc = observedUtc,
            LastSeenUtc = observedUtc
        };
    }

    private static ModelDownloadLicenseEvidence LicenseEvidenceFor(ModelDiscoveryCandidate candidate, DateTime observedUtc) => new()
    {
        MetadataStatus = ModelLicenseReviewRules.GetMetadataStatus(candidate.DeclaredLicense, metadataRetrieved: true),
        PublisherDeclaredLicense = candidate.DeclaredLicense,
        MetadataObservedUtc = observedUtc,
        ReviewRequired = ModelLicenseReviewRules.RequiresReview(candidate.DeclaredLicense, metadataRetrieved: true)
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend(request, cancellationToken);
    }

    private sealed class RecordingCompletionHandler : IModelDownloadCompletionHandler
    {
        public List<string> Paths { get; } = new();
        public void OnGgufDownloaded(string modelPath, string label) => Paths.Add(modelPath);
    }

    /// <summary>A GET response body that trickles out in chunks with a delay in between, so a
    /// test can reliably observe a partial download in progress and cancel it mid-stream.</summary>
    private sealed class ThrottledStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delayBetweenChunks;
        private int _position;

        public ThrottledStream(byte[] data, int chunkSize, TimeSpan delayBetweenChunks)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delayBetweenChunks = delayBetweenChunks;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position > 0) await Task.Delay(_delayBetweenChunks, cancellationToken);
            int remaining = _data.Length - _position;
            if (remaining <= 0) return 0;
            int toCopy = Math.Min(Math.Min(_chunkSize, remaining), buffer.Length);
            _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Fresh_download_computes_and_verifies_checksum_against_X_Linked_ETag_header()
    {
        byte[] content = new byte[5000];
        new Random(42).NextBytes(content);
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string fileName = NewTrackedFileName();

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    head.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + sha256 + "\"");
                    return Task.FromResult(head);
                }
                Assert.Null(req.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadRequest request = RequestFor(fileName);
        request.ExpectedSha256 = sha256.ToUpperInvariant();
        ModelDownloadStatus created = await service.CreateDownloadAsync(request, CancellationToken.None);
        Assert.Equal(sha256, created.Sha256);
        Assert.Null(created.VerificationStatus);

        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", final.Status);
        Assert.Equal("verified", final.VerificationStatus);
        Assert.Equal(content.Length, final.BytesDownloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(final.LocalPath!));
    }

    [Fact]
    public async Task Fresh_download_rejects_a_repository_digest_that_differs_from_catalogue_pin()
    {
        byte[] content = new byte[128];
        string repositorySha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string fileName = NewTrackedFileName();
        int getCount = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    head.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + repositorySha + "\"");
                    return Task.FromResult(head);
                }
                getCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        ModelDownloadRequest request = RequestFor(fileName);
        request.ExpectedSha256 = new string('f', 64);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CreateDownloadAsync(request, CancellationToken.None));

        Assert.Contains("pinned SHA-256", error.Message);
        Assert.Equal(0, getCount);
    }

    [Fact]
    public async Task Verified_gguf_download_triggers_exactly_one_automatic_benchmark()
    {
        byte[] content = new byte[1024];
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string fileName = NewTrackedFileName();
        var completion = new RecordingCompletionHandler();
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    head.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + sha256 + "\"");
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler), completion);

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", completed.Status);
        Assert.Single(completion.Paths);
        Assert.Equal(Path.GetFullPath(completed.LocalPath!), Path.GetFullPath(completion.Paths[0]));
    }

    [Fact]
    public async Task Pinned_candidate_download_uses_core_size_and_does_not_trigger_automatic_benchmark()
    {
        byte[] content = new byte[4096];
        new Random(23).NextBytes(content);
        DateTime observedUtc = DateTime.UtcNow;
        string fileName = NewTrackedFileName();
        ModelDiscoveryCandidate candidate = CandidateFor(fileName, observedUtc, declaredSize: 1);
        ModelDownloadLicenseEvidence evidence = LicenseEvidenceFor(candidate, observedUtc);
        var completion = new RecordingCompletionHandler();
        int getCount = 0;
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    head.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() + "\"");
                    return Task.FromResult(head);
                }
                Interlocked.Increment(ref getCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler), completion);

        Task<ModelDownloadStatus> firstStart = service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        Task<ModelDownloadStatus> retryStart = service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        ModelDownloadStatus[] starts = await Task.WhenAll(firstStart, retryStart);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, starts[0].Id, TestTimeouts.JobCompletion);

        Assert.Equal(starts[0].Id, starts[1].Id);
        Assert.Equal(content.Length, starts[0].TotalBytes);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.LocalPath!));
        Assert.Empty(completion.Paths);
        Assert.Equal(1, getCount);
        Assert.Single(service.List(), job => job.FileName == fileName);

        await File.WriteAllBytesAsync(completed.LocalPath!, Enumerable.Repeat((byte)0x5a, content.Length).ToArray());
        ModelDownloadStatus repairedStart = await service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        ModelDownloadStatus repaired = await WaitForTerminalAsync(service, repairedStart.Id, TestTimeouts.JobCompletion);
        Assert.NotEqual(completed.Id, repaired.Id);
        Assert.Equal("completed", repaired.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(repaired.LocalPath!));
        Assert.Equal(2, getCount);
    }

    [Fact]
    public async Task Pinned_candidate_retry_does_not_reuse_completion_with_missing_license_evidence()
    {
        byte[] content = new byte[2048];
        new Random(29).NextBytes(content);
        DateTime observedUtc = DateTime.UtcNow;
        string fileName = NewTrackedFileName();
        ModelDiscoveryCandidate candidate = CandidateFor(fileName, observedUtc, declaredSize: content.Length);
        ModelDownloadLicenseEvidence evidence = LicenseEvidenceFor(candidate, observedUtc);
        int getCount = 0;
        var handler = new FakeHandler
        {
            OnSend = (request, _) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                Interlocked.Increment(ref getCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus first = await service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, first.Id, TestTimeouts.JobCompletion);
        Assert.Equal("completed", completed.Status);

        ModelDownloadProvenance provenance = ModelDownloadProvenanceManifestStore.Get(ModelsDir, fileName)!;
        provenance.LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided;
        provenance.PublisherDeclaredLicense = null;
        provenance.MetadataObservedUtc = null;
        provenance.ReviewRequired = false;
        provenance.ReviewAcknowledged = false;
        provenance.ReviewAcknowledgedUtc = null;
        ModelDownloadProvenanceManifestStore.Upsert(ModelsDir, fileName, provenance);

        ModelDownloadStatus retry = await service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        ModelDownloadStatus repaired = await WaitForTerminalAsync(service, retry.Id, TestTimeouts.JobCompletion);

        Assert.NotEqual(first.Id, retry.Id);
        Assert.Equal("completed", repaired.Status);
        Assert.Equal(2, getCount);
        Assert.Equal(ModelLicenseMetadataStatus.Declared,
            ModelDownloadProvenanceManifestStore.Get(ModelsDir, fileName)!.LicenseMetadataStatus);
    }

    [Fact]
    public async Task Candidate_resume_keeps_exact_size_for_quota_admission_when_head_is_unavailable()
    {
        byte[] content = new byte[1024];
        new Random(31).NextBytes(content);
        DateTime observedUtc = DateTime.UtcNow;
        string fileName = NewTrackedFileName();
        ModelDiscoveryCandidate candidate = CandidateFor(fileName, observedUtc, declaredSize: 3);
        ModelDownloadLicenseEvidence evidence = LicenseEvidenceFor(candidate, observedUtc);
        int headCount = 0;
        var completion = new RecordingCompletionHandler();
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    if (Interlocked.Increment(ref headCount) > 1)
                        throw new HttpRequestException("simulated HEAD outage during resume");
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }

                if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is long rangeStart)
                {
                    byte[] remaining = content[(int)rangeStart..];
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(remaining) };
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeStart, content.Length - 1, content.Length);
                    return Task.FromResult(response);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrottledStream(content, 32, TimeSpan.FromMilliseconds(30)))
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler), completion);

        ModelDownloadStatus started = await service.CreateCandidateDownloadAsync(
            candidate, evidence, observedUtc, CancellationToken.None);
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(started.Id)?.BytesDownloaded ?? 0) <= 0)
            await Task.Delay(10);
        Assert.True(service.Cancel(started.Id));
        Assert.Equal("cancelled", (await WaitForTerminalAsync(service, started.Id, TestTimeouts.JobCompletion)).Status);

        ModelDownloadStatus resumed = await service.CreateDownloadAsync(
            new ModelDownloadRequest { ResumeFromDownloadId = started.Id }, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, resumed.Id, TestTimeouts.JobCompletion);

        Assert.Equal(2, headCount);
        Assert.Equal(content.Length, resumed.TotalBytes);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.LocalPath!));
        Assert.Empty(completion.Paths);
    }

    [Fact]
    public async Task Download_stream_is_capped_even_when_head_size_is_understated_and_get_has_no_length()
    {
        string fileName = NewTrackedFileName();
        byte[] content = new byte[40];
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = 1;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrottledStream(content, 4, TimeSpan.Zero))
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler),
            maximumSingleDownloadBytes: 16);

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("failed", completed.Status);
        Assert.Contains("declared size", completed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(ModelsDir, fileName)));
        Assert.False(File.Exists(Path.Combine(ModelsDir, fileName) + ".partial"));
    }

    [Fact]
    public async Task Resume_sends_Range_from_bytes_already_on_disk_and_appends_when_server_answers_206()
    {
        byte[] content = new byte[8000];
        new Random(7).NextBytes(content);
        const int already = 3000;
        string fileName = NewTrackedFileName();
        string partialPath = Path.Combine(ModelsDir, fileName) + ".partial";
        await File.WriteAllBytesAsync(partialPath, content[..already]);

        long? observedRangeFrom = null;
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                observedRangeFrom = req.Headers.Range?.Ranges.FirstOrDefault()?.From;
                byte[] remaining = content[already..];
                var get = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(remaining) };
                get.Content.Headers.ContentRange = new ContentRangeHeaderValue(already, content.Length - 1, content.Length);
                return Task.FromResult(get);
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal((long)already, observedRangeFrom);
        Assert.Equal("completed", final.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(final.LocalPath!));
    }

    [Fact]
    public async Task Falls_back_to_a_full_restart_when_server_ignores_Range_and_answers_200()
    {
        byte[] content = new byte[4000];
        new Random(11).NextBytes(content);
        string fileName = NewTrackedFileName();
        string partialPath = Path.Combine(ModelsDir, fileName) + ".partial";
        await File.WriteAllBytesAsync(partialPath, new byte[1000]);

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", final.Status);
        Assert.Equal(content.Length, final.BytesDownloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(final.LocalPath!));
    }

    [Fact]
    public async Task Hash_mismatch_fails_and_never_promotes_corrupt_bytes_into_the_library()
    {
        byte[] content = new byte[2000];
        new Random(3).NextBytes(content);
        string wrongHash = Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 })).ToLowerInvariant();
        string fileName = NewTrackedFileName();
        string finalPath = Path.Combine(ModelsDir, fileName);
        string partialPath = finalPath + ".partial";

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    head.Headers.TryAddWithoutValidation("X-Linked-ETag", "\"" + wrongHash + "\"");
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var completion = new RecordingCompletionHandler();
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler), completion);

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("failed", final.Status);
        Assert.Equal("failed", final.VerificationStatus);
        Assert.Null(final.LocalPath);
        Assert.Contains("SHA-256 verification failed", final.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(partialPath));
        Assert.Empty(completion.Paths);
        Assert.DoesNotContain(service.ListManagedModels(), model => model.FileName == fileName);
    }

    [Fact]
    public async Task Non_gguf_checkpoint_download_never_triggers_a_text_model_benchmark()
    {
        // A finished media download is placed in ComfyUI when it exists; scope the root so a real install is never touched.
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(Path.Combine(Path.GetTempPath(), "parux-dl-root-" + Guid.NewGuid().ToString("N")));
        byte[] content = ZImageSafetensors();
        Directory.CreateDirectory(ModelsDir);
        string fileName = "parux-test-" + Guid.NewGuid().ToString("N") + ".safetensors";
        _createdPaths.Add(Path.Combine(ModelsDir, fileName));
        _createdPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial");
        _createdPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial.source.json");
        _createdPaths.Add(Path.Combine(ModelsDir, fileName) + ".partial.source.json.tmp");
        var completion = new RecordingCompletionHandler();
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler), completion);

        // A safetensors download must name its media workflow; the stub bytes are a Z-Image header, so it completes.
        ModelDownloadRequest downloadRequest = RequestFor(fileName);
        downloadRequest.MediaCheckpointType = "image";
        ModelDownloadStatus created = await service.CreateDownloadAsync(downloadRequest, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", completed.Status);
        Assert.Empty(completion.Paths);
    }

    private static byte[] ZImageSafetensors()
    {
        string[] names = { "noise_refiner.0.w", "context_refiner.0.w", "cap_embedder.0.w", "x_embedder.w" };
        var entries = new List<string>();
        for (int i = 0; i < names.Length; i++)
            entries.Add(System.Text.Json.JsonSerializer.Serialize(names[i]) + ":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[" + i + "," + (i + 1) + "]}");
        byte[] header = System.Text.Encoding.UTF8.GetBytes("{" + string.Join(",", entries) + "}");
        byte[] content = new byte[8 + header.Length + names.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(content, 8);
        return content;
    }

    [Fact]
    public async Task No_hash_header_degrades_to_unavailable_verification_without_failing()
    {
        byte[] content = new byte[500];
        new Random(1).NextBytes(content);
        string fileName = NewTrackedFileName();

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        Assert.Null(created.Sha256);

        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("completed", final.Status);
        Assert.Equal("unavailable", final.VerificationStatus);
    }

    [Fact]
    public async Task Cancel_leaves_the_partial_file_in_place_instead_of_deleting_it()
    {
        byte[] content = new byte[3_000_000];
        new Random(5).NextBytes(content);
        string fileName = NewTrackedFileName();
        var stream = new ThrottledStream(content, 200_000, TimeSpan.FromMilliseconds(150));

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);

        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(created.Id)?.BytesDownloaded ?? 0) == 0)
            await Task.Delay(20);
        Assert.True((service.Get(created.Id)?.BytesDownloaded ?? 0) > 0);

        Assert.True(service.Cancel(created.Id));
        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);

        Assert.Equal("cancelled", final.Status);
        string partialPath = Path.Combine(ModelsDir, fileName) + ".partial";
        Assert.True(File.Exists(partialPath));
        long partialLength = new FileInfo(partialPath).Length;
        Assert.True(partialLength > 0);
        Assert.True(partialLength < content.Length);
    }

    // Physical-PC regression, 2026-09-09: Pause/Resume alone left no way to actually discard a
    // paused or failed download - a real gap confirmed live behind a "can't even cancel a
    // download" report. A paused job (and its .partial bytes) would otherwise sit there forever
    // with only Resume offered. Remove() deletes both the job entry and the partial file.
    [Fact]
    public async Task Remove_deletes_the_job_and_the_partial_file_for_a_paused_download()
    {
        byte[] content = new byte[3_000_000];
        new Random(6).NextBytes(content);
        string fileName = NewTrackedFileName();
        var stream = new ThrottledStream(content, 200_000, TimeSpan.FromMilliseconds(150));

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);

        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(created.Id)?.BytesDownloaded ?? 0) == 0)
            await Task.Delay(20);

        Assert.True(service.Cancel(created.Id));
        await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);
        string partialPath = Path.Combine(ModelsDir, fileName) + ".partial";
        Assert.True(File.Exists(partialPath));

        Assert.True(service.Remove(created.Id));

        Assert.Null(service.Get(created.Id));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Remove_refuses_a_download_that_is_still_in_flight()
    {
        byte[] content = new byte[3_000_000];
        new Random(7).NextBytes(content);
        string fileName = NewTrackedFileName();
        var stream = new ThrottledStream(content, 200_000, TimeSpan.FromMilliseconds(150));

        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(created.Id)?.BytesDownloaded ?? 0) == 0)
            await Task.Delay(20);

        Assert.False(service.Remove(created.Id));
        Assert.NotNull(service.Get(created.Id));

        service.Cancel(created.Id);
        await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);
    }

    [Fact]
    public async Task Remove_gives_up_on_a_destination_held_by_another_transfer_instead_of_waiting_for_it()
    {
        // Downloads to one file share its destination slot. The second transfer below holds that slot for far longer than
        // a removal should be willing to wait, so the removal of the first must return rather than queue behind it.
        string fileName = NewTrackedFileName();
        byte[] content = new byte[40_000_000];
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrottledStream(content, 200_000, TimeSpan.FromMilliseconds(150)))
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus first = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(first.Id)?.BytesDownloaded ?? 0) == 0)
            await Task.Delay(20);
        Assert.True(service.Cancel(first.Id));
        await WaitForTerminalAsync(service, first.Id, TestTimeouts.JobCompletion);

        ModelDownloadStatus second = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && (service.Get(second.Id)?.BytesDownloaded ?? 0) == 0)
            await Task.Delay(20);

        Task<bool> removal = Task.Run(() => service.Remove(first.Id));
        Task finished = await Task.WhenAny(removal, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(removal, finished);
        Assert.False(await removal);
        Assert.NotNull(service.Get(first.Id));

        Assert.True(service.Cancel(second.Id));
        await WaitForTerminalAsync(service, second.Id, TestTimeouts.JobCompletion);
    }

    [Fact]
    public void Remove_returns_false_for_an_unknown_id()
    {
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));
        Assert.False(service.Remove(Guid.NewGuid().ToString("N")));
    }

    // Physical-PC regression, 2026-09-09: confirmed live against the real Hugging Face API that
    // /api/models/{repoId} without blobs=true returns siblings with only rfilename - no size, no
    // lfs, nothing to estimate hardware fit from - which is exactly why every GGUF variant in the
    // Compare-variants dialog showed "0 B". This pins both halves of the fix: the outgoing request
    // actually asks for blobs=true, and a size that comes back on Size (not just the nested Lfs.Size
    // fallback) is genuinely returned to the caller.
    [Fact]
    public async Task GetDetailsAsync_requests_blobs_true_and_returns_real_sibling_sizes()
    {
        string revision = new string('c', 40);
        string? capturedUrl = null;
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/raw/" + revision + "/README.md", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("README at pinned commit")
                    });

                capturedUrl = req.RequestUri!.ToString();
                string json = "{\"id\":\"test/repo\",\"sha\":\"" + revision + "\",\"siblings\":["
                    + "{\"rfilename\":\"model-Q4_K_M.gguf\",\"size\":2469396480}"
                    + "]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelRepoDetails details = await service.GetDetailsAsync("test/repo", CancellationToken.None);

        Assert.NotNull(capturedUrl);
        Assert.Contains("blobs=true", capturedUrl);
        Assert.Equal(revision, details.Revision);
        Assert.Equal("README at pinned commit", details.Readme);
        ModelFileDetails file = Assert.Single(details.Files);
        Assert.Equal("model-Q4_K_M.gguf", file.FileName);
        Assert.Equal(2469396480L, file.SizeBytes);
    }

    [Fact]
    public async Task GetDetailsAsync_does_not_invent_a_revision_when_huggingface_metadata_is_invalid()
    {
        var requestedReadmes = new List<string>();
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                requestedReadmes.Add(req.RequestUri!.AbsolutePath);
                string body = req.RequestUri.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
                    ? "{\"id\":\"test/repo\",\"sha\":\"main\",\"siblings\":[]}"
                    : "readme";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelRepoDetails details = await service.GetDetailsAsync("test/repo", CancellationToken.None);

        Assert.Null(details.Revision);
        Assert.Contains("/test/repo/raw/main/README.md", requestedReadmes, StringComparer.Ordinal);
    }

    // Regression: Hugging Face answers a renamed repository (Comfy-Org/Qwen-Image_ComfyUI ->
    // Comfy-Org/Qwen-Image, Comfy-Org/z_image_turbo -> Comfy-Org/Z-Image-Turbo) with 307 and a
    // relative Location. The service disables automatic redirects, so the details endpoint threw
    // and answered 500 for every media safetensors repo while GGUF repos worked.
    [Fact]
    public async Task GetDetailsAsync_follows_a_renamed_repository_redirect_and_reports_the_canonical_repo()
    {
        string revision = new string('d', 40);
        var requested = new List<string>();
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                string path = req.RequestUri!.AbsolutePath;
                requested.Add(path);
                if (path == "/api/models/Comfy-Org/Qwen-Image_ComfyUI")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    redirect.Headers.Location = new Uri("/api/models/Comfy-Org/Qwen-Image?blobs=true", UriKind.Relative);
                    return Task.FromResult(redirect);
                }
                string body = path == "/api/models/Comfy-Org/Qwen-Image"
                    ? "{\"id\":\"Comfy-Org/Qwen-Image\",\"sha\":\"" + revision + "\",\"cardData\":{\"license\":\"apache-2.0\"},"
                        + "\"siblings\":[{\"rfilename\":\"split_files/diffusion_models/qwen_image_fp8_e4m3fn.safetensors\",\"size\":20430635136}]}"
                    : "readme";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelRepoDetails details = await service.GetDetailsAsync("Comfy-Org/Qwen-Image_ComfyUI", CancellationToken.None);

        Assert.Equal("Comfy-Org/Qwen-Image", details.RepoId);
        Assert.Equal(revision, details.Revision);
        Assert.Equal("apache-2.0", details.License, ignoreCase: true);
        Assert.Contains("/Comfy-Org/Qwen-Image/raw/" + revision + "/README.md", requested);
        Assert.Empty(details.Files); // safetensors-only repository: no GGUF rows, but also no failure
    }

    [Fact]
    public async Task GetDetailsAsync_refuses_a_redirect_to_another_host_and_loops()
    {
        int calls = 0;
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                calls++;
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = calls == 1
                    ? new Uri("https://evil.example/api/models/x/y")
                    : new Uri("/api/models/test/repo", UriKind.Relative);
                return Task.FromResult(redirect);
            }
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetDetailsAsync("test/repo", CancellationToken.None));

        calls = 1; // relative-only loop must stop after the hop limit
        var loop = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("/api/models/test/repo", UriKind.Relative);
                return Task.FromResult(redirect);
            }
        };
        var looping = new ModelDownloadService(new HttpClient(loop), new HttpClient(loop));
        await Assert.ThrowsAsync<HttpRequestException>(() => looping.GetDetailsAsync("test/repo", CancellationToken.None));
    }

    private sealed class GatedStream : Stream
    {
        private readonly byte[] _data;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public GatedStream(byte[] data) => _data = data;
        public void Release() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == 0) await _gate.Task.WaitAsync(cancellationToken);
            int remaining = _data.Length - _position;
            if (remaining <= 0) return 0;
            int toCopy = Math.Min(remaining, buffer.Length);
            _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private (FakeHandler Handler, GatedStream[] Gates, string[] FileNames) BuildThreeGatedDownloadsHandler()
    {
        var gates = new GatedStream[3];
        for (int i = 0; i < gates.Length; i++) gates[i] = new GatedStream(new byte[16]);
        int nextGate = 0;
        var gateLock = new object();
        var handler = new FakeHandler
        {
            OnSend = (req, ct) =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = 16;
                    return Task.FromResult(head);
                }
                lock (gateLock)
                {
                    if (nextGate < gates.Length)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gates[nextGate++]) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) });
            }
        };
        return (handler, gates, new[] { NewTrackedFileName(), NewTrackedFileName(), NewTrackedFileName(), NewTrackedFileName() });
    }

    private async Task<ModelDownloadStatus[]> StartThreeAndWaitUntilAllDownloadingAsync(ModelDownloadService service, string[] fileNames)
    {
        var created = new ModelDownloadStatus[3];
        for (int i = 0; i < 3; i++) created[i] = await service.CreateDownloadAsync(RequestFor(fileNames[i]), CancellationToken.None);

        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline && created.Any(c => service.Get(c.Id)?.Status != "downloading"))
            await Task.Delay(20);
        Assert.All(created, c => Assert.Equal("downloading", service.Get(c.Id)!.Status));
        return created;
    }

    [Fact]
    public async Task A_download_beyond_the_concurrency_limit_stays_queued_until_a_slot_frees()
    {
        (FakeHandler handler, GatedStream[] gates, string[] fileNames) = BuildThreeGatedDownloadsHandler();
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        ModelDownloadStatus[] created = await StartThreeAndWaitUntilAllDownloadingAsync(service, fileNames);

        ModelDownloadStatus fourth = await service.CreateDownloadAsync(RequestFor(fileNames[3]), CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal("queued", service.Get(fourth.Id)!.Status);

        gates[0].Release();
        ModelDownloadStatus fourthFinal = await WaitForTerminalAsync(service, fourth.Id, TestTimeouts.JobCompletion);
        Assert.Equal("completed", fourthFinal.Status);

        gates[1].Release();
        gates[2].Release();
        await WaitForTerminalAsync(service, created[0].Id, TestTimeouts.JobCompletion);
        await WaitForTerminalAsync(service, created[1].Id, TestTimeouts.JobCompletion);
        await WaitForTerminalAsync(service, created[2].Id, TestTimeouts.JobCompletion);
    }

    [Fact]
    public async Task Cancelling_a_still_queued_download_never_starts_it_and_leaves_no_partial_file()
    {
        (FakeHandler handler, GatedStream[] gates, string[] fileNames) = BuildThreeGatedDownloadsHandler();
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        ModelDownloadStatus[] created = await StartThreeAndWaitUntilAllDownloadingAsync(service, fileNames);

        ModelDownloadStatus fourth = await service.CreateDownloadAsync(RequestFor(fileNames[3]), CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal("queued", service.Get(fourth.Id)!.Status);

        Assert.True(service.Cancel(fourth.Id));
        ModelDownloadStatus fourthFinal = await WaitForTerminalAsync(service, fourth.Id, TestTimeouts.JobCompletion);
        Assert.Equal("cancelled", fourthFinal.Status);

        string partialPath = Path.Combine(ModelsDir, fileNames[3]) + ".partial";
        Assert.False(File.Exists(partialPath));

        gates[0].Release();
        gates[1].Release();
        gates[2].Release();
        await WaitForTerminalAsync(service, created[0].Id, TestTimeouts.JobCompletion);
        await WaitForTerminalAsync(service, created[1].Id, TestTimeouts.JobCompletion);
        await WaitForTerminalAsync(service, created[2].Id, TestTimeouts.JobCompletion);
    }

    [Fact]
    public void Managed_library_lists_only_top_level_gguf_files_with_quantization()
    {
        string fileName = NewTrackedFileName();
        string path = Path.Combine(ModelsDir, fileName.Replace(".gguf", "-Q4_K_M.gguf", StringComparison.Ordinal));
        _createdPaths.Add(path);
        File.WriteAllBytes(path, new byte[123]);
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        ManagedModelInfo? model = service.ListManagedModels().FirstOrDefault(m => m.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(model);
        Assert.Equal(123, model!.SizeBytes);
        // The full K-quant label, not the coarse "Q4" the old substring parser returned.
        Assert.Equal("Q4_K_M", model.Quantization);
    }

    [Fact]
    public void Managed_library_rejects_traversal_and_deletes_explicit_file()
    {
        string fileName = NewTrackedFileName();
        string path = Path.Combine(ModelsDir, fileName);
        File.WriteAllBytes(path, new byte[] { 1 });
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        Assert.Throws<InvalidDataException>(() => service.DeleteManagedModel("..\\outside.gguf"));
        Assert.True(service.DeleteManagedModel(fileName));
        Assert.False(File.Exists(path));
        _createdPaths.Remove(path);
    }

    [Fact]
    public void Managed_library_import_copies_source_without_moving_it()
    {
        string source = Path.Combine(Path.GetTempPath(), "parux-import-" + Guid.NewGuid().ToString("N") + ".gguf");
        _createdExternalPaths.Add(source);
        File.WriteAllBytes(source, new byte[] { 4, 5, 6 });
        string destination = Path.Combine(ModelsDir, Path.GetFileName(source));
        _createdPaths.Add(destination);
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        ManagedModelInfo imported = service.ImportManagedModel(source);

        Assert.True(File.Exists(source));
        Assert.Equal(3, imported.SizeBytes);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void SetAlias_persists_and_ListManagedModels_surfaces_it_then_clears()
    {
        string fileName = NewTrackedFileName();
        File.WriteAllBytes(Path.Combine(ModelsDir, fileName), new byte[] { 1 });
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        try
        {
            ManagedModelInfo updated = service.SetAlias(fileName, "  My Coding Model  ");
            Assert.Equal("My Coding Model", updated.Alias);

            ManagedModelInfo? listed = service.ListManagedModels().FirstOrDefault(m => m.FileName == fileName);
            Assert.NotNull(listed);
            Assert.Equal("My Coding Model", listed!.Alias);

            ManagedModelInfo cleared = service.SetAlias(fileName, string.Empty);
            Assert.Null(cleared.Alias);
            Assert.Null(service.ListManagedModels().FirstOrDefault(m => m.FileName == fileName)?.Alias);
        }
        finally
        {
            service.SetAlias(fileName, null);
        }
    }

    [Fact]
    public void DeleteManagedModel_also_clears_the_files_alias_so_it_never_orphans()
    {
        string fileName = NewTrackedFileName();
        File.WriteAllBytes(Path.Combine(ModelsDir, fileName), new byte[] { 1 });
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));
        service.SetAlias(fileName, "will be orphaned if not cleaned up");
        ModelDownloadProvenanceManifestStore.Upsert(ModelsDir, fileName, new ModelDownloadProvenance
        {
            RepoId = "test/repo",
            LicenseMetadataStatus = ModelLicenseMetadataStatus.NotProvided,
            ComputedSha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant(),
            IntegrityStatus = "unavailable",
            SizeBytes = 1,
            FileLastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(ModelsDir, fileName)),
            CompletedUtc = DateTime.UtcNow
        });

        Assert.True(service.DeleteManagedModel(fileName));
        _createdPaths.Remove(Path.Combine(ModelsDir, fileName));

        string aliasesPath = Path.Combine(ModelsDir, ".aliases.json");
        string json = File.Exists(aliasesPath) ? File.ReadAllText(aliasesPath) : string.Empty;
        Assert.DoesNotContain(fileName, json, StringComparison.Ordinal);
        Assert.Null(ModelDownloadProvenanceManifestStore.Get(ModelsDir, fileName));
    }

    [Fact]
    public async Task A_completed_download_persists_its_repo_id_and_ListManagedModels_surfaces_it()
    {
        string fileName = NewTrackedFileName();
        var handler = new FakeHandler
        {
            OnSend = (req, ct) => Task.FromResult(req.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9, 9, 9 }) })
        };
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));

        ModelDownloadStatus created = await service.CreateDownloadAsync(RequestFor(fileName), CancellationToken.None);
        ModelDownloadStatus final = await WaitForTerminalAsync(service, created.Id, TestTimeouts.JobCompletion);
        Assert.Equal("completed", final.Status);

        ManagedModelInfo? listed = service.ListManagedModels().FirstOrDefault(m => m.FileName == fileName);
        Assert.NotNull(listed);
        Assert.Equal("test/repo", listed!.RepoId);
    }

    [Fact]
    public void ImportManagedModel_leaves_repo_id_null_since_provenance_is_genuinely_unknown()
    {
        string source = Path.Combine(Path.GetTempPath(), "parux-import-repoid-" + Guid.NewGuid().ToString("N") + ".gguf");
        _createdExternalPaths.Add(source);
        File.WriteAllBytes(source, new byte[] { 7 });
        string destination = Path.Combine(ModelsDir, Path.GetFileName(source));
        _createdPaths.Add(destination);
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        ManagedModelInfo imported = service.ImportManagedModel(source);

        Assert.Null(imported.RepoId);
        Assert.Null(imported.Provenance);
    }

    [Fact]
    public void SetAlias_does_not_disturb_an_already_recorded_repo_id()
    {
        string fileName = NewTrackedFileName();
        File.WriteAllBytes(Path.Combine(ModelsDir, fileName), new byte[] { 1 });
        string repoIdsPath = Path.Combine(ModelsDir, ".repo-ids.json");
        var existing = ModelDownloadService.ExportRepoIdsForBackup() ?? new();
        existing[fileName] = "some/repo";
        File.WriteAllText(repoIdsPath, System.Text.Json.JsonSerializer.Serialize(existing));
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        try
        {
            ManagedModelInfo updated = service.SetAlias(fileName, "A Coding Model");
            Assert.Equal("some/repo", updated.RepoId);
            Assert.Equal("some/repo", service.ListManagedModels().FirstOrDefault(m => m.FileName == fileName)?.RepoId);
        }
        finally
        {
            service.SetAlias(fileName, null);
        }
    }

    [Fact]
    public void DeleteManagedModel_also_clears_the_files_repo_id_so_it_never_orphans()
    {
        string fileName = NewTrackedFileName();
        File.WriteAllBytes(Path.Combine(ModelsDir, fileName), new byte[] { 1 });
        string repoIdsPath = Path.Combine(ModelsDir, ".repo-ids.json");
        var existing = ModelDownloadService.ExportRepoIdsForBackup() ?? new();
        existing[fileName] = "will/be-orphaned-if-not-cleaned-up";
        File.WriteAllText(repoIdsPath, System.Text.Json.JsonSerializer.Serialize(existing));
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));

        Assert.True(service.DeleteManagedModel(fileName));
        _createdPaths.Remove(Path.Combine(ModelsDir, fileName));

        string json = File.Exists(repoIdsPath) ? File.ReadAllText(repoIdsPath) : string.Empty;
        Assert.DoesNotContain(fileName, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRepoIdsForBackup_returns_null_when_nothing_has_ever_been_recorded()
    {
        string isolatedDir = Path.Combine(Path.GetTempPath(), "parux-repoid-export-test-" + Guid.NewGuid().ToString("N"));
        Assert.Null(ModelDownloadService.ExportRepoIdsForBackup(isolatedDir));
    }

    [Fact]
    public void ImportRepoIdsForBackup_then_ExportRepoIdsForBackup_round_trips_against_an_isolated_directory()
    {
        string isolatedDir = Path.Combine(Path.GetTempPath(), "parux-repoid-roundtrip-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repoIds = new Dictionary<string, string> { ["a.gguf"] = "org/model-a", ["b.gguf"] = "org/model-b" };
            ModelDownloadService.ImportRepoIdsForBackup(repoIds, isolatedDir);

            Dictionary<string, string>? exported = ModelDownloadService.ExportRepoIdsForBackup(isolatedDir);
            Assert.NotNull(exported);
            Assert.Equal(repoIds, exported);
        }
        finally
        {
            try { Directory.Delete(isolatedDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SetAlias_rejects_a_file_not_in_the_managed_library()
    {
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));
        Assert.Throws<FileNotFoundException>(() => service.SetAlias(NewTrackedFileName(), "does not exist"));
    }

    [Fact]
    public void SetAlias_rejects_path_traversal()
    {
        var service = new ModelDownloadService(new HttpClient(new FakeHandler()), new HttpClient(new FakeHandler()));
        Assert.Throws<InvalidDataException>(() => service.SetAlias("..\\outside.gguf", "x"));
    }
}
