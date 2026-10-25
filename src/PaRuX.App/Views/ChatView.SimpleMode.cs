using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private bool _simpleModeFolderEventsAttached;
        private bool _isSimpleMode;

        public void ApplyInterfaceMode(bool advanced)
        {
            _isSimpleMode = !advanced;
            CodeFolderButton.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            ProfileSelectorPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelSelector.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            ModelRouteExplanationButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            SimpleModeAnsweringModelPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            SimpleModeRuntimeHealthPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
            SkillSelector.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            if (!_simpleModeFolderEventsAttached)
            {
                _simpleModeFolderEventsAttached = true;
                ProjectWorkspaceSession.Current.Changed += (_, _) =>
                    _dispatcher.TryEnqueue(RefreshCodeFolderLabel);
            }
            RefreshCodeFolderLabel();
            RefreshSimpleModeAnsweringModel();
            RefreshSimpleModeRuntimeHealth();
        }

        public void RefreshSimpleModeAnsweringModel()
        {
            ChatModelRouteInfo? route = _lastChatModelRoute;
            if (route == null)
            {
                ChatMessage? lastAssistant = ChatSession.Current.Conversation?.Messages
                    .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.ModelId));
                if (lastAssistant != null)
                {
                    route = new ChatModelRouteInfo
                    {
                        SelectedModel = lastAssistant.ModelId!,
                        Reason = lastAssistant.FallbackReason ?? string.Empty
                    };
                }
            }

            AnsweringModelDisplayInfo info = AnsweringModelLabelFormatter.Format(route);
            if (SimpleModeAnsweringModelLabel != null)
            {
                SimpleModeAnsweringModelLabel.Text = info.BadgeText;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeAnsweringModelLabel, info.AccessibleName);
            }
            if (SimpleModeAnsweringModelBadge != null)
            {
                ToolTipService.SetToolTip(SimpleModeAnsweringModelBadge, info.TooltipText);
            }
            if (SimpleModeRouteReasonButton != null)
            {
                ToolTipService.SetToolTip(SimpleModeRouteReasonButton, string.IsNullOrWhiteSpace(info.OneLineReason) ? "Why this model?" : info.OneLineReason);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRouteReasonButton, "Why this model? " + info.OneLineReason);
            }
        }

        private void RefreshCodeFolderLabel()
        {
            string? root = string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId)
                ? SettingsStore.Current.Data.AgentCodingWorkspaceRoot
                : ProjectWorkspaceSession.Current.RepositoryRoot;
            bool hasFolder = !string.IsNullOrWhiteSpace(root);
            string name = hasFolder ? Path.GetFileName(root!.TrimEnd(Path.DirectorySeparatorChar)) : string.Empty;
            CodeFolderLabel.Text = name.Length == 0 ? "Choose code folder" : name;
            ToolTipService.SetToolTip(CodeFolderButton,
                name.Length == 0 ? "Choose the repository PaRuX will work on" : root);
            // Icon-only button: without a name screen readers announced nothing at all.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CodeFolderButton,
                name.Length == 0 ? "Choose code folder" : "Code folder: " + name + ". Choose another");
            CodeFolderClearButton.Visibility = (CodeFolderButton.Visibility == Visibility.Visible && hasFolder)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void CodeFolderClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId))
            {
                ProjectWorkspaceSession.Current.SetRepositoryRoot(null);
            }
            SettingsStore.Current.Data.AgentCodingWorkspaceRoot = string.Empty;
            SettingsStore.Current.Save();
            RefreshCodeFolderLabel();
            ShowTransientStatus("Code folder deselected.");
        }

        private async void CodeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            CodeFolderButton.IsEnabled = false;
            try
            {
                string? root = await CodeHandoffService.ChooseRepositoryAsync(ProjectWorkspaceSession.Current.ProjectId);
                if (root != null)
                {
                    CodingTaskSandboxWorkspacePolicy.TryBindConversationSandboxTasks(
                        ChatSession.Current.Conversation, root);
                    RefreshCodeFolderLabel();
                    ShowTransientStatus("Code folder selected. Tell PaRuX what to change in this chat.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Code folder selection failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapFolderSelection(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
            }
            finally { CodeFolderButton.IsEnabled = true; }
        }

        public async void RefreshSimpleModeRuntimeHealth()
        {
            if (SimpleModeRuntimeHealthPanel == null) return;
            try
            {
                var components = await RuntimeManagerClient.Current.GetComponentsAsync(System.Threading.CancellationToken.None);
                _dispatcher.TryEnqueue(() => ApplySimpleModeRuntimeHealth(components));
            }
            catch (Exception ex)
            {
                Log.Write("ChatView RefreshSimpleModeRuntimeHealth failed: " + ex.Message);
                _dispatcher.TryEnqueue(() =>
                {
                    var fallback = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(null);
                    ApplySimpleModeRuntimeHealthInfo(fallback);
                });
            }
        }

        public void ApplySimpleModeRuntimeHealth(System.Collections.Generic.IReadOnlyList<PaRuX.Contracts.ComponentStatus>? components)
        {
            var info = ComponentLifecyclePresenter.FormatSimpleModeRuntimeHealth(components);
            ApplySimpleModeRuntimeHealthInfo(info);
        }

        private void ApplySimpleModeRuntimeHealthInfo(SimpleModeRuntimeHealthInfo info)
        {
            if (SimpleModeRuntimeHealthLabel != null)
            {
                SimpleModeRuntimeHealthLabel.Text = info.BadgeText;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRuntimeHealthLabel, info.AccessibleName);
            }
            if (SimpleModeRuntimeHealthButton != null)
            {
                ToolTipService.SetToolTip(SimpleModeRuntimeHealthButton, info.TooltipText);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SimpleModeRuntimeHealthButton, info.AccessibleName);
            }
            if (SimpleModeRuntimeHealthIcon != null)
            {
                SimpleModeRuntimeHealthIcon.Glyph = info.IsHealthy ? "\uE73E" : (info.IsInstalled ? "\uE783" : "\uE711");
            }
        }

        private void SimpleModeRuntimeHealthButton_Click(object sender, RoutedEventArgs e)
        {
            App.MainWin?.ShowSettings();
        }
    }
}
