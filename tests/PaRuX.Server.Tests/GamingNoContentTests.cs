using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingNoContentTests
{
    [Fact]
    public async Task Successful_delete_without_body_is_not_a_json_error()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        var result = await GamingResponseReader.ReadAsync<object>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
    }
}
