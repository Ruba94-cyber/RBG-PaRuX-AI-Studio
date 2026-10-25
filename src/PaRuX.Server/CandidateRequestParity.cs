using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Request-level parity with the production managed chat path (ChatProxyService.PrepareProviderPayload),
/// version <see cref="Version"/>. Evidence measured for an actual AUTO launch must be produced with the
/// same request shaping a real chat turn receives:
/// <list type="bullet">
/// <item>no temperature/top-p/top-k/min-p/seed override: the launch's own sampling defaults apply, exactly
/// as for a real chat turn (so five fresh runs measure real answer variance, not one replayed seed);</item>
/// <item>reasoning fields from the production adaptive reasoning policy for the same prompt
/// (reasoning_format, reasoning_control, thinking_budget_tokens, enable_thinking, reasoning_effort
/// resolved against the served template's vocabulary);</item>
/// <item>the only declared deviation is a bounded answer: max_tokens = answer bound + thinking budget, so a
/// thinking case keeps the same visible-answer bound as a non-thinking one.</item>
/// </list>
/// Decisions are frozen per prompt into the suite/workload identity. If the user's execution profile would
/// change a decision for this model, evaluation refuses instead of measuring a different request.
/// </summary>
internal static class CandidateRequestParity
{
    internal const string Version = "production-chat-request-v1";

    internal sealed record Decision(string Effort, int Budget);

    /// <summary>The profile-independent production decision for one user prompt.</summary>
    internal static Decision Frozen(string userPrompt)
    {
        string role = AdaptiveReasoningPolicy.InferRole(userPrompt);
        double demand = CandidatePoolRanker.EstimateTaskIntelligenceDemand(userPrompt, role);
        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.Decide(demand, role);
        return new Decision(decision.Effort, decision.Budget);
    }

    /// <summary>Stable digest of the frozen decisions; part of the suite/workload request configuration.</summary>
    internal static string Describe(IEnumerable<string> userPrompts)
    {
        string canonical = string.Join("\n", userPrompts.Select(prompt =>
        {
            Decision decision = Frozen(prompt);
            return decision.Effort + ":" + decision.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Version + "\n" + canonical)))
            .ToLowerInvariant()[..16];
    }

    /// <summary>
    /// Null when the live production decision for <paramref name="modelKey"/> equals the frozen one;
    /// otherwise a bounded refusal reason. The execution profile is the only live input to the decision.
    /// </summary>
    internal static string? CheckLive(string userPrompt, string? modelKey)
    {
        Decision frozen = Frozen(userPrompt);
        AdaptiveReasoningPolicy.Decision live;
        try
        {
            live = AdaptiveReasoningPolicy.ApplyExecutionProfile(
                new AdaptiveReasoningPolicy.Decision(frozen.Effort, frozen.Budget, 0),
                ExecutionProfileStore.Current.Get(modelKey).Effective);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException)
        {
            return "request_parity_profile_unavailable";
        }
        return live.Effort == frozen.Effort && live.Budget == frozen.Budget
            ? null : "request_parity_execution_profile_differs";
    }

    /// <summary>First refusal across every parity case (null when all live decisions equal the frozen ones).</summary>
    internal static string? CheckLive(IEnumerable<CandidateTextQualitySuite.Case> cases, string? modelKey) =>
        cases.Where(item => item.ProductionRequestParity).Select(item => CheckLive(item.Prompt, modelKey))
            .FirstOrDefault(reason => reason is not null);

    /// <summary>Applies the production reasoning fields and the bounded answer budget.</summary>
    internal static void Apply(JsonObject payload, Decision decision, int answerMaxTokens,
        IReadOnlyCollection<string>? supportedEfforts)
    {
        payload["max_tokens"] = checked(answerMaxTokens + Math.Max(0, decision.Budget));
        payload["reasoning_format"] = "auto";
        payload["reasoning_control"] = true;
        payload["thinking_budget_tokens"] = decision.Budget;
        payload["chat_template_kwargs"] = new JsonObject
        {
            ["enable_thinking"] = decision.Budget > 0,
            ["reasoning_effort"] = ReasoningEffortCompatibility.Resolve(decision.Effort, supportedEfforts)
        };
    }

    internal static int MaximumTokens(string userPrompt, int answerMaxTokens) =>
        checked(answerMaxTokens + Math.Max(0, Frozen(userPrompt).Budget));
}
