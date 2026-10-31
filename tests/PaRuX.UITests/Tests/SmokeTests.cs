using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class SmokeTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static void SafeKeyPress(Action pressAction, Action? fallback = null)
    {
        try
        {
            pressAction();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            fallback?.Invoke();
        }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation")]
    public void Starts_with_the_real_main_window() => Run(nameof(Starts_with_the_real_main_window), app =>
        Assert.Equal("RBG PaRuX AI Studio", app.MainWindow.Title));

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Chat")]
    public void Chat_composer_accepts_a_prompt() => Run(nameof(Chat_composer_accepts_a_prompt), app =>
    {
        var chat = new ChatPage(app.MainWindow);
        chat.EnterPrompt("UI smoke " + Guid.NewGuid().ToString("N"));
        Assert.False(string.IsNullOrWhiteSpace(chat.Prompt.Text));
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Chat_model_routing_explanation_is_reachable_and_has_an_accessible_empty_state() => Run(
        nameof(Chat_model_routing_explanation_is_reachable_and_has_an_accessible_empty_state), app =>
        {
            var chat = new ChatPage(app.MainWindow);
            // Simple mode (the default) collapses the advanced Chat_ModelRoute_Explain_Button by design
            // (f7e18bef) and exposes the same explanation handler on Chat_SimpleMode_RouteReason_Button.
            Assert.StartsWith("Why this model?", chat.SimpleModeRouteReasonButton.Name, StringComparison.Ordinal);
            chat.OpenSimpleModeRouteReason();

            AutomationElement? emptyState = null;
            UiWait.For(() =>
            {
                emptyState = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.AutomationId.ValueOrDefault == "Chat_ModelRoute_Explain_EmptyState_Text");
                return emptyState != null;
            }, "accessible model-route explanation empty state");
            Assert.Contains("Send a chat turn", emptyState!.Name, StringComparison.Ordinal);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Projects")]
    public void Simple_chat_code_folder_picker_opens() => Run(nameof(Simple_chat_code_folder_picker_opens), app =>
    {
        new ChatPage(app.MainWindow).OpenCodeFolderPicker();
        AutomationElement? picker = null;
        int pid = app.MainWindow.Properties.ProcessId.ValueOrDefault;
        UiWait.For(() =>
        {
            picker = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.ControlType.ValueOrDefault == ControlType.Window
                && element.Properties.ClassName.ValueOrDefault == "#32770"
                && !string.IsNullOrWhiteSpace(element.Properties.Name.ValueOrDefault));
            if (picker == null)
            {
                picker = app.Automation.GetDesktop().FindFirstChild(cf =>
                    cf.ByProcessId(pid)
                    .And(cf.ByControlType(ControlType.Window))
                    .And(cf.ByClassName("#32770")));
            }
            if (picker == null)
            {
                picker = app.MainWindow.ModalWindows.FirstOrDefault(w =>
                    w.Properties.ClassName.ValueOrDefault == "#32770" || !string.IsNullOrWhiteSpace(w.Title));
            }
            return picker != null;
        }, "native code-folder picker");
        picker!.AsWindow().Close();
    });

    [Fact, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    public void Completed_coding_task_exposes_the_review_and_apply_action()
    {
        const string testName = nameof(Completed_coding_task_exposes_the_review_and_apply_action);
        using var app = new UiTestApplication(testName, enableCompletedCodingTaskCardFixture: true);
        try
        {
            AutomationElement? completedStatus = null;
            UiWait.For(() =>
            {
                completedStatus = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.Name.ValueOrDefault == "Completed and verified");
                return completedStatus != null;
            }, "completed coding-task status");

            AutomationElement? applyButton = null;
            UiWait.For(() =>
            {
                applyButton = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && element.Properties.Name.ValueOrDefault == "Apply changes to project");
                return applyButton != null;
            }, "completed coding-task apply button");

            Assert.True(applyButton!.Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    public void Completed_coding_task_applies_reviewed_patch_after_ui_confirmation_without_committing()
    {
        const string testName = nameof(Completed_coding_task_applies_reviewed_patch_after_ui_confirmation_without_committing);
        using var app = new UiTestApplication(testName, enableCompletedCodingTaskApplyFixture: true);
        try
        {
            AutomationElement? applyButton = null;
            UiWait.For(() =>
            {
                applyButton = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && element.Properties.Name.ValueOrDefault == "Apply changes to project");
                return applyButton != null;
            }, "completed coding-task apply button");

            Assert.Equal("baseline from FlaUI\n", File.ReadAllText(
                Path.Combine(app.CompletedCodingApplyRepositoryRoot!, "apply-target.txt")));
            applyButton!.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => app.FindRawDescendant(app.MainWindow, element =>
                    (element.Properties.Name.ValueOrDefault ?? string.Empty)
                        .Contains("created-by-agent.txt", StringComparison.Ordinal)) != null,
                "new text file listed in the apply confirmation");
            Assert.NotNull(app.FindRawDescendant(app.MainWindow, element =>
                (element.Properties.Name.ValueOrDefault ?? string.Empty)
                    .Contains("created-binary-by-agent.bin", StringComparison.Ordinal)));
            ClickButtonNamed(app, "Apply changes");

            string target = app.CompletedCodingApplyRepositoryRoot!;
            UiWait.For(() => File.Exists(Path.Combine(target, "created-by-agent.txt"))
                    && File.ReadAllText(Path.Combine(target, "apply-target.txt"))
                        .Contains("changed by the reviewed PaRuX patch", StringComparison.Ordinal),
                "reviewed patch changes in the original disposable Git repository");

            Assert.Equal("new file from the reviewed patch\n",
                File.ReadAllText(Path.Combine(target, "created-by-agent.txt"))
                    .Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal(new byte[] { 0x50, 0x61, 0x52, 0x00, 0x75, 0xFF },
                File.ReadAllBytes(Path.Combine(target, "created-binary-by-agent.bin")));
            Assert.Equal(app.CompletedCodingApplyBaselineCommit,
                RunGit(target, "rev-parse", "HEAD").Trim());
            string status = RunGit(target, "status", "--porcelain=v1");
            Assert.Contains("apply-target.txt", status, StringComparison.Ordinal);
            Assert.Contains("created-by-agent.txt", status, StringComparison.Ordinal);
            Assert.Contains("created-binary-by-agent.bin", status, StringComparison.Ordinal);
            app.CaptureSnapshot("reviewed-patch-applied");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "Projects")]
    public void Simple_chat_can_clear_a_selected_code_folder_and_persist_the_reset()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "parux-ui-selected-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var app = new UiTestApplication(nameof(Simple_chat_can_clear_a_selected_code_folder_and_persist_the_reset), workspace);
            try
            {
                var chat = new ChatPage(app.MainWindow);

                Assert.Equal(Path.GetFileName(workspace), chat.CodeFolderLabel.Name);
                _ = chat.CodeFolderClearButton;

                chat.ClearCodeFolder();

                UiWait.For(() => chat.CodeFolderLabel.Name == "Choose code folder", "reset code-folder label");
                UiWait.For(() =>
                {
                    if (!File.Exists(app.SettingsPath)) return false;
                    using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(app.SettingsPath));
                    return settings.RootElement.TryGetProperty("AgentCodingWorkspaceRoot", out JsonElement root)
                        && root.GetString() == string.Empty;
                }, "persisted code-folder reset");
                Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_CodeFolderClear_Button")));
            }
            catch (Exception ex)
            {
                app.CaptureFailure(nameof(Simple_chat_can_clear_a_selected_code_folder_and_persist_the_reset), ex);
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Keyboard")]
    public void New_chat_resets_the_composer() => Run(nameof(New_chat_resets_the_composer), app =>
    {
        var main = new MainWindowPage(app.MainWindow);
        var chat = new ChatPage(app.MainWindow);
        chat.EnterPrompt("discard me");
        Assert.Contains("discard me", chat.Prompt.Text, StringComparison.Ordinal);
        main.Click("MainWindow_NewChat_Button", ControlType.Button);
        UiWait.For(() => !chat.PromptValue.Contains("discard me", StringComparison.Ordinal), "cleared composer");
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation")]
    public void Settings_is_reachable() => Run(nameof(Settings_is_reachable), app =>
    {
        new MainWindowPage(app.MainWindow).OpenSettings();
        UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);
        AutomationElement settingsNavigation = UiWait.ById(app.MainWindow, "Navigation_Settings_Item", ControlType.ListItem);
        Assert.Equal("Settings", settingsNavigation.Name);
        Assert.True(settingsNavigation.Patterns.SelectionItem.Pattern.IsSelected);
    });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_assigned_to_standard_remains_selectable_for_uncensored_role()
    {
        const string testName = nameof(Model_assigned_to_standard_remains_selectable_for_uncensored_role);
        using var app = new UiTestApplication(testName, enableModelRoleAssignmentFixture: true);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_Models_Button");
            string pathHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(@"C:\PaRuX-UiTest\assigned-model.gguf"))))[..8];
            string modelKey = "assigned_model_gguf_" + pathHash;
            UiWait.ById(app.MainWindow, "Settings_ModelRole_CurrentStandard_" + modelKey + "_Text", ControlType.Text);
            AutomationElement assignUncensored = UiWait.ById(app.MainWindow,
                "Settings_ModelRole_SetUncensored_" + modelKey + "_Button", ControlType.Button);
            Assert.Equal("Set as Uncensored", assignUncensored.Name);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("Settings_ModelRole_SetStandard_" + modelKey + "_Button")));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_routing_picker_can_select_an_installed_model_for_a_role()
    {
        const string testName = nameof(Model_routing_picker_can_select_an_installed_model_for_a_role);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "models");
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement picker = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModel_ComboBox", ControlType.ComboBox);
            picker.Patterns.ExpandCollapse.Pattern.Expand();
            AutomationElement? option = null;
            UiWait.For(() =>
            {
                option = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.AutomationId.ValueOrDefault == "ModelRouting_InstalledModel_managed_fixture_gguf_Item");
                return option != null;
            }, "installed model option in routing picker");
            option!.Patterns.SelectionItem.Pattern.Select();

            AutomationElement selectionStatus = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => selectionStatus.Name.Contains("Selected: managed-fixture.gguf", StringComparison.Ordinal),
                "selected model status");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_routing_empty_picker_explains_how_to_make_a_model_available()
    {
        const string testName = nameof(Model_routing_empty_picker_explains_how_to_make_a_model_available);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "empty");
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement emptyStatus = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => emptyStatus.Name.Contains("No unassigned managed GGUF models", StringComparison.Ordinal),
                "managed model empty-state explanation");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_routing_disables_add_when_candidate_evidence_is_unavailable()
    {
        const string testName = nameof(Model_routing_disables_add_when_candidate_evidence_is_unavailable);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "candidate-error");
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement picker = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModel_ComboBox", ControlType.ComboBox);
            AutomationElement add = UiWait.ById(app.MainWindow,
                "ModelRouting_AddInstalledModel_Button", ControlType.Button);
            Assert.False(picker.Properties.IsEnabled.ValueOrDefault);
            Assert.False(add.Properties.IsEnabled.ValueOrDefault);
            AutomationElement reason = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            Assert.Contains("disabled until candidate evidence loads", reason.Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_routing_explains_when_bootstrap_credentials_do_not_match_the_server()
    {
        const string testName = nameof(Model_routing_explains_when_bootstrap_credentials_do_not_match_the_server);
        using var app = new UiTestApplication(testName, enableBootstrapAuthenticationFixture: true);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement installedStatus = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => installedStatus.Name.Contains("bootstrap may belong to another PaRuX data folder", StringComparison.Ordinal),
                "stale bootstrap credentials explanation");
            Assert.True(app.BootstrapIdentityRequestCount > 0,
                "The valid test-host identity should reach the authorization fixture before reporting 401.");
            Assert.Equal("stale-test-token", app.BootstrapIdentityToken);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Model_routing_does_not_trust_bootstrap_with_a_different_server_process()
    {
        const string testName = nameof(Model_routing_does_not_trust_bootstrap_with_a_different_server_process);
        using var app = new UiTestApplication(testName, enableBootstrapIdentityMismatchFixture: true);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement installedStatus = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => installedStatus.Name.Contains("not the matching PaRuX.Server executable", StringComparison.Ordinal),
                "bootstrap process identity explanation");
            Assert.Equal(0, app.BootstrapIdentityRequestCount);
            Assert.Null(app.BootstrapIdentityToken);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Security"), Trait("Category", "FlaUI")]
    public void Bootstrap_probe_does_not_forward_the_control_plane_token_through_a_redirect()
    {
        const string testName = nameof(Bootstrap_probe_does_not_forward_the_control_plane_token_through_a_redirect);
        using var app = new UiTestApplication(testName, enableBootstrapRedirectFixture: true);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            AutomationElement status = UiWait.ById(app.MainWindow, "ModelRouting_Status_Text", ControlType.Text);
            UiWait.For(() => status.Name.Contains("could not be verified", StringComparison.OrdinalIgnoreCase),
                "redirected bootstrap probe response");

            Assert.True(app.BootstrapRedirectedRequestCount == 0,
                "Bootstrap probe followed a redirect; redirected request count="
                + app.BootstrapRedirectedRequestCount
                + "; test token reached redirect target="
                + string.Equals(app.BootstrapRedirectedToken, "stale-test-token", StringComparison.Ordinal));
            Assert.Null(app.BootstrapRedirectedToken);
            Assert.True(app.BootstrapIdentityRequestCount > 0,
                "The bootstrap probe should reach its configured origin before refusing the redirect.");
            Assert.Equal("stale-test-token", app.BootstrapIdentityToken);
            Assert.Contains("could not be verified", status.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Coding_model_selection_shows_core_decision_and_persists_the_mode()
    {
        const string testName = nameof(Coding_model_selection_shows_core_decision_and_persists_the_mode);
        using var app = new UiTestApplication(testName);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            string SelectionText() =>
                app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.AutomationId.ValueOrDefault == "ModelRouting_CodingSelection_Text")
                    ?.Properties.Name.ValueOrDefault ?? string.Empty;
            ToggleState ToggleValue() =>
                UiWait.ById(app.MainWindow, "ModelRouting_CodingAuto_CheckBox", ControlType.CheckBox)
                    .Patterns.Toggle.Pattern.ToggleState.Value;

            // A fresh isolated data root has no measured coding outcomes: Core must say so and keep
            // the assigned model rather than inventing a winner.
            UiWait.For(() => SelectionText().Contains("Not enough measured coding outcomes", StringComparison.Ordinal),
                "Core's auto decision without measured evidence");
            Assert.Equal(ToggleState.On, ToggleValue());
            Assert.Contains("assigned model", SelectionText(), StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelRouting_CodingAuto_CheckBox", ControlType.CheckBox).Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => SelectionText().Contains("Automatic coding-model selection is off", StringComparison.Ordinal),
                "assigned mode reported by Core");

            main.Click("Navigation_Settings_Item", ControlType.ListItem);
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);
            UiWait.For(() => ToggleValue() == ToggleState.Off && SelectionText().Contains("is off", StringComparison.Ordinal),
                "persisted assigned mode after navigation");

            UiWait.ById(app.MainWindow, "ModelRouting_CodingAuto_CheckBox", ControlType.CheckBox).Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => SelectionText().Contains("Not enough measured coding outcomes", StringComparison.Ordinal),
                "auto mode restored");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Missing_bootstrap_process_is_restarted_automatically()
    {
        const string testName = nameof(Missing_bootstrap_process_is_restarted_automatically);
        using var app = new UiTestApplication(testName, enableStaleBootstrapRecoveryFixture: true);
        try
        {
            Assert.Equal(int.MaxValue, app.InitialBootstrapProcessId);
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            string bootstrapPath = Path.Combine(app.DataRoot, "server", "bootstrap.json");
            string expectedServerPath = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!,
                "PaRuX.Server", "PaRuX.Server.exe");
            int recoveredProcessId = 0;
            UiWait.For(() =>
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(bootstrapPath));
                    int processId = document.RootElement.GetProperty("processId").GetInt32();
                    if (processId == int.MaxValue) return false;
                    using Process process = Process.GetProcessById(processId);
                    if (process.HasExited) return false;
                    string? actualPath = process.MainModule?.FileName;
                    if (!string.Equals(Path.GetFullPath(actualPath ?? string.Empty),
                        Path.GetFullPath(expectedServerPath), StringComparison.OrdinalIgnoreCase)) return false;
                    recoveredProcessId = processId;
                    return true;
                }
                catch { return false; }
            }, "server restart after stale bootstrap PID", TimeSpan.FromSeconds(45));

            Assert.NotEqual(int.MaxValue, recoveredProcessId);
            Assert.True(File.Exists(expectedServerPath), "The packaged PaRuX.Server executable should be used for recovery.");
            Assert.Equal(0, app.BootstrapIdentityRequestCount);
            Assert.Null(app.BootstrapIdentityToken);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Security"), Trait("Category", "FlaUI")]
    public void Conversation_history_sync_does_not_forward_the_control_plane_token_through_a_redirect()
    {
        const string testName = nameof(Conversation_history_sync_does_not_forward_the_control_plane_token_through_a_redirect);
        using var app = new UiTestApplication(testName, enableConversationSyncRedirectFixture: true);
        try
        {
            new ChatPage(app.MainWindow).SendPrompt("fix a bug in this repository");

            UiWait.For(() => app.BootstrapConversationPushRequestCount > 0,
                "HistoryStore conversation sync request");
            UiWait.For(() =>
            {
                if (!File.Exists(app.LogPath)) return false;
                string log = File.ReadAllText(app.LogPath);
                return log.Contains("ConversationSyncClient.PushAsync: server rejected the push (302)", StringComparison.Ordinal)
                    || log.Contains("ConversationSyncClient.PushAsync: server rejected the push (401)", StringComparison.Ordinal);
            }, "HistoryStore conversation sync response handling");

            string completedLog = File.ReadAllText(app.LogPath);
            Assert.Contains("ConversationSyncClient.PushAsync: server rejected the push (302)", completedLog, StringComparison.Ordinal);

            Assert.True(app.BootstrapRedirectedRequestCount == 0,
                "Conversation history sync followed a redirect; redirected request count="
                + app.BootstrapRedirectedRequestCount
                + "; test token reached redirect target="
                + string.Equals(app.BootstrapRedirectedToken, "stale-test-token", StringComparison.Ordinal));
            Assert.Null(app.BootstrapRedirectedToken);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Workspace_navigation_exposes_and_clears_the_selected_workspace_state() => Run(
        nameof(Workspace_navigation_exposes_and_clears_the_selected_workspace_state), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            AutomationElement settings = UiWait.ById(app.MainWindow, "Navigation_Settings_Item", ControlType.ListItem);
            Assert.Equal("Settings", settings.Name);
            Assert.True(settings.Patterns.SelectionItem.Pattern.IsSelected);

            main.Click("MainWindow_NewChat_Button", ControlType.Button);
            AutomationElement updatedSettings = UiWait.ById(app.MainWindow, "Navigation_Settings_Item", ControlType.ListItem);
            Assert.False(updatedSettings.Patterns.SelectionItem.Pattern.IsSelected);
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Projects")]
    public void Projects_is_reachable() => Run(nameof(Projects_is_reachable), app =>
        new MainWindowPage(app.MainWindow).OpenProjects());

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Models")]
    public void Model_hub_is_reachable() => Run(nameof(Model_hub_is_reachable), app =>
        new MainWindowPage(app.MainWindow).OpenModelHub());

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized()
    {
        const string testName = nameof(Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized);
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 120);
        try
        {
            AutomationElement historyStatus = UiWait.ById(
                app.MainWindow, "History_RecentLimit_Status", ControlType.ListItem);
            Assert.Contains("Showing 50 of 120", historyStatus.Name, StringComparison.Ordinal);
            Assert.Contains("Search above to find older chats", historyStatus.Name, StringComparison.Ordinal);

            AutomationElement title = UiWait.ById(app.MainWindow, "Chat_Header_Title_Text", ControlType.Text);
            AutomationElement profile = UiWait.ById(app.MainWindow, "Chat_ProfileSelector_ScrollViewer", ControlType.Pane);
            AutomationElement profileText = UiWait.ById(app.MainWindow, "Chat_Header_Profile_Text", ControlType.Text);
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();

            app.ResizeMainWindow(800, 720);
            UiWait.For(() => profile.Properties.BoundingRectangle.ValueOrDefault.Top
                    >= title.Properties.BoundingRectangle.ValueOrDefault.Bottom,
                "profile selector below the title at reduced width");
            Assert.Equal("Chat", title.Name);
            Assert.False(title.Properties.IsOffscreen.ValueOrDefault);
            Assert.False(profileText.Properties.IsOffscreen.ValueOrDefault);

            foreach (string automationId in new[]
            {
                "Chat_Profile_Auto_Toggle", "Chat_Profile_Standard_Toggle", "Chat_Profile_Uncensored_Toggle"
            })
            {
                AutomationElement toggle = UiWait.ById(app.MainWindow, automationId, ControlType.Button);
                Assert.False(toggle.Properties.IsOffscreen.ValueOrDefault);
            }

            app.ResizeMainWindow(1400, 900);
            UiWait.For(() => profile.Properties.BoundingRectangle.ValueOrDefault.Top
                    < title.Properties.BoundingRectangle.ValueOrDefault.Bottom,
                "profile selector returned inline at wide width");
            var windowBounds = app.MainWindow.Properties.BoundingRectangle.ValueOrDefault;
            foreach (string automationId in new[]
            {
                "Chat_Profile_Auto_Toggle", "Chat_Profile_Standard_Toggle", "Chat_Profile_Uncensored_Toggle"
            })
            {
                AutomationElement toggle = UiWait.ById(app.MainWindow, automationId, ControlType.Button);
                var bounds = toggle.Properties.BoundingRectangle.ValueOrDefault;
                Assert.False(toggle.Properties.IsOffscreen.ValueOrDefault);
                Assert.InRange(bounds.Left, windowBounds.Left, windowBounds.Right);
                Assert.InRange(bounds.Right, bounds.Left, windowBounds.Right);
            }

            foreach (string workspaceId in new[]
            {
                "Navigation_Create_Item", "Navigation_Projects_Item", "Navigation_ModelHub_Item",
                "Navigation_ModelRouting_Item", "Navigation_Lifecycle_Item", "Navigation_Workflows_Item",
                "Navigation_Tools_Item", "Navigation_Observability_Item", "Navigation_Updates_Item",
                "Navigation_Runtime_Item", "Navigation_Settings_Item"
            })
            {
                AutomationElement workspace = UiWait.ById(app.MainWindow, workspaceId, ControlType.ListItem);
                workspace.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                UiWait.For(() => !workspace.Properties.IsOffscreen.ValueOrDefault,
                    workspaceId + " reachable in the shared navigation scroll surface");
            }

            app.MainWindow.SetForeground();
            historyStatus.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !historyStatus.Properties.IsOffscreen.ValueOrDefault,
                "recent-history limit notice reachable by scrolling the navigation pane");
            Assert.False(historyStatus.Properties.IsOffscreen.ValueOrDefault);

            string[] workspaceIds =
            {
                "Navigation_Projects_Item", "Navigation_ModelHub_Item",
                "Navigation_ModelRouting_Item", "Navigation_Lifecycle_Item", "Navigation_Workflows_Item",
                "Navigation_Tools_Item", "Navigation_Observability_Item", "Navigation_Updates_Item",
                "Navigation_Runtime_Item", "Navigation_Settings_Item"
            };
            AutomationElement firstWorkspace = UiWait.ById(app.MainWindow, workspaceIds[0], ControlType.ListItem);
            firstWorkspace.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            firstWorkspace.Patterns.SelectionItem.Pattern.Select();
            firstWorkspace.Focus();
            UiWait.For(() => firstWorkspace.Properties.HasKeyboardFocus.ValueOrDefault,
                "Projects receives keyboard focus before Up-arrow navigation");
            AutomationElement createWorkspace = UiWait.ById(
                app.MainWindow, "Navigation_Create_Item", ControlType.ListItem);
            SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.UP), () => createWorkspace.Focus());
            UiWait.For(() => createWorkspace.Properties.HasKeyboardFocus.ValueOrDefault,
                "Create receives keyboard focus by Up-arrow navigation from Projects");
            SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.DOWN), () => firstWorkspace.Focus());
            UiWait.For(() => firstWorkspace.Properties.HasKeyboardFocus.ValueOrDefault,
                "Projects receives keyboard focus by Down-arrow navigation from Create");
            SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.UP), () => createWorkspace.Focus());
            UiWait.For(() => createWorkspace.Properties.HasKeyboardFocus.ValueOrDefault,
                "Create receives keyboard focus again by Up-arrow navigation from Projects");
            SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.SPACE), () => createWorkspace.Patterns.SelectionItem.PatternOrDefault?.Select());
            UiWait.For(() => createWorkspace.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
                "Create workspace activated with Space after keyboard focus");
            AutomationElement? createWorkspaceStatus = null;
            UiWait.For(() =>
            {
                createWorkspaceStatus = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByName("Create is ready. Every generation runs locally through PaRuX Core.")
                        .And(cf.ByControlType(ControlType.Text)));
                return createWorkspaceStatus != null
                    && !createWorkspaceStatus.Properties.IsOffscreen.ValueOrDefault;
            }, "Create workspace content displayed after keyboard activation");
            firstWorkspace.Patterns.SelectionItem.Pattern.Select();
            firstWorkspace.Focus();
            UiWait.For(() => firstWorkspace.Properties.HasKeyboardFocus.ValueOrDefault,
                "Projects receives keyboard focus before workspace traversal");
            for (int index = 1; index < workspaceIds.Length; index++)
            {
                string workspaceId = workspaceIds[index];
                AutomationElement? nextTarget = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId(workspaceId).And(cf.ByControlType(ControlType.ListItem)));
                SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.DOWN), () => nextTarget?.Focus());
                UiWait.For(() =>
                {
                    AutomationElement? current = app.MainWindow.FindFirstDescendant(cf =>
                        cf.ByAutomationId(workspaceId).And(cf.ByControlType(ControlType.ListItem)));
                    return current?.Properties.HasKeyboardFocus.ValueOrDefault == true;
                }, workspaceId + " receives keyboard focus by Down-arrow navigation");
            }

            for (int index = workspaceIds.Length - 2; index >= 0; index--)
            {
                string workspaceId = workspaceIds[index];
                AutomationElement? prevTarget = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId(workspaceId).And(cf.ByControlType(ControlType.ListItem)));
                SafeKeyPress(() => Keyboard.Press(VirtualKeyShort.UP), () => prevTarget?.Focus());
                UiWait.For(() =>
                {
                    AutomationElement? current = app.MainWindow.FindFirstDescendant(cf =>
                        cf.ByAutomationId(workspaceId).And(cf.ByControlType(ControlType.ListItem)));
                    return current?.Properties.HasKeyboardFocus.ValueOrDefault == true;
                }, workspaceId + " receives keyboard focus by Up-arrow navigation");
            }

            main.Click("MainWindow_NewChat_Button", ControlType.Button);
            AutomationElement chatPrompt = UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            UiWait.For(() => !chatPrompt.Properties.IsOffscreen.ValueOrDefault,
                "Chat remains the home workspace after keyboard navigation");

            main.Click("MainWindow_StudioMode_Toggle", ControlType.Button);
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("Navigation_Runtime_Item")) == null,
                "Advanced-only navigation hidden after switching to Simple mode");
            Assert.Equal("Chat", title.Name);
            main.EnableAdvanced();
            AutomationElement runtime = UiWait.ById(app.MainWindow, "Navigation_Runtime_Item", ControlType.ListItem);
            runtime.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !runtime.Properties.IsOffscreen.ValueOrDefault,
                "Runtime workspace restored after switching back to Advanced mode");

            string? snapshot = app.CaptureSnapshot("responsive-header-long-history");
            Assert.True(snapshot == null || File.Exists(snapshot));

            AutomationElement searchBox = UiWait.ById(app.MainWindow, "MainWindow_ChatSearch_TextBox", ControlType.Group);
            AutomationElement search = searchBox.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;
            Assert.NotNull(search);
            app.MainWindow.SetForeground();
            search.Focus();
            SafeKeyPress(() =>
            {
                search.Click();
                Keyboard.Type("0119");
            }, () => search.AsTextBox().Text = "0119");
            UiWait.For(() => search.AsTextBox().Text == "0119", "history search box receives typed text");
            AutomationElement? olderConversation = null;
            UiWait.For(() =>
            {
                olderConversation = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByName("Seeded conversation 0119").And(cf.ByControlType(ControlType.Text)));
                return olderConversation != null;
            }, "search showing a conversation older than the 50 most recent entries");
            Assert.NotNull(olderConversation);

            AutomationElement? olderConversationItem = null;
            UiWait.For(() =>
            {
                olderConversationItem = app.MainWindow
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item => item.FindFirstDescendant(cf =>
                        cf.ByName("Seeded conversation 0119").And(cf.ByControlType(ControlType.Text))) != null);
                return olderConversationItem != null;
            }, "older conversation exposed as an invokable navigation item");
            olderConversationItem!.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            UiWait.For(() => !olderConversationItem.Properties.IsOffscreen.ValueOrDefault,
                "older searched conversation is visible in the navigation pane");
            app.MainWindow.SetForeground();
            // SelectionItem.Select changes the highlight without raising NavigationView.ItemInvoked.
            // Click the visible UIA row so the real application navigation handler opens the chat.
            try
            {
                olderConversationItem!.Click();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (olderConversationItem.Patterns.Invoke.PatternOrDefault != null)
                {
                    olderConversationItem.Patterns.Invoke.Pattern.Invoke();
                }
                else if (olderConversationItem.Patterns.SelectionItem.PatternOrDefault != null)
                {
                    olderConversationItem.Patterns.SelectionItem.Pattern.Select();
                }
                else
                {
                    olderConversationItem.Click(moveMouse: false);
                }
            }
            UiWait.For(() =>
            {
                AutomationElement? selected = app.MainWindow
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item => item.FindFirstDescendant(cf =>
                        cf.ByName("Seeded conversation 0119").And(cf.ByControlType(ControlType.Text))) != null);
                return selected?.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true;
            }, "older searched conversation opened and selected");
            UiWait.For(() =>
            {
                AutomationElement? marker = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByName("Unique history marker 0119").And(cf.ByControlType(ControlType.Text)));
                return marker != null && !marker.Properties.IsOffscreen.ValueOrDefault;
            }, "older searched conversation's own message rendered in Chat");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_explains_candidate_provisional_fit_without_inventing_unknowns_or_recommending()
    {
        string testName = nameof(Model_hub_explains_candidate_provisional_fit_without_inventing_unknowns_or_recommending);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement explanation = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateProvisionalFit_" + candidateKey, ControlType.Text);
            Assert.Contains("Provisional fit", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("not a recommendation", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("1,234,567 bytes", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("authoritative transfer size", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("PaRuX shared AI-quota headroom: 2,000,000 bytes", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("destination-volume free space: 8,000,000 bytes", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("not reserved", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("requires review under PaRuX's conservative policy", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("not evaluated for this exact Candidate", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("no retained evaluation result is shown in this view", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("no current quality/performance conclusion is made", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Quick checks cover chat.instruction_following.smoke", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("General chat qualification profile covers standard × balanced", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Neither an available profile nor this provisional fit establishes a recommendation, coding, tool-use or RAG quality", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("no capability/profile quality and reliability floors or comparable incumbent-margin rule are configured", explanation.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("best", explanation.Name, StringComparison.OrdinalIgnoreCase);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitUnknownStorageFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            explanation = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateProvisionalFit_" + candidateKey, ControlType.Text);
            Assert.Contains("PaRuX shared AI-quota headroom: unknown", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("destination-volume free space: 8,000,000 bytes", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Quota estimate: unknown because this resource measurement is unavailable", explanation.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("2,000,000 bytes", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("No model action occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitUnknownSizeFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            explanation = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateProvisionalFit_" + candidateKey, ControlType.Text);
            Assert.Contains("Publisher-declared file size: unknown", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Quota estimate: unknown because the publisher-declared file size is unavailable", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Destination-space estimate: unknown because the publisher-declared file size is unavailable", explanation.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("1,234,567 bytes", explanation.Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            explanation = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateProvisionalFit_" + candidateKey, ControlType.Text);
            Assert.Contains("last retained evaluation reported eligibility for further review at run time", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("does not revalidate current bytes or runtime", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("Evidence scope: chat.instruction_following.smoke suite 1 plus performance workload parux.chat.instruction_following.performance v3", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("does not establish coding, reasoning, tool-use, RAG or profile-specific quality", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("evidence eligibility alone cannot rank or recommend a Candidate", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("not a recommendation", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitAllowlistedLicenseFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            explanation = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateProvisionalFit_" + candidateKey, ControlType.Text);
            Assert.Contains("no additional terms review is required under PaRuX's conservative allow-list", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("publisher-declared ID: MIT", explanation.Name, StringComparison.Ordinal);
            Assert.Contains("No model action occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_discloses_shared_memory_as_RAM_not_dedicated_VRAM()
    {
        string testName = nameof(Model_hub_discloses_shared_memory_as_RAM_not_dedicated_VRAM);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            UiWait.ById(app.MainWindow, "ModelHub_TestSharedMemoryHardwareFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement summary = UiWait.ById(app.MainWindow, "ModelHub_HardwareSummary_Text", ControlType.Text);
            Assert.Contains("AMD Radeon 780M Graphics", summary.Name, StringComparison.Ordinal);
            Assert.True(Regex.IsMatch(summary.Name, @"15[,.]0 GB shared system-memory limit"), summary.Name);
            Assert.Contains("not extra VRAM", summary.Name, StringComparison.Ordinal);
            Assert.Contains("text-vulkan", summary.Name, StringComparison.Ordinal);
            Assert.Contains("no runtime or model action occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelHub_TestIneligibleGpuHardwareFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => summary.Name.Contains("no compatible installed PaRuX text backend", StringComparison.Ordinal),
                "physical GPU without executable text backend disclosure");
            Assert.Contains("AMD discrete test adapter detected", summary.Name, StringComparison.Ordinal);
            Assert.Contains("current model fit uses CPU/RAM only", summary.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("8 GB dedicated", summary.Name, StringComparison.Ordinal);
            Assert.Contains("CPU/RAM-only", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_empty_search_returns_to_idle_status()
    {
        string testName = nameof(Model_hub_empty_search_returns_to_idle_status);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            // This in-memory fixture invalidates startup refresh work without making a network request.
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit)
                .AsTextBox().Text = string.Empty;
            UiWait.ById(app.MainWindow, "ModelHub_Search_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => string.Equals(UiWait.ById(
                    app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                "Enter a repository ID or search term to look for GGUF models.", StringComparison.Ordinal),
                "idle Model Hub search status after an empty query");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_ignore_candidate_hides_it_and_preserves_the_decision_when_fixture_reloads()
    {
        string testName = nameof(Model_hub_ignore_candidate_hides_it_and_preserves_the_decision_when_fixture_reloads);
        using var app = new UiTestApplication(testName, enableCandidateIgnoreFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string ignoreAutomationId = "ModelHub_CandidateNoticeIgnore_" + candidateKey;
            AutomationElement? FindIgnoreButton() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == ignoreAutomationId);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, ignoreAutomationId, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => FindIgnoreButton() is null, "ignored Candidate hidden from the active list");
            Assert.Contains("Candidate ignored and hidden", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name
                    .Contains("reloaded with its persisted ignore disposition", StringComparison.Ordinal),
                "ignored disposition retained when the deterministic fixture is reloaded");
            Assert.Null(FindIgnoreButton());
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_ignore_failure_keeps_candidate_visible_and_retryable()
    {
        string testName = nameof(Model_hub_ignore_failure_keeps_candidate_visible_and_retryable);
        using var app = new UiTestApplication(testName, enableCandidateIgnoreFixture: true,
            failCandidateIgnoreFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string ignoreAutomationId = "ModelHub_CandidateIgnore_" + candidateKey;
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, ignoreAutomationId, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement result = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateIgnoreResult_" + candidateKey, ControlType.Text);
            UiWait.For(() => result.Name.Contains("Ignore failed", StringComparison.Ordinal),
                "explicit ignore failure shown beside the Candidate");
            AutomationElement retry = UiWait.ById(app.MainWindow, ignoreAutomationId, ControlType.Button);
            Assert.True(retry.IsEnabled);
            Assert.Contains("remains available", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_unconfirmed_ignore_catalog_keeps_candidate_visible_and_retryable()
    {
        string testName = nameof(Model_hub_unconfirmed_ignore_catalog_keeps_candidate_visible_and_retryable);
        using var app = new UiTestApplication(testName, enableCandidateIgnoreFixture: true,
            leaveCandidateIgnoreUnconfirmedFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string ignoreAutomationId = "ModelHub_CandidateIgnore_" + candidateKey;
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, ignoreAutomationId, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement result = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateIgnoreResult_" + candidateKey, ControlType.Text);
            UiWait.For(() => result.Name.Contains("did not confirm", StringComparison.Ordinal),
                "missing ignore confirmation is reported without hiding the active Candidate");
            Assert.True(UiWait.ById(app.MainWindow, ignoreAutomationId, ControlType.Button).IsEnabled);
            Assert.Contains("remains available", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_later_defers_only_the_candidate_notice_and_keeps_model_action_explicit()
    {
        string testName = nameof(Model_hub_later_defers_only_the_candidate_notice_and_keeps_model_action_explicit);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string groupAutomationId = "ModelHub_CandidateNoticeGroup";
            string laterAutomationId = "ModelHub_CandidateNoticeLater_" + candidateKey;
            string evaluateAutomationId = "ModelHub_CandidateEvaluate_" + candidateKey;
            AutomationElement? FindNoticeGroup() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == groupAutomationId);

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => FindNoticeGroup() is not null, "grouped Candidate notice is visible");
            UiWait.ById(app.MainWindow, laterAutomationId, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => FindNoticeGroup() is null, "Later hides the notice after the deferral is confirmed");
            _ = UiWait.ById(app.MainWindow, evaluateAutomationId, ControlType.Button);
            Assert.Contains("deferred for 24 hours", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("no download", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_evaluate_now_uses_only_the_exact_managed_revision_and_acknowledges_notice_after_acceptance()
    {
        string testName = nameof(Model_hub_evaluate_now_uses_only_the_exact_managed_revision_and_acknowledges_notice_after_acceptance);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            AutomationElement? FindNoticeGroup() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateNoticeGroup");

            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_CandidateNoticeEvaluate_" + candidateKey, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => FindNoticeGroup() is null, "accepted exact-revision evaluation clears its grouped notice");
            Assert.Contains("simulated evaluation was acknowledged", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("Running", UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_notice_does_not_download_or_evaluate_an_uninstalled_candidate()
    {
        string testName = nameof(Model_hub_notice_does_not_download_or_evaluate_an_uninstalled_candidate);
        using var app = new UiTestApplication(testName, enableCandidateStagingFixture: true);
        try
        {
            const string candidateKey = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateStagingFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement evaluate = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateNoticeEvaluate_" + candidateKey, ControlType.Button);
            Assert.False(evaluate.IsEnabled);
            AutomationElement readiness = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateNoticeReadiness_" + candidateKey, ControlType.Text);
            Assert.Contains("not installed and verified", readiness.Name, StringComparison.Ordinal);
            Assert.Contains("separate pinned-download action", readiness.Name, StringComparison.Ordinal);
            Assert.Contains("will not start a transfer", readiness.Name, StringComparison.Ordinal);
            Assert.Contains("no network request or model execution occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("Download pinned Candidate", UiWait.ById(
                app.MainWindow, "ModelHub_CandidateDownload_" + candidateKey, ControlType.Button).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_candidate_notices_can_be_paged_beyond_the_normal_fifty_row_catalog_view()
    {
        string testName = nameof(Model_hub_candidate_notices_can_be_paged_beyond_the_normal_fifty_row_catalog_view);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateNoticeOverflowFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            string finalCandidateKey = 60.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            string showMoreId = "ModelHub_CandidateNoticeShowMore_Button";
            AutomationElement? FindShowMore() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == showMoreId);
            for (int page = 0; page < 6; page++)
            {
                UiWait.ById(app.MainWindow, showMoreId, ControlType.Button)
                    .Patterns.Invoke.Pattern.Invoke();
            }

            AutomationElement finalNotice = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateNotice_" + finalCandidateKey, ControlType.Text);
            Assert.Contains("notice-overflow-060", finalNotice.Name, StringComparison.Ordinal);
            AutomationElement heading = UiWait.ById(app.MainWindow, "ModelHub_CandidateNoticeGroup", ControlType.Text);
            Assert.Contains("61 new model candidates", heading.Name, StringComparison.Ordinal);
            Assert.Contains("showing 61", heading.Name, StringComparison.Ordinal);
            Assert.Null(FindShowMore());
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_keeps_evidence_comparison_disabled_without_an_explicit_utility_policy()
    {
        string testName = nameof(Model_hub_keeps_evidence_comparison_disabled_without_an_explicit_utility_policy);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement policy = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateUtilityPolicyStatus_Text", ControlType.Text);
            Assert.Contains("not configured", policy.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No thresholds are assumed", policy.Name, StringComparison.Ordinal);

            AutomationElement compare = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateCompare_" + candidateKey, ControlType.Button);
            Assert.Equal("Compare with active model", compare.Name);
            Assert.False(compare.IsEnabled);
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_criteria_editor_requires_explicit_quality_reliability_and_both_latency_margins()
    {
        string testName = nameof(Model_hub_criteria_editor_requires_explicit_quality_reliability_and_both_latency_margins);
        using var app = new UiTestApplication(testName,
            enableCandidateUtilityPolicyFixture: true);
        try
        {
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUtilityConfigureFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            var expectedFields = new[]
            {
                "ModelHub_CandidateUtilityQualityFloor_TextBox",
                "ModelHub_CandidateUtilityReliabilityFloor_TextBox",
                "ModelHub_CandidateUtilityMinimumRuns_TextBox",
                "ModelHub_CandidateUtilityTtftMargin_TextBox",
                "ModelHub_CandidateUtilityCompletionMargin_TextBox"
            };
            foreach (string automationId in expectedFields)
            {
                AutomationElement field = UiWait.ById(app.MainWindow, automationId, ControlType.Edit);
                Assert.Equal(string.Empty, field.AsTextBox().Text);
            }

            AutomationElement? save = null;
            UiWait.For(() =>
            {
                save = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && string.Equals(element.Properties.Name.ValueOrDefault, "Save criteria", StringComparison.Ordinal));
                return save != null;
            }, "save button in comparison criteria editor");
            if (save!.Patterns.Invoke.PatternOrDefault != null) save.Patterns.Invoke.Pattern.Invoke();
            else save.Click(moveMouse: false);

            AutomationElement error = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateUtilityConfigureError_Text", ControlType.Text);
            UiWait.For(() => error.Name.Contains("Enter every required value", StringComparison.Ordinal),
                "required comparison criteria validation");
            Assert.NotNull(app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateUtilityConfigure_Dialog"));

            UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityQualityFloor_TextBox", ControlType.Edit)
                .AsTextBox().Text = "90";
            UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityReliabilityFloor_TextBox", ControlType.Edit)
                .AsTextBox().Text = "95";
            UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityMinimumRuns_TextBox", ControlType.Edit)
                .AsTextBox().Text = "3";
            UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityTtftMargin_TextBox", ControlType.Edit)
                .AsTextBox().Text = "100.01";
            UiWait.ById(app.MainWindow, "ModelHub_CandidateUtilityCompletionMargin_TextBox", ControlType.Edit)
                .AsTextBox().Text = "5";
            if (save.Patterns.Invoke.PatternOrDefault != null) save.Patterns.Invoke.Pattern.Invoke();
            else save.Click(moveMouse: false);
            UiWait.For(() => error.Name.Contains("between 0.01 and 100.00", StringComparison.Ordinal),
                "out-of-range speed margin rejected without saving");
            Assert.NotNull(app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "ModelHub_CandidateUtilityConfigure_Dialog"));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_enables_manual_comparison_only_with_ready_policy_and_eligible_evidence()
    {
        string testName = nameof(Model_hub_enables_manual_comparison_only_with_ready_policy_and_eligible_evidence);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUtilityPolicyReady_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement compare = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateCompare_" + candidateKey, ControlType.Button);
            Assert.True(compare.IsEnabled);
            Assert.Contains("human review", UiWait.ById(
                app.MainWindow, "ModelHub_CandidateUtilityPolicyStatus_Text", ControlType.Text).Name,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no comparison or model action occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_serializes_candidate_comparison_against_candidate_file_mutations()
    {
        string testName = nameof(Model_hub_serializes_candidate_comparison_against_candidate_file_mutations);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUtilityPolicyReady_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUtilityInFlight_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement compare = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateCompare_" + candidateKey, ControlType.Button);
            Assert.False(compare.IsEnabled);
            Assert.Contains("Comparing", compare.Name, StringComparison.Ordinal);
            AutomationElement evaluate = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button);
            Assert.False(evaluate.IsEnabled);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateDownload_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
            Assert.Contains("no Core request or model action occurred", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_candidate_evaluation_can_start_and_cancel_without_running_a_model()
    {
        string testName = nameof(Model_hub_candidate_evaluation_can_start_and_cancel_without_running_a_model);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement evaluate = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button);
            Assert.Equal("Evaluate locally", evaluate.Name);
            evaluate.Patterns.Invoke.Pattern.Invoke();

            AutomationElement cancel = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateCancel_" + candidateKey, ControlType.Button);
            AutomationElement runningStatus = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text);
            Assert.Contains("Evaluation running", runningStatus.Name, StringComparison.Ordinal);
            cancel.Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() =>
            {
                AutomationElement? status = app.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("ModelHub_CandidateStatus_" + candidateKey).And(cf.ByControlType(ControlType.Text)));
                return status != null && status.Name.Contains("Evaluation cancelled", StringComparison.Ordinal);
            }, "cancelled Candidate evaluation status");
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_blocks_candidate_evaluation_when_revision_license_evidence_is_stale()
    {
        string testName = nameof(Model_hub_blocks_candidate_evaluation_when_revision_license_evidence_is_stale);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true,
            enableCandidateLicensePreflightRejectionFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.ById(app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement pageStatus = UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text);
            UiWait.For(() => pageStatus.Name.Contains(
                "No evaluation started and the installed file was left unchanged", StringComparison.Ordinal),
                "license-metadata-change rejection with explicit no-start wording");
            Assert.Contains("declared licence no longer matches the saved evidence for this revision", pageStatus.Name,
                StringComparison.Ordinal);
            Assert.Contains("review the current pinned model-card terms", pageStatus.Name, StringComparison.Ordinal);

            AutomationElement candidateStatus = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text);
            Assert.Contains("Evaluation unavailable", candidateStatus.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Evaluation running", candidateStatus.Name, StringComparison.Ordinal);
            AutomationElement retry = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button);
            Assert.Equal("Evaluate again", retry.Name);
            Assert.True(retry.IsEnabled);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateCancel_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_explains_recovered_interrupted_candidate_evaluation_without_claiming_success()
    {
        string testName = nameof(Model_hub_explains_recovered_interrupted_candidate_evaluation_without_claiming_success);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateInterruptedFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text);
            Assert.Contains("Evaluation interrupted by Core restart", status.Name, StringComparison.Ordinal);
            Assert.Contains("outcome is unknown", status.Name, StringComparison.Ordinal);
            Assert.Contains("was not resumed", status.Name, StringComparison.Ordinal);
            Assert.Contains("last recorded phase: performance", status.Name, StringComparison.Ordinal);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateCancel_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_reconciles_an_uncertain_candidate_start_from_durable_history()
    {
        string testName = nameof(Model_hub_reconciles_an_uncertain_candidate_start_from_durable_history);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateHistoryReconciliationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text);
            Assert.Contains("Evaluation interrupted by Core restart", status.Name, StringComparison.Ordinal);
            Assert.Contains("outcome is unknown", status.Name, StringComparison.Ordinal);
            Assert.Contains("was not resumed", status.Name, StringComparison.Ordinal);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateCancel_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
            Assert.Contains("uncertain start reconciled with durable Core history", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("no model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_ignores_a_late_not_found_for_an_older_candidate_job()
    {
        string testName = nameof(Model_hub_ignores_a_late_not_found_for_an_older_candidate_job);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateStaleStatusFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text);
            Assert.Contains("Evaluation running", status.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("may be stale", status.Name, StringComparison.Ordinal);
            _ = UiWait.ById(app.MainWindow, "ModelHub_CandidateCancel_" + candidateKey, ControlType.Button);
            Assert.Contains("stale 404/error ignored", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
            Assert.Contains("No model was run", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_hub_stages_a_pinned_candidate_only_after_explicit_license_review()
    {
        string testName = nameof(Model_hub_stages_a_pinned_candidate_only_after_explicit_license_review);
        using var app = new UiTestApplication(testName, enableCandidateStagingFixture: true);
        try
        {
            const string candidateKey = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateStagingFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement download = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateDownload_" + candidateKey, ControlType.Button);
            Assert.Equal("Download pinned Candidate", download.Name);
            download.Patterns.Invoke.Pattern.Invoke();

            const string fixtureRevision = "dddddddddddddddddddddddddddddddddddddddd";
            AutomationElement? revisionText = null;
            UiWait.For(() =>
            {
                revisionText = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && element.Properties.Name.ValueOrDefault.Contains("Selected immutable repository revision: " + fixtureRevision, StringComparison.Ordinal));
                return revisionText != null;
            }, "pinned Candidate revision in the licence review dialog");
            Assert.Contains("Selected immutable repository revision: " + fixtureRevision,
                revisionText!.Properties.Name.ValueOrDefault, StringComparison.Ordinal);
            AutomationElement? observedText = null;
            UiWait.For(() =>
            {
                observedText = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && element.Properties.Name.ValueOrDefault.Contains("Publisher licence metadata observed (UTC):", StringComparison.Ordinal));
                return observedText != null;
            }, "Candidate metadata observation time in the licence review dialog");

            AutomationElement checkbox = UiWait.ById(
                app.MainWindow, "ModelLicenseConsent_Confirm_CheckBox", ControlType.CheckBox);
            Assert.False(checkbox.AsCheckBox().IsChecked);
            ClickButtonNamed(app, "Download selected file");
            AutomationElement validation = UiWait.ById(
                app.MainWindow, "ModelLicenseConsent_Validation_Text", ControlType.Text);
            Assert.Contains("select the confirmation", validation.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No network request or model execution occurred",
                UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.OrdinalIgnoreCase);

            checkbox.Patterns.Toggle.Pattern.Toggle();
            ClickButtonNamed(app, "Download selected file");
            UiWait.ById(app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button);
            Assert.Contains("synthetic managed Candidate after consent",
                UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.Ordinal);
            Assert.Contains("No network transfer or model execution occurred",
                UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name,
                StringComparison.Ordinal);

            AutomationElement verifyRepair = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateDownload_" + candidateKey, ControlType.Button);
            Assert.Equal("Verify / repair pinned file", verifyRepair.Name);

            UiWait.ById(app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            Assert.Contains("Evaluation running", UiWait.ById(
                app.MainWindow, "ModelHub_CandidateStatus_" + candidateKey, ControlType.Text).Name,
                StringComparison.Ordinal);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateDownload_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Security")]
    public void Model_hub_blocks_candidate_download_when_pinned_url_does_not_match_full_source_path()
    {
        string testName = nameof(Model_hub_blocks_candidate_download_when_pinned_url_does_not_match_full_source_path);
        using var app = new UiTestApplication(testName, enableCandidateStagingFixture: true);
        try
        {
            const string candidateKey = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateTamperedSourceFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement download = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateDownload_" + candidateKey, ControlType.Button);
            Assert.False(download.IsEnabled);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateEvaluate_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Concurrency")]
    public void Model_hub_blocks_candidate_file_repair_when_evaluation_start_is_unconfirmed()
    {
        string testName = nameof(Model_hub_blocks_candidate_file_repair_when_evaluation_start_is_unconfirmed);
        using var app = new UiTestApplication(testName, enableCandidateEvaluationFixture: true);
        try
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateUncertainEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            Assert.Contains("a job may still be active", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
            Assert.Null(app.MainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("ModelHub_CandidateDownload_" + candidateKey).And(cf.ByControlType(ControlType.Button))));
            Assert.Equal("Retry evaluation request", UiWait.ById(
                app.MainWindow, "ModelHub_CandidateEvaluate_" + candidateKey, ControlType.Button).Name);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Concurrency")]
    public void Model_hub_blocks_candidate_download_when_evaluation_is_unconfirmed_and_file_is_missing()
    {
        string testName = nameof(Model_hub_blocks_candidate_download_when_evaluation_is_unconfirmed_and_file_is_missing);
        using var app = new UiTestApplication(testName, enableCandidateStagingFixture: true);
        try
        {
            const string candidateKey = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            UiWait.ById(app.MainWindow, "ModelHub_TestCandidateStagingFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "ModelHub_TestUnmanagedCandidateUncertainEvaluationFixture_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement download = UiWait.ById(
                app.MainWindow, "ModelHub_CandidateDownload_" + candidateKey, ControlType.Button);
            Assert.Equal("Download pinned Candidate", download.Name);
            Assert.False(download.IsEnabled);
            Assert.Contains("a job may still be active", UiWait.ById(
                app.MainWindow, "ModelHub_Status_Text", ControlType.Text).Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    private static void ClickButtonNamed(UiTestApplication app, string name)
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.ControlType.ValueOrDefault == ControlType.Button
                && string.Equals(element.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal));
            return button != null;
        }, "dialog button " + name);
        if (button!.Patterns.Invoke.PatternOrDefault != null) button.Patterns.Invoke.Pattern.Invoke();
        else button.Click(moveMouse: false);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start in the FlaUI test.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git timed out in the FlaUI test.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git " + string.Join(' ', arguments) + " failed: " + error);
        return output;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_license_review_requires_affirmative_action_before_download_callback()
    {
        string testName = nameof(Model_license_review_requires_affirmative_action_before_download_callback);
        using var app = new UiTestApplication(testName, enableModelLicenseConsentFixture: true);
        try
        {
            var fixtureButton = UiWait.ById(app.MainWindow, "ModelHub_TestLicenseConsent_Button", ControlType.Button);
            var marker = Path.Combine(app.DataRoot, "model-license-download-callback.txt");
            string fixtureRevision = new string('d', 40);

            void OpenFixture() => fixtureButton.Patterns.Invoke.Pattern.Invoke();
            void ClickButtonNamed(string name)
            {
                AutomationElement? button = null;
                UiWait.For(() =>
                {
                    button = app.FindRawDescendant(app.MainWindow, element =>
                        element.Properties.ControlType.ValueOrDefault == ControlType.Button
                        && string.Equals(element.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal));
                    return button != null;
                }, "dialog button " + name);
                if (button!.Patterns.Invoke.PatternOrDefault != null) button.Patterns.Invoke.Pattern.Invoke();
                else button.Click(moveMouse: false);
            }

            OpenFixture();
            AutomationElement? revisionText = null;
            UiWait.For(() =>
            {
                revisionText = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && element.Properties.Name.ValueOrDefault.Contains(fixtureRevision, StringComparison.Ordinal));
                return revisionText != null;
            }, "immutable repository revision in licence review");
            Assert.Contains("Selected immutable repository revision: " + fixtureRevision,
                revisionText!.Properties.Name.ValueOrDefault, StringComparison.Ordinal);
            var checkbox = UiWait.ById(app.MainWindow, "ModelLicenseConsent_Confirm_CheckBox", ControlType.CheckBox);
            Assert.False(checkbox.AsCheckBox().IsChecked);

            ClickButtonNamed("Download selected file");
            AutomationElement? validation = null;
            UiWait.For(() =>
            {
                validation = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.AutomationId.ValueOrDefault == "ModelLicenseConsent_Validation_Text");
                return validation != null && validation.Properties.Name.ValueOrDefault.Contains("select the confirmation", StringComparison.OrdinalIgnoreCase);
            }, "unchecked consent validation");
            Assert.False(File.Exists(marker));
            ClickButtonNamed("Cancel");
            UiWait.For(() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "ModelLicenseConsent_Dialog") == null,
                "closed licence review dialog");
            Assert.False(File.Exists(marker));

            OpenFixture();
            var confirmed = UiWait.ById(app.MainWindow, "ModelLicenseConsent_Confirm_CheckBox", ControlType.CheckBox);
            confirmed.Patterns.Toggle.Pattern.Toggle();
            ClickButtonNamed("Download selected file");
            UiWait.For(() => File.Exists(marker), "download callback after explicit consent");
            string[] callback = File.ReadAllLines(marker);
            Assert.Equal("download callback reached", callback[0]);
            Assert.Equal(fixtureRevision, callback[1]);
            Assert.Contains("/resolve/" + fixtureRevision + "/fixture-model.gguf", callback[2], StringComparison.Ordinal);
            Assert.Equal("declared", callback[3]);
            Assert.Equal("custom", callback[4]);
            Assert.Equal(nameof(DateTimeKind.Utc), callback[5]);
            Assert.Equal("True", callback[6]);
            Assert.Equal("True", callback[7]);
            Assert.Equal(nameof(DateTimeKind.Utc), callback[8]);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Allow_listed_license_carries_metadata_but_does_not_invent_a_review_acknowledgement()
    {
        string testName = nameof(Allow_listed_license_carries_metadata_but_does_not_invent_a_review_acknowledgement);
        using var app = new UiTestApplication(testName, enableModelLicenseConsentFixture: true);
        try
        {
            var fixtureButton = UiWait.ById(app.MainWindow, "ModelHub_TestAllowListedLicenseEvidence_Button", ControlType.Button);
            var marker = Path.Combine(app.DataRoot, "model-license-download-callback.txt");
            fixtureButton.Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => File.Exists(marker), "allow-listed download evidence callback without a review dialog");
            string[] callback = File.ReadAllLines(marker);
            Assert.Equal(new[] { "declared", "MIT", "False", "False" }, callback);
            Assert.Null(app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "ModelLicenseConsent_Dialog"));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void Model_download_is_blocked_when_no_immutable_revision_is_available()
    {
        string testName = nameof(Model_download_is_blocked_when_no_immutable_revision_is_available);
        using var app = new UiTestApplication(testName, enableModelLicenseConsentFixture: true);
        try
        {
            var fixtureButton = UiWait.ById(app.MainWindow, "ModelHub_TestMissingRevision_Button", ControlType.Button);
            string marker = Path.Combine(app.DataRoot, "model-license-download-callback.txt");
            fixtureButton.Patterns.Invoke.Pattern.Invoke();

            AutomationElement? dialogTitle = null;
            UiWait.For(() =>
            {
                dialogTitle = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.Name.ValueOrDefault.Contains("Cannot pin this model revision", StringComparison.Ordinal));
                return dialogTitle != null;
            }, "missing immutable revision warning");
            Assert.False(File.Exists(marker));

            AutomationElement? close = null;
            UiWait.For(() =>
            {
                close = app.FindRawDescendant(app.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && string.Equals(element.Properties.Name.ValueOrDefault, "Close", StringComparison.Ordinal));
                return close != null;
            }, "close button for missing-revision warning");
            if (close!.Patterns.Invoke.PatternOrDefault != null) close.Patterns.Invoke.Pattern.Invoke();
            else close.Click(moveMouse: false);
            UiWait.For(() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.Name.ValueOrDefault.Contains("Cannot pin this model revision", StringComparison.Ordinal)) == null,
                "closed missing-revision warning");
            Assert.False(File.Exists(marker));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void First_model_guidance_is_neutral_and_storage_quota_and_volume_space_are_distinct()
    {
        string testName = nameof(First_model_guidance_is_neutral_and_storage_quota_and_volume_space_are_distinct);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            var headline = UiWait.ById(app.MainWindow, "FirstModelHeadlineText", ControlType.Text);
            UiWait.For(() => headline.Name.Contains("compatible chat model in Model Hub", StringComparison.OrdinalIgnoreCase),
                "neutral model selection guidance");
            Assert.DoesNotContain("Qwen", headline.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("starter", headline.Name, StringComparison.OrdinalIgnoreCase);

            var storage = UiWait.ById(app.MainWindow, "FirstModelStorageText", ControlType.Text);
            UiWait.For(() => storage.Name.Contains("456 GB", StringComparison.Ordinal),
                "first-model storage evidence");
            Assert.Contains("PaRuX AI quota headroom: 123 GB of 700 GB", storage.Name, StringComparison.Ordinal);
            Assert.Contains("Free space on the model destination volume: 456 GB", storage.Name, StringComparison.Ordinal);
            Assert.Contains("not reservations", storage.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void First_model_storage_warning_tracks_the_selected_general_chat_candidate()
    {
        string testName = nameof(First_model_storage_warning_tracks_the_selected_general_chat_candidate);
        using var app = new UiTestApplication(testName, enableFirstModelSelectionStorageFixture: true);
        try
        {
            var headline = UiWait.ById(app.MainWindow, "FirstModelHeadlineText", ControlType.Text);
            UiWait.For(() => headline.Name.Contains("compatible chat model in Model Hub", StringComparison.OrdinalIgnoreCase),
                "neutral model guidance before explicit selection");
            Assert.DoesNotContain("Storage fixture model", headline.Name, StringComparison.Ordinal);
            var storage = UiWait.ById(app.MainWindow, "FirstModelStorageText", ControlType.Text);
            UiWait.For(() => storage.Name.Contains("6 GB", StringComparison.Ordinal),
                "fixture destination free space");
            Assert.DoesNotContain("below the approximate selected download size", storage.Name, StringComparison.Ordinal);

            UiWait.ById(app.MainWindow, "FirstModelOtherButton", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            UiWait.ById(app.MainWindow, "FirstModelUseCandidateButton_larger-general-fixture", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            var detail = UiWait.ById(app.MainWindow, "FirstModelDetailText", ControlType.Text);
            UiWait.For(() => detail.Name.Contains("About 7 GB download", StringComparison.Ordinal),
                "selected general-chat candidate size");
            UiWait.For(() => storage.Name.Contains("below the approximate selected download size", StringComparison.Ordinal),
                "storage warning for the selected general-chat candidate");
            Assert.Contains("Free space on the model destination volume: 6 GB", storage.Name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void First_model_options_are_grouped_by_task_and_keep_specialists_out_of_global_chat_choice()
    {
        string testName = nameof(First_model_options_are_grouped_by_task_and_keep_specialists_out_of_global_chat_choice);
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true);
        try
        {
            UiWait.ById(app.MainWindow, "FirstModelOtherButton", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.Name.ValueOrDefault.Contains("Model candidates by task", StringComparison.Ordinal)) != null,
                "task-specific model candidate dialog");
            bool DialogContains(string value, StringComparison comparison = StringComparison.Ordinal) =>
                app.FindRawDescendant(app.MainWindow, element =>
                    (element.Properties.Name.ValueOrDefault ?? string.Empty).Contains(value, comparison)) != null;

            Assert.True(DialogContains("General chat"));
            Assert.True(DialogContains("Coding"));
            Assert.True(DialogContains("uncensored preference", StringComparison.OrdinalIgnoreCase));
            Assert.True(DialogContains("Image generation"));
            Assert.True(DialogContains("Image editing"));
            Assert.True(DialogContains("Video generation"));
            Assert.True(DialogContains("quality and speed have not been measured", StringComparison.OrdinalIgnoreCase));

            Assert.Null(app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == "FirstModelUseCandidateButton_coding-fixture"));
            var codingSearch = UiWait.ById(app.MainWindow, "FirstModelFindCandidateButton_coding-fixture", ControlType.Button);
            Assert.Contains("Find coding candidate in Model Hub", codingSearch.Name, StringComparison.Ordinal);
            codingSearch.Patterns.Invoke.Pattern.Invoke();

            var modelSearch = UiWait.ById(app.MainWindow, "ModelHub_Search_TextBox", ControlType.Edit);
            UiWait.For(() => modelSearch.AsTextBox().Text == "fixture/coding",
                "Model Hub search for the coding candidate");
            var modelStatus = UiWait.ById(app.MainWindow, "ModelHub_Status_Text", ControlType.Text);
            UiWait.For(() => modelStatus.Name.Contains("no network or download", StringComparison.OrdinalIgnoreCase),
                "safe UI fixture response without downloading");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "Accessibility")]
    public void First_model_storage_advice_displays_zero_separately_from_unavailable()
    {
        string testName = nameof(First_model_storage_advice_displays_zero_separately_from_unavailable);
        using var app = new UiTestApplication(testName, enableFirstModelZeroStorageFixture: true);
        try
        {
            var storage = UiWait.ById(app.MainWindow, "FirstModelStorageText", ControlType.Text);
            UiWait.For(() => storage.Name.Contains("Free space on the model destination volume: 0 GB", StringComparison.Ordinal),
                "zero destination free space");
            Assert.Contains("PaRuX AI quota headroom: 0 GB of 700 GB", storage.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("unknown size", storage.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unavailable", storage.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Tools")]
    public void Advanced_tools_navigation_routes_to_the_tools_page_when_invoked() => Run(
        nameof(Advanced_tools_navigation_routes_to_the_tools_page_when_invoked), app =>
    {
        var main = new MainWindowPage(app.MainWindow);
        main.EnableAdvanced();
        main.OpenTools();
        AutomationElement toolsNavigation = UiWait.ById(app.MainWindow, "Navigation_Tools_Item", ControlType.ListItem);
        Assert.True(toolsNavigation.Patterns.SelectionItem.Pattern.IsSelected);
        Assert.False(toolsNavigation.Properties.IsOffscreen.ValueOrDefault);
        AutomationElement? toolsHeading = app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.ControlType.ValueOrDefault == ControlType.Text
            && element.Properties.Name.ValueOrDefault == "Tools & Automations");
        Assert.NotNull(toolsHeading);
        string? snapshot = app.CaptureSnapshot("advanced-workspace-navigation");
        Assert.True(snapshot == null || File.Exists(snapshot));
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Settings_controls_have_stable_automation_ids() => Run(nameof(Settings_controls_have_stable_automation_ids), app =>
    {
        new MainWindowPage(app.MainWindow).OpenSettings();
        UiWait.ById(app.MainWindow, "Settings_Startup_Toggle", ControlType.Button);
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Integrations_page_shows_gateway_state_and_every_supported_client() => Run(nameof(Integrations_page_shows_gateway_state_and_every_supported_client), app =>
    {
        var main = new MainWindowPage(app.MainWindow);
        main.OpenSettings();
        main.OpenSettingsCategory("Settings_Category_Integrations_Button");
        AutomationElement state = UiWait.ById(app.MainWindow, "Settings_Gateway_State_Text", ControlType.Text);
        // The page must resolve to a real state (running, port in use, off, or Core unreachable), never stay on the placeholder.
        UiWait.For(() => !state.Name.StartsWith("Checking", StringComparison.Ordinal), "gateway state resolved", TimeSpan.FromSeconds(60));
        // Every supported client gets a card with a truthful status, installed or not; detection never writes anything.
        foreach (string client in new[] { "claudecode", "codex", "opencode" })
            UiWait.ById(app.MainWindow, "Settings_Integration_" + client + "_Status_Text", ControlType.Text);
        UiWait.ById(app.MainWindow, "Settings_Integrations_Refresh_Button", ControlType.Button);
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Chat")]
    public void Chat_header_offers_Return_to_AI_while_paused_by_Gaming_and_clicking_it_lifts_the_pause() =>
        Run(nameof(Chat_header_offers_Return_to_AI_while_paused_by_Gaming_and_clicking_it_lifts_the_pause), app =>
    {
        // No Core and no model: Gaming Mode is just the marker in this test's isolated state dir.
        string marker = Path.Combine(app.LocalAiStateRoot, "game-active.json");
        Directory.CreateDirectory(app.LocalAiStateRoot);
        File.WriteAllText(marker, "{\"started\":\"" + DateTime.UtcNow.ToString("o") + "\"}");

        var button = UiWait.ById(app.MainWindow, "Chat_Header_ReturnToAi_Button", ControlType.Button);
        Assert.Equal("Return to AI", button.Name);
        app.MainWindow.SetForeground();
        if (button.Patterns.Invoke.PatternOrDefault != null) button.Patterns.Invoke.Pattern.Invoke();
        else button.Click(moveMouse: false);

        // The click must route through GamingManager.ExitAsync (-> the Core "GameStop" runtime
        // action (user-initiated exit). The stub Core of the UI harness may not confirm it, so
        // the proof is the app's own action log, not the marker.
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        bool actionIssued = false;
        while (!actionIssued && DateTime.UtcNow < deadline)
        {
            try
            {
                using var stream = new FileStream(app.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                actionIssued = reader.ReadToEnd().Contains("Return to AI requested by the user", StringComparison.Ordinal);
            }
            catch (IOException) { }
            if (!actionIssued) Thread.Sleep(250);
        }
        Assert.True(actionIssued, "Return to AI must call GamingManager.ExitAsync");

        // Once Gaming Mode really ends (marker gone, as Core's GameStop does) the affordance
        // disappears without any restart: the header notices the marker change by itself.
        File.Delete(marker);
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var found = app.MainWindow.FindFirstDescendant(c => c.ByAutomationId("Chat_Header_ReturnToAi_Button"));
            if (found == null || found.IsOffscreen) return;
            Thread.Sleep(200);
        }
        Assert.Fail("Return to AI stayed visible after Gaming Mode ended");
    });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Settings")]
    public void Gaming_settings_status_control_is_reachable() => Run(nameof(Gaming_settings_status_control_is_reachable), app =>
    {
        var main = new MainWindowPage(app.MainWindow);
        main.OpenSettings();
        main.OpenSettingsCategory("Settings_Category_GamingAISharing_Button");
        UiWait.ById(app.MainWindow, "Settings_Gaming_Refresh_Button", ControlType.Button);
    });
}
