using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Durable conversation storage. SQLite WAL mode keeps saves atomic and
    /// leaves one future-proof store for search, attachments, and migrations.
    /// </summary>
    public static class HistoryStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions();
        private static readonly object Gate = new object();
        private const int MaxRetainedBranches = 64;
        private static bool _initialized;
        private static bool _backupChecked;

        /// <summary>The exact JSON shape stored per conversation row, reused by ConversationSyncClient
        /// so the server-pushed document round-trips identically to what is stored locally.</summary>
        public static string SerializeDocument(Conversation conversation) =>
            JsonSerializer.Serialize(conversation, JsonOpts);

        public static Conversation? DeserializeDocument(string documentJson)
        {
            try { return JsonSerializer.Deserialize<Conversation>(documentJson); }
            catch (Exception ex) { Log.Write("HistoryStore.DeserializeDocument failed: " + ex.Message); return null; }
        }

        private static SqliteConnection Open()
        {
            try
            {
                return OpenCore();
            }
            catch (SqliteException ex)
            {
                Log.Write("HistoryStore detected an unreadable database: " + ex.Message);
                RecoverDatabase();
                return OpenCore();
            }
        }

        private static SqliteConnection OpenCore()
        {
            AppPaths.EnsureCreated();
            var connection = new SqliteConnection("Data Source=" + AppPaths.DatabaseFile + ";Mode=ReadWriteCreate;Cache=Shared");
            connection.Open();
            EnsureSchema(connection);
            CreateDailyBackup(connection);
            return connection;
        }

        private static void RecoverDatabase()
        {
            lock (Gate)
            {
                _initialized = false;
                _backupChecked = false;
                string suffix = ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                foreach (string path in new[] { AppPaths.DatabaseFile, AppPaths.DatabaseFile + "-wal", AppPaths.DatabaseFile + "-shm" })
                {
                    try { if (File.Exists(path)) File.Move(path, path + suffix, true); }
                    catch (Exception ex) { Log.Write("HistoryStore quarantine failed: " + ex.Message); }
                }

                try
                {
                    FileInfo? latest = new DirectoryInfo(AppPaths.BackupsDir).GetFiles("history-*.db")
                        .OrderByDescending(f => f.Name).FirstOrDefault();
                    if (latest != null)
                    {
                        File.Copy(latest.FullName, AppPaths.DatabaseFile, true);
                        Log.Write("HistoryStore restored the latest daily backup.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("HistoryStore restore failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Re-verified on every connection: the database file can be deleted or restored after the static
        /// flag was set, and the reopened file is then empty. Mirrors PaRuX.Server's SqliteSchemaGuard,
        /// which this project cannot reference.
        /// </summary>
        private static bool ConversationsTableExists(SqliteConnection connection)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'conversations';";
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
            }
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            lock (Gate)
            {
                if (_initialized && ConversationsTableExists(connection)) return;
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS conversations (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    document_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_conversations_updated
    ON conversations(updated_utc DESC);";
                    command.ExecuteNonQuery();
                }
                _initialized = true;
            }
        }

        /// <summary>All conversations, newest first. Corrupt files are skipped, not fatal.</summary>
        public static List<Conversation> LoadAll()
        {
            var result = new List<Conversation>();
            try
            {
                lock (Gate)
                {
                    using (SqliteConnection connection = Open())
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT document_json FROM conversations ORDER BY updated_utc DESC;";
                        using (SqliteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                try
                                {
                                    Conversation? c = JsonSerializer.Deserialize<Conversation>(reader.GetString(0));
                                    if (c != null && !string.IsNullOrEmpty(c.Id)) result.Add(c);
                                }
                                catch (Exception ex)
                                {
                                    Log.Write("HistoryStore: skipping unreadable row: " + ex.Message);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore.LoadAll failed: " + ex.Message);
            }
            return result;
        }

        private static void CreateDailyBackup(SqliteConnection source)
        {
            if (_backupChecked) return;
            lock (Gate)
            {
                if (_backupChecked) return;
                _backupChecked = true;
                try
                {
                    string backup = Path.Combine(AppPaths.BackupsDir, "history-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".db");
                    if (!File.Exists(backup))
                    {
                        using var destination = new SqliteConnection("Data Source=" + backup + ";Mode=ReadWriteCreate");
                        destination.Open();
                        source.BackupDatabase(destination);
                    }
                    foreach (FileInfo stale in new DirectoryInfo(AppPaths.BackupsDir)
                        .GetFiles("history-*.db").OrderByDescending(f => f.Name).Skip(7))
                        stale.Delete();
                }
                catch (Exception ex)
                {
                    Log.Write("HistoryStore backup failed: " + ex.Message);
                }
            }
        }

        /// <summary>The stored conversation, or null when there is no such row or it could not be
        /// read - both of which the callers already handle.</summary>
        public static Conversation? Load(string id)
        {
            try
            {
                lock (Gate)
                {
                    using (SqliteConnection connection = Open())
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT document_json FROM conversations WHERE id = $id;";
                        command.Parameters.AddWithValue("$id", id);
                        object? value = command.ExecuteScalar();
                        return value == null ? null : JsonSerializer.Deserialize<Conversation>((string)value);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore.Load failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Persists a conversation and reports whether the durable boundary was crossed. Branch
        /// retention and UpdatedUtc are prepared on an isolated copy and published back to the live
        /// object only after SQLite accepted the complete document. A write failure therefore cannot
        /// leave the UI believing a branch exists only in memory.
        /// </summary>
        public static bool Save(Conversation conversation)
        {
            if (conversation == null) return false;
            // An untouched empty conversation is intentionally not materialized on disk.
            if (conversation.Messages == null || conversation.Messages.Count == 0) return true;

            Conversation? candidate = CloneConversation(conversation);
            if (candidate == null)
            {
                Log.Write("HistoryStore.Save failed: conversation could not be cloned before persistence.");
                return false;
            }

            List<ConversationBranch> prunedBranches = new List<ConversationBranch>();
            try
            {
                lock (Gate)
                {
                    using (SqliteConnection connection = Open())
                    {
                        Conversation? previous = LoadExisting(connection, candidate.Id);
                        prunedBranches = RetainSupersededPath(candidate, previous);

                        // If the conversation already exists and has not changed in content, preserve UpdatedUtc
                        // so selecting, loading, or toggling favorites on a chat does not bump it to the top.
                        if (previous != null && !HasContentChanged(candidate, previous))
                        {
                            candidate.UpdatedUtc = previous.UpdatedUtc;
                        }
                        else
                        {
                            candidate.UpdatedUtc = DateTime.UtcNow;
                        }

                        string json = JsonSerializer.Serialize(candidate, JsonOpts);

                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = @"
INSERT INTO conversations(id, title, created_utc, updated_utc, document_json)
VALUES($id, $title, $created, $updated, $json)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title,
    updated_utc = excluded.updated_utc,
    document_json = excluded.document_json;";
                            command.Parameters.AddWithValue("$id", candidate.Id);
                            command.Parameters.AddWithValue("$title", candidate.Title ?? "New chat");
                            command.Parameters.AddWithValue("$created", candidate.CreatedUtc.ToString("O"));
                            command.Parameters.AddWithValue("$updated", candidate.UpdatedUtc.ToString("O"));
                            command.Parameters.AddWithValue("$json", json);
                            command.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore.Save failed: " + ex.Message);
                return false;
            }

            // Publish storage-owned state only after the durable write succeeded. The caller owns
            // Messages/title/etc.; branch retention and UpdatedUtc are the fields Save itself mutates.
            conversation.UpdatedUtc = candidate.UpdatedUtc;
            conversation.Branches = candidate.Branches ?? new List<ConversationBranch>();

            // Branch retention is bounded. Once an old branch falls outside the retention window,
            // release any PaRuX-managed attachment that is no longer referenced by the active path,
            // another retained branch, or another conversation. Do this only after the new document
            // is durable so cleanup can use the database as the authoritative retained-reference set.
            if (prunedBranches.Count > 0)
            {
                DeleteUnreferencedManagedAttachments(prunedBranches
                    .SelectMany(branch => branch.Messages ?? new List<ChatMessage>()));
            }

            // Best-effort push so a paired Android device converges on the exact durable document.
            // Never awaited: the local SQLite write above is already durable, and a temporarily
            // unreachable server must never slow down or fail a local save.
            _ = ConversationSyncClient.Current.PushAsync(candidate);
            return true;
        }

        /// <summary>
        /// Restores one retained path and immediately persists it. The live conversation is not
        /// changed until the candidate document has been committed. The path that was current before
        /// restoration is detected as a divergence by Save and retained, so switching versions stays
        /// reversible without false-success UI on I/O/SQLite failure.
        /// </summary>
        public static bool RestoreBranch(Conversation conversation, string branchId)
        {
            if (conversation == null || string.IsNullOrWhiteSpace(branchId) || conversation.Branches == null)
                return false;

            Conversation? candidate = CloneConversation(conversation);
            if (candidate?.Branches == null) return false;
            ConversationBranch? branch = candidate.Branches.FirstOrDefault(value =>
                string.Equals(value.Id, branchId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (branch?.Messages == null || branch.Messages.Count == 0) return false;

            candidate.Messages = CloneMessages(branch.Messages);
            if (!Save(candidate)) return false;

            // candidate now contains exactly the storage-owned branch metadata that was committed.
            conversation.Messages = CloneMessages(candidate.Messages);
            conversation.Branches = candidate.Branches;
            conversation.UpdatedUtc = candidate.UpdatedUtc;
            return true;
        }

        private static Conversation? CloneConversation(Conversation conversation)
        {
            try
            {
                string json = JsonSerializer.Serialize(conversation, JsonOpts);
                return JsonSerializer.Deserialize<Conversation>(json, JsonOpts);
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore conversation clone failed: " + ex.Message);
                return null;
            }
        }

        private static bool HasContentChanged(Conversation candidate, Conversation previous)
        {
            if (!string.Equals(candidate.Title, previous.Title, StringComparison.Ordinal)) return true;

            var candMsgs = candidate.Messages ?? new List<ChatMessage>();
            var prevMsgs = previous.Messages ?? new List<ChatMessage>();
            if (candMsgs.Count != prevMsgs.Count) return true;

            for (int i = 0; i < candMsgs.Count; i++)
            {
                ChatMessage m1 = candMsgs[i];
                ChatMessage m2 = prevMsgs[i];
                if (!string.Equals(m1.Role, m2.Role, StringComparison.Ordinal)) return true;
                if (!string.Equals(m1.Content, m2.Content, StringComparison.Ordinal)) return true;
                if (!string.Equals(m1.Reasoning, m2.Reasoning, StringComparison.Ordinal)) return true;
                if (m1.IsStreaming != m2.IsStreaming) return true;
                if (m1.IsError != m2.IsError) return true;
                if (!string.Equals(m1.CodeTaskId, m2.CodeTaskId, StringComparison.Ordinal)) return true;
                if (!string.Equals(m1.ActionKind, m2.ActionKind, StringComparison.Ordinal)) return true;
                if (!string.Equals(m1.ActionPayload, m2.ActionPayload, StringComparison.Ordinal)) return true;
                if ((m1.Attachments?.Count ?? 0) != (m2.Attachments?.Count ?? 0)) return true;
            }

            var candBranches = candidate.Branches ?? new List<ConversationBranch>();
            var prevBranches = previous.Branches ?? new List<ConversationBranch>();
            if (candBranches.Count != prevBranches.Count) return true;

            return false;
        }

        private static Conversation? LoadExisting(SqliteConnection connection, string id)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT document_json FROM conversations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            object? value = command.ExecuteScalar();
            if (value == null) return null;
            try { return JsonSerializer.Deserialize<Conversation>((string)value, JsonOpts); }
            catch (Exception ex)
            {
                Log.Write("HistoryStore: previous document could not be read for branch retention: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Save is the single durable boundary for all chat mutations. If the path being saved is no
        /// longer a strict extension of the previously durable path, preserve the old path before it
        /// is overwritten. This catches RegenerateAsync and edit-and-continue without coupling the
        /// storage invariant to one UI command and also protects future mutation surfaces.
        /// </summary>
        private static List<ConversationBranch> RetainSupersededPath(Conversation current, Conversation? previous)
        {
            var pruned = new List<ConversationBranch>();
            current.Branches ??= new List<ConversationBranch>();
            if (previous == null || previous.Messages == null || previous.Messages.Count == 0) return pruned;

            // Preserve branch metadata that may exist in the durable document but be absent from a
            // stale in-memory/synced representation. Fingerprints make this merge idempotent.
            foreach (ConversationBranch previousBranch in previous.Branches ?? new List<ConversationBranch>())
            {
                if (previousBranch.Messages == null || previousBranch.Messages.Count == 0) continue;
                string fingerprint = string.IsNullOrWhiteSpace(previousBranch.Fingerprint)
                    ? PathFingerprint(previousBranch.Messages)
                    : previousBranch.Fingerprint;
                if (current.Branches.Any(branchValue => string.Equals(
                    EffectiveFingerprint(branchValue), fingerprint, StringComparison.OrdinalIgnoreCase))) continue;
                current.Branches.Add(new ConversationBranch
                {
                    Id = string.IsNullOrWhiteSpace(previousBranch.Id) ? Guid.NewGuid().ToString("N") : previousBranch.Id,
                    CreatedUtc = previousBranch.CreatedUtc,
                    ForkIndex = previousBranch.ForkIndex,
                    Fingerprint = fingerprint,
                    Reason = previousBranch.Reason,
                    Messages = CloneMessages(previousBranch.Messages)
                });
            }

            int commonPrefix = CommonPrefixLength(previous.Messages, current.Messages);
            // A strict extension is a normal appended turn. Any shorter/different path is a branch.
            if (commonPrefix != previous.Messages.Count)
            {
                string previousFingerprint = PathFingerprint(previous.Messages);
                if (!current.Branches.Any(branchValue => string.Equals(
                    EffectiveFingerprint(branchValue), previousFingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    current.Branches.Add(new ConversationBranch
                    {
                        // Order alternatives by when their path was last current, not by the
                        // moment they were archived. Otherwise Save stamps the retained path
                        // after candidate.UpdatedUtc and the active path appears first again
                        // after every Previous/Next navigation.
                        CreatedUtc = previous.UpdatedUtc,
                        ForkIndex = commonPrefix,
                        Fingerprint = previousFingerprint,
                        Reason = current.Messages.Count <= previous.Messages.Count ? "regenerate-or-edit" : "divergence",
                        Messages = CloneMessages(previous.Messages)
                    });
                }
            }

            if (current.Branches.Count > MaxRetainedBranches)
            {
                List<ConversationBranch> ordered = current.Branches
                    .OrderByDescending(branchValue => branchValue.CreatedUtc)
                    .ToList();
                pruned.AddRange(ordered.Skip(MaxRetainedBranches));
                current.Branches = ordered.Take(MaxRetainedBranches).ToList();
            }

            return pruned;
        }

        private static int CommonPrefixLength(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right)
        {
            int count = Math.Min(left.Count, right.Count);
            for (int i = 0; i < count; i++)
            {
                string a = JsonSerializer.Serialize(BranchComparable(left[i]), JsonOpts);
                string b = JsonSerializer.Serialize(BranchComparable(right[i]), JsonOpts);
                if (!string.Equals(a, b, StringComparison.Ordinal)) return i;
            }
            return count;
        }

        /// <summary>
        /// Only fields that materially change the conversational path participate in branch
        /// detection. UI/audit metadata such as thumbs-up/down, feedback tags, training-candidate
        /// state, timestamps, streaming flags, selected skills and workstation handoff metadata must
        /// never manufacture a fake conversation version when the user has not changed the chat.
        /// Attachment identity is path-independent so Windows/Android path remapping also does not
        /// create a false branch.
        /// </summary>
        private static object BranchComparable(ChatMessage message)
        {
            return new
            {
                Role = message.Role ?? string.Empty,
                Content = message.Content ?? string.Empty,
                Reasoning = message.Reasoning ?? string.Empty,
                Attachments = (message.Attachments ?? new List<ChatAttachment>())
                    .Select(attachment => new
                    {
                        Id = attachment.Id ?? string.Empty,
                        FileName = attachment.FileName ?? string.Empty,
                        MediaType = attachment.MediaType ?? string.Empty,
                        SizeBytes = attachment.SizeBytes
                    })
                    .ToArray()
            };
        }

        private static string EffectiveFingerprint(ConversationBranch branch)
        {
            if (!string.IsNullOrWhiteSpace(branch.Fingerprint)) return branch.Fingerprint;
            return branch.Messages == null ? string.Empty : PathFingerprint(branch.Messages);
        }

        private static string PathFingerprint(IReadOnlyList<ChatMessage> messages)
        {
            string json = JsonSerializer.Serialize(messages.Select(BranchComparable).ToArray(), JsonOpts);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static List<ChatMessage> CloneMessages(IReadOnlyList<ChatMessage> messages)
        {
            string json = JsonSerializer.Serialize(messages, JsonOpts);
            return JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOpts) ?? new List<ChatMessage>();
        }

        public static void Delete(string id)
        {
            Conversation? removed = Load(id);
            try
            {
                lock (Gate)
                {
                    using (SqliteConnection connection = Open())
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM conversations WHERE id = $id;";
                        command.Parameters.AddWithValue("$id", id);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore.Delete failed: " + ex.Message);
                return;
            }

            _ = ConversationSyncClient.Current.DeleteAsync(id);
            DeleteUnreferencedManagedAttachments(removed);
        }

        private static IEnumerable<ChatMessage> AllMessages(Conversation? conversation)
        {
            if (conversation == null) yield break;
            foreach (ChatMessage message in conversation.Messages ?? new List<ChatMessage>())
                yield return message;
            foreach (ConversationBranch branch in conversation.Branches ?? new List<ConversationBranch>())
            {
                foreach (ChatMessage message in branch.Messages ?? new List<ChatMessage>())
                    yield return message;
            }
        }

        private static void DeleteUnreferencedManagedAttachments(Conversation? removed)
        {
            if (removed == null) return;
            DeleteUnreferencedManagedAttachments(AllMessages(removed));
        }

        private static void DeleteUnreferencedManagedAttachments(IEnumerable<ChatMessage> removedMessages)
        {
            try
            {
                List<ChatMessage> removed = removedMessages?.ToList() ?? new List<ChatMessage>();
                if (removed.Count == 0) return;

                var retained = new HashSet<string>(LoadAll()
                    .SelectMany(AllMessages)
                    .SelectMany(m => m.Attachments ?? new List<ChatAttachment>())
                    .Select(a => a.LocalPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.OrdinalIgnoreCase);

                foreach (string path in removed
                    .SelectMany(m => m.Attachments ?? new List<ChatAttachment>())
                    .Select(a => a.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(path) && StorageQuotaService.IsManagedPath(path)
                        && !retained.Contains(path) && File.Exists(path))
                        File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Write("HistoryStore attachment cleanup failed: " + ex.Message);
            }
        }

        /// <summary>Derives a sidebar title from the first user turn.</summary>
        public static string DeriveTitle(Conversation conversation)
        {
            if (conversation == null || conversation.Messages == null) return "New chat";
            ChatMessage? first = conversation.Messages.FirstOrDefault(m => m.Role == "user");
            if (first == null || string.IsNullOrWhiteSpace(first.Content)) return "New chat";

            string text = first.Content.Trim().Replace("\r", " ").Replace("\n", " ");
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            if (text.Length > 48) text = text.Substring(0, 48).TrimEnd() + "\u2026";
            return text;
        }
    }
}
