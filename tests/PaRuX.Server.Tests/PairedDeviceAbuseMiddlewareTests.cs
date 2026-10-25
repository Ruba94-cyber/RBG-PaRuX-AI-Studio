using Microsoft.AspNetCore.Http;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PairedDeviceAbuseMiddlewareTests : IDisposable
{
    public PairedDeviceAbuseMiddlewareTests() => PairedDeviceAbuseMiddleware.ResetForTests();
    public void Dispose() => PairedDeviceAbuseMiddleware.ResetForTests();

    private static DefaultHttpContext Context(string method, string path, string? owner = "device:test-device")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (owner != null) context.Items["PaRuXOwner"] = owner;
        return context;
    }

    private static PairedDeviceAbuseMiddleware Middleware() => new(context =>
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Loopback_desktop_owner_is_not_subject_to_paired_device_buckets_or_mobile_allowlist()
    {
        PairedDeviceAbuseMiddleware middleware = Middleware();
        for (int i = 0; i < PairedDeviceAbuseMiddleware.MutationLimitPerMinute + 20; i++)
        {
            DefaultHttpContext context = Context("POST", "/api/v1/runtime/install", owner: "desktop");
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData("POST", "/api/v1/runtime/install")]
    [InlineData("POST", "/api/v1/runtime/performance/tune")]
    [InlineData("POST", "/api/v1/backup/import")]
    [InlineData("GET", "/api/v1/backup/export")]
    [InlineData("POST", "/api/v1/mcp/servers")]
    [InlineData("POST", "/api/v1/training/engine/install")]
    [InlineData("POST", "/api/v1/training/jobs")]
    [InlineData("POST", "/api/v1/threed/engine/install")]
    [InlineData("POST", "/api/v1/avatar/engine/install")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/worktree")]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/desktop-worktree")]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/workspace")]
    [InlineData("PUT", "/api/v1/orchestrator/tasks/task-1/workspace")]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("PUT", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/repositories")]
    [InlineData("DELETE", "/api/v1/orchestrator/tasks/task-1/repositories/backend")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/resume")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/delegate-batch")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/user-question")]
    [InlineData("POST", "/api/v1/orchestrator/schedule")]
    [InlineData("POST", "/api/v1/orchestrator/coding-tasks")]
    [InlineData("POST", "/api/v1/projects/desktop")]
    [InlineData("GET", "/api/v1/models/library")]
    [InlineData("POST", "/api/v1/models/library/load")]
    [InlineData("POST", "/api/v1/models/benchmarks")]
    [InlineData("POST", "/api/v1/skills")]
    [InlineData("POST", "/api/v1/context/entities")]
    [InlineData("POST", "/api/v1/context/relations")]
    [InlineData("GET", "/api/v1/context/export")]
    [InlineData("GET", "/api/v1/context/entities/entity-1/traverse")]
    [InlineData("GET", "/api/v1/chat/capability-overrides")]
    [InlineData("GET", "/api/v1/chat/capability-overrides/coding/balanced")]
    [InlineData("PUT", "/api/v1/chat/capability-overrides/coding/balanced")]
    [InlineData("DELETE", "/api/v1/chat/capability-overrides/coding/balanced")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/logs")]
    [InlineData("POST", "/api/v2/plugins/install")]
    [InlineData("POST", "/api/v2/plugins/inspect")]
    [InlineData("GET", "/api/v2/plugins/trusted-publishers")]
    [InlineData("GET", "/api/v2/execution-profiles")]
    [InlineData("PUT", "/api/v2/execution-profiles/quality")]
    [InlineData("PUT", "/api/v2/execution-profiles/models/model-a")]
    [InlineData("GET", "/api/v2/observability/dashboard")]
    [InlineData("POST", "/api/v2/goals")]
    [InlineData("POST", "/api/v2/goals/tasks")]
    [InlineData("POST", "/api/v2/virtual-context/ledger")]
    [InlineData("GET", "/api/v2/semantic-index/repositories")]
    [InlineData("POST", "/api/v2/semantic-index/search")]
    [InlineData("GET", "/api/v2/visual-context/project/p1/snapshots")]
    [InlineData("POST", "/api/v2/media/engine-race/image")]
    public async Task Desktop_only_capabilities_are_denied_even_with_a_valid_paired_identity(string method, string path)
    {
        DefaultHttpContext context = Context(method, path);
        await Middleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/projects")]
    [InlineData("POST", "/api/v1/projects/project-1/tasks")]
    [InlineData("GET", "/api/v1/projects/project-1/knowledge")]
    [InlineData("POST", "/api/v1/chat/completions")]
    [InlineData("GET", "/api/v1/chat/model-routing/models")]
    [InlineData("POST", "/api/v1/models/downloads")]
    [InlineData("GET", "/api/v1/skills")]
    [InlineData("POST", "/api/v1/mobile/skills")]
    [InlineData("POST", "/api/v1/orchestrator/tasks")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/cancel")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/computer-use/approve-once")]
    [InlineData("GET", "/api/v1/orchestrator/tasks/task-1/user-question")]
    [InlineData("POST", "/api/v1/orchestrator/tasks/task-1/user-question/answer")]
    [InlineData("GET", "/api/v1/mobile/artifacts/artifact-1/content")]
    [InlineData("GET", "/api/v1/context/entities/entity-1/neighborhood")]
    [InlineData("POST", "/api/v1/workflows/workflow-1/run")]
    [InlineData("POST", "/api/v1/threed/generate")]
    [InlineData("GET", "/api/v2/mobile/execution-profile")]
    [InlineData("PUT", "/api/v2/mobile/execution-profile/quality")]
    [InlineData("GET", "/api/v2/mobile/observability/dashboard")]
    [InlineData("POST", "/api/v2/mobile/goals")]
    [InlineData("GET", "/api/v2/mobile/goals/goal-1")]
    [InlineData("GET", "/api/v2/mobile/goals/goal-1/versions")]
    [InlineData("POST", "/api/v2/mobile/goals/goal-1/versions")]
    [InlineData("POST", "/api/v2/mobile/goals/tasks")]
    [InlineData("GET", "/api/v2/mobile/goals/tasks/task-1")]
    [InlineData("GET", "/api/v2/mobile/goals/tasks/task-1/verification-records")]
    public async Task Intended_mobile_control_plane_routes_are_allowed(string method, string path)
    {
        DefaultHttpContext context = Context(method, path);
        await Middleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task Unknown_future_endpoint_is_remote_denied_by_default()
    {
        DefaultHttpContext context = Context("GET", "/api/v2/mobile/future-dangerous-capability");
        await Middleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Ordinary_remote_mutations_are_bounded_per_device()
    {
        PairedDeviceAbuseMiddleware middleware = Middleware();
        for (int i = 0; i < PairedDeviceAbuseMiddleware.MutationLimitPerMinute; i++)
        {
            DefaultHttpContext context = Context("POST", "/api/v1/memory");
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }

        DefaultHttpContext rejected = Context("POST", "/api/v1/memory");
        await middleware.InvokeAsync(rejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.True(rejected.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Expensive_remote_work_has_a_tighter_bucket()
    {
        PairedDeviceAbuseMiddleware middleware = Middleware();
        for (int i = 0; i < PairedDeviceAbuseMiddleware.ExpensiveLimitPerMinute; i++)
        {
            DefaultHttpContext context = Context("POST", "/api/v1/chat/completions");
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }

        DefaultHttpContext rejected = Context("POST", "/api/v1/chat/completions");
        await middleware.InvokeAsync(rejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
    }

    [Fact]
    public async Task Emergency_cancel_is_not_charged_to_the_expensive_bucket()
    {
        PairedDeviceAbuseMiddleware middleware = Middleware();
        for (int i = 0; i < PairedDeviceAbuseMiddleware.ExpensiveLimitPerMinute + 10; i++)
        {
            DefaultHttpContext context = Context("POST", "/api/v1/models/downloads/job-1/cancel");
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Buckets_are_independent_for_each_validated_device_identity()
    {
        PairedDeviceAbuseMiddleware middleware = Middleware();
        for (int i = 0; i < PairedDeviceAbuseMiddleware.ExpensiveLimitPerMinute; i++)
        {
            DefaultHttpContext a = Context("POST", "/api/v1/jobs", "device:A");
            await middleware.InvokeAsync(a);
            Assert.Equal(StatusCodes.Status204NoContent, a.Response.StatusCode);
        }

        DefaultHttpContext aRejected = Context("POST", "/api/v1/jobs", "device:A");
        await middleware.InvokeAsync(aRejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, aRejected.Response.StatusCode);

        DefaultHttpContext b = Context("POST", "/api/v1/jobs", "device:B");
        await middleware.InvokeAsync(b);
        Assert.Equal(StatusCodes.Status204NoContent, b.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/chat/completions")]
    [InlineData("/api/v1/orchestrator/tasks")]
    [InlineData("/api/v1/models/downloads")]
    [InlineData("/api/v1/workflows/abc/run")]
    [InlineData("/api/v1/speech/transcribe")]
    [InlineData("/api/v2/mobile/goals/tasks")]
    public void Known_resource_heavy_posts_are_classified_as_expensive(string path)
    {
        DefaultHttpContext context = Context("POST", path);
        Assert.True(PairedDeviceAbuseMiddleware.IsExpensive(context.Request));
    }
}
