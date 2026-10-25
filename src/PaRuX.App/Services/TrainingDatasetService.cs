using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class DatasetExportStats
    {
        public int TotalCandidates { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int MalformedSkipped { get; set; }
        public int TrainCount { get; set; }
        public int ValidationCount { get; set; }
    }

    /// <summary>
    /// Builds a fine-tuning-style JSONL dataset from conversations the user has
    /// explicitly marked - either a thumbs-up rating or "Save as training candidate"
    /// on a specific assistant reply. Nothing is added automatically just because a
    /// conversation happened; the spec is explicit that not every chat should become
    /// training data. Curation here is deliberately simple (exact-duplicate removal,
    /// a fixed train/validation split, and a malformed-record check), matching
    /// "quality over quantity" rather than any deep dataset science.
    /// </summary>
    public static class TrainingDatasetService
    {
        /// <summary>Writes {basePathWithoutExtension}-train.jsonl and -val.jsonl. Every 10th distinct candidate becomes validation.</summary>
        public static async Task<DatasetExportStats> ExportJsonlAsync(string trainPath, string validationPath)
        {
            var stats = new DatasetExportStats();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var records = new List<string>();

            foreach (Conversation conversation in HistoryStore.LoadAll())
            {
                List<ChatMessage> messages = conversation.Messages;
                for (int i = 0; i < messages.Count; i++)
                {
                    ChatMessage reply = messages[i];
                    if (reply.Role != "assistant") continue;
                    if (!reply.TrainingCandidate && reply.FeedbackRating != "up") continue;
                    if (reply.IsError || string.IsNullOrWhiteSpace(reply.Content))
                    {
                        stats.MalformedSkipped++;
                        continue;
                    }

                    stats.TotalCandidates++;

                    var wire = new List<object>();
                    bool malformed = false;
                    for (int j = 0; j <= i; j++)
                    {
                        ChatMessage turn = messages[j];
                        if (turn.IsError) continue;
                        if (string.IsNullOrWhiteSpace(turn.Content) && (turn.Attachments == null || turn.Attachments.Count == 0))
                        {
                            malformed = true;
                            break;
                        }
                        wire.Add(new { role = turn.Role, content = turn.Content ?? string.Empty });
                    }
                    if (malformed || wire.Count == 0)
                    {
                        stats.MalformedSkipped++;
                        continue;
                    }

                    var record = new
                    {
                        messages = wire,
                        metadata = new
                        {
                            conversationId = conversation.Id,
                            conversationTitle = conversation.Title,
                            timestampUtc = reply.TimestampUtc,
                            feedbackRating = reply.FeedbackRating,
                            feedbackTags = reply.FeedbackTags,
                            trainingCandidate = reply.TrainingCandidate,
                            switchedToUncensored = reply.SwitchedToUncensored,
                            ragSources = reply.RagSources
                        }
                    };

                    string json = JsonSerializer.Serialize(record);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(wire))));
                    if (!seen.Add(hash))
                    {
                        stats.DuplicatesSkipped++;
                        continue;
                    }

                    records.Add(json);
                }
            }

            using var trainWriter = new StreamWriter(trainPath, false, new UTF8Encoding(false));
            using var valWriter = new StreamWriter(validationPath, false, new UTF8Encoding(false));
            for (int i = 0; i < records.Count; i++)
            {
                bool isValidation = records.Count >= 10 && (i + 1) % 10 == 0;
                if (isValidation)
                {
                    await valWriter.WriteLineAsync(records[i]);
                    stats.ValidationCount++;
                }
                else
                {
                    await trainWriter.WriteLineAsync(records[i]);
                    stats.TrainCount++;
                }
            }

            return stats;
        }
    }
}
