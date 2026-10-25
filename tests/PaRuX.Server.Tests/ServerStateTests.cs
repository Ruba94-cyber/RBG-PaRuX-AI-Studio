using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ServerStateTests
{
    [Fact]
    public void Bootstrap_promotion_replaces_stale_file_while_desktop_reader_allows_atomic_rename()
    {
        string directory = Path.Combine(Path.GetTempPath(), "parux-bootstrap-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "bootstrap.json");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(destination, "{\"processId\":1}");
            using var reader = new FileStream(
                destination, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var expected = new ServerBootstrap
            {
                Endpoint = "http://127.0.0.1:51123",
                Token = "new-token",
                ProcessId = 4242,
                StartedUtc = new DateTime(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc)
            };

            ServerState.PublishAtomically(destination, expected);

            ServerBootstrap? actual = JsonSerializer.Deserialize<ServerBootstrap>(File.ReadAllText(destination),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(actual);
            Assert.Equal(expected.ProcessId, actual.ProcessId);
            Assert.Equal(expected.Token, actual.Token);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
