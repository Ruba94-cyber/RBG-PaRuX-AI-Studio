using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Thin Windows client for the server-owned PaRuX Project Manager. Desktop creation goes through
    /// a loopback-only endpoint so the current coding permission profile can be copied atomically
    /// into coding tasks before the background scheduler sees them. Mobile keeps the ordinary
    /// Project endpoint and therefore remains offline by default.
    /// </summary>
    public sealed class ProjectManagerClient
    {
        public static ProjectManagerClient Current { get; } = new ProjectManagerClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(2));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private ProjectManagerClient() { }

        public Task<ProjectDto> CreateAsync(string goal, CancellationToken ct) => CreateAsync(goal, null, ct);

        public Task<ProjectDto> CreateAsync(string goal, string? workspaceRoot, CancellationToken ct) =>
            SendAsync<ProjectDto>(HttpMethod.Post, "/api/v1/projects/desktop",
                new CreateDesktopProjectRequest
                {
                    Goal = goal,
                    WorkspaceRoot = workspaceRoot,
                    CodingPermissionProfile = CodingPermissionProfile.Normalize(
                        SettingsStore.Current.Data.CodingPermissionProfile)
                }, ct);

        /// <summary>
        /// Attach an already-created durable agent task to a Project through the server-owned
        /// Context Graph. This is used by Chat/CLI-style task creation paths so Project knowledge
        /// sees the same task instead of only tasks born inside ProjectManagerService.PlanAsync.
        /// </summary>
        public Task<ProjectDto> AttachTaskAsync(string projectId, string taskId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("A project id is required.", nameof(projectId));
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("A task id is required.", nameof(taskId));
            return SendAsync<ProjectDto>(HttpMethod.Post,
                "/api/v1/projects/" + Uri.EscapeDataString(projectId) + "/tasks",
                new AttachProjectTaskRequest { TaskId = taskId }, ct);
        }

        public Task<ContextEntityListResponse> ListAsync(CancellationToken ct) =>
            SendAsync<ContextEntityListResponse>(HttpMethod.Get, "/api/v1/context/entities?type=project", null, ct);

        public Task<ContextNeighborhoodResponse> GetNeighborhoodAsync(string entityId, CancellationToken ct) =>
            SendAsync<ContextNeighborhoodResponse>(HttpMethod.Get,
                "/api/v1/context/entities/" + Uri.EscapeDataString(entityId) + "/neighborhood", null, ct);

        public async Task<List<ProjectDto>> ListProjectsAsync(CancellationToken ct)
        {
            ContextEntityListResponse entities = await ListAsync(ct).ConfigureAwait(false);
            var projects = new List<ProjectDto>();
            foreach (ContextEntityDto entity in entities.Entities
                .Where(e => string.Equals(e.Source, "project-manager", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                ContextNeighborhoodResponse neighborhood = await GetNeighborhoodAsync(entity.Id, ct).ConfigureAwait(false);
                projects.Add(ToProject(entity, neighborhood));
            }
            return projects.OrderByDescending(p => p.UpdatedUtc).ToList();
        }

        public async Task<ProjectDto> GetProjectAsync(string projectId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("A project id is required.", nameof(projectId));
            ContextNeighborhoodResponse neighborhood = await GetNeighborhoodAsync(projectId, ct).ConfigureAwait(false);
            if (!string.Equals(neighborhood.Center.EntityType, "project", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(neighborhood.Center.Source, "project-manager", StringComparison.OrdinalIgnoreCase))
                throw new LocalAiException("The requested Context Graph entity is not a PaRuX Project workspace.");
            return ToProject(neighborhood.Center, neighborhood);
        }

        private static ProjectDto ToProject(ContextEntityDto entity, ContextNeighborhoodResponse neighborhood)
        {
            var taskIds = new List<string>();
            foreach (ContextRelationDto relation in neighborhood.OutgoingRelations
                .Where(r => string.Equals(r.RelationType, "CONTAINS", StringComparison.OrdinalIgnoreCase)))
            {
                ContextEntityDto? target = neighborhood.NeighborEntities.FirstOrDefault(e => e.Id == relation.ToEntityId);
                if (target == null || !string.Equals(target.EntityType, "task", StringComparison.OrdinalIgnoreCase)) continue;
                string? taskId = TryGetString(target?.MetadataJson, "orchestratorTaskId");
                if (!string.IsNullOrWhiteSpace(taskId)) taskIds.Add(taskId);
            }
            return new ProjectDto
            {
                Id = entity.Id,
                Goal = TryGetString(entity.MetadataJson, "goal") ?? entity.Name,
                TaskIds = taskIds.Distinct(StringComparer.Ordinal).ToList(),
                CreatedUtc = entity.CreatedUtc,
                UpdatedUtc = entity.UpdatedUtc
            };
        }

        private static string? TryGetString(string? json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty(propertyName, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException) { return null; }
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
                throw new LocalAiException("Project Manager request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Project Manager response.");
            return value;
        }
    }
}
