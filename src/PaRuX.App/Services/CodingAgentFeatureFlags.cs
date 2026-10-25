using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Switches for the coding-agent speed features (batched reads, parent continuation). The
    /// product setting (AppSettings.CodingBatchReadEnabled / CodingParentContinuationEnabled, both
    /// default ON, in settings.json) is the only control an installed product honours.
    ///
    /// For before/after measurement the same binary can be flipped per process with
    /// PARUX_CODING_BATCH_READ / PARUX_CODING_PARENT_CONTINUATION = 0, but ONLY when
    /// PARUX_BENCHMARK_MODE=1 is also set. An ambient environment variable therefore cannot silently
    /// alter a normal user's agent. The effective values and their source are logged at task start.
    /// </summary>
    public static class CodingAgentFeatureFlags
    {
        public const string BenchmarkModeVariable = "PARUX_BENCHMARK_MODE";
        public const string BatchReadVariable = "PARUX_CODING_BATCH_READ";
        public const string ParentContinuationVariable = "PARUX_CODING_PARENT_CONTINUATION";

        /// <summary>Wired by the app to the persisted settings; defaults keep the features on.</summary>
        public static Func<bool> BatchReadSetting { get; set; } = () => true;
        public static Func<bool> ParentContinuationSetting { get; set; } = () => true;

        public static bool BenchmarkMode =>
            string.Equals(Environment.GetEnvironmentVariable(BenchmarkModeVariable)?.Trim(), "1", StringComparison.Ordinal);

        public static bool BatchReadEnabled => Resolve(BatchReadSetting, BatchReadVariable, out _);

        public static bool ParentContinuationEnabled => Resolve(ParentContinuationSetting, ParentContinuationVariable, out _);

        /// <summary>One line for the task log: what is in force and why.</summary>
        public static string Describe()
        {
            bool batch = Resolve(BatchReadSetting, BatchReadVariable, out string batchSource);
            bool continuation = Resolve(ParentContinuationSetting, ParentContinuationVariable, out string continuationSource);
            return "Coding agent features: batch_read=" + (batch ? "on" : "off") + " (" + batchSource + "), "
                + "parent continuation=" + (continuation ? "on" : "off") + " (" + continuationSource + ")";
        }

        internal static bool Resolve(Func<bool> setting, string variable, out string source)
        {
            bool configured;
            try { configured = setting(); } catch (Exception) { configured = true; }

            if (!configured)
            {
                source = "settings";
                return false;
            }

            if (BenchmarkMode && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                if (!IsEnabled(value))
                {
                    source = "benchmark-mode environment " + variable;
                    return false;
                }
            }

            source = "settings";
            return true;
        }

        public static bool IsEnabled(string? value)
        {
            string text = (value ?? string.Empty).Trim();
            return !(text.Equals("0", StringComparison.Ordinal)
                || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                || text.Equals("off", StringComparison.OrdinalIgnoreCase)
                || text.Equals("no", StringComparison.OrdinalIgnoreCase));
        }
    }
}
