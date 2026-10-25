using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ActiveReleaseStatusContractTests
{
    [Fact]
    public void Canonical_active_status_is_partial_until_competitive_backlog_and_physical_acceptance_are_closed()
    {
        string root = FindRepositoryRoot();
        string plan = File.ReadAllText(Path.Combine(root, "PARUX_EXECUTION_PLAN.md"));
        string currentStatus = ExtractSection(plan, "## Current status");
        string activeBacklog = ExtractSection(plan, "## Active competitive-gap backlog — 2026-09-10");
        string currentStatusLine = currentStatus
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .First();

        Assert.Contains("PARTIAL", currentStatusLine, StringComparison.Ordinal);
        Assert.Contains("PHYSICAL_PC_ACCEPTANCE_REQUIRED", currentStatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain("SOURCE_SOFTWARE_COMPLETE", currentStatusLine, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCE_VERIFIED", currentStatusLine, StringComparison.Ordinal);

        for (int i = 1; i <= 17; i++)
            Assert.Contains($"BIC-{i:D2}", activeBacklog, StringComparison.Ordinal);
        var bicRows = System.Text.RegularExpressions.Regex.Matches(
            activeBacklog,
            @"(?m)^\|\s*(BIC-\d{2})\s*\|\s*(P[012])\s*\|\s*(DONE|PARTIAL|MISSING|BLOCKED-EXTERNAL)\s*\|");
        Assert.Equal(17, bicRows.Count);
        var bicIds = bicRows.Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(17, bicIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 17).Select(index => $"BIC-{index:D2}"), bicIds.OrderBy(id => id, StringComparer.Ordinal));
        var bic16 = Assert.Single(bicRows.Cast<System.Text.RegularExpressions.Match>(), match => match.Groups[1].Value == "BIC-16");
        Assert.Equal("P0", bic16.Groups[2].Value);
        int openRows = bicRows.Cast<System.Text.RegularExpressions.Match>().Count(match => match.Groups[3].Value != "DONE");
        if (openRows > 0)
        {
            Assert.Contains("PARTIAL", currentStatusLine, StringComparison.Ordinal);
            Assert.DoesNotContain("SOURCE_SOFTWARE_COMPLETE", currentStatusLine, StringComparison.Ordinal);
            Assert.DoesNotContain("REFERENCE_VERIFIED", currentStatusLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Active_status_sections_name_the_current_backlog_and_preserve_historical_closeout()
    {
        string root = FindRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        string releasing = File.ReadAllText(Path.Combine(root, "docs", "RELEASING.md"));
        string validation = File.ReadAllText(Path.Combine(root, "docs", "PARUX_FINAL_VALIDATION_REPORT.md"));
        string referenceAudit = File.ReadAllText(Path.Combine(root, "docs", "PARUX_REFERENCE_LOCAL_AI_AUDIT.md"));
        string historicalCloseout = File.ReadAllText(
            Path.Combine(root, "docs", "PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md"));

        string readmeStatus = ExtractSection(readme, "## Project status");
        Assert.Contains("PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED", readmeStatus, StringComparison.Ordinal);
        Assert.Contains("[PARUX_EXECUTION_PLAN.md](PARUX_EXECUTION_PLAN.md)", readmeStatus, StringComparison.Ordinal);
        Assert.Contains("historical", readmeStatus, StringComparison.OrdinalIgnoreCase);

        (string Name, string Section)[] currentStatusSections =
        {
            ("release policy", releasing.Split("## Release assets", StringSplitOptions.None)[0]),
            ("validation report status", ExtractSection(validation, "## Status")),
            ("reference audit status", ExtractSection(referenceAudit, "## Current status")),
            ("historical closeout's current-status preface", historicalCloseout.Split("## Historical source status", StringSplitOptions.None)[0])
        };

        foreach ((string name, string section) in currentStatusSections)
        {
            Assert.Contains("PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED", section, StringComparison.Ordinal);
            Assert.Contains("BIC-01", section, StringComparison.Ordinal);
            Assert.Contains("BIC-17", section, StringComparison.Ordinal);
        }

        Assert.Contains("SOURCE_SOFTWARE_COMPLETE", historicalCloseout, StringComparison.Ordinal);
        Assert.Contains("historical", historicalCloseout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-09-10", historicalCloseout, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_validators_cover_all_seventeen_canonical_bic_rows()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[] { "tools/release-gate.ps1", "tools/validate-software-complete.ps1" })
        {
            string validator = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains("1..17", validator, StringComparison.Ordinal);
            Assert.Contains("BIC-01 through BIC-17", validator, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Physical_pc_acceptance_procedure_remains_versioned_but_is_not_the_only_open_gate()
    {
        string root = FindRepositoryRoot();
        string acceptancePath = Path.Combine(root, "docs", "PHYSICAL_PC_ACCEPTANCE_2026-09-08.md");
        Assert.True(File.Exists(acceptancePath), "The blocking physical-PC procedure must remain versioned in the repository.");

        string acceptance = File.ReadAllText(acceptancePath);
        Assert.Contains("exact `main` commit intended for release", acceptance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release-gate.ps1 -Restore", acceptance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INCONCLUSIVE", acceptance, StringComparison.Ordinal);
        Assert.Contains("actual NPU activity", acceptance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark-coding-agent-competitors.ps1", acceptance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Android_documentation_does_not_reintroduce_a_physical_phone_gate()
    {
        string root = FindRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "docs", "PARUX_ANDROID_README.md"));
        string boundary = File.ReadAllText(Path.Combine(root, "docs", "PARUX_ANDROID_API.md"));

        Assert.DoesNotContain("Required physical/device final validation", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physical Android phone is not a blocking acceptance prerequisite", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PHYSICAL_PC_ACCEPTANCE_REQUIRED", boundary, StringComparison.Ordinal);
        Assert.Contains("not a second blocking release gate", boundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Android_computer_use_is_windows_owned_and_universal_stop_copy_is_reconciled()
    {
        string root = FindRepositoryRoot();
        string androidUi = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Android", "app", "src", "main", "java", "com", "parux", "android", "ui", "tools", "ToolsScreen.kt"));
        string androidReadme = File.ReadAllText(Path.Combine(root, "docs", "PARUX_ANDROID_README.md"));
        string stopAll = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "Control", "StopAllService.cs"));
        string copyReconciliation = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Views", "SettingsView.CopyReconciliation.cs"));
        string templateHook = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Views", "SettingsView.BinaryAnalysis.cs"));

        Assert.Contains("Computer Use (paired Windows PC)", androidUi, StringComparison.Ordinal);
        Assert.Contains("Android has no Accessibility or raw-input privilege", androidUi, StringComparison.Ordinal);
        Assert.Contains("Android itself performs no taps/typing/scrolling", androidReadme, StringComparison.Ordinal);
        string stopAllNormalized = System.Text.RegularExpressions.Regex.Replace(
            stopAll.Replace("///", " "), @"\s+", " ");
        Assert.Contains("there is no separate Android Computer Use loop", stopAllNormalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CurrentAndroidComputerUseStopCopy", copyReconciliation, StringComparison.Ordinal);
        Assert.Contains("supervised Windows tasks", copyReconciliation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReconcileLegacyStopAllCopy(this)", templateHook, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_status_scripts_read_the_canonical_plan_as_utf8()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[] { "tools/release-gate.ps1", "tools/validate-software-complete.ps1" })
        {
            string script = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains("Get-Content $planPath -Raw -Encoding UTF8", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ExtractSection(string markdown, string heading)
    {
        int start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing canonical section: " + heading);
        int next = markdown.IndexOf("\n## ", start + heading.Length + 1, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
