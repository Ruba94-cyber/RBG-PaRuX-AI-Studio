using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Fixed local text workload; its hash binds the request and sampling contract.</summary>
internal static class CandidateTextPerformanceWorkload
{
    internal const string Id = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId;
    internal const int Version = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion;
    internal const int Repetitions = 3;
    internal const string SystemPrompt = "Answer the user's request in one concise paragraph.";
    internal const string Prompt = "Explain why a local application should verify a downloaded model file before loading it.";
    internal const int MaxTokens = 96;
    // v3: production request parity (see CandidateRequestParity); the frozen reasoning decision is bound here.
    internal static string RequestConfiguration { get; } = "request-parity=" + CandidateRequestParity.Version
        + "|sampling=launch-defaults|seed=none|answer-max-tokens=96|max-tokens=answer+thinking-budget|stream=true|usage=include|serial=3|timings=required"
        + "|reasoning=" + CandidateRequestParity.Describe(new[] { Prompt });
    /// <summary>Upper bound on generated tokens for one sample: answer bound plus the frozen thinking budget.</summary>
    internal static int MaximumGeneratedTokens => CandidateRequestParity.MaximumTokens(Prompt, MaxTokens);

    internal static string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Id, Version, Repetitions, SystemPrompt, Prompt, RequestConfiguration)))).ToLowerInvariant();
    internal static string WorkloadFingerprint { get; } = "performance-workload-sha256:" + Sha256;
}
