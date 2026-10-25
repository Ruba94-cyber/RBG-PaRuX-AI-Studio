namespace PaRuX.Server;

/// <summary>
/// Turns a llama-server exit that happened before the runtime became healthy into a cause the user can act on.
/// A binary built for an instruction set this CPU lacks (AVX2 or AVX-512 on an older processor) is killed by an
/// illegal-instruction trap before it writes anything useful, so the bare exit code is all the user would see.
/// Pure: the exit code and the stderr tail are the only inputs, which keeps the mapping testable without a process.
/// </summary>
internal static class RuntimeLaunchFailureDiagnosis
{
    /// <summary>STATUS_ILLEGAL_INSTRUCTION (0xC000001D) as Process.ExitCode reports it on Windows (signed 32-bit).</summary>
    internal const int WindowsIllegalInstructionExitCode = unchecked((int)0xC000001D);

    /// <summary>128 + SIGILL (4): the shell and .NET convention for a process killed by an illegal-instruction signal.</summary>
    internal const int PosixIllegalInstructionExitCode = 132;

    /// <summary>The raw signal number, for launchers that report a signalled child as the negated signal.</summary>
    internal const int RawSigillExitCode = -4;

    internal enum CauseKind
    {
        /// <summary>The process executed an instruction this CPU does not implement.</summary>
        IllegalInstruction,

        /// <summary>The process reported that a vector or CPU extension it needs is not supported on this host.</summary>
        MissingCpuFeature,

        /// <summary>llama.cpp does not know the GGUF architecture: the runtime build is older than the model.</summary>
        UnsupportedModelArchitecture
    }

    /// <param name="Kind">The machine-readable cause.</param>
    /// <param name="Cause">A sentence the user can read without knowing llama.cpp.</param>
    /// <param name="SuggestedBuildVariant">The build the user should select or install instead.</param>
    internal sealed record Diagnosis(CauseKind Kind, string Cause, string SuggestedBuildVariant);

    private const string CpuBaselineBuild =
        "Suggested build: the CPU-baseline llama.cpp build (x64 without AVX2 or AVX-512 kernels). Select or install it in Runtime Manager.";

    /// <summary>
    /// Returns the diagnosis for a failed launch, or null when neither the exit code nor the stderr tail names a
    /// CPU-related cause. An unrecognised exit is never guessed at: the caller keeps the raw tail.
    /// </summary>
    internal static Diagnosis? Diagnose(int exitCode, string? stderrTail)
    {
        string text = stderrTail ?? string.Empty;

        if (text.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase))
        {
            return new Diagnosis(
                CauseKind.UnsupportedModelArchitecture,
                "This model needs a newer llama.cpp runtime: the installed build does not know its architecture.",
                "Update the llama.cpp runtime in Runtime Manager (PaRuX-managed runtimes can be updated on approval; a user-managed or system llama.cpp is never modified, so update it yourself).");
        }

        if (IsIllegalInstructionExit(exitCode)
            || text.Contains("illegal instruction", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SIGILL", StringComparison.Ordinal))
        {
            return new Diagnosis(
                CauseKind.IllegalInstruction,
                "This CPU does not support an instruction the runtime was built to use, so the process stopped with an illegal-instruction fault (typically AVX2 or AVX-512).",
                CpuBaselineBuild);
        }

        if (MentionsUnsupportedVectorExtension(text))
        {
            return new Diagnosis(
                CauseKind.MissingCpuFeature,
                "The runtime needs a CPU vector extension that this processor does not provide.",
                CpuBaselineBuild);
        }

        return null;
    }

    /// <summary>True for the exit codes an illegal-instruction trap produces on Windows, POSIX shells and raw signal reporting.</summary>
    internal static bool IsIllegalInstructionExit(int exitCode) =>
        exitCode is WindowsIllegalInstructionExitCode or PosixIllegalInstructionExitCode or RawSigillExitCode;

    /// <summary>
    /// A feature name next to an explicit refusal. The feature name alone is not enough: llama-server prints its
    /// detected feature list ("AVX2 = 1") on every healthy start, so only an explicit "not supported" style phrase counts.
    /// </summary>
    private static bool MentionsUnsupportedVectorExtension(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool namesFeature = text.Contains("AVX", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FMA", StringComparison.OrdinalIgnoreCase)
            || text.Contains("F16C", StringComparison.OrdinalIgnoreCase);
        bool refuses = text.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || text.Contains("requires", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not available", StringComparison.OrdinalIgnoreCase);
        return namesFeature && refuses;
    }
}
