using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// CF-06: exit code 0 must not verify a change when the runner executed no tests. The VSTest lines are copied
/// from real `dotnet test` output in the English and Italian consoles. The other runners use their documented
/// summary formats.
/// </summary>
public sealed class CodingTestRunEvidenceTests
{
    private static CodingVerificationCommandPolicy.TestRunEvidence Parse(string? output) =>
        CodingVerificationCommandPolicy.ParseTestRunSummary(output);

    [Fact]
    public void Vstest_summaries_count_executed_tests_in_the_english_and_italian_consoles()
    {
        CodingVerificationCommandPolicy.TestRunEvidence english = Parse(
            "Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 724 ms - X.dll (net8.0)");
        Assert.True(english.Parsed);
        Assert.Equal(30, english.Executed);
        Assert.Equal(0, english.Skipped);

        CodingVerificationCommandPolicy.TestRunEvidence italian = Parse(
            "Superato!     - Non superati:     0. Superati:    30. Ignorati:     0. Totale:    30. Durata: 724 ms - PaRuX.Server.Tests.dll (net8.0)");
        Assert.True(italian.Parsed);
        Assert.Equal(30, italian.Executed);
        Assert.False(italian.ExecutedNoTests);
    }

    [Fact]
    public void Italian_failures_count_as_executed_tests_and_are_not_read_as_passes()
    {
        CodingVerificationCommandPolicy.TestRunEvidence evidence = Parse(
            "Non superato!  - Non superati:     2. Superati:    0. Ignorati:     1. Totale:    3. Durata: 9 ms - X.dll (net8.0)");

        Assert.Equal(2, evidence.Executed);
        Assert.Equal(1, evidence.Skipped);
        Assert.False(evidence.ExecutedNoTests);
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_refused_although_dotnet_test_exits_zero()
    {
        // Observed on this machine: a non-matching --filter prints no summary line and exits 0.
        Assert.True(Parse("No test matches the given testcase filter `FullyQualifiedName~NoSuchTest` in X.dll").ExecutedNoTests);
        Assert.True(Parse(
            "Nessun test corrisponde al filtro di test case specificato `FullyQualifiedName~NoSuchTest` in X.dll").ExecutedNoTests);
    }

    [Fact]
    public void Zero_executed_and_all_skipped_vstest_summaries_are_refused()
    {
        Assert.True(Parse("Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0, Duration: 1 ms - X.dll (net8.0)").ExecutedNoTests);
        Assert.True(Parse("Passed!  - Failed:     0, Passed:     0, Skipped:     4, Total:     4, Duration: 1 ms - X.dll (net8.0)").ExecutedNoTests);
    }

    [Fact]
    public void An_assembly_without_tests_does_not_hide_tests_that_ran_in_another_assembly()
    {
        CodingVerificationCommandPolicy.TestRunEvidence evidence = Parse(
            "No test matches the given testcase filter `X` in A.dll\n" +
            "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 2 ms - B.dll (net8.0)");

        Assert.Equal(5, evidence.Executed);
        Assert.False(evidence.ExecutedNoTests);
    }

    [Fact]
    public void Pytest_jest_and_vitest_summaries_are_read_from_their_summary_lines()
    {
        Assert.Equal(3, Parse("=========== 3 passed, 1 skipped in 0.12s ===========").Executed);
        Assert.True(Parse("============ no tests ran in 0.01s ============").ExecutedNoTests);
        Assert.True(Parse("===== 4 skipped in 0.05s =====").ExecutedNoTests);

        Assert.Equal(2, Parse("Tests:       2 passed, 2 total").Executed);
        Assert.True(Parse("Tests:       0 total").ExecutedNoTests);
        Assert.True(Parse("Tests:       3 skipped, 3 total").ExecutedNoTests);

        Assert.Equal(2, Parse("Tests  2 passed (2)").Executed);
        Assert.Equal(3, Parse("Tests  1 failed | 2 passed (3)").Executed);
        Assert.True(Parse("Tests  3 skipped (3)").ExecutedNoTests);
    }

    [Fact]
    public void Output_without_a_recognised_summary_is_unparsed_and_is_not_reported_as_zero_tests()
    {
        CodingVerificationCommandPolicy.TestRunEvidence evidence = Parse("Build succeeded.\n    0 Warning(s)\n    0 Error(s)");

        Assert.False(evidence.Parsed);
        Assert.False(evidence.ExecutedNoTests);
        Assert.Contains("no recognised test summary", evidence.Note, StringComparison.Ordinal);
        Assert.False(Parse(null).Parsed);
        Assert.False(Parse("   ").Parsed);
    }

    [Fact]
    public void An_assembly_without_tests_is_refused_in_the_italian_and_english_consoles()
    {
        // The lines the SDK prints for an assembly that contains no test, copied from its resources.
        Assert.True(Parse(
            "Non ci sono test disponibili in X.dll. Assicurarsi che l'agente di individuazione test e gli executor di test " +
            "siano registrati e che le impostazioni delle versioni di piattaforma e framework siano appropriate, quindi riprovare.").ExecutedNoTests);
        Assert.True(Parse(
            "No test is available in X.dll. Make sure that test discoverer & executors are registered and platform & " +
            "framework version settings are appropriate and try again.").ExecutedNoTests);

        CodingVerificationCommandPolicy.TestRunEvidence mixed = Parse(
            "Non ci sono test disponibili in A.dll. Assicurarsi che l'agente di individuazione test.\n" +
            "Superato!     - Non superati:     0. Superati:     5. Ignorati:     0. Totale:     5. Durata: 2 ms - B.dll (net8.0)");
        Assert.Equal(5, mixed.Executed);
        Assert.False(mixed.ExecutedNoTests);
    }

    [Fact]
    public void A_filter_with_invalid_properties_is_refused_in_the_italian_and_english_consoles()
    {
        Assert.True(Parse(
            "No tests matched the filter because it contains one or more properties that are not valid (Bogus). " +
            "Specify filter expression containing valid properties (FullyQualifiedName).").ExecutedNoTests);
        Assert.True(Parse(
            "Nessun test corrisponde al filtro perché contiene una o più proprietà non valide (Bogus). " +
            "Specificare un'espressione di filtro contenente proprietà valide (FullyQualifiedName).").ExecutedNoTests);
    }
}
