using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiMalformedResponseTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"message\":null}]}")]
    [InlineData("{\"choices\":[{\"message\":[]}]}")]
    public async Task CompletionRejectsWrongJsonShapes(string json)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"delta\":null}]}")]
    [InlineData("{\"choices\":[{\"delta\":[]}]}")]
    public async Task StreamingIgnoresUnexpectedJsonShapes(string json)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: " + json + "\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream")
        };
        var output = new List<string>();
        await foreach (string delta in ExplicitApiStreamingReader.ReadDeltasAsync(
            response, CancellationToken.None))
            output.Add(delta);
        Assert.Empty(output);
    }
}
