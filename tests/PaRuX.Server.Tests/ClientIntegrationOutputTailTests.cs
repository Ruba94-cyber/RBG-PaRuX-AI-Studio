using System.Text;
using PaRuX.Services.Integrations;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>CF-08: a headless client test keeps only the tail of a client's output, never the whole stream.</summary>
public sealed class ClientIntegrationOutputTailTests
{
    [Fact]
    public async Task Chatty_client_output_is_bounded_and_keeps_the_final_error()
    {
        const int limit = 64 * 1024;
        const string finalError = "FINAL-CLIENT-ERROR: gateway rejected the key";
        var block = new string('x', 1023) + "\n";
        var stream = new StringBuilder(capacity: 10 * 1024 * 1024 + finalError.Length + 2);
        while (stream.Length < 10 * 1024 * 1024) stream.Append(block);
        stream.Append(finalError);
        using var reader = new StringReader(stream.ToString());

        string tail = await ClientIntegrationService.ReadBoundedTailAsync(reader, limit);

        Assert.True(tail.Length <= limit, "Retained " + tail.Length + " characters from a 10 MB stream.");
        Assert.EndsWith(finalError, tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_output_is_returned_whole()
    {
        using var reader = new StringReader("first line\nsecond line");

        Assert.Equal("first line\nsecond line", await ClientIntegrationService.ReadBoundedTailAsync(reader, 64 * 1024));
    }
}
