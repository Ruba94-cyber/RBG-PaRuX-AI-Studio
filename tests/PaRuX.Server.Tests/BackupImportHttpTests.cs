using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// HTTP contract of the restore endpoints (BIC-15). The host's BackupService is replaced with one rooted entirely
/// in a temp directory, so these destructive restores can never touch real PaRuX data.
/// </summary>
public sealed class BackupImportHttpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-backup-http-" + Guid.NewGuid().ToString("N"));
    private readonly BackupResilienceTests.Instance _app;
    private readonly WebApplicationFactory<global::Program> _factory;
    private readonly HttpClient _client;

    public BackupImportHttpTests()
    {
        string server = Path.Combine(_root, "server"), client = Path.Combine(_root, "client"), models = Path.Combine(_root, "models");
        foreach (string d in new[] { server, client, models }) Directory.CreateDirectory(d);
        _app = BackupResilienceTests.Build(server, client, models);
        _factory = new ApiSecurityTests.PaRuXFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BackupService>();
                services.AddSingleton(_app.Backups);
                // TestServer has no socket; give requests the loopback peer a real desktop client would have.
                services.AddTransient<IStartupFilter, LoopbackPeerStartupFilter>();
            }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _app.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ServerDir => Path.Combine(_root, "server");
    private string SnapshotDir => Path.Combine(_root, "client", "backups");

    private static void Save(ConversationStore store, string id, string title) =>
        store.Save(id, new PaRuX.Contracts.ConversationUpsertRequest
        {
            Title = title, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, DocumentJson = "{}"
        });

    private async Task<byte[]> ArchiveAndDivergeAsync()
    {
        Save(_app.Conversations, "a", "in-archive");
        byte[] zip = await _app.Backups.ExportAsync(CancellationToken.None);
        _app.Conversations.Delete("a");
        Save(_app.Conversations, "live", "live-chat");
        return zip;
    }

    private void BreakBenchmarkStore()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string path = Path.Combine(ServerDir, "model-benchmarks.db" + suffix);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private Task<HttpResponseMessage> Post(string url, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return _client.PostAsync(url, content);
    }

    public static IEnumerable<object[]> Routes() => new[]
    {
        new object[] { "/api/v2/backup/import" },
        new object[] { "/api/v1/backup/import" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unsnapshottable_store_is_refused_with_409_and_nothing_changes(string route)
    {
        byte[] zip = await ArchiveAndDivergeAsync();
        BreakBenchmarkStore();

        using HttpResponseMessage response = await Post(route, zip);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("incomplete-rollback-snapshot", body.RootElement.GetProperty("code").GetString());
        Assert.Contains(body.RootElement.GetProperty("unsnapshottableStores").EnumerateArray(),
            e => (e.GetString() ?? "").Contains("model-benchmarks.db"));
        Assert.Equal("live-chat", _app.Conversations.Get("live")!.Title);
        Assert.Null(_app.Conversations.Get("a"));
        Assert.True(!Directory.Exists(SnapshotDir) || Directory.GetFiles(SnapshotDir).Length == 0);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Explicit_opt_in_restores_and_reports_the_partial_snapshot(string route)
    {
        byte[] zip = await ArchiveAndDivergeAsync();
        BreakBenchmarkStore();

        using HttpResponseMessage response = await Post(route + "?allowIncompleteRollbackSnapshot=true", zip);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(body.RootElement.GetProperty("unsnapshottableStores").EnumerateArray(),
            e => (e.GetString() ?? "").Contains("model-benchmarks.db"));
        Assert.True(File.Exists(body.RootElement.GetProperty("rollbackSnapshotPath").GetString()));
        Assert.Equal("in-archive", _app.Conversations.Get("a")!.Title);
    }

    [Fact]
    public async Task Clear_safety_snapshots_endpoint_removes_them()
    {
        byte[] zip = await ArchiveAndDivergeAsync();
        using (HttpResponseMessage ok = await Post("/api/v1/backup/import", zip)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotEmpty(Directory.GetFiles(SnapshotDir, "pre-restore-*.zip"));

        using HttpResponseMessage cleared = await _client.DeleteAsync("/api/v1/backup/safety-snapshots");

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("removed").GetInt32() >= 1);
        Assert.Empty(Directory.GetFiles(SnapshotDir, "pre-restore-*"));
    }

    private sealed class LoopbackPeerStartupFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (Microsoft.AspNetCore.Http.HttpContext context, Func<Task> nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
