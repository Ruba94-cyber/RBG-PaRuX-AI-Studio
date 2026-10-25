using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Durable Project↔Artifact scope using the Context Graph, which is already the Project
    /// Manager's source of truth. ArtifactStore remains the artifact/version source of truth;
    /// the graph stores only a small artifact entity containing artifactId plus a CONTAINS edge
    /// from the project. This avoids a second artifact database/schema and makes scope inspectable.
    /// </summary>
    public sealed class ProjectArtifactBridge
    {
        public static ProjectArtifactBridge Current { get; } = new ProjectArtifactBridge();
        private ProjectArtifactBridge() { }

        public async Task LinkAsync(string projectId, ArtifactDto artifact, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("A project id is required.", nameof(projectId));

            ContextNeighborhoodResponse project = await ContextGraphClient.Current.GetNeighborhoodAsync(projectId, ct).ConfigureAwait(false);
            ContextEntityDto? entity = project.NeighborEntities.FirstOrDefault(e =>
                string.Equals(e.EntityType, "artifact", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadArtifactId(e.MetadataJson), artifact.Id, StringComparison.Ordinal));

            if (entity == null)
            {
                ContextEntityListResponse allArtifacts = await ContextGraphClient.Current.ListEntitiesAsync("artifact", ct).ConfigureAwait(false);
                entity = allArtifacts.Entities.FirstOrDefault(e =>
                    string.Equals(e.Source, "artifact-workspace", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadArtifactId(e.MetadataJson), artifact.Id, StringComparison.Ordinal));
            }

            if (entity == null)
            {
                entity = await ContextGraphClient.Current.CreateEntityAsync(new CreateContextEntityRequest
                {
                    EntityType = "artifact",
                    Name = artifact.Title,
                    Source = "artifact-workspace",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        artifactId = artifact.Id,
                        artifactType = artifact.ArtifactType
                    })
                }, ct).ConfigureAwait(false);
            }

            project = await ContextGraphClient.Current.GetNeighborhoodAsync(projectId, ct).ConfigureAwait(false);
            bool alreadyLinked = project.OutgoingRelations.Any(r =>
                r.ToEntityId == entity.Id && string.Equals(r.RelationType, "CONTAINS", StringComparison.OrdinalIgnoreCase));
            if (alreadyLinked) return;

            await ContextGraphClient.Current.CreateRelationAsync(new CreateContextRelationRequest
            {
                FromEntityId = projectId,
                ToEntityId = entity.Id,
                RelationType = "CONTAINS",
                Source = "project-artifact-bridge",
                MetadataJson = JsonSerializer.Serialize(new { artifactId = artifact.Id })
            }, ct).ConfigureAwait(false);
        }

        public async Task<List<ArtifactDto>> ListForProjectAsync(string projectId, CancellationToken ct)
        {
            ContextNeighborhoodResponse project = await ContextGraphClient.Current.GetNeighborhoodAsync(projectId, ct).ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContextRelationDto relation in project.OutgoingRelations
                .Where(r => string.Equals(r.RelationType, "CONTAINS", StringComparison.OrdinalIgnoreCase)))
            {
                ContextEntityDto? entity = project.NeighborEntities.FirstOrDefault(e => e.Id == relation.ToEntityId);
                if (entity == null || !string.Equals(entity.EntityType, "artifact", StringComparison.OrdinalIgnoreCase)) continue;
                string? artifactId = ReadArtifactId(entity.MetadataJson);
                if (!string.IsNullOrWhiteSpace(artifactId)) ids.Add(artifactId);
            }

            var results = new List<ArtifactDto>();
            foreach (string id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try { results.Add(await ArtifactWorkspaceClient.Current.GetAsync(id, ct).ConfigureAwait(false)); }
                catch (LocalAiException) { /* stale graph link: don't fabricate an artifact */ }
            }
            return results.OrderByDescending(a => a.UpdatedUtc).ToList();
        }

        private static string? ReadArtifactId(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(metadataJson);
                return document.RootElement.TryGetProperty("artifactId", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
