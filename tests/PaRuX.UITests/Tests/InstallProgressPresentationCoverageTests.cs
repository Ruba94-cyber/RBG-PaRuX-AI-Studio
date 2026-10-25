using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class InstallProgressPresentationCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Onboarding_first_model_download_progress_header_and_idle_status_are_plain_language()
    {
        string testName = nameof(Onboarding_first_model_download_progress_header_and_idle_status_are_plain_language);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new InstallProgressPage(app);

            Assert.NotNull(page.OnboardingDownloadHeader);
            Assert.Equal("DOWNLOAD PROGRESS", page.OnboardingDownloadHeader!.Properties.Name.ValueOrDefault);

            Assert.NotNull(page.OnboardingDownloadStatus);
            UiWait.For(() =>
            {
                string status = page.OnboardingDownloadStatus?.Properties.Name.ValueOrDefault ?? string.Empty;
                return status.StartsWith("No model downloads yet.", StringComparison.Ordinal)
                    || status.Contains("download(s)", StringComparison.Ordinal);
            }, "Onboarding download progress status settled");

            string settledStatus = page.OnboardingDownloadStatus!.Properties.Name.ValueOrDefault!;
            Assert.Contains("Start one from Model Hub to see its progress here.", settledStatus, StringComparison.Ordinal);

            Assert.DoesNotContain("NONE", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("IDLE", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT_STARTED", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("QUEUED", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("0%", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", settledStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("null", settledStatus, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Onboarding_download_rows_display_truthful_empty_state_without_spurious_progress_bar()
    {
        string testName = nameof(Onboarding_download_rows_display_truthful_empty_state_without_spurious_progress_bar);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new InstallProgressPage(app);

            AutomationElement? emptyRow = null;
            UiWait.For(() =>
            {
                emptyRow = page.OnboardingDownloadEmptyRow;
                return emptyRow != null;
            }, "Onboarding download rows empty state text");

            Assert.NotNull(emptyRow);
            Assert.Equal("No downloads yet.", emptyRow!.Properties.Name.ValueOrDefault);

            var downloadProgressBar = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.ProgressBar
                && (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("download", StringComparison.OrdinalIgnoreCase));
            Assert.Null(downloadProgressBar);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Onboarding_components_list_displays_plain_ownership_and_honest_idle_state()
    {
        string testName = nameof(Onboarding_components_list_displays_plain_ownership_and_honest_idle_state);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new InstallProgressPage(app);

            AutomationElement? statusElem = null;
            UiWait.For(() =>
            {
                statusElem = page.FindOnboardingComponentsStatus();
                return statusElem != null;
            }, "Onboarding components discovery status settled");

            string statusText = statusElem!.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert.True(statusText.Contains("checked. Detection never installs or changes software.", StringComparison.Ordinal)
                || statusText.Contains("Component state updated.", StringComparison.Ordinal)
                || statusText.Contains("Component discovery failed", StringComparison.Ordinal),
                "Status must be honest about component detection");

            var allTextElements = app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var textValues = allTextElements.Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty).ToList();

            bool hasPlainOwnership = textValues.Any(t =>
                t.Contains("managed by PaRuX", StringComparison.Ordinal)
                || t.Contains("managed by you", StringComparison.Ordinal)
                || t.Contains("detected on this PC", StringComparison.Ordinal)
                || t.Contains("not configured", StringComparison.Ordinal));
            Assert.True(hasPlainOwnership, "Component ownership and state must be presented in plain language");

            foreach (string t in textValues)
            {
                Assert.DoesNotContain("PARUX_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("USER_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("SYSTEM_DETECTED", t, StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Runtime_manager_cancel_install_control_is_accessible_and_enabled_when_idle()
    {
        string testName = nameof(Runtime_manager_cancel_install_control_is_accessible_and_enabled_when_idle);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new InstallProgressPage(app);
            page.OpenRuntimeManager();

            Assert.NotNull(page.RuntimeManagerTitle);
            Assert.Equal("Runtime Manager", page.RuntimeManagerTitle!.Properties.Name.ValueOrDefault);

            var cancelBtn = page.RuntimeManagerCancelInstallButton;
            Assert.NotNull(cancelBtn);
            Assert.Equal("Cancel active install", cancelBtn!.Properties.Name.ValueOrDefault);
            Assert.True(cancelBtn.Properties.IsEnabled.ValueOrDefault, "Cancel active install button must be enabled and accessible");

            var refreshBtn = page.RuntimeManagerRefreshButton;
            Assert.NotNull(refreshBtn);
            Assert.Equal("Refresh", refreshBtn!.Properties.Name.ValueOrDefault);
            Assert.True(refreshBtn.Properties.IsEnabled.ValueOrDefault, "Refresh button must be enabled");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Runtime_manager_cancel_when_idle_displays_plain_remediation_sentence()
    {
        string testName = nameof(Runtime_manager_cancel_when_idle_displays_plain_remediation_sentence);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new InstallProgressPage(app);
            page.OpenRuntimeManager();

            // Wait for the initial discovery refresh to finish; otherwise its completion text can legitimately
            // replace the cancel message that this test is about to produce.
            UiWait.For(() => (page.RuntimeManagerStatus?.Properties.Name.ValueOrDefault ?? string.Empty)
                .Contains("component(s) detected or configured", StringComparison.Ordinal), "Runtime discovery refresh finished");

            var cancelBtn = page.RuntimeManagerCancelInstallButton;
            Assert.NotNull(cancelBtn);
            page.Invoke(cancelBtn!);

            UiWait.For(() =>
            {
                var status = page.RuntimeManagerStatus;
                string text = status?.Properties.Name.ValueOrDefault ?? string.Empty;
                return text.Contains("No Runtime Manager install is active in this view.", StringComparison.Ordinal);
            }, "Remediation sentence displayed after cancel click");

            string currentStatus = page.RuntimeManagerStatus!.Properties.Name.ValueOrDefault!;
            Assert.Equal("No Runtime Manager install is active in this view.", currentStatus);

            Assert.DoesNotContain("0%", currentStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", currentStatus, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Runtime_manager_component_rows_expose_plain_language_ownership_and_actions()
    {
        string testName = nameof(Runtime_manager_component_rows_expose_plain_language_ownership_and_actions);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new InstallProgressPage(app);
            page.OpenRuntimeManager();

            Assert.NotNull(page.RuntimeComponentsHeader);
            Assert.Equal("Runtime components", page.RuntimeComponentsHeader!.Properties.Name.ValueOrDefault);

            UiWait.For(() => page.RuntimeManagerStatus != null, "Runtime manager discovery status settled");

            var allTexts = app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var textValues = allTexts.Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty).ToList();

            bool hasPlainOwnership = textValues.Any(t =>
                t.Contains("PaRuX managed", StringComparison.Ordinal)
                || t.Contains("User managed", StringComparison.Ordinal)
                || t.Contains("System detected", StringComparison.Ordinal)
                || t.Contains("not configured", StringComparison.Ordinal));
            Assert.True(hasPlainOwnership, "Runtime Manager rows must display plain-words ownership");

            foreach (string t in textValues)
            {
                Assert.DoesNotContain("PARUX_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("USER_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("SYSTEM_DETECTED", t, StringComparison.Ordinal);
            }

            var allButtons = app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            var buttonNames = allButtons.Select(b => b.Properties.Name.ValueOrDefault ?? string.Empty).ToList();
            bool hasPlainAction = buttonNames.Any(n =>
                n.StartsWith("Install", StringComparison.Ordinal)
                || n.StartsWith("Locate", StringComparison.Ordinal)
                || n.StartsWith("Skip", StringComparison.Ordinal)
                || n.StartsWith("Configure", StringComparison.Ordinal)
                || n == "Refresh"
                || n == "Cancel active install");
            Assert.True(hasPlainAction, "Runtime Manager actions must use plain sentence/title case labels");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Installation"), Trait("Category", "FlaUI")]
    public void Settings_components_lifecycle_reflects_honest_idle_state_and_plain_actions()
    {
        string testName = nameof(Settings_components_lifecycle_reflects_honest_idle_state_and_plain_actions);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new InstallProgressPage(app);
            page.OpenSettingsModelsCategory();

            UiWait.For(() =>
            {
                var status = page.SettingsComponentsStatus;
                string text = status?.Properties.Name.ValueOrDefault ?? string.Empty;
                return text.Contains("checked. Detection never installs or changes software.", StringComparison.Ordinal)
                    || text.Contains("Component discovery failed", StringComparison.Ordinal);
            }, "Settings components status settled");

            // The button is intentionally disabled while the opening discovery runs; it must be usable once idle.
            var refreshBtn = page.SettingsRefreshComponentsButton;
            Assert.NotNull(refreshBtn);
            Assert.True(refreshBtn!.Properties.IsEnabled.ValueOrDefault, "Settings components refresh button must be enabled");

            var progressBar = page.SettingsComponentsProgress;
            if (progressBar != null)
            {
                Assert.True(progressBar.Properties.IsOffscreen.ValueOrDefault,
                    "Components progress bar must be collapsed / offscreen when idle");
            }

            var allTexts = app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var textValues = allTexts.Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty).ToList();

            foreach (string t in textValues)
            {
                Assert.DoesNotContain("PARUX_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("USER_MANAGED", t, StringComparison.Ordinal);
                Assert.DoesNotContain("SYSTEM_DETECTED", t, StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }
}
