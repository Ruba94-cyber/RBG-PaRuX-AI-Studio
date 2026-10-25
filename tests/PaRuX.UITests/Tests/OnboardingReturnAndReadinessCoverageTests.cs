using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class OnboardingReturnAndReadinessCoverageTests
{
    private static string CreateIsolatedFreshSettingsRoot()
    {
        string isolatedRoot = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data");
        string testRoot = Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(Path.Combine(testRoot, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = false,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled"
        }));
        return testRoot;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Return_banner_appears_only_during_model_hub_handoff_and_is_absent_initially()
    {
        string testName = nameof(Return_banner_appears_only_during_model_hub_handoff_and_is_absent_initially);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            Assert.Null(page.ReturnToSetupButton);
            Assert.Null(page.ReturnBanner);

            var installButton = page.FirstModelInstallButton;
            Assert.NotNull(installButton);
            Assert.Contains("in Model Hub", installButton.Name, StringComparison.OrdinalIgnoreCase);

            page.TriggerModelHubHandoff();

            var returnButton = page.ReturnToSetupButton;
            Assert.NotNull(returnButton);
            Assert.Equal("Return to Setup", returnButton!.Name);

            var banner = page.ReturnBanner;
            Assert.NotNull(banner);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Return_to_setup_button_navigates_back_to_onboarding_and_dismisses_banner()
    {
        string testName = nameof(Return_to_setup_button_navigates_back_to_onboarding_and_dismisses_banner);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            page.TriggerModelHubHandoff();
            Assert.NotNull(page.ReturnToSetupButton);

            page.ClickReturnToSetup();

            Assert.Null(page.ReturnToSetupButton);
            Assert.Null(page.ReturnBanner);

            var installButton = page.FirstModelInstallButton;
            Assert.NotNull(installButton);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Handoff_return_banner_persists_across_workspace_switches_and_returns_to_onboarding()
    {
        string testName = nameof(Handoff_return_banner_persists_across_workspace_switches_and_returns_to_onboarding);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            page.TriggerModelHubHandoff();
            Assert.NotNull(page.ReturnToSetupButton);

            page.OpenProjects();

            UiWait.For(() =>
            {
                var title = app.FindRawDescendant(app.MainWindow, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault == "Projects & Artifacts");
                return title != null && !title.Properties.IsOffscreen.ValueOrDefault;
            }, "Projects & Artifacts workspace loaded during handoff");

            Assert.NotNull(page.ReturnToSetupButton);
            Assert.NotNull(page.ReturnBanner);

            page.ClickReturnToSetup();

            Assert.Null(page.ReturnToSetupButton);
            Assert.Null(page.ReturnBanner);

            var installButton = page.FirstModelInstallButton;
            Assert.NotNull(installButton);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Unproven_readiness_card_explains_missing_verification_and_prompts_dialog_on_completion_attempt()
    {
        string testName = nameof(Unproven_readiness_card_explains_missing_verification_and_prompts_dialog_on_completion_attempt);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            Assert.Null(page.UnprovenReadinessCard);

            page.ClickGetStarted();

            UiWait.For(() => page.UnprovenReadinessCard != null, "Unproven readiness card visible");
            Assert.Equal("UNPROVEN READINESS", page.UnprovenReadinessHeader.Name);

            string unprovenText = page.UnprovenReadinessText.Name;
            Assert.False(string.IsNullOrWhiteSpace(unprovenText));
            Assert.Contains("• ", unprovenText);

            UiWait.For(() => page.UnprovenReadinessDialog != null, "Unproven Chat Readiness dialog visible");

            page.ClickGoBack();

            UiWait.For(() => page.UnprovenReadinessDialog == null, "Unproven Chat Readiness dialog dismissed");

            var getStarted = page.GetStartedButton;
            Assert.NotNull(getStarted);

            var chatPrompt = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.AutomationId.ValueOrDefault == "Chat_Prompt_TextBox");
            Assert.True(chatPrompt == null || chatPrompt.Properties.IsOffscreen.ValueOrDefault);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Explicit_proceed_anyway_action_completes_onboarding_and_persists_ready_state()
    {
        string testName = nameof(Explicit_proceed_anyway_action_completes_onboarding_and_persists_ready_state);
        string freshRoot = CreateIsolatedFreshSettingsRoot();
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true, restartDataRoot: freshRoot);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            page.ClickGetStarted();

            UiWait.For(() => page.UnprovenReadinessDialog != null, "Unproven Chat Readiness dialog visible");

            page.ClickProceedAnyway();

            var chatPrompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            Assert.False(chatPrompt.Properties.IsOffscreen.ValueOrDefault);

            Assert.Null(page.ReturnToSetupButton);

            UiWait.For(() =>
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(app.SettingsPath));
                return settings.RootElement.GetProperty("OnboardingCompleted").GetBoolean();
            }, "OnboardingCompleted persisted as true");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Onboarding")]
    public void Uncompleted_onboarding_preserves_onboarding_gate_across_app_restart()
    {
        string testName = nameof(Uncompleted_onboarding_preserves_onboarding_gate_across_app_restart);
        string freshRoot = CreateIsolatedFreshSettingsRoot();
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true, restartDataRoot: freshRoot);
        try
        {
            var page = new OnboardingReturnAndReadinessPage(app);

            page.ClickGetStarted();
            UiWait.For(() => page.UnprovenReadinessDialog != null, "Unproven Chat Readiness dialog visible before restart");

            page.ClickGoBack();
            UiWait.For(() => page.UnprovenReadinessDialog == null, "Unproven Chat Readiness dialog dismissed before restart");

            using (var settings = JsonDocument.Parse(File.ReadAllText(app.SettingsPath)))
            {
                Assert.False(settings.RootElement.GetProperty("OnboardingCompleted").GetBoolean());
            }

            using var restarted = app.Restart(nameof(Uncompleted_onboarding_preserves_onboarding_gate_across_app_restart));
            var restartedPage = new OnboardingReturnAndReadinessPage(restarted);

            var getStarted = restartedPage.GetStartedButton;
            Assert.NotNull(getStarted);

            var chatPrompt = restarted.FindRawDescendant(restarted.MainWindow, el =>
                el.Properties.AutomationId.ValueOrDefault == "Chat_Prompt_TextBox");
            Assert.True(chatPrompt == null || chatPrompt.Properties.IsOffscreen.ValueOrDefault);

            using (var settings = JsonDocument.Parse(File.ReadAllText(restarted.SettingsPath)))
            {
                Assert.False(settings.RootElement.GetProperty("OnboardingCompleted").GetBoolean());
            }
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }
}
