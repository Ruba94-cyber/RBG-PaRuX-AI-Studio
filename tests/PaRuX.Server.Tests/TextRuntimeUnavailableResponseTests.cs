using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TextRuntimeUnavailableResponseTests
{
    private static async Task<IHost> StartAsync(Action<IApplicationBuilder> routes) =>
        await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app =>
        {
            app.UseMiddleware<TextRuntimeUnavailableMiddleware>();
            routes(app);
        })).StartAsync();

    private static async Task AssertTyped503(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(TextRuntimeStateUnavailableException.ErrorCode, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(message, doc.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/media/start")]
    [InlineData("/speech/start")]
    [InlineData("/chat/start")]
    [InlineData("/runtime/action")]
    public async Task Escaping_exception_becomes_the_same_typed_503_on_every_route(string path)
    {
        using IHost host = await StartAsync(app => app.Run(_ =>
            throw new TextRuntimeStateUnavailableException("port table unreadable")));
        await AssertTyped503(await host.GetTestClient().PostAsync(path, null), "port table unreadable");
    }

    [Fact]
    public async Task Other_exceptions_are_untouched_and_started_responses_are_not_rewritten()
    {
        using IHost other = await StartAsync(app => app.Run(_ => throw new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetTestClient().GetAsync("/x"));

        using IHost started = await StartAsync(app => app.Run(async ctx =>
        {
            await ctx.Response.WriteAsync("partial");
            await ctx.Response.Body.FlushAsync();
            throw new TextRuntimeStateUnavailableException("late");
        }));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage r = await started.GetTestClient().GetAsync("/x");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            await r.Content.ReadAsStringAsync();
        });
    }

    [Fact]
    public async Task Helper_matches_the_original_load_endpoint_body_and_refuses_after_response_started()
    {
        var ex = new TextRuntimeStateUnavailableException("m");
        using IHost host = await StartAsync(app => app.Run(async ctx =>
        {
            bool wrote = await TextRuntimeUnavailableResponse.TryWriteAsync(ctx.Response, ex, ctx.RequestAborted);
            Assert.True(wrote);
            Assert.False(await TextRuntimeUnavailableResponse.TryWriteAsync(ctx.Response, ex, ctx.RequestAborted));
        }));
        await AssertTyped503(await host.GetTestClient().GetAsync("/x"), "m");
    }
}
