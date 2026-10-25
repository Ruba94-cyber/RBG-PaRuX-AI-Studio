using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Best-effort registration of files that have already been produced successfully. Artifact
    /// bookkeeping must never turn a successful image/video/presentation export into a failed
    /// user operation. ArtifactStore remains source of truth; when a Project is active the durable
    /// Project↔Artifact relation is added through Context Graph after creation.
    ///
    /// A tiny local path→artifact-id ledger prevents the same generated file being registered again
    /// every time its conversation is saved or after an app restart. The ledger stores identities
    /// only; all artifact metadata/version state still lives in ArtifactStore.
    /// </summary>
    public sealed class ArtifactCaptureService
    {
        public static ArtifactCaptureService Current { get; } = new ArtifactCaptureService();

        private static readonly object LedgerGate = new object();
        private static Dictionary<string, string>? _ledger;
        private static string LedgerPath => Path.Combine(AppPaths.PaRuXRoot, "artifact-captures.json");

        private ArtifactCaptureService() { }

        public async Task<ArtifactDto?> CaptureFileAsync(
            string path,
            string artifactType,
            string title,
            string source,
            object? metadata,
            bool markFinal,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            string fullPath = Path.GetFullPath(path);

            try
            {
                string? existingId = GetCapturedArtifactId(fullPath);
                if (!string.IsNullOrWhiteSpace(existingId))
                {
                    try
                    {
                        ArtifactDto existing = await ArtifactWorkspaceClient.Current.GetAsync(existingId, ct).ConfigureAwait(false);
                        await LinkToActiveProjectBestEffortAsync(existing, ct).ConfigureAwait(false);
                        return existing;
                    }
                    catch (LocalAiException)
                    {
                        // The server-side record disappeared while the local file still exists.
                        // Forget only the stale identity and recreate the artifact below.
                        ForgetCapturedArtifact(fullPath);
                    }
                }

                ArtifactDto artifact = await ArtifactWorkspaceClient.Current.CreateAsync(new CreateArtifactRequest
                {
                    ArtifactType = string.IsNullOrWhiteSpace(artifactType) ? "file" : artifactType.Trim(),
                    Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(fullPath) : title.Trim(),
                    ContentRef = fullPath,
                    MetadataJson = metadata == null ? null : JsonSerializer.Serialize(metadata),
                    Source = string.IsNullOrWhiteSpace(source) ? "desktop" : source.Trim()
                }, ct).ConfigureAwait(false);

                if (markFinal)
                    await ArtifactWorkspaceClient.Current.SetStatusAsync(artifact.Id, "final", ct).ConfigureAwait(false);

                RecordCapturedArtifact(fullPath, artifact.Id);
                await LinkToActiveProjectBestEffortAsync(artifact, ct).ConfigureAwait(false);
                return artifact;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("Artifact capture failed after successful file generation: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Registers only assistant-owned attachments from a persisted conversation. User uploads
        /// are intentionally ignored. ChatSession never persists incognito conversations, but this
        /// method repeats the guard so a future caller cannot accidentally weaken that promise.
        /// </summary>
        public async Task CaptureConversationAttachmentsAsync(Conversation conversation, CancellationToken ct)
        {
            if (conversation == null || conversation.IsIncognito || conversation.Messages == null) return;

            foreach (ChatMessage message in conversation.Messages.Where(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.Attachments != null && m.Attachments.Count > 0))
            {
                foreach (ChatAttachment attachment in message.Attachments)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath)) continue;

                    string type = ArtifactTypeForMedia(attachment.MediaType);
                    string title = !string.IsNullOrWhiteSpace(attachment.FileName)
                        ? attachment.FileName
                        : Path.GetFileName(attachment.LocalPath);

                    await CaptureFileAsync(
                        attachment.LocalPath,
                        type,
                        title,
                        "chat-generation",
                        new
                        {
                            conversationId = conversation.Id,
                            mediaType = attachment.MediaType,
                            sizeBytes = attachment.SizeBytes,
                            generatedUtc = message.TimestampUtc
                        },
                        markFinal: true,
                        ct).ConfigureAwait(false);
                }
            }
        }

        private async Task LinkToActiveProjectBestEffortAsync(ArtifactDto artifact, CancellationToken ct)
        {
            string? projectId = ProjectWorkspaceSession.Current.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId)) return;
            try { await ProjectArtifactBridge.Current.LinkAsync(projectId, artifact, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("Artifact project-link failed after successful capture: " + ex.Message);
            }
        }

        private static string ArtifactTypeForMedia(string? mediaType)
        {
            if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return "image";
            if (mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return "video";
            if (mediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return "audio";
            return "file";
        }

        private static string? GetCapturedArtifactId(string fullPath)
        {
            lock (LedgerGate)
            {
                EnsureLedgerLoaded();
                return _ledger!.TryGetValue(fullPath, out string? id) ? id : null;
            }
        }

        private static void RecordCapturedArtifact(string fullPath, string artifactId)
        {
            lock (LedgerGate)
            {
                EnsureLedgerLoaded();
                _ledger![fullPath] = artifactId;
                SaveLedger();
            }
        }

        private static void ForgetCapturedArtifact(string fullPath)
        {
            lock (LedgerGate)
            {
                EnsureLedgerLoaded();
                if (_ledger!.Remove(fullPath)) SaveLedger();
            }
        }

        private static void EnsureLedgerLoaded()
        {
            if (_ledger != null) return;
            AppPaths.EnsureCreated();
            try
            {
                Dictionary<string, string>? loaded = File.Exists(LedgerPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LedgerPath))
                    : null;
                _ledger = loaded == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Write("Artifact capture ledger could not be read; starting a fresh ledger: " + ex.Message);
                _ledger = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveLedger()
        {
            try
            {
                AppPaths.EnsureCreated();
                string temp = LedgerPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_ledger));
                File.Move(temp, LedgerPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Artifact capture ledger could not be saved: " + ex.Message);
            }
        }
    }
}
