using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed partial class TaskStore
{
    internal const int MaxChildTimelineLines = 100;
    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlySet<string> ReviewStatusSections =
        new HashSet<string>(StringComparer.Ordinal) { "findings", "childTimeline", "revert" };

    /// <summary>
    /// Atomically replaces one section (findings, childTimeline or revert) of the additive review status
    /// document. Sections have independent writers (desktop worker: findings/revert; Core worker: child
    /// timeline), so each write is a read-modify-write of only its own property under the store lock.
    /// </summary>
    public bool SetReviewStatusSection(string id, string section, JsonNode? value)
    {
        if (!ReviewStatusSections.Contains(section)) throw new ArgumentException("Unknown review status section.", nameof(section));
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            JsonObject document = ReadReviewDocument(connection, id, out bool exists);
            if (!exists) return false;
            document[section] = value?.DeepClone();
            WriteReviewDocument(connection, id, document);
            return true;
        }
    }

    internal bool AppendChildTimeline(string id, string kind, string subject, string detail)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            JsonObject document = ReadReviewDocument(connection, id, out bool exists);
            if (!exists) return false;
            JsonArray lines = document["childTimeline"] as JsonArray ?? new JsonArray();
            lines.Add(JsonSerializer.SerializeToNode(new AgentChildTimelineLineDto
            {
                AtUtc = DateTime.UtcNow,
                Kind = kind,
                Subject = subject,
                Detail = detail.Length > 300 ? detail[..300] : detail
            }, ReviewJson));
            while (lines.Count > MaxChildTimelineLines) lines.RemoveAt(0);
            document["childTimeline"] = lines;
            WriteReviewDocument(connection, id, document);
            return true;
        }
    }

    /// <summary>Marks the user's request to revert the agent's own changes. Idempotent; refused when nothing is offered.</summary>
    public (bool Ok, string Error) RequestOwnChangeRevert(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            JsonObject document = ReadReviewDocument(connection, id, out bool exists);
            if (!exists) return (false, "Task not found.");
            AgentOwnChangeRevertStateDto? state = document["revert"]?.Deserialize<AgentOwnChangeRevertStateDto>(ReviewJson);
            if (state == null || state.OwnChangedFiles <= 0)
                return (false, "No agent-owned changes are recorded for this task.");
            if (state.Completed) return (false, "The agent's own changes were already reverted.");
            if (!state.Requested)
            {
                state.Requested = true;
                state.RequestedUtc = DateTime.UtcNow;
                document["revert"] = JsonSerializer.SerializeToNode(state, ReviewJson);
                WriteReviewDocument(connection, id, document);
            }
            return (true, string.Empty);
        }
    }

    private static JsonObject ReadReviewDocument(SqliteConnection connection, string id, out bool exists)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT review_status_json FROM agent_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        object? value = command.ExecuteScalar();
        exists = value != null;
        if (value == null || value is DBNull) return new JsonObject();
        try { return JsonNode.Parse((string)value) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static void WriteReviewDocument(SqliteConnection connection, string id, JsonObject document)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_tasks SET review_status_json = $json, updated_utc = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$json", document.ToJsonString(ReviewJson));
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
