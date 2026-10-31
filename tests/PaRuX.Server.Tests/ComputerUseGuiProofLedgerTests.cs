using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiProofLedgerTests
{
    private const int Pid = 137;
    private const long Hwnd = 804;
    private const string TwoControls = """
        {"windows":[{"hwnd":804,"elements":[
          {"selector":"txt-main","value":"ready"},
          {"selector":"txt-secondary","name":"Confirmed","value":"correct"}
        ]}]}
        """;
    private static WindowsUiTreeObservationService.Observation Evidence(
        string tree = TwoControls, int pid = Pid, long hwnd = Hwnd) =>
        new("observed", tree, pid, hwnd);

    private static ComputerUseGuiFinalProof Proof(
        string selector, string property, string expected, int pid = Pid, long hwnd = Hwnd) =>
        ComputerUseGuiFinalProof.FromVerifiedAssertion(
            pid, hwnd, selector, property, expected,
            WindowsUiAssertionPolicy.Verdict.Verified)!;

    [Fact]
    public void Final_readback_rechecks_every_distinct_control_not_only_the_last()
    {
        var ledger = new ComputerUseGuiProofLedger();
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("txt-main", "value", "ready")));
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("txt-secondary", "name", "Confirmed")));
        Assert.Equal(2, ledger.Count);
        Assert.True(ledger.RecheckAll(Evidence()));

        const string firstChanged = """
            {"windows":[{"hwnd":804,"elements":[
              {"selector":"txt-main","value":"wrong"},
              {"selector":"txt-secondary","name":"Confirmed","value":"correct"}
            ]}]}
            """;
        // The old lastGuiProof accepted this: it only rechecked txt-secondary.
        Assert.False(ledger.RecheckAll(Evidence(firstChanged)));
        Assert.True(Proof("txt-secondary", "name", "Confirmed").Recheck(Evidence(firstChanged)));

        const string secondChanged = """
            {"windows":[{"hwnd":804,"elements":[
              {"selector":"txt-main","value":"ready"},
              {"selector":"txt-secondary","name":"No longer confirmed","value":"correct"}
            ]}]}
            """;
        Assert.False(ledger.RecheckAll(Evidence(secondChanged)));
        Assert.False(ledger.RecheckAll(Evidence(TwoControls, 138)));
        Assert.False(ledger.RecheckAll(Evidence(TwoControls, Pid, Hwnd + 1)));
    }

    [Fact]
    public void Duplicate_identical_checks_are_idempotent_and_conflicts_fail_closed()
    {
        var ledger = new ComputerUseGuiProofLedger();
        var initial = Proof("txt-main", "value", "ready");
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded, ledger.TryRecord(initial));
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.AlreadyRecorded,
            ledger.TryRecord(Proof("txt-main", "value", "ready")));
        Assert.Equal(1, ledger.Count);
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.ConflictingExpectation,
            ledger.TryRecord(Proof("txt-main", "value", "changed")));
        Assert.Equal(1, ledger.Count);
        Assert.True(ledger.RecheckAll(Evidence()));
        // The agent must invalidate after this conflict; the ledger cannot
        // make a contradictory assertion true by replacing an earlier value.
        ledger.Invalidate();
        Assert.False(ledger.RecheckAll(Evidence()));
    }

    [Theory]
    [InlineData(138, 804)]
    [InlineData(137, 805)]
    public void Another_window_or_process_cannot_join_one_foreground_proof_batch(
        int pid, long hwnd)
    {
        var ledger = new ComputerUseGuiProofLedger();
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("txt-main", "value", "ready")));
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.DifferentWindow,
            ledger.TryRecord(Proof("txt-secondary", "name", "Confirmed", pid, hwnd)));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void Eight_control_limit_is_bounded_and_requires_a_new_mutation_epoch()
    {
        var ledger = new ComputerUseGuiProofLedger();
        for (int i = 0; i < ComputerUseGuiProofLedger.MaxControls; i++)
            Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
                ledger.TryRecord(Proof("control-" + i, "value", "same")));
        Assert.Equal(ComputerUseGuiProofLedger.MaxControls, ledger.Count);
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.AlreadyRecorded,
            ledger.TryRecord(Proof("control-0", "value", "same")));
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.CapacityExceeded,
            ledger.TryRecord(Proof("control-extra", "value", "same")));
        Assert.Equal(ComputerUseGuiProofLedger.MaxControls, ledger.Count);

        ledger.Invalidate();
        Assert.False(ledger.HasProof);
        Assert.Equal(0, ledger.Count);
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("control-extra", "value", "same")));
    }

    [Fact]
    public void Unverified_null_bad_observation_and_duplicate_selector_do_not_create_false_pass()
    {
        var ledger = new ComputerUseGuiProofLedger();
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.InvalidProof,
            ledger.TryRecord(null));
        Assert.False(ledger.RecheckAll(Evidence()));
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("txt-main", "value", "ready")));
        Assert.False(ledger.RecheckAll(null));
        Assert.False(ledger.RecheckAll(new("timeout", string.Empty, Pid, Hwnd)));
        const string ambiguous = """
            {"windows":[{"hwnd":804,"elements":[
              {"selector":"txt-main","value":"ready"},
              {"selector":"txt-main","value":"ready"}
            ]}]}
            """;
        Assert.False(ledger.RecheckAll(Evidence(ambiguous)));
    }

    [Fact]
    public void Sensitive_expectations_and_selectors_are_not_emitted_by_ledger_text()
    {
        var ledger = new ComputerUseGuiProofLedger();
        Assert.Equal(ComputerUseGuiProofLedger.AddResult.Recorded,
            ledger.TryRecord(Proof("private-sensitive-selector", "value",
                "private sensitive password-like expected text")));
        Assert.DoesNotContain("private-sensitive-selector", ledger.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private sensitive password-like expected text",
            ledger.ToString(), StringComparison.Ordinal);
    }
}
