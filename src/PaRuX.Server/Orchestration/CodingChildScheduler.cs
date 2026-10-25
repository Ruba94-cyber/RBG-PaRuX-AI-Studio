using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Server.Orchestration
{
    internal enum CodingChildState { Running, Completed, Failed, Cancelled }

    internal interface ICodingChildRunner
    {
        Task CancelAsync(string childId, CancellationToken ct);
    }

    internal sealed record CodingChildTimelineEvent(DateTimeOffset AtUtc, string Kind, string Subject, string Detail);

    internal sealed record CodingParentStepDecision(bool CanRun, IReadOnlyList<string> BlockingChildIds, string Reason);

    /// <summary>
    /// Dependency-safe parent continuation. The parent may run steps while delegated children are live,
    /// but a step that touches (by declared file set, overlapping paths or directory prefixes) or reads the
    /// output of an unfinished child waits. A child with no declared file set is treated as touching everything.
    /// STOP cancels every live child. All transitions are appended to a timeline for display in the task view.
    /// </summary>
    internal sealed class CodingChildScheduler
    {
        private sealed class Child
        {
            public string Id = string.Empty;
            public string[] Files = Array.Empty<string>();
            public CodingChildState State;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Child> _children = new(StringComparer.Ordinal);
        private readonly List<CodingChildTimelineEvent> _timeline = new();
        private readonly ICodingChildRunner _runner;
        private readonly TimeProvider _clock;
        private bool _stopped;

        public CodingChildScheduler(ICodingChildRunner runner, TimeProvider? clock = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _clock = clock ?? TimeProvider.System;
        }

        public bool IsStopped { get { lock (_gate) return _stopped; } }

        public IReadOnlyList<CodingChildTimelineEvent> Timeline { get { lock (_gate) return _timeline.ToArray(); } }

        public bool RegisterChild(string childId, IEnumerable<string>? declaredFiles)
        {
            lock (_gate)
            {
                if (_stopped) { Log("child-rejected", childId, "parent stopped"); return false; }
                string[] files = (declaredFiles ?? Array.Empty<string>()).Select(NormalizePath).Where(p => p.Length > 0).ToArray();
                _children[childId] = new Child { Id = childId, Files = files, State = CodingChildState.Running };
                Log("child-started", childId, files.Length == 0 ? "no declared files (blocks all file steps)" : string.Join(", ", files));
                return true;
            }
        }

        public void ReportChildState(string childId, CodingChildState state)
        {
            lock (_gate)
            {
                if (!_children.TryGetValue(childId, out Child? c) || c.State != CodingChildState.Running) return;
                c.State = state;
                Log("child-" + state.ToString().ToLowerInvariant(), childId, string.Empty);
            }
        }

        public CodingParentStepDecision DecideStep(string stepId, IEnumerable<string>? stepFiles, bool readsChildOutput)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    Log("step-refused", stepId, "parent stopped");
                    return new CodingParentStepDecision(false, Array.Empty<string>(), "parent stopped");
                }
                string[] files = (stepFiles ?? Array.Empty<string>()).Select(NormalizePath).Where(p => p.Length > 0).ToArray();
                List<string> blockers = FindBlockers(files, readsChildOutput,
                    _children.Values.Where(x => x.State == CodingChildState.Running).Select(c => (c.Id, (IReadOnlyList<string>)c.Files))).ToList();
                if (blockers.Count == 0)
                {
                    Log("step-run", stepId, _children.Values.Any(x => x.State == CodingChildState.Running) ? "independent of live children" : "no live children");
                    return new CodingParentStepDecision(true, blockers, "independent");
                }
                Log("step-wait", stepId, "waiting for " + string.Join(", ", blockers));
                return new CodingParentStepDecision(false, blockers, "depends on unfinished child");
            }
        }

        /// <summary>Parent STOP: no further children/steps; every live child is cancelled even if one cancel throws.</summary>
        public async Task StopAsync(CancellationToken ct = default)
        {
            string[] live;
            lock (_gate)
            {
                _stopped = true;
                live = _children.Values.Where(c => c.State == CodingChildState.Running).Select(c => c.Id).ToArray();
                Log("parent-stop", "parent", live.Length + " live child(ren)");
            }
            foreach (string id in live)
            {
                try
                {
                    await _runner.CancelAsync(id, ct).ConfigureAwait(false);
                    ReportChildState(id, CodingChildState.Cancelled);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (_gate) Log("child-cancel-failed", id, ex.Message);
                }
            }
        }

        /// <summary>
        /// Pure dependency rule shared by the scheduler and <c>AgentParentContinuationPolicy</c>: a step is
        /// blocked by a live child when it reads child output, or touches files and the child's declared file
        /// set overlaps (an empty declared set means "unknown", which blocks every file-touching step).
        /// </summary>
        internal static IReadOnlyList<string> FindBlockers(
            IReadOnlyList<string> normalizedStepFiles, bool readsChildOutput,
            IEnumerable<(string Id, IReadOnlyList<string> Files)> liveChildren)
        {
            var blockers = new List<string>();
            foreach ((string id, IReadOnlyList<string> childFiles) in liveChildren)
            {
                bool dependent = readsChildOutput
                    || (normalizedStepFiles.Count > 0
                        && (childFiles.Count == 0 || normalizedStepFiles.Any(f => childFiles.Any(cf => Overlaps(f, cf)))));
                if (dependent) blockers.Add(id);
            }
            return blockers;
        }

        internal static string[] NormalizeFiles(IEnumerable<string>? files) =>
            (files ?? Array.Empty<string>()).Select(NormalizePath).Where(p => p.Length > 0).ToArray();

        private void Log(string kind, string subject, string detail) =>
            _timeline.Add(new CodingChildTimelineEvent(_clock.GetUtcNow(), kind, subject, detail));

        private static string NormalizePath(string p) =>
            (p ?? string.Empty).Trim().Replace('\\', '/').TrimStart('.', '/').TrimEnd('/').ToLowerInvariant();

        private static bool Overlaps(string a, string b) =>
            a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
    }
}
