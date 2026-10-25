using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Talks to the server-owned Multi-Agent task engine. Desktop-only workspace/worktree/repository
    /// methods stay in this same authenticated Core client so IDE/chat/CLI surfaces converge on one
    /// durable task state; paired devices never receive workstation filesystem paths. Delegation,
    /// user-question pause/resume and manual resume are typed Core operations; local agents never
    /// receive the Core auth token.
    /// </summary>
    public sealed class AgentOrchestratorClient
    {
        public static AgentOrchestratorClient Current { get; } = new AgentOrchestratorClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private AgentOrchestratorClient() { }

        public Task<AgentTaskListResponse> ListAsync(CancellationToken ct) =>
            SendAsync<AgentTaskListResponse>(HttpMethod.Get, "/api/v1/orchestrator/tasks", null, ct);

        public Task<AgentTaskDto> GetAsync(string id, CancellationToken ct) =>
            SendAsync<AgentTaskDto>(HttpMethod.Get, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id), null, ct);

        public Task<AgentTaskDto> CreateAsync(CreateAgentTaskRequest request, CancellationToken ct) =>
            SendAsync<AgentTaskDto>(HttpMethod.Post, "/api/v1/orchestrator/tasks", request, ct);

        /// <summary>
        /// Creates the canonical desktop root coding task through Core: the complete repository
        /// authority set, bounded self-delegation and package-restore profile are assigned by one
        /// server boundary before the worker can freeze execution authority.
        /// </summary>
        public Task<AgentTaskDto> CreateCodingTaskAsync(
            string goal,
            string workspaceRoot,
            string codingPermissionProfile,
            int maxRepairAttempts,
            CancellationToken ct,
            IEnumerable<AddAgentRepositoryAuthorityRequest>? additionalRepositories = null,
            string codingRunPermissionMode = CodingRunPermissionModes.Bypass) =>
            SendAsync<AgentTaskDto>(HttpMethod.Post, "/api/v1/orchestrator/coding-tasks",
                new CreateDesktopCodingTaskRequest
                {
                    Goal = goal,
                    WorkspaceRoot = workspaceRoot,
                    AdditionalRepositories = additionalRepositories?.ToList() ?? new List<AddAgentRepositoryAuthorityRequest>(),
                    CodingPermissionProfile = codingPermissionProfile,
                    CodingRunPermissionMode = CodingRunPermissionModes.Normalize(codingRunPermissionMode),
                    MaxRepairAttempts = maxRepairAttempts
                }, ct);

        public Task<DelegateAgentTaskResponse> DelegateAsync(
            string parentId, DelegateAgentTaskRequest request, CancellationToken ct)
        {
            request.ParentTaskId = parentId;
            return SendAsync<DelegateAgentTaskResponse>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(parentId) + "/delegate", request, ct);
        }

        public Task<DelegateAgentTaskBatchResponse> DelegateBatchAsync(
            string parentId, DelegateAgentTaskBatchRequest request, CancellationToken ct)
        {
            foreach (DelegateAgentTaskRequest child in request.Children)
                child.ParentTaskId = parentId;
            return SendAsync<DelegateAgentTaskBatchResponse>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(parentId) + "/delegate-batch", request, ct);
        }

        /// <summary>
        /// Pauses a continuing parent behind its live delegated children (no-op when none is live).
        /// </summary>
        public Task<AwaitDelegatedChildrenResponse> AwaitDelegatedChildrenAsync(string parentId, CancellationToken ct) =>
            SendAsync<AwaitDelegatedChildrenResponse>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(parentId) + "/await-children", null, ct);

        public Task CancelAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public Task<AgentTaskDto> ResumeAsync(string id, string source, CancellationToken ct) =>
            SendAsync<AgentTaskDto>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/resume",
                new ResumeDesktopAgentTaskRequest { Source = source }, ct);

        public Task<AgentUserQuestionDto> AskUserQuestionAsync(
            string id, AskAgentUserQuestionRequest request, CancellationToken ct) =>
            SendAsync<AgentUserQuestionDto>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/user-question", request, ct);

        public Task<AgentUserQuestionStateDto> GetUserQuestionStateAsync(string id, CancellationToken ct) =>
            SendAsync<AgentUserQuestionStateDto>(HttpMethod.Get,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/user-question", null, ct);

        public Task<AgentUserQuestionStateDto> AnswerUserQuestionAsync(
            string id, AnswerAgentUserQuestionRequest request, CancellationToken ct) =>
            SendAsync<AgentUserQuestionStateDto>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/user-question/answer", request, ct);

        public Task<AgentTaskLogListResponse> GetLogsAsync(string id, CancellationToken ct) =>
            SendAsync<AgentTaskLogListResponse>(HttpMethod.Get, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/logs", null, ct);

        public async Task<IReadOnlyList<string>> ScheduleAsync(CancellationToken ct)
        {
            JsonElement value = await SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/orchestrator/schedule", null, ct)
                .ConfigureAwait(false);
            var result = new List<string>();
            if (value.TryGetProperty("dispatchedTaskIds", out JsonElement ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    string? text = id.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
            }
            return result;
        }

        public Task UpdateStatusAsync(string id, UpdateAgentTaskStatusRequest request, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/status", request, ct);

        public Task AppendLogAsync(string id, string message, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/logs",
                new AppendAgentTaskLogRequest { Message = message }, ct);

        public Task SetWorktreeAsync(string id, string worktreePath, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/worktree",
                new SetAgentTaskWorktreeRequest { WorktreePath = worktreePath }, ct);

        /// <summary>Publishes one section (findings, childTimeline, revert) of the additive review status document.</summary>
        public Task PutReviewStatusSectionAsync(string id, string section, object value, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Put,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/review-status/" + Uri.EscapeDataString(section), value, ct);

        /// <summary>Desktop-owner action: ask the desktop worker to revert exactly the agent's own changes.</summary>
        public Task RequestRevertOwnChangesAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/revert-own-changes", null, ct);

        public Task<AgentDesktopWorktreeDto> GetDesktopWorktreeAsync(string id, CancellationToken ct) =>
            SendAsync<AgentDesktopWorktreeDto>(HttpMethod.Get,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/desktop-worktree", null, ct);

        /// <summary>The worktree appears after a coding task is scheduled; 404 is expected until then.</summary>
        public async Task<AgentDesktopWorktreeDto?> TryGetDesktopWorktreeAsync(string id, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                bootstrap.Endpoint.TrimEnd('/') + "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/desktop-worktree");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Agent orchestrator request failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadFromJsonAsync<AgentDesktopWorktreeDto>(JsonOptions, ct).ConfigureAwait(false)
                ?? throw new LocalAiException("PaRuX Core returned an empty desktop worktree response.");
        }

        public Task<AgentWorkspaceAssignmentDto> SetWorkspaceAssignmentAsync(
            string id, string workspaceRoot, CancellationToken ct) =>
            SendAsync<AgentWorkspaceAssignmentDto>(HttpMethod.Put,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/workspace",
                new SetAgentWorkspaceAssignmentRequest { WorkspaceRoot = workspaceRoot }, ct);

        public async Task<AgentWorkspaceAssignmentDto?> TryGetWorkspaceAssignmentAsync(string id, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                bootstrap.Endpoint.TrimEnd('/') + "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/workspace");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Agent workspace request failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadFromJsonAsync<AgentWorkspaceAssignmentDto>(JsonOptions, ct).ConfigureAwait(false);
        }

        public Task<AgentRepositorySetDto> GetRepositorySetAsync(string id, CancellationToken ct) =>
            SendAsync<AgentRepositorySetDto>(HttpMethod.Get,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/repositories", null, ct);

        /// <summary>
        /// Replaces primary + all aliases in one Core-owned atomic authority update. Use this for
        /// every new multi-repository surface; Add/Remove below exist only for legacy tasks that
        /// have not adopted the complete-set v2 store.
        /// </summary>
        public Task<AgentRepositorySetDto> ReplaceRepositorySetAsync(
            string id,
            string primaryWorkspaceRoot,
            IEnumerable<AddAgentRepositoryAuthorityRequest>? additionalRepositories,
            CancellationToken ct) =>
            SendAsync<AgentRepositorySetDto>(HttpMethod.Put,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/repositories",
                new ReplaceAgentRepositorySetRequest
                {
                    PrimaryWorkspaceRoot = primaryWorkspaceRoot,
                    AdditionalRepositories = additionalRepositories?.ToList() ?? new List<AddAgentRepositoryAuthorityRequest>()
                }, ct);

        public Task<AgentRepositorySetDto> AddRepositoryAsync(
            string id, string alias, string workspaceRoot, CancellationToken ct) =>
            SendAsync<AgentRepositorySetDto>(HttpMethod.Post,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/repositories",
                new AddAgentRepositoryAuthorityRequest { Alias = alias, WorkspaceRoot = workspaceRoot }, ct);

        public Task RemoveRepositoryAsync(string id, string alias, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete,
                "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(id) + "/repositories/" + Uri.EscapeDataString(alias),
                null, ct);

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Agent orchestrator request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Agent orchestrator request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty orchestrator response.");
            return value;
        }
    }
}
