using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Honest reporting for tests that need an optional install, such as the local embedding engine. A missing
/// prerequisite is reported as a Skipped test that names what did not run, never as a Passed test with no
/// assertions. A release or CI lane that must cover these sets <see cref="RequireSwitch"/> to 1, and the same
/// gap becomes a failure.
///
/// The skip is decided at discovery, through the Skip property of a fact attribute, not by throwing at run
/// time. This project's runner reports a run-time SkipException as a failure, so a dynamic skip would turn an
/// honest gap into a red run.
/// </summary>
internal static class OptionalPrerequisite
{
    public const string RequireSwitch = "PARUX_REQUIRE_OPTIONAL";

    public static bool IsRequired() =>
        string.Equals(Environment.GetEnvironmentVariable(RequireSwitch), "1", StringComparison.Ordinal);

    /// <summary>
    /// The skip reason for a test whose optional prerequisite is absent, or null when the test should run. A
    /// required prerequisite never skips: the test runs, and <see cref="AssertAvailable"/> fails it.
    /// </summary>
    public static string? SkipReasonWhenAbsent(bool available, string prerequisite, bool required)
    {
        if (available || required) return null;
        return "Optional prerequisite is not installed on this host, so these assertions did not run: "
            + prerequisite + ". Set " + RequireSwitch + "=1 to make this a failure.";
    }

    /// <summary>
    /// Body guard for a test that was not skipped at discovery. It is reached only with the prerequisite present,
    /// or with it required and absent, and that second case is a failure.
    /// </summary>
    public static void AssertAvailable(bool available, string prerequisite)
    {
        Assert.True(available,
            "Required optional prerequisite is not installed on this host: " + prerequisite + ". "
            + RequireSwitch + "=1 requires it.");
    }
}
