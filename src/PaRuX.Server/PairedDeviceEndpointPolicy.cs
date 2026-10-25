namespace PaRuX.Server;

/// <summary>
/// Authoritative LAN capability boundary for paired Android clients. A paired-device token proves
/// identity, not desktop-equivalent privilege: every Core endpoint is remote-denied by default and
/// must be explicitly listed here before Android can reach it. This prevents newly-added runtime,
/// filesystem, backup, MCP, local-API, installer or performance endpoints from silently becoming
/// remotely callable just because they share the authenticated Core process.
///
/// Android intentionally tracks a safe subset of the Windows product surface. New V2 parity uses
/// /api/v2/mobile projections so workstation paths, per-model overrides and privileged stores never
/// cross the LAN boundary merely because Android needs the same high-level feature.
/// </summary>
internal static class PairedDeviceEndpointPolicy
{
    internal static bool IsAllowed(string method, PathString requestPath)
    {
        string path = Normalize(requestPath.Value);
        method = (method ?? string.Empty).ToUpperInvariant();

        return method switch
        {
            "GET" => IsAllowedGet(path),
            "POST" => IsAllowedPost(path),
            "PUT" => IsAllowedPut(path),
            "DELETE" => IsAllowedDelete(path),
            _ => false
        };
    }

    internal static bool IsLegacyComputerUseApprovalPath(string method, PathString requestPath) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && MatchesSuffixAfterOneId(Normalize(requestPath.Value), "/api/v1/orchestrator/tasks", "/status");

    internal static string? ExtractOneId(string requestPath, string prefix, string suffix)
    {
        string path = Normalize(requestPath);
        prefix = prefix.TrimEnd('/');
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string remainder = path[(prefix.Length + 1)..];
        string id = remainder[..^suffix.Length].TrimEnd('/');
        return id.Length is > 0 and <= 160 && id.IndexOf('/') < 0 ? id : null;
    }

    private static bool IsAllowedGet(string path)
    {
        if (Exact(path,
            "/api/v1/health",
            "/api/v1/ai/profile",
            "/api/v1/conversations",
            "/api/v1/jobs",
            "/api/v1/internet",
            "/api/v1/memory",
            "/api/v1/rag/sources",
            "/api/v1/speech/engine",
            "/api/v1/models/search",
            "/api/v1/models/budget",
            "/api/v1/models/downloads",
            "/api/v1/avatar/status",
            "/api/v1/threed/engine",
            "/api/v1/skills",
            "/api/v1/orchestrator/tasks",
            "/api/v1/projects",
            "/api/v1/workflows",
            "/api/v1/observability/timeline",
            "/api/v1/automations",
            "/api/v1/context/entities",
            "/api/v1/chat/model-routing",
            "/api/v1/chat/model-routing/tuning",
            "/api/v1/chat/model-routing/models",
            "/api/v1/system/stop-all/status",
            "/api/v2/mobile/execution-profile",
            "/api/v2/mobile/observability/dashboard")) return true;

        return MatchesOneId(path, "/api/v1/conversations")
            || MatchesOneId(path, "/api/v1/jobs")
            || IsMediaJobPath(path)
            || MatchesOneId(path, "/api/v1/orchestrator/tasks")
            || MatchesOneId(path, "/api/v1/autopilot/runs")
            || MatchesSuffixAfterOneId(path, "/api/v1/orchestrator/tasks", "/logs")
            || MatchesSuffixAfterOneId(path, "/api/v1/orchestrator/tasks", "/user-question")
            || MatchesSuffixAfterOneId(path, "/api/v1/projects", "/knowledge")
            || MatchesSuffixAfterOneId(path, "/api/v1/context/entities", "/neighborhood")
            || path.Equals("/api/v1/mobile/artifacts", StringComparison.OrdinalIgnoreCase)
            || MatchesOneId(path, "/api/v1/mobile/artifacts", "/content")
            || MatchesOneId(path, "/api/v2/mobile/goals")
            || MatchesSuffixAfterOneId(path, "/api/v2/mobile/goals", "/versions")
            || MatchesOneId(path, "/api/v2/mobile/goals/tasks")
            || MatchesSuffixAfterOneId(path, "/api/v2/mobile/goals/tasks", "/verification-records");
    }

    // /api/v1/media/{id} is a job id; these literal segments are desktop-owner resources and are never job ids.
    private static readonly string[] MediaLiteralSegments =
        { "loras", "comfy-workflows", "checkpoints", "companions", "workflows" };

    private static bool IsMediaJobPath(string path) =>
        MatchesOneId(path, "/api/v1/media")
        && !MediaLiteralSegments.Any(s => path.Equals("/api/v1/media/" + s, StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedPost(string path)
    {
        if (Exact(path,
            "/api/v1/chat/completions",
            "/api/v1/ai/profile",
            "/api/v1/jobs",
            "/api/v1/internet",
            "/api/v1/memory",
            "/api/v1/research",
            "/api/v1/presentations",
            "/api/v1/speech/synthesize",
            "/api/v1/speech/transcribe",
            "/api/v2/speech/synthesize/stream",
            "/api/v1/models/downloads",
            "/api/v1/avatar/generate",
            "/api/v1/threed/generate",
            "/api/v1/mobile/skills",
            "/api/v1/orchestrator/tasks",
            "/api/v1/autopilot/runs",
            "/api/v1/system/stop-all",
            "/api/v1/system/resume-automations",
            "/api/v1/projects",
            "/api/v1/chat/model-routing",
            "/api/v1/chat/model-routing/tuning",
            "/api/v2/mobile/goals",
            "/api/v2/mobile/goals/tasks")) return true;

        return MatchesSuffixAfterOneId(path, "/api/v1/jobs", "/cancel")
            || MatchesSuffixAfterOneId(path, "/api/v1/rag/sources", "/reindex")
            || MatchesSuffixAfterOneId(path, "/api/v1/models/downloads", "/cancel")
            || MatchesSuffixAfterOneId(path, "/api/v1/skills", "/enabled")
            || MatchesSuffixAfterOneId(path, "/api/v1/orchestrator/tasks", "/cancel")
            || MatchesSuffixAfterOneId(path, "/api/v1/orchestrator/tasks", "/computer-use/approve-once")
            || MatchesSuffixAfterOneId(path, "/api/v1/orchestrator/tasks", "/user-question/answer")
            || MatchesSuffixAfterOneId(path, "/api/v1/autopilot/runs", "/approve")
            || MatchesSuffixAfterOneId(path, "/api/v1/projects", "/tasks")
            || MatchesSuffixAfterOneId(path, "/api/v1/workflows", "/run")
            || MatchesSuffixAfterOneId(path, "/api/v1/automations", "/enabled")
            || MatchesSuffixAfterOneId(path, "/api/v2/mobile/goals", "/versions");
    }

    private static bool IsAllowedPut(string path) =>
        path.Equals("/api/v2/mobile/execution-profile/quality", StringComparison.OrdinalIgnoreCase)
        || MatchesOneId(path, "/api/v1/conversations");

    private static bool IsAllowedDelete(string path) =>
        path.Equals("/api/v1/chat/model-routing/tuning", StringComparison.OrdinalIgnoreCase)
        || MatchesOneId(path, "/api/v1/conversations")
        || MatchesOneId(path, "/api/v1/memory")
        || MatchesOneId(path, "/api/v1/rag/sources")
        || MatchesOneId(path, "/api/v1/skills")
        || MatchesOneId(path, "/api/v1/automations");

    private static bool Exact(string path, params string[] allowed) =>
        allowed.Any(candidate => path.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesOneId(string path, string prefix, string suffix = "")
    {
        prefix = prefix.TrimEnd('/');
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
        string remainder = path[(prefix.Length + 1)..];
        if (suffix.Length == 0)
            return remainder.Length is > 0 and <= 160 && remainder.IndexOf('/') < 0;
        if (!remainder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string id = remainder[..^suffix.Length].TrimEnd('/');
        return id.Length is > 0 and <= 160 && id.IndexOf('/') < 0;
    }

    private static bool MatchesSuffixAfterOneId(string path, string prefix, string suffix) =>
        MatchesOneId(path, prefix, suffix);

    private static string Normalize(string? value)
    {
        string path = string.IsNullOrWhiteSpace(value) ? "/" : value!;
        if (path.Length > 1) path = path.TrimEnd('/');
        return path;
    }
}
