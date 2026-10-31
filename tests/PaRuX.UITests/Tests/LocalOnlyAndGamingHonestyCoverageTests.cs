using System;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class LocalOnlyAndGamingHonestyCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try
        {
            action(app);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(step, ex);
            throw;
        }
    }

    // The cloud-fallback block sits below the fold of the Settings page and focusing its switch does not scroll it
    // into view. Step the enclosing ScrollViewer until the awaited text is on screen, bounded to 21 steps.
    private static void ScrollUntilOnScreen(AutomationElement anchor, Func<bool> isOnScreen, string description)
    {
        if (isOnScreen()) return;
        FlaUI.Core.Patterns.IScrollPattern? scroll = null;
        AutomationElement? current = anchor;
        for (int depth = 0; depth < 16 && scroll == null; depth++)
        {
            current = current?.Parent;
            if (current == null) break;
            scroll = current.Patterns.Scroll.PatternOrDefault;
        }
        if (scroll == null) return;

        for (int percent = 0; percent <= 100; percent += 5)
        {
            scroll.SetScrollPercent(-1, percent);
            try
            {
                UiWait.For(isOnScreen, description, TimeSpan.FromMilliseconds(400));
                return;
            }
            catch (TimeoutException)
            {
                // Not on screen at this scroll position; try the next step.
            }
        }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings"), Trait("Category", "Gaming")]
    public void Gaming_status_in_privacy_settings_is_reachable_and_shows_ready_state_word() => Run(
        nameof(Gaming_status_in_privacy_settings_is_reachable_and_shows_ready_state_word), app =>
        {
            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_PrivacyNetwork_Button");

            AutomationElement? gamingHeading = null;
            UiWait.For(() =>
            {
                gamingHeading = page.FindTextElement("GAMING");
                return gamingHeading != null && !gamingHeading.Properties.IsOffscreen.ValueOrDefault;
            }, "GAMING section heading in Privacy & Network");
            Assert.NotNull(gamingHeading);

            var statusText = page.GetGamingStatusText();
            Assert.NotNull(statusText);
            Assert.Contains("Ready", statusText.Name, StringComparison.Ordinal);
            Assert.Contains("heavy AI stays unloaded until requested", statusText.Name, StringComparison.Ordinal);

            var enterButton = page.GetEnterGamingButton();
            Assert.True(enterButton.IsEnabled, "Enter Gaming Mode button should be enabled when not gaming");

            var exitButton = page.GetExitGamingButton();
            Assert.False(exitButton.IsEnabled, "Exit Gaming Mode button should be disabled when not gaming");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings"), Trait("Category", "Gaming")]
    public void Gaming_orchestrator_status_control_in_settings_is_reachable_and_shows_state_word() => Run(
        nameof(Gaming_orchestrator_status_control_in_settings_is_reachable_and_shows_state_word), app =>
        {
            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_GamingAISharing_Button");

            AutomationElement? orchestratorHeading = null;
            UiWait.For(() =>
            {
                orchestratorHeading = page.FindTextElement("GAMING & AI RESOURCE SHARING");
                return orchestratorHeading != null && !orchestratorHeading.Properties.IsOffscreen.ValueOrDefault;
            }, "GAMING & AI RESOURCE SHARING section heading");
            Assert.NotNull(orchestratorHeading);

            var refreshButton = UiWait.ById(app.MainWindow, "Settings_Gaming_Refresh_Button", ControlType.Button);
            Assert.True(refreshButton.IsEnabled, "Refresh status button must be enabled");

            var enterButton = UiWait.ById(app.MainWindow, "Settings_Gaming_Enter_Button", ControlType.Button);
            Assert.NotNull(enterButton);

            var exitButton = UiWait.ById(app.MainWindow, "Settings_Gaming_Exit_Button", ControlType.Button);
            Assert.NotNull(exitButton);

            var badge = page.GetGamingStatusBadge();
            Assert.NotNull(badge);
            Assert.False(string.IsNullOrWhiteSpace(badge.Name), "Gaming status badge should display a state word");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings"), Trait("Category", "Models")]
    public void Cloud_fallback_section_is_labeled_optional_and_off_by_default() => Run(
        nameof(Cloud_fallback_section_is_labeled_optional_and_off_by_default), app =>
        {
            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_Models_Button");

            AutomationElement? fallbackHeading = null;
            UiWait.For(() =>
            {
                fallbackHeading = page.FindTextElement("CLOUD FALLBACK (OPTIONAL)");
                return fallbackHeading != null && !fallbackHeading.Properties.IsOffscreen.ValueOrDefault;
            }, "CLOUD FALLBACK (OPTIONAL) section heading");
            Assert.NotNull(fallbackHeading);

            AutomationElement? honestyNotice = null;
            UiWait.For(() =>
            {
                honestyNotice = page.FindTextElement("PaRuX runs entirely on this device. This is the one exception, and it is off unless you turn it on");
                return honestyNotice != null;
            }, "Local-only honesty notice for cloud fallback");
            Assert.NotNull(honestyNotice);

            var toggle = page.GetCloudFallbackToggle();
            Assert.NotNull(toggle);
            var togglePattern = toggle.Patterns.Toggle.PatternOrDefault;
            if (togglePattern != null)
            {
                Assert.Equal(ToggleState.Off, togglePattern.ToggleState.Value);
            }

            ScrollUntilOnScreen(toggle,
                () =>
                {
                    AutomationElement? visibleStatus = page.FindTextElement("Off. Every turn stays on this device; a local failure stays a failure.");
                    return visibleStatus != null && !visibleStatus.Properties.IsOffscreen.ValueOrDefault;
                },
                "Cloud fallback initial off status text on screen");

            AutomationElement? statusText = null;
            UiWait.For(() =>
            {
                statusText = page.FindTextElement("Off. Every turn stays on this device; a local failure stays a failure.");
                return statusText != null && !statusText.Properties.IsOffscreen.ValueOrDefault;
            }, "Cloud fallback initial off status text");
            Assert.NotNull(statusText);

            var saveButton = page.GetSaveCloudFallbackButton();
            Assert.True(saveButton == null || saveButton.Properties.IsOffscreen.ValueOrDefault,
                "Save button should be collapsed while cloud fallback is off");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings"), Trait("Category", "Models")]
    public void Cloud_fallback_enabling_requires_explicit_configuration_and_save_action() => Run(
        nameof(Cloud_fallback_enabling_requires_explicit_configuration_and_save_action), app =>
        {
            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_Models_Button");

            var toggle = page.GetCloudFallbackToggle();
            // A UIA toggle on an unrealized, below-the-fold switch does nothing, so bring the switch on screen first.
            ScrollUntilOnScreen(toggle, () => !toggle.Properties.IsOffscreen.ValueOrDefault, "Cloud fallback switch on screen");
            page.ToggleSwitch(toggle);
            AutomationElement? guidanceText = null;
            UiWait.For(() =>
            {
                guidanceText = page.FindTextElement("Choose a provider, paste an API key and a model id, then Save.");
                return guidanceText != null;
            }, "Cloud fallback explicit configuration guidance text");
            Assert.NotNull(guidanceText);
            page.ScrollSettingsContentUntilOnScreen(guidanceText!, "Cloud fallback guidance on screen");
            UiWait.For(() => !guidanceText.Properties.IsOffscreen.ValueOrDefault,
                "Cloud fallback explicit configuration guidance visible");

            AutomationElement? saveButton = null;
            UiWait.For(() =>
            {
                saveButton = page.GetSaveCloudFallbackButton();
                return saveButton != null;
            }, "Save button in cloud fallback detail panel");
            Assert.NotNull(saveButton);
            page.ScrollSettingsContentUntilOnScreen(saveButton!, "Cloud fallback Save button on screen");
            UiWait.For(() => !saveButton.Properties.IsOffscreen.ValueOrDefault,
                "Save button visible in cloud fallback detail panel");
            Assert.True(saveButton.IsEnabled, "Save button should be enabled for explicit action");

            var testButton = page.GetTestCloudFallbackButton();
            Assert.NotNull(testButton);

            AutomationElement? keyBox = null;
            UiWait.For(() =>
            {
                keyBox = page.GetCloudFallbackApiKeyBox();
                return keyBox != null;
            }, "API key box in cloud fallback detail panel");
            Assert.NotNull(keyBox);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Chat"), Trait("Category", "Gaming")]
    public void Chat_header_paused_state_wording_is_consistent_with_settings_gaming_active_state() => Run(
        nameof(Chat_header_paused_state_wording_is_consistent_with_settings_gaming_active_state), app =>
        {
            string marker = Path.Combine(app.LocalAiStateRoot, "game-active.json");
            Directory.CreateDirectory(app.LocalAiStateRoot);
            File.WriteAllText(marker, "{\"started\":\"" + DateTime.UtcNow.ToString("o") + "\"}");

            AutomationElement? profileText = null;
            UiWait.For(() =>
            {
                profileText = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Header_Profile_Text"));
                return profileText != null && string.Equals(profileText.Name, "Paused for gaming", StringComparison.Ordinal);
            }, "Chat header ProfileText displaying 'Paused for gaming'");
            Assert.NotNull(profileText);
            Assert.Equal("Paused for gaming", profileText.Name);

            var returnButton = UiWait.ById(app.MainWindow, "Chat_Header_ReturnToAi_Button", ControlType.Button);
            Assert.Equal("Return to AI", returnButton.Name);
            Assert.False(returnButton.Properties.IsOffscreen.ValueOrDefault, "Return to AI button should be visible");

            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_PrivacyNetwork_Button");

            var statusText = page.GetGamingStatusText();
            Assert.Contains("ACTIVE", statusText.Name, StringComparison.Ordinal);
            Assert.Contains("AI jobs, coding agents, Computer Use, and the bubble are suspended", statusText.Name, StringComparison.Ordinal);

            var enterButton = page.GetEnterGamingButton();
            Assert.False(enterButton.IsEnabled, "Enter Gaming Mode button should be disabled when gaming is active");

            var exitButton = page.GetExitGamingButton();
            Assert.True(exitButton.IsEnabled, "Exit Gaming Mode button should be enabled when gaming is active");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Chat"), Trait("Category", "Gaming")]
    public void Chat_header_and_settings_revert_to_ready_when_gaming_mode_cleared() => Run(
        nameof(Chat_header_and_settings_revert_to_ready_when_gaming_mode_cleared), app =>
        {
            string marker = Path.Combine(app.LocalAiStateRoot, "game-active.json");
            Directory.CreateDirectory(app.LocalAiStateRoot);
            File.WriteAllText(marker, "{\"started\":\"" + DateTime.UtcNow.ToString("o") + "\"}");

            UiWait.For(() =>
            {
                var btn = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Header_ReturnToAi_Button"));
                return btn != null && !btn.Properties.IsOffscreen.ValueOrDefault;
            }, "Chat_Header_ReturnToAi_Button visible while gaming active");

            File.Delete(marker);

            UiWait.For(() =>
            {
                var profileText = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Header_Profile_Text"));
                return profileText != null && string.Equals(profileText.Name, "Model idle", StringComparison.Ordinal);
            }, "Chat header ProfileText reverting to 'Model idle'");

            UiWait.For(() =>
            {
                var btn = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Header_ReturnToAi_Button"));
                return btn == null || btn.Properties.IsOffscreen.ValueOrDefault;
            }, "Chat_Header_ReturnToAi_Button hidden after gaming ends");

            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_PrivacyNetwork_Button");

            var statusText = page.GetGamingStatusText();
            Assert.Contains("Ready", statusText.Name, StringComparison.Ordinal);

            var enterButton = page.GetEnterGamingButton();
            Assert.True(enterButton.IsEnabled, "Enter Gaming Mode button should be enabled after gaming ends");

            var exitButton = page.GetExitGamingButton();
            Assert.False(exitButton.IsEnabled, "Exit Gaming Mode button should be disabled after gaming ends");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Chat"), Trait("Category", "Models")]
    public void No_visible_text_in_chat_or_model_hub_claims_ai_runs_in_cloud_by_default() => Run(
        nameof(No_visible_text_in_chat_or_model_hub_claims_ai_runs_in_cloud_by_default), app =>
        {
            var profileText = UiWait.ById(app.MainWindow, "Chat_Header_Profile_Text", ControlType.Text);
            Assert.Equal("Model idle", profileText.Name);

            var providerGuidance = UiWait.ById(app.MainWindow, "Chat_EmptyState_Provider_Guidance_Text", ControlType.Text);
            Assert.Contains("Choose a provider and compatible model", providerGuidance.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Qwen", providerGuidance.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("entirely on your machine", providerGuidance.Name, StringComparison.OrdinalIgnoreCase);

            var routeReasonBtn = UiWait.ById(app.MainWindow, "Chat_SimpleMode_RouteReason_Button", ControlType.Button);
            Assert.StartsWith("Why this model?", routeReasonBtn.Name, StringComparison.Ordinal);

            var incognitoBtn = UiWait.ById(app.MainWindow, "MainWindow_NewIncognitoChat_Button", ControlType.Button);
            Assert.NotNull(incognitoBtn);

            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenModelHub();

            UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit);

            AutomationElement? modelHubIntro = null;
            UiWait.For(() =>
            {
                modelHubIntro = page.FindTextElement("Choose the right local model, not just a file.");
                return modelHubIntro != null && !modelHubIntro.Properties.IsOffscreen.ValueOrDefault;
            }, "Model Hub introductory local-only text");
            Assert.NotNull(modelHubIntro);
            Assert.Contains("Compare GGUF quantizations against this PC's real GPU/RAM capacity", modelHubIntro.Name, StringComparison.Ordinal);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Settings_privacy_and_agent_sections_reinforce_local_only_honesty() => Run(
        nameof(Settings_privacy_and_agent_sections_reinforce_local_only_honesty), app =>
        {
            var page = new LocalOnlyAndGamingHonestyPage(app.MainWindow, app);
            page.OpenSettings();
            page.SelectCategory("Settings_Category_PrivacyNetwork_Button");

            AutomationElement? localNetworkNotice = null;
            UiWait.For(() =>
            {
                localNetworkNotice = page.FindTextElement("Off by default. When enabled, PaRuX binds only to the selected private IPv4 address over HTTPS. No cloud, UPnP, router changes, or Internet exposure.");
                return localNetworkNotice != null;
            }, "Local network privacy notice");
            Assert.NotNull(localNetworkNotice);

            page.SelectCategory("Settings_Category_Agents_Button");

            AutomationElement? agentNotice = null;
            UiWait.For(() =>
            {
                agentNotice = page.FindTextElement("entirely on this machine, no cloud involved.");
                return agentNotice != null;
            }, "Agent command center local execution notice");
            Assert.NotNull(agentNotice);
        });
}
