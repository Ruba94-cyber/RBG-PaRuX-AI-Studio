using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record CodingDelegationSpec(
        string Goal,
        string? Role,
        IReadOnlyList<string>? Capabilities,
        IReadOnlyList<string>? ContextRefs,
        int? MaxRepairAttempts);

    public sealed record CodingDelegationResult(
        bool Success,
        string Message,
        string? ChildTaskId = null,
        IReadOnlyList<string>? ChildTaskIds = null,
        bool ParentContinued = false);

    /// <summary>
    /// Typed desktop bridge for coding-agent self delegation. The local model never receives Core's
    /// auth token or workstation repository paths. Single-child and multi-child delegation both use
    /// the same desktop-only Core batch boundary: Core owns depth/fan-out/deduplication/capability
    /// narrowing, freezes prepared children, inherits repository authority, then atomically pauses
    /// the parent while queueing every prepared child. The client only requests that transaction and
    /// best-effort nudges the scheduler; the hosted Core scheduler remains authoritative if that
    /// immediate nudge fails after a successful commit.
    /// </summary>
    public static class CodingAgentDelegationService
    {
        private static readonly HashSet<string> KnownRoles = new(StringComparer.Ordinal)
        {
            "planner", "research", "review", "documentation", "testing", "security",
            "coding", "binary-analysis", "ui-verification", "computer-use"
        };

        public static async Task<CodingDelegationResult> DelegateAsync(
            string parentTaskId,
            string childGoal,
            string? role,
            IReadOnlyList<string>? capabilities,
            IReadOnlyList<string>? contextRefs,
            int? maxRepairAttempts,
            CancellationToken ct,
            bool continueParent = false)
        {
            CodingDelegationSpec spec = new(childGoal, role, capabilities, contextRefs, maxRepairAttempts);
            if (!TryNormalizeSpec(parentTaskId, spec, out DelegateAgentTaskRequest? request, out string error))
                return new CodingDelegationResult(false, error);

            try
            {
                DelegateAgentTaskBatchResponse delegated = await AgentOrchestratorClient.Current.DelegateBatchAsync(
                    parentTaskId,
                    new DelegateAgentTaskBatchRequest
                    {
                        Children = new List<DelegateAgentTaskRequest> { request! },
                        ContinueParent = continueParent
                    },
                    ct).ConfigureAwait(false);

                DelegateAgentTaskResponse? child = delegated.Children.SingleOrDefault();
                if (child == null || string.IsNullOrWhiteSpace(child.ChildTaskId))
                    return new CodingDelegationResult(false, "Core returned no child id for coding delegation.");

                string childId = child.ChildTaskId;
                await KickSchedulerBestEffortAsync().ConfigureAwait(false);
                return new CodingDelegationResult(
                    true,
                    delegated.DelegationSummary + (delegated.ParentContinued
                        ? " Core committed the read-only child for scheduling and left this parent running."
                        : " Core atomically paused the parent and committed the child for scheduling."),
                    childId,
                    new[] { childId },
                    delegated.ParentContinued);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or LocalAiException or IOException or UnauthorizedAccessException)
            {
                return new CodingDelegationResult(false, "Core rejected coding delegation: " + ex.Message);
            }
        }

        public static async Task<CodingDelegationResult> DelegateManyAsync(
            string parentTaskId,
            IReadOnlyList<CodingDelegationSpec>? children,
            CancellationToken ct,
            bool continueParent = false)
        {
            if (string.IsNullOrWhiteSpace(parentTaskId))
                return new CodingDelegationResult(false, "Batch self-delegation is available only inside a durable orchestrator coding task.");
            if (children == null || children.Count < 2)
                return new CodingDelegationResult(false, "delegate_tasks requires at least two independent children; use delegate_task for one child.");
            if (children.Count > 6)
                return new CodingDelegationResult(false, "delegate_tasks may request at most six direct children.");

            var requests = new List<DelegateAgentTaskRequest>(children.Count);
            foreach (CodingDelegationSpec child in children)
            {
                if (!TryNormalizeSpec(parentTaskId, child, out DelegateAgentTaskRequest? request, out string error))
                    return new CodingDelegationResult(false, error);
                requests.Add(request!);
            }

            try
            {
                DelegateAgentTaskBatchResponse delegated = await AgentOrchestratorClient.Current.DelegateBatchAsync(
                    parentTaskId,
                    new DelegateAgentTaskBatchRequest { Children = requests, ContinueParent = continueParent },
                    ct).ConfigureAwait(false);

                List<string> childIds = delegated.Children
                    .Select(child => child.ChildTaskId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (childIds.Count != delegated.Children.Count || childIds.Count == 0)
                    return new CodingDelegationResult(false, "Core returned an invalid child-id set for batch delegation.");

                await KickSchedulerBestEffortAsync().ConfigureAwait(false);
                return new CodingDelegationResult(
                    true,
                    delegated.DelegationSummary + (delegated.ParentContinued
                        ? " Core committed all read-only children for scheduling and left this parent running."
                        : " Core atomically paused the parent and committed all delegated children for scheduling."),
                    childIds[0],
                    childIds,
                    delegated.ParentContinued);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or LocalAiException or IOException or UnauthorizedAccessException)
            {
                return new CodingDelegationResult(false, "Core rejected coding batch delegation: " + ex.Message);
            }
        }

        /// <summary>
        /// Delegation is already durable and committed when this runs. The local scheduler kick is
        /// latency-only; never report a committed delegation as failed merely because this optional
        /// request lost a race with shutdown/restart. AgentSchedulerHostedService will retry on its
        /// normal pass after Core is available again.
        /// </summary>
        private static async Task KickSchedulerBestEffortAsync()
        {
            try
            {
                _ = await AgentOrchestratorClient.Current.ScheduleAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or LocalAiException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                _ = ex; // Correctness is owned by the hosted Core scheduler, not this latency hint.
            }
        }

        private static bool TryNormalizeSpec(
            string parentTaskId,
            CodingDelegationSpec spec,
            out DelegateAgentTaskRequest? request,
            out string error)
        {
            request = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(parentTaskId))
            {
                error = "Self-delegation is available only inside a durable orchestrator coding task.";
                return false;
            }

            string goal = (spec.Goal ?? string.Empty).Trim();
            if (goal.Length == 0)
            {
                error = "Delegated child goal must be non-empty.";
                return false;
            }
            if (goal.Length > 4_000)
            {
                error = "Delegated child goal exceeds the 4,000 character bound.";
                return false;
            }

            string? role = string.IsNullOrWhiteSpace(spec.Role) ? null : spec.Role.Trim().ToLowerInvariant();
            if (role != null && !KnownRoles.Contains(role))
            {
                error = "Unsupported delegated role '" + role + "'.";
                return false;
            }

            List<string>? capabilities = spec.Capabilities?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();
            List<string>? refs = spec.ContextRefs?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            request = new DelegateAgentTaskRequest
            {
                ParentTaskId = parentTaskId,
                Goal = goal,
                Role = role,
                GrantedCapabilities = capabilities,
                ContextRefs = refs,
                MaxRepairAttempts = Math.Clamp(spec.MaxRepairAttempts ?? 1, 0, 3)
            };
            return true;
        }
    }
}
