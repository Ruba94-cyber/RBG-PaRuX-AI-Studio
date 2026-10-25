using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-04 S3: downloads pause for Gaming. A real in-process HTTP server (HttpListener, real
/// sockets, Range support, throttled body) feeds the production downloader; the request URI is
/// rewritten from the validated huggingface.co form to the loopback fixture.
/// </summary>
public sealed class GamingDownloadPauseTests : IDisposable
{
    private static readonly string Revision = new('c', 40);
    private readonly string _fileName = "parux-gaming-pause-" + Guid.NewGuid().ToString("N") + ".gguf";
    private static string ModelsDir => Path.Combine(PaRuXDataRoot.Path, "models");
    private string FinalPath => Path.Combine(ModelsDir, _fileName);
    private string PartialPath => FinalPath + ".partial";

    public void Dispose()
    {
        foreach (string p in new[] { FinalPath, PartialPath, PartialPath + ".source.json", PartialPath + ".source.json.tmp" })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        try
        {
            string ids = Path.Combine(ModelsDir, ".repo-ids.json");
            if (File.Exists(ids))
            {
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ids)) ?? new();
                if (map.Remove(_fileName)) File.WriteAllText(ids, System.Text.Json.JsonSerializer.Serialize(map));
            }
        }
        catch { }
        try { ModelDownloadProvenanceManifestStore.Remove(ModelsDir, _fileName); } catch { }
    }

    /// <summary>Loopback HTTP server: HEAD length, GET with optional Range, 8 KiB chunks 15 ms apart.</summary>
    private sealed class ThrottledServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _content;
        private readonly CancellationTokenSource _stop = new();
        public readonly List<long?> RangeStarts = new();
        public int Port { get; }

        public ThrottledServer(byte[] content)
        {
            _content = content;
            for (int attempt = 0; ; attempt++)
            {
                int port = Random.Shared.Next(20000, 60000);
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Port = port;
                    break;
                }
                catch (HttpListenerException) when (attempt < 20) { }
            }
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private async Task Serve(HttpListenerContext ctx)
        {
            try
            {
                long start = 0;
                bool ranged = false;
                string? range = ctx.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes=") && long.TryParse(range[6..].TrimEnd('-'), out long from))
                {
                    start = from; ranged = true;
                    lock (RangeStarts) RangeStarts.Add(from);
                }
                ctx.Response.StatusCode = ranged ? 206 : 200;
                ctx.Response.ContentLength64 = _content.Length - start;
                if (ranged) ctx.Response.Headers["Content-Range"] = $"bytes {start}-{_content.Length - 1}/{_content.Length}";
                if (ctx.Request.HttpMethod == "HEAD") { ctx.Response.Close(); return; }
                for (long o = start; o < _content.Length; o += 8192)
                {
                    int n = (int)Math.Min(8192, _content.Length - o);
                    await ctx.Response.OutputStream.WriteAsync(_content.AsMemory((int)o, n));
                    await ctx.Response.OutputStream.FlushAsync();
                    await Task.Delay(15);
                }
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        public void Dispose() { _stop.Cancel(); try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private sealed class LoopbackRewriteHandler(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{port}{request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, ct);
        }
    }

    private ModelDownloadRequest Request() => new()
    {
        RepoId = "test/gaming-pause",
        Revision = Revision,
        FileName = _fileName,
        DownloadUrl = "https://huggingface.co/test/gaming-pause/resolve/" + Revision + "/" + _fileName,
        SizeBytes = 0
    };

    private static async Task<T> WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            T? value = probe();
            if (value != null) return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not reached");
    }

    [Fact]
    public async Task Model_download_pauses_mid_transfer_for_Gaming_keeps_exact_partial_and_resumes_byte_exact()
    {
        Directory.CreateDirectory(ModelsDir);
        byte[] content = new byte[512 * 1024];
        new Random(4).NextBytes(content);
        using var server = new ThrottledServer(content);
        bool gaming = false;
        var handler = new LoopbackRewriteHandler(server.Port);
        var service = new ModelDownloadService(new HttpClient(handler), new HttpClient(new LoopbackRewriteHandler(server.Port)))
        {
            GamingActive = () => Volatile.Read(ref gaming)
        };

        ModelDownloadStatus first = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        await WaitFor(() => service.Get(first.Id)?.BytesDownloaded > 64 * 1024 ? service.Get(first.Id) : null, TimeSpan.FromSeconds(20));

        Volatile.Write(ref gaming, true);
        ModelDownloadStatus paused = await WaitFor(
            () => service.Get(first.Id) is { Status: "paused" } s ? s : null, TimeSpan.FromSeconds(10));
        Assert.Equal("Paused for Gaming", paused.Error);

        // The transfer really stopped: the partial no longer grows and is an exact prefix.
        long frozen = new FileInfo(PartialPath).Length;
        await Task.Delay(400);
        Assert.Equal(frozen, new FileInfo(PartialPath).Length);
        Assert.InRange(frozen, 1, content.Length - 1);
        byte[] partial = await File.ReadAllBytesAsync(PartialPath);
        Assert.True(partial.AsSpan().SequenceEqual(content.AsSpan(0, partial.Length)), "partial must be an exact prefix");
        Assert.False(File.Exists(FinalPath));

        // Still gaming: nothing resumes, even when asked.
        Assert.Equal(0, await service.ResumeGamingPausedAsync(CancellationToken.None));
        Assert.Equal(frozen, new FileInfo(PartialPath).Length);

        // Gaming over, explicit resume policy: continues from the partial and completes intact.
        Volatile.Write(ref gaming, false);
        Assert.Equal(1, await service.ResumeGamingPausedAsync(CancellationToken.None));
        ModelDownloadStatus resumedJob = service.List().First(j => j.Id != first.Id);
        ModelDownloadStatus done = await WaitFor(
            () => service.Get(resumedJob.Id) is { Status: "completed" or "failed" } s ? s : null, TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.Status);
        Assert.Contains(frozen, server.RangeStarts.Select(r => r ?? -1));
        Assert.True(File.ReadAllBytes(FinalPath).AsSpan().SequenceEqual(content));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FinalPath))));
        Assert.False(File.Exists(PartialPath));
    }

    [Fact]
    public async Task Without_the_resume_policy_a_paused_download_stays_paused_until_the_user_resumes_it()
    {
        Directory.CreateDirectory(ModelsDir);
        byte[] content = new byte[256 * 1024];
        new Random(5).NextBytes(content);
        using var server = new ThrottledServer(content);
        bool gaming = false;
        var service = new ModelDownloadService(
            new HttpClient(new LoopbackRewriteHandler(server.Port)), new HttpClient(new LoopbackRewriteHandler(server.Port)))
        {
            GamingActive = () => Volatile.Read(ref gaming)
        };
        ModelDownloadStatus first = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        await WaitFor(() => service.Get(first.Id)?.BytesDownloaded > 16 * 1024 ? service.Get(first.Id) : null, TimeSpan.FromSeconds(20));
        Volatile.Write(ref gaming, true);
        await WaitFor(() => service.Get(first.Id) is { Status: "paused" } s ? s : null, TimeSpan.FromSeconds(10));
        Volatile.Write(ref gaming, false);

        await Task.Delay(500); // no automatic resume
        Assert.Equal("paused", service.Get(first.Id)!.Status);
        Assert.Single(service.List());

        // Manual resume (the existing user action) works from the same partial.
        ModelDownloadStatus resumed = await service.CreateDownloadAsync(
            new ModelDownloadRequest { ResumeFromDownloadId = first.Id }, CancellationToken.None);
        ModelDownloadStatus done = await WaitFor(
            () => service.Get(resumed.Id) is { Status: "completed" or "failed" } s ? s : null, TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.Status);
        Assert.True(File.ReadAllBytes(FinalPath).AsSpan().SequenceEqual(content));
    }

    [Fact]
    public async Task User_cancel_during_Gaming_is_not_relabelled_as_a_Gaming_pause()
    {
        Directory.CreateDirectory(ModelsDir);
        byte[] content = new byte[256 * 1024];
        using var server = new ThrottledServer(content);
        var service = new ModelDownloadService(
            new HttpClient(new LoopbackRewriteHandler(server.Port)), new HttpClient(new LoopbackRewriteHandler(server.Port)))
        {
            GamingActive = () => false
        };
        ModelDownloadStatus first = await service.CreateDownloadAsync(Request(), CancellationToken.None);
        await WaitFor(() => service.Get(first.Id)?.BytesDownloaded > 0 ? service.Get(first.Id) : null, TimeSpan.FromSeconds(20));
        Assert.True(service.Cancel(first.Id));
        ModelDownloadStatus cancelled = await WaitFor(
            () => service.Get(first.Id) is { Status: "cancelled" } s ? s : null, TimeSpan.FromSeconds(10));
        Assert.NotEqual("Paused for Gaming", cancelled.Error);
        Assert.Equal(0, await service.ResumeGamingPausedAsync(CancellationToken.None));
    }

    // ---- GamingHardOffWatch: persistently unreadable marker is reported, not silent ------

    [Fact]
    public void Watch_reports_a_persistently_unreadable_marker_once_and_stays_fail_open()
    {
        var warnings = new List<string>();
        using var watch = new GamingHardOffWatch(
            () => throw new IOException("denied"), CancellationToken.None, TimeSpan.FromMilliseconds(5), warnings.Add);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (watch.UnreadableWarnings == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Thread.Sleep(100);

        Assert.Equal(1, watch.UnreadableWarnings);
        Assert.Single(warnings);
        Assert.Contains("unreadable", warnings[0]);
        Assert.False(watch.Token.IsCancellationRequested);
        Assert.False(watch.Triggered);
    }

    [Fact]
    public void Watch_does_not_warn_for_a_transient_read_error()
    {
        int calls = 0;
        var warnings = new List<string>();
        using var watch = new GamingHardOffWatch(
            () => Interlocked.Increment(ref calls) % 3 == 0 ? throw new IOException("blip") : false,
            CancellationToken.None, TimeSpan.FromMilliseconds(5), warnings.Add);
        Thread.Sleep(600);
        Assert.Empty(warnings);
        Assert.Equal(0, watch.UnreadableWarnings);
    }
}
