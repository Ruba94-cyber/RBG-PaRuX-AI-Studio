using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class SettingsCategoriesCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Default_category_is_General_with_its_sections_visible_and_other_categories_hidden() =>
        Run(nameof(Default_category_is_General_with_its_sections_visible_and_other_categories_hidden), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            // General category button exists and is labeled General
            var generalButton = settings.GetCategoryButton("General");
            Assert.Equal("General", generalButton.Name);

            // General section heading is visible
            settings.WaitForSectionHeadingVisible("GENERAL");

            // General controls are visible
            var themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            Assert.False(themeCombo.Properties.IsOffscreen.ValueOrDefault);

            var startupToggle = UiWait.ById(app.MainWindow, "Settings_Startup_Toggle", ControlType.Button);
            Assert.False(startupToggle.Properties.IsOffscreen.ValueOrDefault);

            // Representative sections of other categories are hidden
            settings.WaitForSectionHeadingHidden("MODELS");
            settings.WaitForSectionHeadingHidden("COMPUTER USE");
            settings.WaitForSectionHeadingHidden("ADVANCED");

            // Controls belonging to other categories are collapsed / off-screen
            var gatewayText = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Gateway_State_Text"));
            Assert.True(gatewayText == null || gatewayText.Properties.IsOffscreen.ValueOrDefault);

            var gamingRefresh = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Gaming_Refresh_Button"));
            Assert.True(gamingRefresh == null || gamingRefresh.Properties.IsOffscreen.ValueOrDefault);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Category_rail_contains_every_category_button_defined_in_SettingsCategories() =>
        Run(nameof(Category_rail_contains_every_category_button_defined_in_SettingsCategories), app =>
        {
            new MainWindowPage(app.MainWindow).OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            foreach (string category in SettingsCategoriesPage.Categories)
            {
                var button = settings.GetCategoryButton(category);
                Assert.NotNull(button);
                Assert.Equal(ControlType.Button, button.ControlType);
                Assert.Equal(category, button.Name);
                Assert.True(button.IsEnabled);
            }

            Assert.Equal(13, SettingsCategoriesPage.Categories.Length);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Switching_to_each_category_shows_its_section_and_hides_other_categories() =>
        Run(nameof(Switching_to_each_category_shows_its_section_and_hides_other_categories), app =>
        {
            new MainWindowPage(app.MainWindow).OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            // Initially on General
            settings.WaitForSectionHeadingVisible("GENERAL");

            // Switch to Models
            settings.SelectCategory("Models");
            settings.WaitForSectionHeadingVisible("MODELS");
            settings.WaitForSectionHeadingHidden("GENERAL");

            // Switch to Integrations
            settings.SelectCategory("Integrations");
            settings.WaitForSectionHeadingVisible("LOCAL AI GATEWAY");
            settings.WaitForSectionHeadingHidden("MODELS");

            // Switch to Gaming & AI Sharing
            settings.SelectCategory("Gaming & AI Sharing");
            settings.WaitForSectionHeadingVisible("GAMING & AI RESOURCE SHARING");
            settings.WaitForSectionHeadingHidden("LOCAL AI GATEWAY");

            // Switch to Data
            settings.SelectCategory("Data");
            settings.WaitForSectionHeadingVisible("STORAGE");
            settings.WaitForSectionHeadingHidden("GAMING & AI RESOURCE SHARING");

            // Switch to Advanced
            settings.SelectCategory("Advanced");
            settings.WaitForSectionHeadingVisible("ADVANCED");
            settings.WaitForSectionHeadingHidden("STORAGE");

            // Switch back to General
            settings.SelectCategory("General");
            settings.WaitForSectionHeadingVisible("GENERAL");
            settings.WaitForSectionHeadingHidden("ADVANCED");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings")]
    public void Every_visible_section_in_a_category_has_a_section_heading() =>
        Run(nameof(Every_visible_section_in_a_category_has_a_section_heading), app =>
        {
            new MainWindowPage(app.MainWindow).OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            // General category sections
            settings.WaitForSectionHeadingVisible("GENERAL");
            string[] generalHeadings = { "GENERAL", "QUICK CHAT BUBBLE", "FINDING YOUR WAY AROUND" };
            foreach (string heading in generalHeadings)
            {
                var h = settings.FindSectionHeading(heading);
                Assert.NotNull(h);
                Assert.Equal(heading, h.Name);
            }

            // Chat Behavior category sections
            settings.SelectCategory("Chat Behavior");
            settings.WaitForSectionHeadingVisible("RAG (LOCAL DOCUMENTS)");
            string[] chatHeadings = { "RAG (LOCAL DOCUMENTS)", "PERSONAS", "MEMORY", "SKILLS" };
            foreach (string heading in chatHeadings)
            {
                var h = settings.FindSectionHeading(heading);
                Assert.NotNull(h);
                Assert.Equal(heading, h.Name);
            }

            // Agents category sections
            settings.SelectCategory("Agents");
            settings.WaitForSectionHeadingVisible("AGENT COMMAND CENTER");
            string[] agentHeadings = { "AGENT COMMAND CENTER", "PROJECTS & AUTOPILOT", "AUTOMATIONS", "CONTEXT GRAPH INSPECTOR" };
            foreach (string heading in agentHeadings)
            {
                var h = settings.FindSectionHeading(heading);
                Assert.NotNull(h);
                Assert.Equal(heading, h.Name);
            }

            // Data category sections
            settings.SelectCategory("Data");
            settings.WaitForSectionHeadingVisible("STORAGE");
            string[] dataHeadings = { "STORAGE", "BACKUP & RESTORE", "IMPORT CHAT HISTORY" };
            foreach (string heading in dataHeadings)
            {
                var h = settings.FindSectionHeading(heading);
                Assert.NotNull(h);
                Assert.Equal(heading, h.Name);
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings")]
    public void Scrolling_reaches_the_last_control_of_each_category() =>
        Run(nameof(Scrolling_reaches_the_last_control_of_each_category), app =>
        {
            new MainWindowPage(app.MainWindow).OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            // 1. General: Show me around button in Finding your way around section
            // The Settings page populates after navigation; wait for the control instead of racing the first lookup.
            AutomationElement? generalLast = null;
            UiWait.For(() => (generalLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Show me around")))) != null, "General last control present");
            Assert.NotNull(generalLast);
            SettingsCategoriesPage.ScrollIntoView(generalLast);
            UiWait.For(() => !generalLast.Properties.IsOffscreen.ValueOrDefault, "General last control in view");

            // 2. Models: Start group chat button in Group Chat section
            settings.SelectCategory("Models");
            settings.WaitForSectionHeadingVisible("MODELS");
            var modelsLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Start group chat")));
            Assert.NotNull(modelsLast);
            SettingsCategoriesPage.ScrollIntoView(modelsLast);
            UiWait.For(() => !modelsLast.Properties.IsOffscreen.ValueOrDefault, "Models last control in view");

            // 3. Chat Behavior: New skill button in Skills section
            settings.SelectCategory("Chat Behavior");
            settings.WaitForSectionHeadingVisible("RAG (LOCAL DOCUMENTS)");
            var chatLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("New skill…")));
            Assert.NotNull(chatLast);
            SettingsCategoriesPage.ScrollIntoView(chatLast);
            UiWait.For(() => !chatLast.Properties.IsOffscreen.ValueOrDefault, "Chat Behavior last control in view");

            // 4. Your PC apps: Use PaRuX's folder button in Documents section
            settings.SelectCategory("Your PC apps");
            settings.WaitForSectionHeadingVisible("APPS ON THIS PC");
            var appsLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Use PaRuX's folder")));
            Assert.NotNull(appsLast);
            SettingsCategoriesPage.ScrollIntoView(appsLast);
            UiWait.For(() => !appsLast.Properties.IsOffscreen.ValueOrDefault, "Your PC apps last control in view");

            // 5. Media Generation: Generate 3D model button in 3D Studio section
            settings.SelectCategory("Media Generation");
            settings.WaitForSectionHeadingVisible("CUSTOM WORKFLOWS");
            var mediaLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Generate 3D model")));
            Assert.NotNull(mediaLast);
            SettingsCategoriesPage.ScrollIntoView(mediaLast);
            UiWait.For(() => !mediaLast.Properties.IsOffscreen.ValueOrDefault, "Media Generation last control in view");

            // 6. Automation: Start training button in Training Lab section
            settings.SelectCategory("Automation");
            settings.WaitForSectionHeadingVisible("COMPUTER USE");
            var autoLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Start training")));
            Assert.NotNull(autoLast);
            SettingsCategoriesPage.ScrollIntoView(autoLast);
            UiWait.For(() => !autoLast.Properties.IsOffscreen.ValueOrDefault, "Automation last control in view");

            // 7. Agents: Add relation button in Context Graph Inspector section
            settings.SelectCategory("Agents");
            settings.WaitForSectionHeadingVisible("AGENT COMMAND CENTER");
            var agentsLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Add relation")));
            Assert.NotNull(agentsLast);
            SettingsCategoriesPage.ScrollIntoView(agentsLast);
            UiWait.For(() => !agentsLast.Properties.IsOffscreen.ValueOrDefault, "Agents last control in view");

            // 8. Integrations: Create a key for another app button in Other apps section
            settings.SelectCategory("Integrations");
            settings.WaitForSectionHeadingVisible("LOCAL AI GATEWAY");
            var integrationsLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Create a key for another app")));
            Assert.NotNull(integrationsLast);
            SettingsCategoriesPage.ScrollIntoView(integrationsLast);
            UiWait.For(() => !integrationsLast.Properties.IsOffscreen.ValueOrDefault, "Integrations last control in view");

            // 9. Gaming & AI Sharing: Save Profile Settings button
            settings.SelectCategory("Gaming & AI Sharing");
            settings.WaitForSectionHeadingVisible("GAMING & AI RESOURCE SHARING");
            var gamingLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Save Profile Settings")));
            Assert.NotNull(gamingLast);
            SettingsCategoriesPage.ScrollIntoView(gamingLast);
            UiWait.For(() => !gamingLast.Properties.IsOffscreen.ValueOrDefault, "Gaming last control in view");

            // 10. PaRuX Platform: Clear button in Universal Diff section
            settings.SelectCategory("PaRuX Platform");
            settings.WaitForSectionHeadingVisible("LICENSE & TRUST");
            var platformLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Clear")));
            Assert.NotNull(platformLast);
            SettingsCategoriesPage.ScrollIntoView(platformLast);
            UiWait.For(() => !platformLast.Properties.IsOffscreen.ValueOrDefault, "Platform last control in view");

            // 11. Privacy & Network: Sign out all browsers button in Browser Access section
            settings.SelectCategory("Privacy & Network");
            settings.WaitForSectionHeadingVisible("GAMING");
            var privacyLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Sign out all browsers")));
            Assert.NotNull(privacyLast);
            SettingsCategoriesPage.ScrollIntoView(privacyLast);
            UiWait.For(() => !privacyLast.Properties.IsOffscreen.ValueOrDefault, "Privacy last control in view");

            // 12. Data: Find models from other apps button, the last control of the Import chat history section
            settings.SelectCategory("Data");
            settings.WaitForSectionHeadingVisible("STORAGE");
            var dataLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Find models from other apps…")));
            Assert.NotNull(dataLast);
            SettingsCategoriesPage.ScrollIntoView(dataLast);
            UiWait.For(() => !dataLast.Properties.IsOffscreen.ValueOrDefault, "Data last control in view");

            // 13. Advanced: System RAM label in Diagnostics table
            settings.SelectCategory("Advanced");
            settings.WaitForSectionHeadingVisible("ADVANCED");
            var advancedLast = app.MainWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Text).And(cf.ByName("System RAM")));
            Assert.NotNull(advancedLast);
            SettingsCategoriesPage.ScrollIntoView(advancedLast);
            UiWait.For(() => !advancedLast.Properties.IsOffscreen.ValueOrDefault, "Advanced last control in view");
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Switching_categories_back_and_forth_keeps_selected_category_consistent() =>
        Run(nameof(Switching_categories_back_and_forth_keeps_selected_category_consistent), app =>
        {
            new MainWindowPage(app.MainWindow).OpenSettings();
            var settings = new SettingsCategoriesPage(app.MainWindow);

            // Initially on General
            settings.WaitForSectionHeadingVisible("GENERAL");

            // Switch to Advanced
            settings.SelectCategory("Advanced");
            settings.WaitForSectionHeadingVisible("ADVANCED");
            settings.WaitForSectionHeadingHidden("GENERAL");

            // Switch to Models
            settings.SelectCategory("Models");
            settings.WaitForSectionHeadingVisible("MODELS");
            settings.WaitForSectionHeadingHidden("ADVANCED");

            // Re-click Models (idempotency check)
            settings.SelectCategory("Models");
            settings.WaitForSectionHeadingVisible("MODELS");
            settings.WaitForSectionHeadingHidden("ADVANCED");

            // Switch back to General
            settings.SelectCategory("General");
            settings.WaitForSectionHeadingVisible("GENERAL");
            settings.WaitForSectionHeadingHidden("MODELS");

            // Verify General controls remain interactive and present
            var themeCombo = UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
            Assert.True(themeCombo.IsEnabled);

            // Switch to Your PC apps
            settings.SelectCategory("Your PC apps");
            settings.WaitForSectionHeadingVisible("APPS ON THIS PC");
            settings.WaitForSectionHeadingHidden("GENERAL");

            // Switch back to General again
            settings.SelectCategory("General");
            settings.WaitForSectionHeadingVisible("GENERAL");
            settings.WaitForSectionHeadingHidden("APPS ON THIS PC");
        });
}
