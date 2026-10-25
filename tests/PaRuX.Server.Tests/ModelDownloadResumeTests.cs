using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Focused coverage for Model Hub pause/resume. "Pause" deliberately reuses the pre-existing
/// cancel operation because that operation has always preserved the .partial file; resume creates
/// a new immutable history job and lets ModelDownloadService's real HTTP Range path continue from
/// those bytes. No network access is used here.
/// </summary>
public sealed class ModelDownloadResumeTests : IDisposable
{
    private readonly string _fileName = "parux-resume-test-" + Guid.NewGuid().ToString("N") + ".gguf";
    private readonly string _modelsDirectory = Path.Combine(Path.GetTempPath(), "parux-resume-models-" + Guid.NewGuid().ToString("N"));
    private static readonly string PinnedRevision = new('b', 40);

    private string ModelsDir => _modelsDirectory;

    private string FinalPath => Path.Combine(ModelsDir, _fileName);
    private string PartialPath => FinalPath + ".partial";

    public void Dispose()
    {
        try { if (File.Exists(FinalPath)) File.Delete(FinalPath); } catch { }
        try { if (File.Exists(PartialPath)) File.Delete(PartialPath); } catch { }
        try { if (File.Exists(PartialPath + ".source.json")) File.Delete(PartialPath + ".source.json"); } catch { }
        try { if (File.Exists(PartialPath + ".source.json.tmp")) File.Delete(PartialPath + ".source.json.tmp"); } catch { }
        try
        {
            string repoIdsPath = Path.Combine(ModelsDir, ".repo-ids.json");
            if (File.Exists(repoIdsPath))
            {
                var repoIds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(repoIdsPath)) ?? new();
                if (repoIds.Remove(_fileName))
                    File.WriteAllText(repoIdsPath, System.Text.Json.JsonSerializer.Serialize(repoIds));
            }
        }
        catch { }
        try { ModelDownloadProvenanceManifestStore.Remove(ModelsDir, _fileName); } catch { }
        try { if (Directory.Exists(ModelsDir)) Directory.Delete(ModelsDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cancelled_download_can_resume_from_preserved_partial_without_resending_source_metadata()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        Directory.CreateDirectory(ModelsDir);
        byte[] content = new byte[32 * 1024];
        new Random(27).NextBytes(content);
        long? resumedFrom = null;
        string? requestedUrl = null;
        int getCount = 0;

        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                requestedUrl = request.RequestUri!.AbsoluteUri;
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }

                getCount++;
                long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
                if (from.HasValue)
                {
                    resumedFrom = from.Value;
                    byte[] remaining = content[(int)from.Value..];
                    var ranged = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(remaining)
                    };
                    ranged.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        from.Value, content.Length - 1, content.Length);
                    return Task.FromResult(ranged);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new SlowStream(content, 1024, TimeSpan.FromMilliseconds(80)))
                });
            }
        };
        var service = CreateService(handler);

        ModelDownloadStatus first = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        Assert.Equal(PinnedRevision, first.Revision);
        await WaitUntilAsync(() => service.Get(first.Id)?.BytesDownloaded > 0, TimeSpan.FromSeconds(5));
        Assert.True(service.Cancel(first.Id));
        ModelDownloadStatus cancelled = await WaitForStatusAsync(service, first.Id, "cancelled", TestTimeouts.JobCompletion);

        Assert.Equal("cancelled", cancelled.Status);
        Assert.True(File.Exists(PartialPath));
        long partialBytes = new FileInfo(PartialPath).Length;
        Assert.True(partialBytes > 0);
        Assert.True(partialBytes < content.Length);

        ModelDownloadStatus resumed = await service.CreateDownloadAsync(new ModelDownloadRequest
        {
            ResumeFromDownloadId = first.Id
        }, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForStatusAsync(service, resumed.Id, "completed", TestTimeouts.JobCompletion);

        Assert.NotEqual(first.Id, resumed.Id);
        Assert.Equal(PinnedRevision, resumed.Revision);
        Assert.Equal(PinnedRevision, completed.Revision);
        Assert.Equal("https://huggingface.co/test/resume/resolve/" + PinnedRevision + "/" + _fileName, requestedUrl);
        Assert.True(getCount >= 2);
        Assert.Equal(partialBytes, resumedFrom);
        Assert.Equal(content.Length, completed.BytesDownloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.LocalPath!));
        Assert.False(File.Exists(PartialPath));
        ModelDownloadProvenance provenance = Assert.IsType<ModelDownloadProvenance>(
            Assert.Single(service.ListManagedModels()).Provenance);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), provenance.ComputedSha256);
        Assert.Equal("unavailable", provenance.IntegrityStatus);
    }

    [Fact]
    public async Task Partial_from_another_revision_is_preserved_until_discard_is_explicitly_requested()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        Directory.CreateDirectory(ModelsDir);
        string firstRevision = new('a', 40);
        byte[] oldContent = new byte[32 * 1024];
        new Random(41).NextBytes(oldContent);
        byte[] newContent = new byte[24 * 1024];
        new Random(42).NextBytes(newContent);
        bool newRevisionReceivedRange = false;
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                string url = request.RequestUri!.AbsoluteUri;
                bool isNewRevision = url.Contains("/resolve/" + PinnedRevision + "/", StringComparison.Ordinal);
                byte[] content = isNewRevision ? newContent : oldContent;
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }

                long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
                if (isNewRevision)
                {
                    newRevisionReceivedRange = from.HasValue;
                    if (from.HasValue)
                    {
                        var ranged = new HttpResponseMessage(HttpStatusCode.PartialContent)
                        {
                            Content = new ByteArrayContent(newContent[(int)from.Value..])
                        };
                        ranged.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                            from.Value, newContent.Length - 1, newContent.Length);
                        return Task.FromResult(ranged);
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(newContent)
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new SlowStream(oldContent, 1024, TimeSpan.FromMilliseconds(80)))
                });
            }
        };
        var service = CreateService(handler);
        var oldRevisionRequest = new ModelDownloadRequest
        {
            RepoId = "test/resume",
            Revision = firstRevision,
            FileName = _fileName,
            DownloadUrl = "https://huggingface.co/test/resume/resolve/" + firstRevision + "/" + _fileName
        };

        ModelDownloadStatus oldJob = await service.CreateDownloadAsync(oldRevisionRequest, CancellationToken.None);
        await WaitUntilAsync(() => service.Get(oldJob.Id)?.BytesDownloaded > 0, TimeSpan.FromSeconds(5));
        Assert.True(service.Cancel(oldJob.Id));
        await WaitForStatusAsync(service, oldJob.Id, "cancelled", TestTimeouts.JobCompletion);
        Assert.True(File.Exists(PartialPath));
        byte[] preservedPartial = await File.ReadAllBytesAsync(PartialPath);
        string identityPath = PartialPath + ".source.json";
        string preservedIdentity = await File.ReadAllTextAsync(identityPath);

        ModelDownloadStatus newJob = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        ModelDownloadStatus refused = await WaitForStatusAsync(service, newJob.Id, "failed", TestTimeouts.JobCompletion);

        Assert.Equal("permanent", refused.FailureClass);
        Assert.Contains("different model source", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DiscardPartial=true", refused.Remediation, StringComparison.Ordinal);
        Assert.False(newRevisionReceivedRange);
        Assert.Equal(preservedPartial, await File.ReadAllBytesAsync(PartialPath));
        Assert.Equal(preservedIdentity, await File.ReadAllTextAsync(identityPath));

        ModelDownloadStatus retry = await service.CreateDownloadAsync(new ModelDownloadRequest
        {
            ResumeFromDownloadId = newJob.Id,
            DiscardPartial = true
        }, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForStatusAsync(service, retry.Id, "completed", TestTimeouts.JobCompletion);

        Assert.Equal(PinnedRevision, completed.Revision);
        Assert.False(newRevisionReceivedRange);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(completed.LocalPath!));
        Assert.False(File.Exists(PartialPath));
    }

    [Fact]
    public async Task Untracked_legacy_main_partial_remains_resumable_without_revision_metadata()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        Directory.CreateDirectory(ModelsDir);
        byte[] content = new byte[8 * 1024];
        new Random(43).NextBytes(content);
        byte[] initialBytes = content[..2048];
        await File.WriteAllBytesAsync(PartialPath, initialBytes);
        long? resumedFrom = null;
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }

                long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
                resumedFrom = from;
                var ranged = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(content[(int)from..])
                };
                ranged.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
                return Task.FromResult(ranged);
            }
        };
        var service = CreateService(handler);
        var legacyRequest = new ModelDownloadRequest
        {
            RepoId = "test/resume",
            FileName = _fileName,
            DownloadUrl = "https://huggingface.co/test/resume/resolve/main/" + _fileName
        };

        ModelDownloadStatus queued = await service.CreateDownloadAsync(legacyRequest, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForStatusAsync(service, queued.Id, "completed", TestTimeouts.JobCompletion);

        Assert.Null(completed.Revision);
        Assert.Equal(initialBytes.Length, resumedFrom);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.LocalPath!));
    }

    [Fact]
    public async Task Corrupt_partial_identity_is_preserved_until_discard_is_explicitly_requested()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        Directory.CreateDirectory(ModelsDir);
        byte[] oldContent = { 8, 6, 7, 5, 3, 0, 9 };
        byte[] replacementContent = { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(PartialPath, oldContent);
        string identityPath = PartialPath + ".source.json";
        const string corruptIdentity = "{ malformed";
        await File.WriteAllTextAsync(identityPath, corruptIdentity);
        int getCount = 0;
        long? resumedFrom = null;
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    head.Content.Headers.ContentLength = replacementContent.Length;
                    return Task.FromResult(head);
                }

                getCount++;
                resumedFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(replacementContent)
                });
            }
        };
        var service = CreateService(handler);
        var legacyRequest = new ModelDownloadRequest
        {
            RepoId = "test/resume",
            FileName = _fileName,
            DownloadUrl = "https://huggingface.co/test/resume/resolve/main/" + _fileName
        };

        ModelDownloadStatus queued = await service.CreateDownloadAsync(legacyRequest, CancellationToken.None);
        ModelDownloadStatus refused = await WaitForStatusAsync(service, queued.Id, "failed", TestTimeouts.JobCompletion);

        Assert.Equal("permanent", refused.FailureClass);
        Assert.Contains("identity could not be verified", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DiscardPartial=true", refused.Remediation, StringComparison.Ordinal);
        Assert.Equal(0, getCount);
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(PartialPath));
        Assert.Equal(corruptIdentity, await File.ReadAllTextAsync(identityPath));

        ModelDownloadStatus retry = await service.CreateDownloadAsync(new ModelDownloadRequest
        {
            ResumeFromDownloadId = refused.Id,
            DiscardPartial = true
        }, CancellationToken.None);
        ModelDownloadStatus completed = await WaitForStatusAsync(service, retry.Id, "completed", TestTimeouts.JobCompletion);

        Assert.Equal(1, getCount);
        Assert.Null(resumedFrom);
        Assert.Equal(replacementContent, await File.ReadAllBytesAsync(completed.LocalPath!));
    }

    [Fact]
    public async Task Resume_rejects_unknown_job_without_attempting_network_access()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        var handler = new FakeHandler
        {
            OnSend = (_, _) => throw new InvalidOperationException("Network must not be touched for an unknown resume id.")
        };
        var service = CreateService(handler);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateDownloadAsync(new ModelDownloadRequest
            {
                ResumeFromDownloadId = "missing-job"
            }, CancellationToken.None));

        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_rejects_a_job_that_is_still_active_to_prevent_duplicate_writers()
    {
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDir);
        byte[] content = new byte[16 * 1024];
        var handler = new FakeHandler
        {
            OnSend = (request, ct) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    head.Content.Headers.ContentLength = content.Length;
                    return Task.FromResult(head);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new SlowStream(content, 512, TimeSpan.FromMilliseconds(100)))
                });
            }
        };
        var service = CreateService(handler);
        ModelDownloadStatus active = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        await WaitUntilAsync(() => service.Get(active.Id)?.Status is "queued" or "downloading", TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDownloadAsync(new ModelDownloadRequest
            {
                ResumeFromDownloadId = active.Id
            }, CancellationToken.None));

        service.Cancel(active.Id);
        await WaitForStatusAsync(service, active.Id, "cancelled", TestTimeouts.JobCompletion);
    }

    private ModelDownloadRequest Request() => new()
    {
        RepoId = "test/resume",
        Revision = PinnedRevision,
        FileName = _fileName,
        DownloadUrl = "https://huggingface.co/test/resume/resolve/" + PinnedRevision + "/" + _fileName,
        SizeBytes = 0
    };

    private static ModelDownloadService CreateService(FakeHandler handler)
    {
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(handler));
        service.BudgetProbe = _ => Task.FromResult(new AiStorageBudgetStatus
        {
            WithinBudget = true,
            LimitBytes = long.MaxValue,
            QuotaHeadroomBytes = long.MaxValue,
            DestinationFreeBytes = long.MaxValue
        });
        return service;
    }

    private static async Task<ModelDownloadStatus> WaitForStatusAsync(
        ModelDownloadService service, string id, string status, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? current = service.Get(id);
            if (current?.Status == status) return current;
            if ((current?.Status is "failed" or "completed" or "cancelled") && current.Status != status)
                throw new InvalidOperationException("Job reached unexpected terminal state " + current.Status + ": " + current.Error);
            await Task.Delay(20);
        }
        throw new TimeoutException("Download did not reach status '" + status + "'.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition was not reached in time.");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend(request, cancellationToken);
    }

    private sealed class SlowStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public SlowStream(byte[] data, int chunkSize, TimeSpan delay)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position > 0) await Task.Delay(_delay, cancellationToken);
            int remaining = _data.Length - _position;
            if (remaining <= 0) return 0;
            int count = Math.Min(Math.Min(_chunkSize, remaining), buffer.Length);
            _data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
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
}
