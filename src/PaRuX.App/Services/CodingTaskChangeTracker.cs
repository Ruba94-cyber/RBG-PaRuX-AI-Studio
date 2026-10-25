using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Where review status sections are published (Core API in production, a fake in tests).</summary>
    public interface ICodingReviewStatusSink
    {
        Task PutSectionAsync(string taskId, string section, object value, CancellationToken ct);
        Task AppendLogAsync(string taskId, string message, CancellationToken ct);
    }

    /// <summary>
    /// Desktop-side owner of a coding task's review findings ledger and of the exact set of files the agent
    /// wrote. <see cref="AgentCodingWorkerService"/> calls it (a) around every agent file write, (b) after
    /// each patch update to reconcile findings and publish them, (c) after a failed verification to offer
    /// (or, when already authorized, perform) a revert of the agent's own changes, and (d) while polling to
    /// honour the desktop owner's "Revert my changes" request. User changes are never touched.
    /// </summary>
    public sealed class CodingTaskChangeTracker
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Regex FindingLine = new(
            @"^\s*(?:[-*+]\s+|\d+[.)]\s+)?(?:\[(?<s>critical|high|medium|low|info|error|warning)\]\s*|(?<s2>critical|high|medium|low|info|error|warning)\s*[:\-]\s*)?`?(?<f>[A-Za-z0-9_.@+\-/\\]+\.[A-Za-z0-9]+):(?<l>\d+)(?:-(?<e>\d+))?`?\s*[:\-]?\s*(?<m>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static CodingTaskChangeTracker Current { get; private set; } = new CodingTaskChangeTracker(
            Path.Combine(Path.GetTempPath(), "parux-coding-review"), null);

        /// <summary>Production composition root: durable state lives in the app's coding-agent state directory.</summary>
        public static void Configure(string stateDirectory, ICodingReviewStatusSink? sink) =>
            Current = new CodingTaskChangeTracker(stateDirectory, sink);

        private sealed class TaskState
        {
            public required string Root;
            public required CodingOwnChangeReverter Reverter;
            public required CodingReviewFindingLedger Ledger;
            public bool SeededFromRepairContext;
        }

        private readonly ConcurrentDictionary<string, TaskState> _tasks = new(StringComparer.Ordinal);
        private readonly string _stateDirectory;
        private readonly ICodingReviewStatusSink? _sink;
        private readonly TimeProvider _clock;

        public CodingTaskChangeTracker(string stateDirectory, ICodingReviewStatusSink? sink, TimeProvider? clock = null)
        {
            _stateDirectory = stateDirectory;
            _sink = sink;
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>No task/mode currently authorizes reverting the agent's own changes without asking.</summary>
        public static bool IsRevertAuthorizedByMode(string? permissionMode) => false;

        public int OwnChangeCount(string taskId) => _tasks.TryGetValue(taskId, out TaskState? s) ? s.Reverter.TrackedCount : 0;

        public CodingReviewFindingLedger LedgerFor(string taskId, string workspaceRoot) => State(taskId, workspaceRoot).Ledger;

        public void BeforeWrite(string taskId, string workspaceRoot, string relativePath)
        {
            try { State(taskId, workspaceRoot).Reverter.BeforeWrite(relativePath); }
            catch (InvalidOperationException) { /* outside the workspace: the write itself is refused elsewhere */ }
        }

        public void AfterWrite(string taskId, string workspaceRoot, string relativePath) =>
            State(taskId, workspaceRoot).Reverter.AfterWrite(relativePath);

        /// <summary>Parses "path:line[-end] message" lines (optionally prefixed by a severity) into findings.</summary>
        public int SeedFindingsFromText(string taskId, string workspaceRoot, string? reviewText)
        {
            if (string.IsNullOrWhiteSpace(reviewText)) return 0;
            TaskState state = State(taskId, workspaceRoot);
            int added = 0;
            foreach (string line in reviewText.Replace("\r\n", "\n").Split('\n'))
            {
                Match m = FindingLine.Match(line);
                if (!m.Success) continue;
                string rel = m.Groups["f"].Value.Replace('\\', '/');
                if (Path.IsPathRooted(rel) || rel.Split('/').Any(p => p == ".." || p == ".git")) continue;
                string full = Path.GetFullPath(Path.Combine(state.Root, rel));
                if (!full.StartsWith(Path.GetFullPath(state.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                int start = int.Parse(m.Groups["l"].Value);
                int end = m.Groups["e"].Success ? int.Parse(m.Groups["e"].Value) : start;
                string text;
                try { text = File.ReadAllText(full); } catch (IOException) { continue; }
                int lineCount = text.Replace("\r\n", "\n").Split('\n').Length;
                if (start < 1 || end < start || end > lineCount) continue;
                string severity = (m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["s2"].Success ? m.Groups["s2"].Value : "medium").ToLowerInvariant();
                int before = state.Ledger.Findings.Count;
                state.Ledger.Add(rel, start, end, severity, m.Groups["m"].Value.Trim(), text, _clock.GetUtcNow());
                if (state.Ledger.Findings.Count > before) added++;
            }
            Persist(taskId, state);
            return added;
        }

        /// <summary>
        /// Called after every patch update: seeds findings from the repair context once, re-anchors every
        /// finding against the files as they are now, persists the ledger and publishes it. Findings are
        /// never dropped; a finding whose code changed is reported as fixed or outdated with an audit trail.
        /// </summary>
        public async Task<IReadOnlyList<CodingFindingReconciliation>> ReconcileAndPublishAsync(
            string taskId, string workspaceRoot, string? repairContext, CancellationToken ct)
        {
            TaskState state = State(taskId, workspaceRoot);
            if (!state.SeededFromRepairContext)
            {
                state.SeededFromRepairContext = true;
                SeedFindingsFromText(taskId, workspaceRoot, repairContext);
            }
            var files = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string rel in state.Ledger.Findings.Select(f => f.File).Distinct(StringComparer.Ordinal))
            {
                string full = Path.GetFullPath(Path.Combine(state.Root, rel));
                try { files[rel] = File.Exists(full) ? File.ReadAllText(full) : null; }
                catch (IOException) { files[rel] = null; }
            }
            IReadOnlyList<CodingFindingReconciliation> result = state.Ledger.Reconcile(files, _clock.GetUtcNow());
            Persist(taskId, state);
            if (_sink != null && state.Ledger.Findings.Count > 0)
                await _sink.PutSectionAsync(taskId, "findings", ToDtos(state.Ledger), ct).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Verification outcome hook. Returns the decision taken: NotNeeded when it passed or the agent changed
        /// nothing, Offer by default (published so the card shows "Revert my changes"), Perform only when
        /// <paramref name="autoRevert"/> is true (the task/mode already authorizes it).
        /// </summary>
        public async Task<CodingRevertDecision> OnVerificationOutcomeAsync(
            string taskId, bool verificationPassed, bool autoRevert, CancellationToken ct)
        {
            if (!_tasks.TryGetValue(taskId, out TaskState? state)) return CodingRevertDecision.NotNeeded;
            int own = state.Reverter.TrackedCount;
            CodingRevertDecision decision = CodingOwnChangeReverter.DecideAfterVerification(verificationPassed, own > 0, autoRevert);
            if (decision == CodingRevertDecision.Perform)
            {
                IReadOnlyList<CodingRevertItem> items = state.Reverter.Revert();
                await PublishRevertAsync(taskId, new AgentOwnChangeRevertStateDto
                {
                    Offered = false, OwnChangedFiles = own, AutoAuthorized = true, Requested = true, Completed = true,
                    RequestedUtc = _clock.GetUtcNow().UtcDateTime, Items = ToDtos(items)
                }, ct).ConfigureAwait(false);
                if (_sink != null)
                    await _sink.AppendLogAsync(taskId, "Verification failed; reverted the agent's own changes ("
                        + Summarize(items) + "). User changes were not touched.", ct).ConfigureAwait(false);
            }
            else if (decision == CodingRevertDecision.Offer)
            {
                await PublishRevertAsync(taskId, new AgentOwnChangeRevertStateDto { Offered = true, OwnChangedFiles = own }, ct)
                    .ConfigureAwait(false);
                if (_sink != null)
                    await _sink.AppendLogAsync(taskId, "Verification failed after the agent changed " + own
                        + " file(s). Reverting only the agent's own changes is available from the task card.", ct).ConfigureAwait(false);
            }
            return decision;
        }

        /// <summary>Honours a desktop-owner revert request found in the task's published review status.</summary>
        public async Task<bool> HandleRevertRequestAsync(string taskId, string? reviewStatusJson, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reviewStatusJson)) return false;
            AgentOwnChangeRevertStateDto? requested;
            try { requested = JsonSerializer.Deserialize<AgentTaskReviewStatusDto>(reviewStatusJson, Json)?.Revert; }
            catch (JsonException) { return false; }
            if (requested == null || !requested.Requested || requested.Completed) return false;

            if (!_tasks.TryGetValue(taskId, out TaskState? state) || state.Reverter.TrackedCount == 0)
            {
                // Honest failure: the in-memory snapshot is gone (desktop restarted); do not guess with git.
                await PublishRevertAsync(taskId, new AgentOwnChangeRevertStateDto
                {
                    Offered = false, OwnChangedFiles = requested.OwnChangedFiles, Requested = true, Completed = true,
                    RequestedUtc = requested.RequestedUtc,
                    Items = new List<AgentRevertItemDto> { new() { Path = "*", Outcome = "no-recorded-changes" } }
                }, ct).ConfigureAwait(false);
                if (_sink != null)
                    await _sink.AppendLogAsync(taskId, "Revert requested but the agent's own change snapshot is no longer available; nothing was modified.", ct).ConfigureAwait(false);
                return true;
            }

            int own = state.Reverter.TrackedCount;
            IReadOnlyList<CodingRevertItem> items = state.Reverter.Revert();
            await PublishRevertAsync(taskId, new AgentOwnChangeRevertStateDto
            {
                Offered = false, OwnChangedFiles = own, Requested = true, Completed = true,
                RequestedUtc = requested.RequestedUtc, Items = ToDtos(items)
            }, ct).ConfigureAwait(false);
            if (_sink != null)
                await _sink.AppendLogAsync(taskId, "Reverted the agent's own changes (" + Summarize(items)
                    + "). Files changed by the user after the agent were left untouched.", ct).ConfigureAwait(false);
            // Re-anchor findings against the reverted tree.
            await ReconcileAndPublishAsync(taskId, state.Root, null, ct).ConfigureAwait(false);
            return true;
        }

        public void Forget(string taskId) => _tasks.TryRemove(taskId, out _);

        private TaskState State(string taskId, string workspaceRoot) =>
            _tasks.GetOrAdd(taskId, id => new TaskState
            {
                Root = Path.GetFullPath(workspaceRoot),
                Reverter = new CodingOwnChangeReverter(workspaceRoot),
                Ledger = LoadLedger(id)
            });

        private CodingReviewFindingLedger LoadLedger(string taskId)
        {
            CodingReviewFindingLedger ledger = CodingReviewFindingLedger.Load(LedgerPath(taskId));
            ledger.TaskId = taskId;
            return ledger;
        }

        private string LedgerPath(string taskId) =>
            Path.Combine(_stateDirectory, new string(taskId.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray()) + ".findings.json");

        private void Persist(string taskId, TaskState state)
        {
            try { state.Ledger.Save(LedgerPath(taskId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; in-memory state stays authoritative */ }
        }

        private Task PublishRevertAsync(string taskId, AgentOwnChangeRevertStateDto dto, CancellationToken ct) =>
            _sink == null ? Task.CompletedTask : _sink.PutSectionAsync(taskId, "revert", dto, ct);

        private static List<AgentReviewFindingStatusDto> ToDtos(CodingReviewFindingLedger ledger) =>
            ledger.Findings.Select(f => new AgentReviewFindingStatusDto
            {
                Id = f.Id, File = f.File, StartLine = f.StartLine, EndLine = f.EndLine, Severity = f.Severity,
                Message = f.Message, State = f.State.ToString().ToLowerInvariant(), OutdatedReason = f.OutdatedReason
            }).ToList();

        private static List<AgentRevertItemDto> ToDtos(IReadOnlyList<CodingRevertItem> items) =>
            items.Select(i => new AgentRevertItemDto { Path = i.Path, Outcome = i.Outcome.ToString() }).ToList();

        private static string Summarize(IReadOnlyList<CodingRevertItem> items) =>
            string.Join(", ", items.GroupBy(i => i.Outcome).Select(g => g.Count() + " " + g.Key));
    }
}
