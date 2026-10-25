using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Turns the coding agent's "Step N: ..." log lines into a throttled, ordered task stage so Chat,
    /// Settings and the Android client can show live movement. Updates are chained so a late "running"
    /// stage can never land after the worker's terminal status (call <see cref="DrainAsync"/> first).
    /// </summary>
    public sealed class CodingStepStageReporter
    {
        private static readonly Regex StepLine = new(@"^Step (?<n>\d+):\s*(?<text>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private const int MaxStageText = 140;

        private readonly TimeSpan _minimumInterval;
        private readonly object _gate = new();
        private DateTime _lastUtc = DateTime.MinValue;
        private int _lastStep;
        private Task _pending = Task.CompletedTask;
        private bool _stopped;

        public CodingStepStageReporter(TimeSpan minimumInterval) => _minimumInterval = minimumInterval;

        /// <summary>Progress grows with steps but never reaches the verification band (90%).</summary>
        public static int PercentForStep(int step) =>
            Math.Clamp(6 + (int)Math.Round(79.0 * step / (step + 12.0)), 6, 85);

        public bool TryBuild(string? line, DateTime utcNow, out string stage, out int percent)
        {
            stage = string.Empty;
            percent = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;
            Match match = StepLine.Match(line.Trim());
            if (!match.Success || !int.TryParse(match.Groups["n"].Value, out int step)) return false;
            lock (_gate)
            {
                if (_stopped) return false;
                // A delayed stream callback from a previous attempt cannot
                // regress the visible task stage. The durable execution log
                // remains authoritative; this is best-effort progress only.
                if (step < _lastStep) return false;
                // Always report a new step promptly after the interval; drop bursts within one step.
                if (step == _lastStep && utcNow - _lastUtc < _minimumInterval) return false;
                if (utcNow - _lastUtc < _minimumInterval) return false;
                _lastUtc = utcNow;
                _lastStep = step;
            }
            string text = match.Groups["text"].Value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > MaxStageText) text = text[..MaxStageText] + "…";
            stage = "Coding step " + step + (text.Length == 0 ? string.Empty : ": " + text);
            percent = PercentForStep(step);
            return true;
        }

        /// <summary>Runs <paramref name="update"/> after every earlier update; failures are ignored.</summary>
        public void Enqueue(Func<Task> update)
        {
            lock (_gate)
            {
                if (_stopped) return;
                _pending = _pending.ContinueWith(async _ =>
                {
                    try { await update().ConfigureAwait(false); }
                    catch { /* Stage feedback is best-effort; the task log carries the same line. */ }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>Stops new stage updates and waits for queued ones before a terminal status is written.</summary>
        public Task DrainAsync()
        {
            lock (_gate)
            {
                _stopped = true;
                return _pending;
            }
        }
    }
}
