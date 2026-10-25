using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;

namespace PaRuX.Server;

/// <summary>
/// Builds explicit Project-scoped context from authoritative Core stores. A graph edge alone is
/// never trusted as AI context: task ids are resolved against TaskStore and artifact ids against
/// ArtifactStore. This keeps a paired client from manufacturing a graph node that is then silently
/// promoted into trusted model context.
/// </summary>
public sealed class ProjectKnowledgeService
{
    public const int DefaultMaxCharacters = 12_000;
    public const int MinimumMaxCharacters = 1_000;
    public const int MaximumMaxCharacters = 30_000;
    private const int MaxItems = 32;
    private const int MaxItemCharacters = 3_000;
    private const int MaxTaskLogCharacters = 1_600;
    private const string ContainsRelation = "CONTAINS";

    private readonly ProjectManagerService _projects;
    private readonly ContextGraphStore _graph;
    private readonly TaskStore _tasks;
    private readonly ArtifactStore _artifacts;

    public ProjectKnowledgeService(
        ProjectManagerService projects,
        ContextGraphStore graph,
        TaskStore tasks,
        ArtifactStore artifacts)
    {
        _projects = projects;
        _graph = graph;
        _tasks = tasks;
        _artifacts = artifacts;
    }

    public ProjectKnowledgePackDto Build(string projectId, int maxCharacters = DefaultMaxCharacters)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidDataException("A Project id is required.");

        ProjectDto project = _projects.GetProject(projectId)
            ?? throw new InvalidDataException("Project not found.");
        ContextNeighborhoodResponse neighborhood = _graph.GetNeighborhood(project.Id)
            ?? throw new InvalidDataException("Project context could not be loaded.");

        int budget = Math.Clamp(maxCharacters, MinimumMaxCharacters, MaximumMaxCharacters);
        var items = new List<ProjectKnowledgeItemDto>();

        foreach (ContextRelationDto relation in neighborhood.OutgoingRelations
            .Where(relation => string.Equals(relation.RelationType, ContainsRelation, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(relation => relation.CreatedUtc))
        {
            if (items.Count >= MaxItems) break;
            ContextEntityDto? entity = neighborhood.NeighborEntities.FirstOrDefault(e => e.Id == relation.ToEntityId);
            if (entity == null) continue;

            if (string.Equals(entity.EntityType, "task", StringComparison.OrdinalIgnoreCase))
            {
                string? taskId = ReadMetadataString(entity.MetadataJson, "orchestratorTaskId");
                AgentTaskDto? task = string.IsNullOrWhiteSpace(taskId) ? null : _tasks.Get(taskId);
                if (task != null) items.Add(TaskItem(task));
                continue;
            }

            if (string.Equals(entity.EntityType, "artifact", StringComparison.OrdinalIgnoreCase))
            {
                string? artifactId = ReadMetadataString(entity.MetadataJson, "artifactId");
                ArtifactDto? artifact = string.IsNullOrWhiteSpace(artifactId) ? null : _artifacts.Get(artifactId);
                if (artifact != null) items.Add(ArtifactItem(artifact));
            }
        }

        items = items
            .OrderByDescending(item => item.UpdatedUtc)
            .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .ToList();

        (string prompt, bool truncated) = BuildPrompt(project, items, budget);
        return new ProjectKnowledgePackDto
        {
            ProjectId = project.Id,
            Goal = project.Goal,
            UpdatedUtc = project.UpdatedUtc,
            Items = items,
            PromptText = prompt,
            UsedCharacters = prompt.Length,
            MaxCharacters = budget,
            Truncated = truncated
        };
    }

    private ProjectKnowledgeItemDto TaskItem(AgentTaskDto task)
    {
        var content = new StringBuilder();
        content.Append("Goal: ").Append(task.Goal);
        content.Append("\nRole: ").Append(string.IsNullOrWhiteSpace(task.Role) ? "auto" : task.Role);
        content.Append("\nState: ").Append(task.Status);
        if (!string.IsNullOrWhiteSpace(task.Stage)) content.Append(" · ").Append(task.Stage);
        if (!string.IsNullOrWhiteSpace(task.VerificationStatus))
            content.Append("\nVerification: ").Append(task.VerificationStatus);
        if (!string.IsNullOrWhiteSpace(task.Error))
            content.Append("\nError: ").Append(Bound(task.Error, 700));

        string? summary = TryReadResultSummary(task.ResultJson);
        if (!string.IsNullOrWhiteSpace(summary))
            content.Append("\nResult summary: ").Append(Bound(summary, 1_800));

        List<AgentTaskLogEntryDto> logs = _tasks.GetLogs(task.Id)
            .OrderByDescending(log => log.TimestampUtc)
            .Take(3)
            .OrderBy(log => log.TimestampUtc)
            .ToList();
        if (logs.Count > 0)
        {
            var logText = new StringBuilder();
            foreach (AgentTaskLogEntryDto log in logs)
            {
                if (logText.Length > 0) logText.Append("\n");
                logText.Append(log.TimestampUtc.ToString("u")).Append(" ").Append(log.Message);
            }
            content.Append("\nRecent log:\n").Append(Bound(logText.ToString(), MaxTaskLogCharacters));
        }

        return new ProjectKnowledgeItemDto
        {
            Kind = "task",
            Identity = "task:" + task.Id,
            Title = task.Goal,
            Status = task.Status,
            Content = Bound(content.ToString(), MaxItemCharacters),
            UpdatedUtc = task.UpdatedUtc
        };
    }

    private ProjectKnowledgeItemDto ArtifactItem(ArtifactDto artifact)
    {
        ArtifactVersionDto? version = _artifacts.GetVersion(artifact.Id, artifact.CurrentVersionNumber);
        string content;
        if (version == null)
        {
            content = "Artifact metadata exists, but its current version could not be resolved.";
        }
        else if (!string.Equals(artifact.Status, ArtifactStore.StatusFinal, StringComparison.OrdinalIgnoreCase))
        {
            // Drafts may be useful to show in the workspace, but are never injected as verified
            // substantive content. This mirrors ContextEngineService's final-only trust rule.
            content = "Draft artifact v" + version.VersionNumber + ". Draft content is not treated as authoritative Project knowledge.";
        }
        else
        {
            content = DescribeFinalArtifact(artifact, version);
        }

        return new ProjectKnowledgeItemDto
        {
            Kind = "artifact",
            Identity = "artifact:" + artifact.Id + ":v" + artifact.CurrentVersionNumber,
            Title = artifact.Title,
            Status = artifact.Status,
            Content = Bound(content, MaxItemCharacters),
            UpdatedUtc = artifact.UpdatedUtc
        };
    }

    private static string DescribeFinalArtifact(ArtifactDto artifact, ArtifactVersionDto version)
    {
        string reference = version.ContentRef ?? string.Empty;
        try
        {
            if (File.Exists(reference))
            {
                var info = new FileInfo(reference);
                // Filename is useful provenance; never include directory/path in model context.
                return "Verified final " + artifact.ArtifactType + " file v" + version.VersionNumber
                    + ": " + info.Name + " (" + FormatBytes(info.Length) + ").";
            }
            if (Path.IsPathRooted(reference))
                return "Verified final " + artifact.ArtifactType + " artifact v" + version.VersionNumber
                    + "; its referenced local file is currently unavailable.";
        }
        catch
        {
            return "Verified final " + artifact.ArtifactType + " artifact v" + version.VersionNumber
                + "; its content reference could not be inspected safely.";
        }

        // Small inline text/JSON artifacts are useful Project knowledge. File outputs are described
        // above instead of reading arbitrary paths through a context request.
        return "Verified final " + artifact.ArtifactType + " artifact v" + version.VersionNumber + ":\n"
            + Bound(reference, MaxItemCharacters - 100);
    }

    private static (string Prompt, bool Truncated) BuildPrompt(
        ProjectDto project, IReadOnlyList<ProjectKnowledgeItemDto> items, int maxCharacters)
    {
        var output = new StringBuilder();
        bool truncated = false;
        AppendBounded(output, "[PROJECT]\nGoal: " + project.Goal + "\nProject id: " + project.Id, maxCharacters, ref truncated);

        foreach (ProjectKnowledgeItemDto item in items)
        {
            if (output.Length >= maxCharacters) { truncated = true; break; }
            string block = "\n\n[PROJECT " + item.Kind.ToUpperInvariant() + "] " + item.Title
                + (string.IsNullOrWhiteSpace(item.Status) ? string.Empty : " · " + item.Status)
                + "\n" + item.Content;
            AppendBounded(output, block, maxCharacters, ref truncated);
        }
        return (output.ToString(), truncated);
    }

    private static void AppendBounded(StringBuilder output, string value, int maxCharacters, ref bool truncated)
    {
        int remaining = maxCharacters - output.Length;
        if (remaining <= 0) { truncated = true; return; }
        if (value.Length <= remaining) { output.Append(value); return; }
        if (remaining == 1) output.Append('…');
        else output.Append(value.AsSpan(0, remaining - 1)).Append('…');
        truncated = true;
    }

    private static string? TryReadResultSummary(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (string key in new[] { "summary", "response", "result" })
            {
                if (document.RootElement.TryGetProperty(key, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? ReadMetadataString(string? metadataJson, string property)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Bound(string value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters) return value ?? string.Empty;
        return value[..Math.Max(1, maxCharacters - 1)].TrimEnd() + "…";
    }

    private static string FormatBytes(long value)
    {
        double bytes = Math.Max(0, value);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }
}
