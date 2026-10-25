using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Durable, inspectable state for one local coding-agent session. This is execution evidence,
    /// not hidden model reasoning: task identity, recent tool observations, verification gates and
    /// progress counters. Schema v2 adds per-repository verification scopes while transparently
    /// migrating v1's single boolean gate on load. Callers that still use the legacy boolean gate
    /// are bridged to the default "." scope on save, so schema evolution cannot silently disable a
    /// completion gate while older execution code is being upgraded.
    /// </summary>
    /// <summary>A verification command the agent really executed (after the last testable change), with its real exit code.</summary>
    public sealed class CodingExecutedCheckRecord
    {
        public string Command { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string Scope { get; set; } = ".";
    }

    public sealed class CodingAgentCheckpoint
    {
        public int SchemaVersion { get; set; } = 2;
        public string SessionId { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
        public int StepsTaken { get; set; }
        public int ConsecutiveNonProgressSteps { get; set; }
        public int LastRepairAttempt { get; set; }
        public string Plan { get; set; } = string.Empty;
        public bool VerificationRequired { get; set; }
        public bool VerificationSatisfied { get; set; }
        /// <summary>An accepted verification command succeeded after the last testable change.</summary>
        public bool VerifiedByCommand { get; set; }
        /// <summary>A browser interaction cannot count as observed until a subsequent typed DOM assertion passes.</summary>
        public bool BrowserPostconditionPending { get; set; }
        /// <summary>SHA-256 of the Git worktree diff at the last accepted test command.</summary>
        public string? VerifiedWorkspaceFingerprint { get; set; }
        public List<string> VerificationRequiredScopes { get; set; } = new();
        public List<string> VerificationSatisfiedScopes { get; set; } = new();
        public List<string> Observations { get; set; } = new();
        /// <summary>
        /// Question ids of user answers already appended to the evidence log. Kept apart from Observations,
        /// which are trimmed to a bounded window, so an answer cannot be consumed again after its marker is trimmed.
        /// </summary>
        public List<string> ConsumedUserInputIds { get; set; } = new();
        /// <summary>Permission mode frozen when this coding run first persisted its checkpoint.</summary>
        public string PermissionMode { get; set; } = CodingAgentPermissionMode.Bypass;
        /// <summary>Durable Ask-mode approval bound to one unanswered Core user question and exact command.</summary>
        public string PendingCommandApprovalQuestionId { get; set; } = string.Empty;
        public string PendingCommandApprovalText { get; set; } = string.Empty;
        /// <summary>One-shot approved shell command; exact text is compared and cleared before dispatch.</summary>
        public string ApprovedCommandText { get; set; } = string.Empty;
        /// <summary>Durable Ask-mode approval bound to one exact staged file mutation.</summary>
        public string PendingFileApprovalQuestionId { get; set; } = string.Empty;
        public string PendingFileApprovalKey { get; set; } = string.Empty;
        public string ApprovedFileApprovalKey { get; set; } = string.Empty;
        /// <summary>Read-only delegated children this task launched while it kept running (bounded).</summary>
        public List<string> ContinuedChildTaskIds { get; set; } = new();
        /// <summary>The subset of <see cref="ContinuedChildTaskIds"/> whose results the model has already been shown.</summary>
        public List<string> CollectedChildTaskIds { get; set; } = new();
        /// <summary>A continuing delegation was ever made; Core then decides at done whether any child is live.</summary>
        public bool ContinuationUsed { get; set; }
        /// <summary>Accepted checks executed since the last testable change; cleared by every such change.</summary>
        public List<CodingExecutedCheckRecord> ExecutedChecks { get; set; } = new();
        /// <summary>Bounded, derived recovery index; raw observations remain separately retained.</summary>
        public string RecoveryDigest { get; set; } = string.Empty;
        /// <summary>
        /// Write-ahead execution intent: persisted BEFORE dispatch, removed only with
        /// the durable tool outcome. On crash, action success is UNKNOWN.
        /// </summary>
        public int PendingActionStep { get; set; }
        public string PendingActionKind { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Deterministic recovery index built from *observed* execution evidence. This is not
    /// a model-generated summary and never declares a task successful. It lets a heavily
    /// compacted prompt retain recent edit/verification evidence without replaying logs.
    /// The original checkpoint, task ledger and large tool artifacts remain authoritative.
    /// </summary>
    public static class CodingAgentRecoveryDigest
    {
        public const int MaximumCharacters = 1_800;

        public static string Build(
            CodingAgentCheckpoint checkpoint, IReadOnlyList<string>? observations,
            int maximumCharacters = 1_200)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            int budget = Math.Clamp(maximumCharacters, 0, MaximumCharacters);
            if (budget == 0) return string.Empty;

            var output = new StringBuilder();
            void Append(string value)
            {
                if (output.Length >= budget) return;
                int available = budget - output.Length;
                output.Append(value.Length <= available ? value : value[..available]);
            }

            Append("Recovery digest (checkpoint observations only, NOT independent verification):\n");
            Append("Last completed step: " + Math.Max(0, checkpoint.StepsTaken)
                + "; verified by command: " + checkpoint.VerifiedByCommand
                + "; verification required: " + checkpoint.VerificationRequired
                + "; verification satisfied: " + checkpoint.VerificationSatisfied + ".\n");
            if (!string.IsNullOrWhiteSpace(checkpoint.Plan))
                Append("Plan excerpt: " + OneLine(checkpoint.Plan, 220) + "\n");

            IReadOnlyList<string> evidence = observations ?? Array.Empty<string>();

            // Long tool outputs live in the existing immutable ArtifactStore. Keep
            // a compact index even when the model cannot replay their text; this
            // avoids the observed read-the-same-file-again loop after compaction.
            // The id is derived only from a real persisted observation, not LLM
            // speculation, and must be retrieved through read_artifact.
            string[] archived = evidence
                .Select(TryExtractArtifactId)
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(4)
                .ToArray();
            foreach (string artifactId in archived)
            {
                string line = "Saved artifact ID (read_artifact): " + artifactId + "\n";
                // Never truncate the identifier itself: a partially copied id is
                // fabricated evidence and cannot be resolved from ArtifactStore.
                if (line.Length <= budget - output.Length) Append(line);
            }

            var priority = evidence.Where(IsSignificant).TakeLast(4);
            var recent = evidence.TakeLast(2);
            string[] selected = priority.Concat(recent)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .TakeLast(6)
                .ToArray();
            if (selected.Length > 0) Append("Evidence excerpts (not claims of success):\n");
            foreach (string item in selected)
                Append("- " + OneLine(RedactUnsafeArtifactIds(item), 230) + "\n");
            return output.ToString();
        }

        private static string TryExtractArtifactId(string? observation)
        {
            const string marker = "[FULL EVIDENCE artifactId=";
            if (string.IsNullOrWhiteSpace(observation)) return string.Empty;
            int start = observation.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += marker.Length;
            int end = observation.IndexOf(';', start);
            if (end <= start || end - start > 100) return string.Empty;
            string id = observation[start..end];
            return id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
                ? id : string.Empty;
        }

        // Excerpts are copied verbatim from raw observations, so a forged or malformed artifact
        // marker in one would be re-presented as a handle. Only the id value is replaced; valid
        // handles remain listed by the "Saved artifact ID" lines above.
        private static readonly Regex ArtifactMarkerId = new(
            @"\[FULL EVIDENCE artifactId=(?<id>[^;\]\r\n]*)", RegexOptions.CultureInvariant);

        private static string RedactUnsafeArtifactIds(string value) =>
            ArtifactMarkerId.Replace(value, match =>
            {
                string id = match.Groups["id"].Value;
                bool safe = id.Length is > 0 and <= 100
                    && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
                return "[FULL EVIDENCE artifactId=" + (safe ? id : "<unverified-id>");
            });

        private static bool IsSignificant(string? observation)
        {
            if (string.IsNullOrEmpty(observation)) return false;
            string[] markers =
            {
                "replace_range", "write_file", "create_file", "rename_file", "delete_file",
                "run_command", "verification", "test failed", "test passed", "[USER INPUT",
                "repair attempt", "infrastructure blocked"
            };
            return markers.Any(marker =>
                observation.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string OneLine(string value, int maximumCharacters)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maximumCharacters
                ? normalized : normalized[..(maximumCharacters - 1)] + "…";
        }
    }

    /// <summary>
    /// Durable, two-phase tool delivery ledger. Inspired by the independent
    /// proposal/dispatch/result distinction used by other open-source agents,
    /// but implemented independently for PaRuX's existing checkpoint format.
    /// A crashed action is NOT blindly repeated or declared successful.
    /// </summary>
    public static class CodingAgentDispatchJournal
    {
        public static void Begin(CodingAgentCheckpoint checkpoint, int proposedStep, string action)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (proposedStep <= checkpoint.StepsTaken
                || proposedStep <= 0
                || checkpoint.PendingActionStep > 0
                || string.IsNullOrWhiteSpace(action) || action.Length > 80)
                throw new InvalidOperationException("Cannot dispatch an untracked or overlapping agent action.");
            checkpoint.PendingActionStep = proposedStep;
            checkpoint.PendingActionKind = action.Trim();
        }

        public static void Complete(CodingAgentCheckpoint checkpoint, int completedStep)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (checkpoint.PendingActionStep != completedStep)
                throw new InvalidOperationException("No matching pending action to complete.");
            checkpoint.PendingActionStep = 0;
            checkpoint.PendingActionKind = string.Empty;
        }

        /// <summary>
        /// Call once after restoring a checkpoint but before allowing the model
        /// another tool. The uncertain step is consumed and requires a fresh
        /// repository/diff inspection and verification, not replay by automation.
        /// Caller must SAVE the repaired checkpoint immediately.
        /// </summary>
        public static string ReconcileInterruptedAction(CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (checkpoint.PendingActionStep <= 0) return string.Empty;
            int step = checkpoint.PendingActionStep;
            string kind = string.IsNullOrWhiteSpace(checkpoint.PendingActionKind)
                ? "unknown" : checkpoint.PendingActionKind;
            checkpoint.StepsTaken = Math.Max(checkpoint.StepsTaken, step);
            checkpoint.PendingActionStep = 0;
            checkpoint.PendingActionKind = string.Empty;
            checkpoint.VerifiedByCommand = false;
            checkpoint.VerifiedWorkspaceFingerprint = null;
            if (CodingBrowserPostconditionPolicy.IsMutationAction(kind))
                checkpoint.BrowserPostconditionPending = true;
            checkpoint.VerificationRequired = true;
            checkpoint.VerificationSatisfied = false;
            checkpoint.VerificationRequiredScopes ??= new List<string>();
            if (!checkpoint.VerificationRequiredScopes.Contains(".", StringComparer.OrdinalIgnoreCase))
                checkpoint.VerificationRequiredScopes.Add(".");
            checkpoint.VerificationSatisfiedScopes ??= new List<string>();
            checkpoint.VerificationSatisfiedScopes.Clear();
            return "INTERRUPTED ACTION RECOVERY: step " + step + " (" + kind
                + ") was dispatched but its final result was not durably recorded. "
                + "The action may or may not have changed files. Do NOT replay it automatically "
                + "or claim success. First inspect git status, git diff and relevant files, "
                + "then choose the next minimal action and independently verify the result.";
        }
    }

    /// <summary>
    /// The checkpoint file exists but cannot be trusted as the write-ahead receipt of a dispatched step.
    /// The run must stop: starting fresh could replay that step, and the next save would overwrite the
    /// evidence. The original stays in place and a copy is kept beside it in quarantine.
    /// </summary>
    public sealed class CodingAgentCheckpointCorruptException : InvalidOperationException
    {
        public CodingAgentCheckpointCorruptException(string message, string checkpointPath, string? quarantinePath)
            : base(message)
        {
            CheckpointPath = checkpointPath;
            QuarantinePath = quarantinePath;
        }

        public string CheckpointPath { get; }
        public string? QuarantinePath { get; }
    }

    /// <summary>
    /// Durable single-file publication shared by the coding checkpoint and artifact stores. The bytes are
    /// flushed to stable storage before the caller renames the temporary file over the final name, so a
    /// power loss cannot leave an empty or truncated file under the final name. Durability of the parent
    /// directory entry is platform-dependent and is not claimed here.
    /// </summary>
    internal static class CodingDurableFile
    {
        /// <summary>
        /// Test seam: performs the stable-storage flush of a file that is about to be published. Production always
        /// flushes to disk. A test replaces it to observe that the flush happens before the rename.
        /// </summary>
        internal static Action<FileStream> FlushToStableStorage { get; set; } = static stream => stream.Flush(flushToDisk: true);

        internal static void WriteText(string path, string content)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None);
            stream.Write(bytes, 0, bytes.Length);
            FlushToStableStorage(stream);
        }

        /// <summary>
        /// Keeps one copy of a file beside it. The name embeds the content digest, so a repeated load reuses the
        /// copy. An existing copy is reused only when its bytes equal the original's. A copy is written under a
        /// temporary name and published only when complete, so a failed write throws and is never reported as a
        /// preserved copy.
        /// </summary>
        internal static string Quarantine(string path, string kind = "corrupt")
        {
            byte[] bytes = File.ReadAllBytes(path);
            string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
            string copy = path + "." + kind + "-" + digest;
            if (File.Exists(copy) && File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes)) return copy;
            string temporary = copy + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    FlushToStableStorage(stream);
                }
                File.Move(temporary, copy, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return copy;
        }
    }

    public static class CodingAgentCheckpointStore
    {
        internal const int MaximumObservations = 128;
        internal const int MaximumConsumedUserInputs = 64;
        internal const int MaximumObservationCharacters = 4_000;
        internal const int MaximumVerificationScopes = 16;
        private const string DirectChatSessionPrefix = "chat-";
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        /// <summary>
        /// A user answer resumes planning and clears the no-progress watchdog,
        /// but is NEVER evidence that a code build/test passed. Preserve all
        /// verification booleans, per-repository scopes and command receipts.
        /// </summary>
        internal static void ResumeAfterUserAnswer(CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.ConsecutiveNonProgressSteps = 0;
        }

        /// <summary>
        /// Every successful testable mutation invalidates ALL previously accepted
        /// verification scopes. A previously passing command never certifies
        /// source changes made afterwards. Even repositories without an
        /// automatically discovered test command remain verification-required.
        /// </summary>
        internal static void InvalidateVerificationAfterMutation(CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.VerificationRequiredScopes ??= new List<string>();
            checkpoint.VerificationSatisfiedScopes ??= new List<string>();
            if (checkpoint.VerificationRequiredScopes.Count == 0)
                checkpoint.VerificationRequiredScopes.Add(".");
            checkpoint.VerificationSatisfiedScopes.Clear();
            checkpoint.VerificationRequired = true;
            checkpoint.VerificationSatisfied = false;
            checkpoint.VerifiedByCommand = false;
            checkpoint.VerifiedWorkspaceFingerprint = null;
        }

        /// <summary>
        /// Only an actual accepted verification command outcome may restore
        /// satisfaction. A failed command erases stale success; a successful
        /// command certifies the currently required scopes, never past edits.
        /// </summary>
        internal static void RecordVerificationCommandOutcome(
            CodingAgentCheckpoint checkpoint, bool succeeded)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            checkpoint.VerificationRequiredScopes ??= new List<string>();
            checkpoint.VerificationSatisfiedScopes ??= new List<string>();
            if (checkpoint.VerificationRequiredScopes.Count == 0)
                checkpoint.VerificationRequiredScopes.Add(".");
            checkpoint.VerificationRequired = true;
            checkpoint.VerificationSatisfiedScopes.Clear();
            if (succeeded)
                checkpoint.VerificationSatisfiedScopes.AddRange(checkpoint.VerificationRequiredScopes);
            checkpoint.VerificationSatisfied = succeeded;
            checkpoint.VerifiedByCommand = succeeded;
            if (!succeeded) checkpoint.VerifiedWorkspaceFingerprint = null;
        }

        internal static bool BeginRepairAttempt(CodingAgentCheckpoint checkpoint, int repairAttempt)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (repairAttempt <= 0 || repairAttempt <= checkpoint.LastRepairAttempt) return false;
            checkpoint.LastRepairAttempt = repairAttempt;
            checkpoint.ConsecutiveNonProgressSteps = 0;
            return true;
        }

        public static string ResolveSessionId(string workspaceRoot, string task, string? explicitSessionId)
        {
            if (!string.IsNullOrWhiteSpace(explicitSessionId))
                return SanitizeSessionId(explicitSessionId);

            string canonical;
            try { canonical = Path.GetFullPath(workspaceRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { canonical = workspaceRoot ?? string.Empty; }
            string material = canonical + "\n" + (task ?? string.Empty).Trim();
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return DirectChatSessionPrefix + Convert.ToHexString(hash).ToLowerInvariant()[..24];
        }

        public static CodingAgentCheckpoint? Load(string sessionId, string workspaceRoot, string task) =>
            LoadFromDirectory(AppPaths.CodingAgentStateDir, sessionId, workspaceRoot, task);

        internal static CodingAgentCheckpoint? LoadFromDirectory(
            string stateDirectory, string sessionId, string workspaceRoot, string task)
        {
            lock (Gate)
            {
                string sanitizedSession = SanitizeSessionId(sessionId);
                string path = PathFor(stateDirectory, sanitizedSession);
                // Only a file that is genuinely absent means "no checkpoint". Any other read failure propagates, because
                // the file may be the write-ahead receipt of a dispatched step and starting fresh could replay that step.
                string? text = ReadTextIfPresent(path);
                if (text == null) return null;
                CodingAgentCheckpoint checkpoint = ParseCheckpoint(path, text, sanitizedSession);
                // A checkpoint owned by another workspace is not corruption, and it is not reused either. It is kept as a
                // copy before this run starts, so the next save of this run cannot replace it without a preserved copy.
                if (!PathMatches(checkpoint.WorkspaceRoot, workspaceRoot))
                {
                    CodingDurableFile.Quarantine(path, "foreign");
                    return null;
                }

                bool isDirectDerivedSession = sanitizedSession.StartsWith(
                    DirectChatSessionPrefix, StringComparison.Ordinal);
                if (isDirectDerivedSession
                    && !string.Equals(checkpoint.Task?.Trim(), task?.Trim(), StringComparison.Ordinal))
                {
                    CodingDurableFile.Quarantine(path, "foreign");
                    return null;
                }

                checkpoint.Task = (task ?? string.Empty).Trim();
                checkpoint.Observations = NormalizeObservations(checkpoint.Observations);
                checkpoint.ConsumedUserInputIds = NormalizeConsumedUserInputIds(checkpoint.ConsumedUserInputIds);
                NormalizeContinuedChildren(checkpoint);
                if (checkpoint.SchemaVersion == 1)
                {
                    checkpoint.VerificationRequiredScopes = checkpoint.VerificationRequired
                        ? new List<string> { "." } : new List<string>();
                    checkpoint.VerificationSatisfiedScopes = checkpoint.VerificationSatisfied
                        ? new List<string> { "." } : new List<string>();
                    checkpoint.SchemaVersion = 2;
                }
                else
                {
                    BridgeLegacyBooleansIntoScopes(checkpoint);
                }
                NormalizeVerificationState(checkpoint);
                // The previous answer-resume path could persist 'verified'
                // scopes after a user reply without a successful verification
                // command. Repair only that identifiable legacy condition.
                // Prefer an extra real test to trusting an unsupported PASS.
                if (checkpoint.VerificationRequired
                    && checkpoint.VerificationSatisfied
                    && !checkpoint.VerifiedByCommand
                    && checkpoint.Observations.Any(value =>
                        value.StartsWith("[USER INPUT ", StringComparison.Ordinal)))
                {
                    checkpoint.VerificationSatisfied = false;
                    checkpoint.VerificationSatisfiedScopes.Clear();
                }
                checkpoint.RecoveryDigest = CodingAgentRecoveryDigest.Build(checkpoint, checkpoint.Observations);
                return checkpoint;
            }
        }

        /// <summary>
        /// Returns the checkpoint text, or null only when the file is genuinely absent. Every other failure, such as
        /// a denied or locked file or a path that is not a file, propagates: the file is then intact and a later
        /// attempt can read it, and the run must not start fresh in the meantime.
        /// </summary>
        private static string? ReadTextIfPresent(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses checkpoint text that must exist. Any defect that makes it untrustworthy throws after the file
        /// is preserved.
        /// </summary>
        private static CodingAgentCheckpoint ParseCheckpoint(string path, string text, string sanitizedSession)
        {
            CodingAgentCheckpoint? checkpoint;
            try
            {
                checkpoint = JsonSerializer.Deserialize<CodingAgentCheckpoint>(text, JsonOptions);
            }
            catch (JsonException)
            {
                throw Unreadable(path, "is not valid JSON");
            }
            if (checkpoint == null) throw Unreadable(path, "holds no checkpoint object");
            if (checkpoint.SchemaVersion is not (1 or 2))
                throw Unreadable(path, "uses unsupported schema version " + checkpoint.SchemaVersion);
            if (!string.Equals(SanitizeSessionId(checkpoint.SessionId), sanitizedSession, StringComparison.Ordinal))
                throw Unreadable(path, "records a different session identity");
            if (string.IsNullOrWhiteSpace(checkpoint.WorkspaceRoot))
                throw Unreadable(path, "records no workspace");
            return checkpoint;
        }

        private static CodingAgentCheckpointCorruptException Unreadable(string path, string defect)
        {
            string? quarantine = null;
            try
            {
                quarantine = CodingDurableFile.Quarantine(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still fail closed below: the original is never overwritten, so no evidence is lost.
            }
            return new CodingAgentCheckpointCorruptException(
                "The coding checkpoint " + Path.GetFileName(path) + " " + defect + ". "
                    + (quarantine == null
                        ? "A preserved copy could not be written. "
                        : "A preserved copy is at " + quarantine + ". ")
                    + "The run was stopped, so a step that may already have run is neither replayed nor overwritten. "
                    + "No action in the app clears it: inspect the workspace, then delete " + Path.GetFileName(path)
                    + " from the coding state folder by hand to start that task over.",
                path, quarantine);
        }

        public static void Save(CodingAgentCheckpoint checkpoint)
        {
            AppPaths.EnsureCreated();
            SaveToDirectory(AppPaths.CodingAgentStateDir, checkpoint);
        }

        internal static void SaveToDirectory(string stateDirectory, CodingAgentCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (string.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A checkpoint directory is required.", nameof(stateDirectory));

            lock (Gate)
            {
                Directory.CreateDirectory(stateDirectory);
                checkpoint.SchemaVersion = 2;
                checkpoint.SessionId = SanitizeSessionId(checkpoint.SessionId);
                checkpoint.Observations = NormalizeObservations(checkpoint.Observations);
                checkpoint.ConsumedUserInputIds = NormalizeConsumedUserInputIds(checkpoint.ConsumedUserInputIds);
                NormalizeContinuedChildren(checkpoint);
                BridgeLegacyBooleansIntoScopes(checkpoint);
                NormalizeVerificationState(checkpoint);
                checkpoint.RecoveryDigest = CodingAgentRecoveryDigest.Build(checkpoint, checkpoint.Observations);
                checkpoint.UpdatedUtc = DateTime.UtcNow;

                string path = PathFor(stateDirectory, checkpoint.SessionId);
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    // Write-ahead receipt: its bytes must be on stable storage before they replace the previous one.
                    CodingDurableFile.WriteText(temporary, JsonSerializer.Serialize(checkpoint, JsonOptions));
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        public static void Clear(string sessionId) => ClearFromDirectory(AppPaths.CodingAgentStateDir, sessionId);

        internal static void ClearFromDirectory(string stateDirectory, string sessionId)
        {
            lock (Gate)
            {
                try
                {
                    string path = PathFor(stateDirectory, sessionId);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
        }

        internal static string PathFor(string sessionId) => PathFor(AppPaths.CodingAgentStateDir, sessionId);

        internal static string PathFor(string stateDirectory, string sessionId)
        {
            string safe = SanitizeSessionId(sessionId);
            return Path.Combine(stateDirectory, safe + ".json");
        }

        internal static string SanitizeSessionId(string? value)
        {
            string input = string.IsNullOrWhiteSpace(value) ? "session" : value.Trim();
            var builder = new StringBuilder(Math.Min(input.Length, 96));
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_') builder.Append(c);
                if (builder.Length >= 96) break;
            }
            if (builder.Length > 0) return builder.ToString();
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant()[..24];
        }

        /// <summary>
        /// True when an answer with this question id was already appended to the evidence log. The id set is kept
        /// apart from the bounded observation window, so the answer is still recognised after its marker line is trimmed.
        /// </summary>
        internal static bool HasConsumedUserInput(CodingAgentCheckpoint checkpoint, string? questionId) =>
            !string.IsNullOrEmpty(questionId)
            && checkpoint.ConsumedUserInputIds.Contains(questionId, StringComparer.Ordinal);

        internal static void RecordConsumedUserInput(CodingAgentCheckpoint checkpoint, string? questionId)
        {
            if (string.IsNullOrEmpty(questionId) || HasConsumedUserInput(checkpoint, questionId)) return;
            checkpoint.ConsumedUserInputIds.Add(questionId);
            if (checkpoint.ConsumedUserInputIds.Count > MaximumConsumedUserInputs)
                checkpoint.ConsumedUserInputIds.RemoveRange(0, checkpoint.ConsumedUserInputIds.Count - MaximumConsumedUserInputs);
        }

        /// <summary>An answered user exchange from the orchestrator, reduced to the fields the evidence log records.</summary>
        internal readonly record struct UserAnswerExchange(string? QuestionId, string? Question, string? Answer);

        /// <summary>
        /// Appends each answered user exchange to the evidence log the first time its question id is seen, and records
        /// the id as consumed. An exchange is already consumed when its id is in the durable set, or when its marker line
        /// is still in the log. That second case covers checkpoints written before the set existed, and the id is then
        /// copied into the set, so the answer stays consumed after its marker line is trimmed. Only a newly consumed
        /// answer resets the no-progress watchdog, so a repeated answer cannot extend an unproductive run. Returns true
        /// when at least one answer was newly consumed.
        /// </summary>
        internal static bool ConsumeUserAnswers(
            CodingAgentCheckpoint checkpoint, IEnumerable<UserAnswerExchange> exchanges, List<string> log)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            ArgumentNullException.ThrowIfNull(exchanges);
            ArgumentNullException.ThrowIfNull(log);

            bool consumedNewAnswer = false;
            foreach (UserAnswerExchange exchange in exchanges)
            {
                string marker = "[USER INPUT " + exchange.QuestionId + "]";
                bool markerLogged = log.Any(entry => entry.StartsWith(marker, StringComparison.Ordinal));
                if (markerLogged) RecordConsumedUserInput(checkpoint, exchange.QuestionId);
                bool alreadyConsumed = markerLogged || HasConsumedUserInput(checkpoint, exchange.QuestionId);
                if (!CodingVerificationCommandPolicy.IsNewUserAnswer(alreadyConsumed, exchange.Answer)) continue;

                log.Add(marker + " Question: " + ClipForEvidence(exchange.Question, 1_000)
                    + "\nAnswer: " + ClipForEvidence(exchange.Answer, 2_000));
                RecordConsumedUserInput(checkpoint, exchange.QuestionId);
                consumedNewAnswer = true;
            }
            // An answer can resolve a question or decision, NOT prove that already-edited code built or passed
            // tests, so every verification scope and command receipt is retained.
            if (consumedNewAnswer) ResumeAfterUserAnswer(checkpoint);
            return consumedNewAnswer;
        }

        private static string ClipForEvidence(string? value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= maximum ? value : value.Substring(0, maximum) + "\n… (truncated)";
        }

        internal static List<string> NormalizeConsumedUserInputIds(IEnumerable<string>? ids) =>
            (ids ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaximumConsumedUserInputs)
                .ToList();

        internal static List<string> NormalizeObservations(IEnumerable<string>? observations) =>
            (observations ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Length <= MaximumObservationCharacters
                    ? value
                    : CompactObservation(value))
                .TakeLast(MaximumObservations)
                .ToList();

        internal const int MaximumExecutedChecks = 16;

        private static void NormalizeContinuedChildren(CodingAgentCheckpoint checkpoint)
        {
            checkpoint.ExecutedChecks = (checkpoint.ExecutedChecks ?? new List<CodingExecutedCheckRecord>())
                .Where(check => check != null && !string.IsNullOrWhiteSpace(check.Command))
                .Select(check => new CodingExecutedCheckRecord
                {
                    Command = check.Command.Trim().Length <= 500 ? check.Command.Trim() : check.Command.Trim()[..500],
                    ExitCode = check.ExitCode,
                    Scope = string.IsNullOrWhiteSpace(check.Scope) ? "." : check.Scope.Trim()
                })
                .TakeLast(MaximumExecutedChecks)
                .ToList();
            checkpoint.ContinuedChildTaskIds = CodingAgentContinuation.NormalizeIds(checkpoint.ContinuedChildTaskIds);
            checkpoint.CollectedChildTaskIds = CodingAgentContinuation.NormalizeIds(checkpoint.CollectedChildTaskIds)
                .Where(id => checkpoint.ContinuedChildTaskIds.Contains(id, StringComparer.Ordinal))
                .ToList();
        }

        private static string CompactObservation(string value)
        {
            const string note = "\n… (checkpoint truncated)\n";
            const int tail = 500;
            int front = MaximumObservationCharacters - tail - note.Length;
            return value[..front] + note + value.Substring(value.Length - tail);
        }

        internal static List<string> NormalizeScopes(IEnumerable<string>? scopes) =>
            (scopes ?? Array.Empty<string>())
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim().Replace('\\', '/'))
                .Where(scope => scope.Length <= 64 && scope != ".." && !scope.Contains("../", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumVerificationScopes)
                .ToList();

        private static void BridgeLegacyBooleansIntoScopes(CodingAgentCheckpoint checkpoint)
        {
            checkpoint.VerificationRequiredScopes ??= new List<string>();
            checkpoint.VerificationSatisfiedScopes ??= new List<string>();
            if (checkpoint.VerificationRequired && checkpoint.VerificationRequiredScopes.Count == 0)
                checkpoint.VerificationRequiredScopes.Add(".");
            if (checkpoint.VerificationSatisfied
                && checkpoint.VerificationSatisfiedScopes.Count == 0
                && checkpoint.VerificationRequiredScopes.Count > 0)
            {
                checkpoint.VerificationSatisfiedScopes.AddRange(checkpoint.VerificationRequiredScopes);
            }
        }

        private static void NormalizeVerificationState(CodingAgentCheckpoint checkpoint)
        {
            checkpoint.VerificationRequiredScopes = NormalizeScopes(checkpoint.VerificationRequiredScopes);
            checkpoint.VerificationSatisfiedScopes = NormalizeScopes(checkpoint.VerificationSatisfiedScopes)
                .Where(scope => checkpoint.VerificationRequiredScopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
                .ToList();
            checkpoint.VerificationRequired = checkpoint.VerificationRequiredScopes.Count > 0;
            checkpoint.VerificationSatisfied = checkpoint.VerificationRequiredScopes.Count == 0
                || checkpoint.VerificationRequiredScopes.All(scope =>
                    checkpoint.VerificationSatisfiedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase));
        }

        private static bool PathMatches(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
