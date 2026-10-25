using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Conservative gate for llama.cpp prompt/KV reuse. PaRuX never persists KV state itself and
/// never exposes llama-server's mutable /slots API. Reuse is permitted only when two consecutive
/// managed-runtime requests resolve to the exact same opaque scope identity.
///
/// Scope identity includes Project + Conversation (or a hashed legacy prompt prefix) AND the real
/// runtime identity: text-process PID, active model/profile/role and active managed CUDA/Vulkan
/// build tags. A model swap, runtime restart, build switch, Project switch or Conversation switch
/// therefore makes the boundary turn cold even though the provider still lives on the same port
/// and Chat's provider-facing logical model id remains "local-default".
///
/// When "Optimize this PC" has measured prompt-cache behavior for the exact active
/// model/runtime/GPU/workload identity, an explicit negative recommendation disables reuse. No
/// benchmark evidence remains neutral and preserves the historical conservative behavior.
///
/// This is deliberately single-active-scope because PaRuX currently launches llama-server with
/// --parallel 1. Switching scope therefore disables reuse for the boundary turn; only a following
/// turn in the same complete scope may reuse the previous KV prefix.
/// </summary>
public sealed class PromptCacheScopePolicy
{
    public const int ReuseChunkTokens = 256;

    /// <summary>
    /// Reserved ChatCompletionRequest.SkillParameters metadata key used by first-party clients to
    /// carry the opaque active Project id for cache isolation. It never reaches the model provider:
    /// BuildProviderPayload serializes only role/content/images. Normal Skill parameters may use
    /// any non-reserved key as before.
    /// </summary>
    public const string ProjectScopeParameter = "__parux_project_scope";

    private readonly object _gate = new();
    private readonly PromptCacheBenefitPolicy _benefit = new();
    private string? _lastScopeKey;

    /// <summary>Production path: captures runtime identity fresh for every provider request. This
    /// is important for the refusal fallback, which may restart/swap the model between the first
    /// request and the retry without recreating ChatProxyService or this policy instance.</summary>
    public PromptCacheDecision Decide(ChatCompletionRequest request, string providerIdentity)
    {
        string runtimeIdentity = CaptureRuntimeIdentity();
        return Decide(request, providerIdentity, runtimeIdentity, _benefit.Resolve(runtimeIdentity));
    }

    /// <summary>Deterministic overload for tests and diagnostics. A null empirical recommendation
    /// is neutral; false is an explicit measured veto; true allows the normal same-scope rule.</summary>
    internal PromptCacheDecision Decide(
        ChatCompletionRequest request,
        string providerIdentity,
        string runtimeIdentity,
        bool? empiricallyRecommended = null)
    {
        string current = BuildScopeKey(request, providerIdentity, runtimeIdentity);
        lock (_gate)
        {
            bool sameScope = _lastScopeKey != null
                && string.Equals(_lastScopeKey, current, StringComparison.Ordinal);
            bool reuse = sameScope && empiricallyRecommended != false;
            _lastScopeKey = current;
            return new PromptCacheDecision(
                current,
                reuse,
                reuse ? ReuseChunkTokens : 0,
                empiricallyRecommended);
        }
    }

    internal static string BuildScopeKey(
        ChatCompletionRequest request,
        string providerIdentity,
        string runtimeIdentity = "test-runtime")
    {
        string conversation = NormalizeOpaque(request.ConversationId);
        string project = request.SkillParameters.TryGetValue(ProjectScopeParameter, out string? projectValue)
            ? NormalizeOpaque(projectValue)
            : string.Empty;

        // Project is intentionally independent from Conversation. The same conversation reopened
        // under another active Project must not inherit KV created from the previous Project's
        // bounded knowledge pack.
        string projectScope = project.Length == 0 ? "project:none" : "project:" + Hash(project);
        string conversationScope;
        if (conversation.Length > 0)
        {
            conversationScope = "conversation:" + Hash(conversation);
        }
        else
        {
            ChatCompletionMessage? system = request.Messages.FirstOrDefault(message =>
                string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase));
            ChatCompletionMessage? firstUser = request.Messages.FirstOrDefault(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            conversationScope = "prefix:" + Hash(
                (system?.Content ?? string.Empty) + "\n<first-user>\n" + (firstUser?.Content ?? string.Empty));
        }

        return Hash((providerIdentity ?? string.Empty).TrimEnd('/')
            + "|logical-model=" + (request.Model ?? string.Empty)
            + "|runtime=" + (runtimeIdentity ?? string.Empty)
            + "|" + projectScope
            + "|" + conversationScope);
    }

    /// <summary>
    /// Exact-enough identity for a process-local KV cache. PID is included deliberately: even if
    /// the same model/build/profile restarts, the new llama-server has no relation to the previous
    /// process's in-memory KV. Including both managed build tags also invalidates reuse when an
    /// update/rollback changes the runtime selection before the next launch.
    /// </summary>
    internal static string CaptureRuntimeIdentity()
    {
        string pid = ReadTrimmed(LocalAiPaths.TextPidFile);
        string activeRole = ReadTrimmed(LocalAiPaths.ActiveRoleFile);
        string activeModel = ReadTrimmed(LocalAiPaths.ActiveModelPathFile);
        string activeProfile = ReadTrimmed(LocalAiPaths.ActiveProfileFile);
        string cudaTag = SafeBuildTag("text-cuda", LocalAiPaths.DefaultCudaBuildTag);
        string vulkanTag = SafeBuildTag("text-vulkan", LocalAiPaths.DefaultVulkanBuildTag);

        return "pid=" + pid
            + "|role=" + activeRole
            + "|model=" + activeModel
            + "|profile=" + activeProfile
            + "|cuda=" + cudaTag
            + "|vulkan=" + vulkanTag;
    }

    private static string SafeBuildTag(string backend, string fallback)
    {
        try { return LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? fallback; }
        catch { return fallback; }
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch { return string.Empty; }
    }

    private static string NormalizeOpaque(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        // IDs are opaque cache-partition metadata, not filesystem paths or prompts. Hashing an
        // oversized value would still be safe, but bounding it prevents a hostile paired client
        // from turning a cache-key calculation into unbounded string work.
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record PromptCacheDecision(
    string ScopeKey,
    bool Reuse,
    int ReuseChunkTokens,
    bool? EmpiricallyRecommended = null);
