using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatSimpleAdvancedCoverageTests
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

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode")]
    public void Simple_mode_active_by_default_exposes_simple_controls_and_collapses_advanced_controls() => Run(
        nameof(Simple_mode_active_by_default_exposes_simple_controls_and_collapses_advanced_controls), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);

            // Default startup interface mode is Simple.
            Assert.False(chat.IsAdvancedModeActive(), "PaRuX must start in Simple interface mode by default.");

            // Simple controls must be visible on screen.
            UiWait.For(
                () => chat.AreSimpleControlsVisible(),
                "Simple mode controls (code folder, answering model, route reason, runtime health) are visible");
            Assert.True(chat.AreSimpleControlsVisible(), "Simple mode controls must be visible at startup.");

            // Advanced controls must be collapsed/hidden in Simple mode.
            UiWait.For(
                () => chat.AreAdvancedControlsHidden(),
                "Advanced mode controls (model selector, route explanation, skill selector, profile toggle) are hidden");
            Assert.True(chat.AreAdvancedControlsHidden(), "Advanced controls must be collapsed in Simple mode.");

            // Advanced workspace navigation items must also remain collapsed.
            AutomationElement? routingItem = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.AutomationId.ValueOrDefault == "Navigation_ModelRouting_Item");
            Assert.True(ChatSimpleAdvancedPage.IsElementHidden(routingItem), "Model routing nav item must be collapsed in Simple mode.");

            AutomationElement? runtimeItem = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.AutomationId.ValueOrDefault == "Navigation_Runtime_Item");
            Assert.True(ChatSimpleAdvancedPage.IsElementHidden(runtimeItem), "Runtime nav item must be collapsed in Simple mode.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode")]
    public void Toggling_to_advanced_mode_exposes_advanced_controls_and_collapses_simple_controls() => Run(
        nameof(Toggling_to_advanced_mode_exposes_advanced_controls_and_collapses_simple_controls), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);

            const string draftPrompt = "Drafting message before switching to Advanced";
            chat.EnterPrompt(draftPrompt);
            Assert.Equal(draftPrompt, chat.PromptText);

            chat.EnableAdvanced();

            Assert.True(chat.IsAdvancedModeActive(), "Interface mode toggle must report Advanced state.");

            UiWait.For(
                () => chat.AreAdvancedControlsVisible(),
                "Advanced mode controls are visible after toggling to Advanced");
            Assert.True(chat.AreAdvancedControlsVisible(), "Advanced controls must be visible in Advanced mode.");

            UiWait.For(
                () => chat.AreSimpleControlsHidden(),
                "Simple mode controls are collapsed after toggling to Advanced");
            Assert.True(chat.AreSimpleControlsHidden(), "Simple controls must be collapsed in Advanced mode.");

            // Toggling interface mode must keep the existing prompt and conversation intact.
            Assert.Equal(draftPrompt, chat.PromptText);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode")]
    public void Toggling_back_to_simple_mode_restores_simple_controls_and_preserves_conversation() => Run(
        nameof(Toggling_back_to_simple_mode_restores_simple_controls_and_preserves_conversation), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);

            const string initialPrompt = "Draft turn 1 in simple mode";
            chat.EnterPrompt(initialPrompt);

            // Switch to Advanced.
            chat.EnableAdvanced();
            UiWait.For(() => chat.AreAdvancedControlsVisible(), "Advanced controls visible");

            // Update prompt while in Advanced mode.
            const string editedPrompt = "Draft turn 1 updated in advanced mode";
            chat.EnterPrompt(editedPrompt);
            Assert.Equal(editedPrompt, chat.PromptText);

            // Switch back to Simple mode.
            chat.EnableSimple();
            Assert.False(chat.IsAdvancedModeActive(), "Interface mode toggle must report Simple state.");

            UiWait.For(
                () => chat.AreSimpleControlsVisible(),
                "Simple mode controls restored after switching back to Simple");
            Assert.True(chat.AreSimpleControlsVisible(), "Simple mode controls must be restored.");

            UiWait.For(
                () => chat.AreAdvancedControlsHidden(),
                "Advanced mode controls collapsed after switching back to Simple");
            Assert.True(chat.AreAdvancedControlsHidden(), "Advanced controls must be collapsed.");

            // Preserves edited conversation text.
            Assert.Equal(editedPrompt, chat.PromptText);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode"), Trait("Category", "Navigation")]
    public void Advanced_mode_persists_after_navigating_to_another_workspace_and_back() => Run(
        nameof(Advanced_mode_persists_after_navigating_to_another_workspace_and_back), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            // Enable Advanced mode in Chat.
            chat.EnableAdvanced();
            UiWait.For(() => chat.AreAdvancedControlsVisible(), "Advanced controls visible in Chat");

            // Navigate to another workspace (Projects).
            nav.NavigateTo(WorkspaceNavigationPage.ProjectsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.ProjectsDescriptor);
            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ProjectsDescriptor),
                "Projects landmark must be visible after navigation.");

            // Navigate back to Chat.
            nav.NavigateToChat();
            nav.WaitForWorkspace(WorkspaceNavigationPage.ChatDescriptor);

            // Interface mode must still be Advanced.
            Assert.True(chat.IsAdvancedModeActive(), "Advanced mode must persist after returning from Projects workspace.");

            UiWait.For(
                () => chat.AreAdvancedControlsVisible(),
                "Advanced controls remain visible in Chat after returning from Projects");
            Assert.True(chat.AreAdvancedControlsVisible(), "Advanced controls must remain visible after round trip.");

            UiWait.For(
                () => chat.AreSimpleControlsHidden(),
                "Simple controls remain collapsed in Chat after returning from Projects");
            Assert.True(chat.AreSimpleControlsHidden(), "Simple controls must remain collapsed after round trip.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode"), Trait("Category", "Navigation")]
    public void Simple_mode_persists_after_navigating_to_another_workspace_and_back() => Run(
        nameof(Simple_mode_persists_after_navigating_to_another_workspace_and_back), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            // Start in Simple mode and type a prompt.
            Assert.False(chat.IsAdvancedModeActive(), "Must start in Simple mode.");
            const string roundtripPrompt = "Draft text preserved across workspace trip";
            chat.EnterPrompt(roundtripPrompt);

            // Navigate to Settings workspace.
            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);
            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.SettingsDescriptor),
                "Settings landmark must be visible after navigation.");

            // Navigate back to Chat.
            nav.NavigateToChat();
            nav.WaitForWorkspace(WorkspaceNavigationPage.ChatDescriptor);

            // Interface mode must still be Simple.
            Assert.False(chat.IsAdvancedModeActive(), "Simple mode must persist after returning from Settings workspace.");

            UiWait.For(
                () => chat.AreSimpleControlsVisible(),
                "Simple controls remain visible after returning from Settings");
            Assert.True(chat.AreSimpleControlsVisible(), "Simple controls must remain visible after round trip.");

            UiWait.For(
                () => chat.AreAdvancedControlsHidden(),
                "Advanced controls remain collapsed after returning from Settings");
            Assert.True(chat.AreAdvancedControlsHidden(), "Advanced controls must remain collapsed after round trip.");

            // Draft prompt must be intact.
            Assert.Equal(roundtripPrompt, chat.PromptText);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode"), Trait("Category", "Responsiveness")]
    public void Simple_mode_keeps_essential_controls_reachable_at_narrow_window_width() => Run(
        nameof(Simple_mode_keeps_essential_controls_reachable_at_narrow_window_width), app =>
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);

            // Resize to a compact narrow width (560x600).
            app.ResizeMainWindow(560, 600);

            UiWait.For(() =>
            {
                var prompt = chat.Prompt;
                var send = chat.SendButton;
                var codeFolder = chat.FindCodeFolderButton();
                var modelBadge = chat.FindSimpleModeAnsweringModelLabel();
                var routeReason = chat.FindSimpleModeRouteReasonButton();
                var runtimeHealth = chat.FindSimpleModeRuntimeHealthButton();

                return ChatSimpleAdvancedPage.IsElementVisible(prompt)
                    && ChatSimpleAdvancedPage.IsElementVisible(send)
                    && ChatSimpleAdvancedPage.IsElementVisible(codeFolder)
                    && ChatSimpleAdvancedPage.IsElementVisible(modelBadge)
                    && ChatSimpleAdvancedPage.IsElementVisible(routeReason)
                    && ChatSimpleAdvancedPage.IsElementVisible(runtimeHealth);
            }, "All essential Simple mode controls visible and reachable at narrow width (560px)");

            // Assert bounds and accessibility of the essential controls.
            var promptRect = chat.Prompt.BoundingRectangle;
            Assert.True(promptRect.Width > 80, "Prompt textbox must retain usable editing width at narrow size.");
            Assert.True(promptRect.Height > 0);

            var sendRect = chat.SendButton.BoundingRectangle;
            Assert.True(sendRect.Width > 0 && sendRect.Height > 0, "Send button must have valid dimensions.");

            var folderRect = chat.FindCodeFolderButton()!.BoundingRectangle;
            Assert.True(folderRect.Width > 0 && folderRect.Height > 0, "Code folder button must have valid dimensions.");

            // Verify user can enter input at narrow width.
            const string narrowPrompt = "Can enter message at narrow width";
            chat.EnterPrompt(narrowPrompt);
            Assert.Equal(narrowPrompt, chat.PromptText);

            // Advanced controls must remain collapsed and not overlap or crowd the interface.
            Assert.True(chat.AreAdvancedControlsHidden(), "Advanced controls must remain collapsed at narrow width in Simple mode.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "InterfaceMode"), Trait("Category", "Accessibility")]
    public void Simple_mode_labels_use_plain_language_without_raw_ids_or_internal_terms()
    {
        using var app = new UiTestApplication(
            nameof(Simple_mode_labels_use_plain_language_without_raw_ids_or_internal_terms),
            initialAgentCodingWorkspaceRoot: @"D:\fixtures\alice-repo");

        try
        {
            var chat = new ChatSimpleAdvancedPage(app.MainWindow, app);

            UiWait.For(
                () => chat.AreSimpleControlsVisible(),
                "Simple controls visible for label inspection");

            // 1. Code folder label: must show the directory name in plain language, not a raw internal URI/hash.
            var folderLabel = chat.FindCodeFolderLabel();
            Assert.NotNull(folderLabel);
            Assert.Equal("alice-repo", folderLabel.Name);

            var folderButton = chat.FindCodeFolderButton();
            Assert.NotNull(folderButton);
            Assert.Contains("alice-repo", folderButton.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".git", folderButton.Name, StringComparison.OrdinalIgnoreCase);

            // 2. Answering model badge: plain language, no raw file extensions or socket URLs.
            var modelLabel = chat.FindSimpleModeAnsweringModelLabel();
            Assert.NotNull(modelLabel);
            string modelText = modelLabel.Name;
            Assert.True(
                modelText.StartsWith("Answering model", StringComparison.OrdinalIgnoreCase) || modelText.StartsWith("Model", StringComparison.OrdinalIgnoreCase),
                $"Model label must start with 'Answering model' or 'Model', but was: '{modelText}'");
            Assert.DoesNotContain(".gguf", modelText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".bin", modelText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localhost", modelText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", modelText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("llama-server", modelText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http:", modelText, StringComparison.OrdinalIgnoreCase);

            // 3. Route reason button: plain question text, no raw backend heuristics or parameter equations.
            var reasonButton = chat.FindSimpleModeRouteReasonButton();
            Assert.NotNull(reasonButton);
            Assert.StartsWith("Why this model?", reasonButton.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("temperature", reasonButton.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("top_p", reasonButton.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gpu_layers", reasonButton.Name, StringComparison.OrdinalIgnoreCase);

            // 4. Runtime health indicator: plain language component status, no raw process paths or port numbers.
            var runtimeLabel = chat.FindSimpleModeRuntimeHealthLabel();
            Assert.NotNull(runtimeLabel);
            string runtimeText = runtimeLabel.Name;
            Assert.True(
                runtimeText.StartsWith("Runtime", StringComparison.OrdinalIgnoreCase) || runtimeText.Contains("runtime", StringComparison.OrdinalIgnoreCase),
                $"Runtime label must use plain language, but was: '{runtimeText}'");
            Assert.DoesNotContain(".exe", runtimeText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", runtimeText, StringComparison.OrdinalIgnoreCase);

            // 5. Interface toggle: plain header and state names.
            var toggle = chat.StudioModeToggle;
            Assert.NotNull(toggle);
            Assert.Contains("Interface", toggle.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Simple_mode_labels_use_plain_language_without_raw_ids_or_internal_terms), ex);
            throw;
        }
    }
}
