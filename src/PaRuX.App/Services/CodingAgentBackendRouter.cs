using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Backends use the identical durable coding task, workspace, stop token and
    /// independent verification path. There is deliberately no mechanism for a
    /// backend to issue new workspace or network grants.
    /// </summary>
    internal interface ICodingAgentExecutionBackend
    {
        string Id { get; }

        Task<CodingAgentResult> RunAsync(
            string workspaceRoot, string task, Action<string> onStep,
            CancellationToken cancellationToken, string sessionId, string permissionMode);
    }

    internal sealed class NativeCodingAgentExecutionBackend : ICodingAgentExecutionBackend
    {
        public string Id => "native";

        public Task<CodingAgentResult> RunAsync(
            string workspaceRoot, string task, Action<string> onStep,
            CancellationToken cancellationToken, string sessionId, string permissionMode) =>
            CodingAgentService.RunAsync(
                workspaceRoot, task, onStep, cancellationToken, sessionId: sessionId,
                permissionMode: permissionMode);
    }

    internal static class CodingAgentBackendRouter
    {
        private static readonly ICodingAgentExecutionBackend Native =
            new NativeCodingAgentExecutionBackend();

        internal static async Task<CodingAgentResult> RunAsync(
            string workspaceRoot, string task, Action<string> onStep,
            CancellationToken cancellationToken, string sessionId, string permissionMode)
        {
            // Disabled-by-default, local-operator-only development switch. This is
            // not a per-task grant, and cannot be used to bypass Core authority.
            var selected = CodingAgentBackendSelectionPolicy.Select(
                Environment.GetEnvironmentVariable("PARUX_CODING_EXECUTION_BACKEND"));
            if (!selected.Allowed)
                throw new InvalidOperationException(selected.Reason);
            onStep?.Invoke("Coding execution backend: " + Native.Id
                + "; workspace and verification remain managed by PaRuX.");
            return await Native.RunAsync(
                workspaceRoot, task, onStep, cancellationToken, sessionId, permissionMode)
                .ConfigureAwait(false);
        }
    }
}
