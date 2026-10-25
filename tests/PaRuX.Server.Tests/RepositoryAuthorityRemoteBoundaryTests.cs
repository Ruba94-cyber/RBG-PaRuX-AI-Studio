using Microsoft.AspNetCore.Http;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RepositoryAuthorityRemoteBoundaryTests
{
    [Theory]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("PUT", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("DELETE", "/api/v1/orchestrator/tasks/task-1/repositories/backend")]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/workspace")]
    [InlineData("PUT", "/api/v1/orchestrator/tasks/task-1/workspace")]
    public async Task Paired_device_cannot_read_or_mutate_workstation_repository_authority(string method, string path)
    {
        PairedDeviceAbuseMiddleware.ResetForTests();
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Items["PaRuXOwner"] = "device:remote-test";
        var middleware = new PairedDeviceAbuseMiddleware(next =>
        {
            next.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
