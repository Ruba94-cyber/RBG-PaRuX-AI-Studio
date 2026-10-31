using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaRuX.UiAcceptanceFixture;

/// <summary>
/// Standalone, disposable native Windows acceptance target. No access to
/// PaRuX credentials/workspace is required. All elements have stable
/// AutomationIds (WinForms Control.Name) and user-visible accessible names.
/// It does not call into production code and cannot certify PaRuX alone.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string nonce = args.Length == 1 && Guid.TryParseExact(args[0], "N", out _)
            ? args[0] : Guid.NewGuid().ToString("N");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FixtureForm(nonce));
    }
}

internal sealed class FixtureForm : Form
{
    internal const string ActionAutomationId = "parux-fixture-run";
    internal const string ResultAutomationId = "parux-fixture-result";
    internal const string InputAutomationId = "parux-fixture-input";
    internal const string ResetAutomationId = "parux-fixture-reset";

    internal FixtureForm(string nonce)
    {
        Text = "PaRuX Disposable UI Acceptance " + nonce;
        Name = "parux-fixture-window";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(490, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var instruction = new Label
        {
            Name = "parux-fixture-instruction",
            Text = "Disposable fixture: click Run to update the result control.",
            AutoSize = true, Location = new Point(20, 22)
        };
        var input = new TextBox
        {
            Name = InputAutomationId,
            AccessibleName = "Fixture input",
            Text = "ready", Location = new Point(20, 65), Width = 445
        };
        var result = new TextBox
        {
            Name = ResultAutomationId,
            AccessibleName = "Fixture result",
            ReadOnly = true,
            TabStop = true,
            Text = "PENDING", Location = new Point(20, 158), Width = 445
        };
        var reset = new Button
        {
            Name = ResetAutomationId,
            AccessibleName = "Reset acceptance result",
            Text = "Reset",
            Location = new Point(215, 106),
            Width = 180, Height = 36
        };
        reset.Click += (_, _) =>
        {
            // Deterministically remove previously verified evidence. A
            // subsequent successful test must invoke Run and re-check again.
            result.Text = "PENDING";
            result.AccessibleDescription = "State reset for a disposable regression test";
        };

        var run = new Button
        {
            Name = ActionAutomationId,
            AccessibleName = "Run acceptance action",
            Text = "Run",
            Location = new Point(20, 106),
            Width = 180, Height = 36
        };
        run.Click += (_, _) =>
        {
            result.Text = "ACCEPTED-" + nonce;
            result.AccessibleDescription = "State changed by an actual button invocation";
        };

        Controls.AddRange(new Control[] { instruction, input, run, reset, result });
    }
}
