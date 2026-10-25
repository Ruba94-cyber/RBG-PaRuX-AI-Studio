using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>Where wall-clock time goes inside one coding-agent step.</summary>
    public enum CodingAgentPhase
    {
        /// <summary>Waiting for the first token: Core queueing, model swap/reload and prompt prefill (not separable from this side of the API).</summary>
        FirstToken,
        /// <summary>Token generation after the first token arrived.</summary>
        Generate,
        /// <summary>Compiling repository context and recalling/remembering durable task memory.</summary>
        Context,
        /// <summary>File and artifact reads.</summary>
        Read,
        /// <summary>grep/glob/directory search.</summary>
        Search,
        /// <summary>git status/diff/log.</summary>
        Git,
        /// <summary>File mutations.</summary>
        Write,
        /// <summary>Sandboxed commands (build, test, restore).</summary>
        Command,
        /// <summary>Browser, delegation and user-question coordination.</summary>
        Coordination,
        /// <summary>Checkpoint persistence.</summary>
        Checkpoint
    }

    /// <summary>
    /// Accumulates per-phase wall-clock time for coding-agent steps and renders it as one stable,
    /// parseable task-log line (<c>[phase-timing] step=3 total=812ms read=40ms ...</c>), which
    /// benchmarks/coding-agent/analyze-task-latency.py aggregates from the durable task log. Phases
    /// are measured, never estimated: a step reports exactly the time spent inside Measure scopes,
    /// and <c>other</c> is the unattributed remainder so the numbers always add up to the step total.
    /// Not thread-safe by design for the step itself; parallel batch reads are measured by the
    /// caller as one Read scope (wall time), not summed per item.
    /// </summary>
    public sealed class CodingAgentPhaseTimer
    {
        public const string LinePrefix = "[phase-timing]";
        public const string TotalLinePrefix = "[phase-timing-total]";

        private readonly Func<long> _clock;
        private readonly double _ticksPerMs;
        private readonly long[] _step = new long[Enum.GetValues<CodingAgentPhase>().Length];
        private readonly long[] _run = new long[Enum.GetValues<CodingAgentPhase>().Length];
        private long _stepStart;
        private long _runStart;
        private int _steps;
        private int _stepNumber;
        private bool _stepOpen;

        public CodingAgentPhaseTimer() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

        /// <summary>Test seam: a deterministic clock with <paramref name="ticksPerSecond"/> ticks per second.</summary>
        public CodingAgentPhaseTimer(Func<long> clock, long ticksPerSecond)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _ticksPerMs = ticksPerSecond / 1000.0;
            _runStart = _clock();
        }

        public int Steps => _steps;

        public void BeginStep(int stepNumber)
        {
            // An unfinished previous step (e.g. an exception path) is closed, never silently dropped.
            if (_stepOpen) EndStep();
            Array.Clear(_step);
            _stepNumber = stepNumber;
            _stepStart = _clock();
            _stepOpen = true;
        }

        public void Add(CodingAgentPhase phase, long elapsedTicks)
        {
            if (elapsedTicks < 0) elapsedTicks = 0;
            _step[(int)phase] += elapsedTicks;
        }

        public async Task<T> MeasureAsync<T>(CodingAgentPhase phase, Func<Task<T>> work)
        {
            long start = _clock();
            try { return await work().ConfigureAwait(false); }
            finally { Add(phase, _clock() - start); }
        }

        public async Task MeasureAsync(CodingAgentPhase phase, Func<Task> work)
        {
            long start = _clock();
            try { await work().ConfigureAwait(false); }
            finally { Add(phase, _clock() - start); }
        }

        public T Measure<T>(CodingAgentPhase phase, Func<T> work)
        {
            long start = _clock();
            try { return work(); }
            finally { Add(phase, _clock() - start); }
        }

        /// <summary>Closes the step, folds it into the run totals and returns the log line.</summary>
        public string EndStep()
        {
            if (!_stepOpen) return string.Empty;
            long total = Math.Max(0, _clock() - _stepStart);
            string line = Render(LinePrefix, "step=" + _stepNumber.ToString(CultureInfo.InvariantCulture), total, _step);
            for (int i = 0; i < _step.Length; i++) _run[i] += _step[i];
            _steps++;
            _stepOpen = false;
            return line;
        }

        /// <summary>Run-level summary line over every closed step (for the end of a run/segment).</summary>
        public string RenderRunTotal()
        {
            long total = Math.Max(0, _clock() - _runStart);
            return Render(TotalLinePrefix, "steps=" + _steps.ToString(CultureInfo.InvariantCulture), total, _run);
        }

        /// <summary>Maps a coding action name to the phase its execution time belongs to.</summary>
        public static CodingAgentPhase PhaseForAction(string? action) =>
            (action ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "read_file" or "read_file_range" or "read_file_lines" or "list_artifacts"
                    or "read_artifact" or "search_artifact" or "list_directory" or "batch_read" => CodingAgentPhase.Read,
                "grep_search" or "glob_search" => CodingAgentPhase.Search,
                "git_status" or "git_diff" or "git_log" => CodingAgentPhase.Git,
                "replace_range" or "write_file" or "create_file" or "delete_file" or "rename_file" => CodingAgentPhase.Write,
                "run_command" or "restore_packages" => CodingAgentPhase.Command,
                _ => CodingAgentPhase.Coordination
            };

        /// <summary>Parses a line produced by <see cref="EndStep"/> / <see cref="RenderRunTotal"/>.</summary>
        public static bool TryParse(string? line, out IReadOnlyDictionary<string, long> values)
        {
            values = new Dictionary<string, long>();
            if (string.IsNullOrWhiteSpace(line)) return false;
            string trimmed = line.Trim();
            string prefix = trimmed.StartsWith(TotalLinePrefix, StringComparison.Ordinal) ? TotalLinePrefix
                : trimmed.StartsWith(LinePrefix, StringComparison.Ordinal) ? LinePrefix : string.Empty;
            if (prefix.Length == 0) return false;
            var parsed = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string token in trimmed[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) return false;
                string key = token[..eq];
                string raw = token[(eq + 1)..];
                if (raw.EndsWith("ms", StringComparison.Ordinal)) raw = raw[..^2];
                if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long number)) return false;
                parsed[key] = number;
            }
            if (!parsed.ContainsKey("total")) return false;
            values = parsed;
            return true;
        }

        private string Render(string prefix, string head, long totalTicks, long[] phases)
        {
            long totalMs = ToMs(totalTicks);
            var sb = new StringBuilder(prefix).Append(' ').Append(head)
                .Append(" total=").Append(totalMs.ToString(CultureInfo.InvariantCulture)).Append("ms");
            long attributed = 0;
            foreach (CodingAgentPhase phase in Enum.GetValues<CodingAgentPhase>())
            {
                long ms = ToMs(phases[(int)phase]);
                attributed += ms;
                sb.Append(' ').Append(Name(phase)).Append('=').Append(ms.ToString(CultureInfo.InvariantCulture)).Append("ms");
            }
            // Rounding or overlapping scopes can push the attributed sum past the total; clamp so the
            // remainder is never negative, and never hide the overlap (it is visible in the phases).
            sb.Append(" other=").Append(Math.Max(0, totalMs - attributed).ToString(CultureInfo.InvariantCulture)).Append("ms");
            return sb.ToString();
        }

        private long ToMs(long ticks) => (long)Math.Round(ticks / _ticksPerMs, MidpointRounding.AwayFromZero);

        internal static string Name(CodingAgentPhase phase) => phase switch
        {
            CodingAgentPhase.FirstToken => "first_token",
            _ => phase.ToString().ToLowerInvariant()
        };
    }
}
