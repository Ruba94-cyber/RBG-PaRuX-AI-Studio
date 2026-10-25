using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiResponseReaderTests
{
    [Fact]
    public async Task ReadsOpenAiCompatibleText()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello!\"}}]}",
                Encoding.UTF8, "application/json")
        };
        Assert.Equal("Hello!", await ExplicitApiResponseReader.ReadTextAsync(
            response, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMissingTextCompletion()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[]}")
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJsonIsRejectedWithoutEchoingResponse()
    {
        const string sensitive = "private-prompt-data";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json " + sensitive)
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
        Assert.DoesNotContain(sensitive, error.Message);
    }

    [Fact]
    public async Task SuccessfulHttpWithProviderErrorIsRejected()
    {
        const string sensitive = "private-provider-error";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"" + sensitive + "\"}}",
                Encoding.UTF8, "application/json")
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
        Assert.DoesNotContain(sensitive, error.Message);
    }

    [Fact]
    public async Task MissingResponseBodyIsRejected()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedResponseBeforeParsing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ExplicitApiResponseReader.MaxResponseBytes + 1])
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplicitApiResponseReader.ReadTextAsync(response, CancellationToken.None));
    }
}
