using System.Collections.Concurrent;

namespace PaRuX.Server;

/// <summary>
/// Per-device authorization + abuse boundary applied only after the global auth middleware has
/// validated a paired-device token and stamped PaRuXOwner=device:&lt;id&gt;. Identity does not grant
/// desktop-equivalent privilege: PairedDeviceEndpointPolicy is checked for every paired-device
/// request before rate limiting or endpoint execution. Desktop/loopback traffic remains outside
/// this remote capability boundary.
/// </summary>
public sealed class PairedDeviceAbuseMiddleware
{
    internal const int MaxConcurrentMutations = 6;
    internal const int MutationLimitPerMinute = 180;
    internal const int ExpensiveLimitPerMinute = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, DeviceBudget> Budgets = new(StringComparer.Ordinal);

    private readonly RequestDelegate _next;

    public PairedDeviceAbuseMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        string? owner = context.Items["PaRuXOwner"] as string;
        if (owner == null || !owner.StartsWith("device:", StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        string deviceId = owner["device:".Length..];
        if (deviceId.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A validated paired-device identity is required." });
            return;
        }

        // Backward-compatible Android clients historically used the generic task-status endpoint
        // for Approve once. Never forward that remote request to the generic state mutation.
        // Instead reinterpret this one path as the narrow atomic Computer Use approval operation.
        if (PairedDeviceEndpointPolicy.IsLegacyComputerUseApprovalPath(context.Request.Method, context.Request.Path))
        {
            string? taskId = PairedDeviceEndpointPolicy.ExtractOneId(
                context.Request.Path.Value ?? string.Empty,
                "/api/v1/orchestrator/tasks",
                "/status");
            if (taskId == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            DeviceBudget approvalBudget = Budgets.GetOrAdd(deviceId, _ => new DeviceBudget(DateTime.UtcNow));
            int approvalActive = Interlocked.Increment(ref approvalBudget.ActiveMutations);
            if (approvalActive > MaxConcurrentMutations)
            {
                Interlocked.Decrement(ref approvalBudget.ActiveMutations);
                await RejectAsync(context, "Too many concurrent paired-device mutations are in flight.", 1);
                return;
            }
            try
            {
                if (!approvalBudget.TryConsume(expensive: false, DateTime.UtcNow, out int retryAfter))
                {
                    await RejectAsync(context, "This paired device is sending mutations too quickly.", retryAfter);
                    return;
                }

                RemoteComputerUseApprovalResult result = new RemoteComputerUseApprovalService().ApproveOnce(taskId, deviceId);
                context.Response.StatusCode = result switch
                {
                    RemoteComputerUseApprovalResult.Approved => StatusCodes.Status204NoContent,
                    RemoteComputerUseApprovalResult.NotFound => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status409Conflict
                };
                if (result == RemoteComputerUseApprovalResult.NotPending)
                    await context.Response.WriteAsJsonAsync(new { error = "This task is not awaiting one-task Computer Use approval." });
                return;
            }
            finally
            {
                Interlocked.Decrement(ref approvalBudget.ActiveMutations);
            }
        }

        if (!PairedDeviceEndpointPolicy.IsAllowed(context.Request.Method, context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This Core capability is desktop-only. Paired devices may use only the explicit PaRuX mobile control plane."
            });
            return;
        }

        if (!IsMutation(context.Request.Method))
        {
            await _next(context);
            return;
        }

        DeviceBudget budget = Budgets.GetOrAdd(deviceId, _ => new DeviceBudget(DateTime.UtcNow));
        int active = Interlocked.Increment(ref budget.ActiveMutations);
        if (active > MaxConcurrentMutations)
        {
            Interlocked.Decrement(ref budget.ActiveMutations);
            await RejectAsync(context, "Too many concurrent paired-device mutations are in flight.", 1);
            return;
        }

        try
        {
            bool expensive = IsExpensive(context.Request);
            if (!budget.TryConsume(expensive, DateTime.UtcNow, out int retryAfterSeconds))
            {
                await RejectAsync(
                    context,
                    expensive
                        ? "This paired device is starting local AI/agent work too quickly."
                        : "This paired device is sending mutations too quickly.",
                    retryAfterSeconds);
                return;
            }

            await _next(context);
        }
        finally
        {
            Interlocked.Decrement(ref budget.ActiveMutations);
        }
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    internal static bool IsExpensive(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        string path = request.Path.Value?.TrimEnd('/') ?? string.Empty;

        if (path.Equals("/api/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/orchestrator/tasks", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/projects", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/jobs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/research", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/presentations", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/avatar/generate", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/threed/generate", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/speech/transcribe", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/speech/synthesize", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v2/speech/synthesize/stream", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v2/mobile/goals/tasks", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/v1/autopilot/runs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/v1/models/downloads", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/v1/workflows/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/run", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task RejectAsync(HttpContext context, string message, int retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = Math.Max(1, retryAfterSeconds).ToString();
        await context.Response.WriteAsJsonAsync(new { error = message });
    }

    internal static void ResetForTests() => Budgets.Clear();

    private sealed class DeviceBudget
    {
        public int ActiveMutations;
        private readonly object _gate = new();
        private DateTime _windowStartedUtc;
        private int _mutations;
        private int _expensive;

        public DeviceBudget(DateTime now) => _windowStartedUtc = now;

        public bool TryConsume(bool expensive, DateTime now, out int retryAfterSeconds)
        {
            lock (_gate)
            {
                TimeSpan age = now - _windowStartedUtc;
                if (age >= Window || age < TimeSpan.Zero)
                {
                    _windowStartedUtc = now;
                    _mutations = 0;
                    _expensive = 0;
                    age = TimeSpan.Zero;
                }

                _mutations++;
                if (expensive) _expensive++;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((Window - age).TotalSeconds));
                return _mutations <= MutationLimitPerMinute
                    && (!expensive || _expensive <= ExpensiveLimitPerMinute);
            }
        }
    }
}