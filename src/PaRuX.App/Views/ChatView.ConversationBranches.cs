using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Models;
using PaRuX.Services;
using Windows.System;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private StackPanel? _conversationBranchNavigationPanel;
        private Button? _conversationPreviousButton;
        private Button? _conversationNextButton;
        private Button? _conversationVersionsButton;
        private TextBlock? _conversationBranchCounter;
        private bool _conversationBranchEventsAttached;

        /// <summary>
        /// Adds conversation-version navigation beside the existing model/skill routing controls.
        /// Previous/next operate on alternatives at the nearest retained fork, while Versions opens
        /// the full retained history. The controls are injected from this partial so the large
        /// ChatView XAML/code-behind does not need a second branch-history implementation path.
        /// </summary>
        private void InitializeConversationBranchHistory()
        {
            if (SkillSelector?.Parent is not Panel routingRow) return;

            // WinUI can re-apply a control template after theme/window changes. Reuse the same
            // navigation group rather than silently losing it or adding duplicates.
            if (_conversationBranchNavigationPanel != null)
            {
                if (!ReferenceEquals(_conversationBranchNavigationPanel.Parent, routingRow))
                {
                    if (_conversationBranchNavigationPanel.Parent is Panel oldParent)
                        oldParent.Children.Remove(_conversationBranchNavigationPanel);
                    routingRow.Children.Add(_conversationBranchNavigationPanel);
                }
                QueueConversationBranchNavigationRefresh();
                return;
            }

            var previous = new Button
            {
                Content = "‹",
                MinWidth = 32,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(previous, "Previous conversation version");
            AutomationProperties.SetHelpText(previous, "Switch to the previous alternative at this fork");
            AutomationProperties.SetAcceleratorKey(previous, "Ctrl+Alt+Left");
            ToolTipService.SetToolTip(previous, "Previous alternative at this fork (Ctrl+Alt+Left)");
            previous.Click += async (sender, args) => await NavigateConversationBranchAsync(-1);
            previous.KeyboardAccelerators.Add(new KeyboardAccelerator
            {
                Key = VirtualKey.Left,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu
            });

            var counter = new TextBlock
            {
                Text = "1/1",
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Opacity = 0.78
            };
            AutomationProperties.SetName(counter, "Conversation version 1 of 1");
            AutomationProperties.SetLiveSetting(counter, AutomationLiveSetting.Polite);

            var next = new Button
            {
                Content = "›",
                MinWidth = 32,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(next, "Next conversation version");
            AutomationProperties.SetHelpText(next, "Switch to the next alternative at this fork");
            AutomationProperties.SetAcceleratorKey(next, "Ctrl+Alt+Right");
            ToolTipService.SetToolTip(next, "Next alternative at this fork (Ctrl+Alt+Right)");
            next.Click += async (sender, args) => await NavigateConversationBranchAsync(1);
            next.KeyboardAccelerators.Add(new KeyboardAccelerator
            {
                Key = VirtualKey.Right,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu
            });

            var versions = new Button
            {
                Content = "Versions",
                MinWidth = 88,
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(versions, "Conversation version history");
            AutomationProperties.SetHelpText(versions, "Open previous conversation paths retained by Regenerate or Edit and continue");
            ToolTipService.SetToolTip(versions,
                "Open previous conversation paths retained by Regenerate or Edit and continue");
            versions.Click += async (sender, args) => await ShowConversationBranchHistoryAsync();

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(previous);
            panel.Children.Add(counter);
            panel.Children.Add(next);
            panel.Children.Add(versions);

            routingRow.Children.Add(panel);
            _conversationBranchNavigationPanel = panel;
            _conversationPreviousButton = previous;
            _conversationNextButton = next;
            _conversationVersionsButton = versions;
            _conversationBranchCounter = counter;

            if (!_conversationBranchEventsAttached)
            {
                ChatSession.Current.ConversationChanged += QueueConversationBranchNavigationRefresh;
                ChatSession.Current.ConversationPersisted += QueueConversationBranchNavigationRefresh;
                _conversationBranchEventsAttached = true;
            }
            RefreshConversationBranchNavigation();
        }

        private void QueueConversationBranchNavigationRefresh()
        {
            try
            {
                if (DispatcherQueue.HasThreadAccess) RefreshConversationBranchNavigation();
                else _ = DispatcherQueue.TryEnqueue(RefreshConversationBranchNavigation);
            }
            catch (Exception ex)
            {
                Log.Write("Conversation branch navigation refresh failed: " + ex.Message);
            }
        }

        private void RefreshConversationBranchNavigation()
        {
            if (_conversationPreviousButton == null || _conversationNextButton == null
                || _conversationVersionsButton == null || _conversationBranchCounter == null)
                return;

            BranchNavigationState state = BuildBranchNavigationState(ChatSession.Current.Conversation);
            string counterText = (state.ActiveIndex + 1) + "/" + state.Paths.Count;
            _conversationBranchCounter.Text = counterText;
            AutomationProperties.SetName(_conversationBranchCounter,
                state.HasRetainedVersions
                    ? $"Conversation version {state.ActiveIndex + 1} of {state.Paths.Count}"
                    : "Conversation version 1 of 1");
            _conversationPreviousButton.IsEnabled = !ChatSession.Current.IsBusy && state.ActiveIndex > 0;
            _conversationNextButton.IsEnabled = !ChatSession.Current.IsBusy && state.ActiveIndex + 1 < state.Paths.Count;
            _conversationVersionsButton.IsEnabled = !ChatSession.Current.IsBusy && state.HasRetainedVersions;
            ToolTipService.SetToolTip(_conversationBranchCounter,
                state.HasRetainedVersions
                    ? "Alternative " + (state.ActiveIndex + 1) + " of " + state.Paths.Count + " at the nearest retained fork"
                    : "No retained alternatives at the current path");
        }

        private async Task NavigateConversationBranchAsync(int direction)
        {
            if (direction != -1 && direction != 1) return;
            if (ChatSession.Current.IsBusy)
            {
                ShowTransientStatus("Stop the current generation before switching conversation versions.");
                return;
            }

            Conversation conversation = ChatSession.Current.Conversation;
            BranchNavigationState state = BuildBranchNavigationState(conversation);
            int targetIndex = state.ActiveIndex + direction;
            if (targetIndex < 0 || targetIndex >= state.Paths.Count) return;
            BranchPathOption target = state.Paths[targetIndex];
            if (string.IsNullOrWhiteSpace(target.BranchId)) return;
            await RestoreConversationBranchAsync(conversation, target.BranchId);
        }

        private async Task ShowConversationBranchHistoryAsync()
        {
            if (ChatSession.Current.IsBusy)
            {
                ShowTransientStatus("Stop the current generation before switching conversation versions.");
                return;
            }

            Conversation conversation = ChatSession.Current.Conversation;
            List<ConversationBranch> branches = DistinctRetainedBranches(conversation)
                .Where(branch => !SameConversationPath(branch.Messages, conversation.Messages))
                .OrderByDescending(branch => branch.CreatedUtc)
                .ToList();

            if (branches.Count == 0)
            {
                ShowTransientStatus("No previous conversation versions have been retained yet.");
                return;
            }

            var selector = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 460
            };

            foreach (ConversationBranch branch in branches)
            {
                selector.Items.Add(new ComboBoxItem
                {
                    Tag = branch.Id,
                    Content = BranchLabel(branch)
                });
            }
            selector.SelectedIndex = 0;

            var content = new StackPanel { Spacing = 10, MinWidth = 480 };
            content.Children.Add(new TextBlock
            {
                Text = "PaRuX keeps up to 64 full paths that existed before a regeneration or edit. Restoring one version also preserves the path you are viewing now, so switching remains reversible. Previous/next above the chat move among alternatives at the nearest fork.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            });
            content.Children.Add(selector);

            var dialog = new ContentDialog
            {
                Title = "Conversation versions",
                Content = content,
                PrimaryButtonText = "Restore selected",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Conversation branch dialog failed: " + ex.Message);
                ShowTransientStatus("Couldn't open conversation versions.");
                return;
            }

            if (result != ContentDialogResult.Primary) return;
            if (selector.SelectedItem is not ComboBoxItem selected || selected.Tag is not string branchId)
                return;

            await RestoreConversationBranchAsync(conversation, branchId);
        }

        private async Task RestoreConversationBranchAsync(Conversation conversation, string branchId)
        {
            string conversationId = conversation.Id;
            if (!HistoryStore.RestoreBranch(conversation, branchId))
            {
                ShowTransientStatus("That conversation version could not be persisted, so the current path was left unchanged.");
                RefreshConversationBranchNavigation();
                return;
            }

            // Re-enter through ChatSession's public lifecycle so every view sharing the session
            // (main Chat and bubble) observes ConversationChanged. The restored document is already
            // durable before this reload; the temporary empty conversation is never persisted.
            ChatSession.Current.NewConversation();
            if (!ChatSession.Current.OpenConversation(conversationId))
            {
                ShowTransientStatus("The version was saved, but the conversation could not be reloaded.");
                return;
            }

            RefreshConversationBranchNavigation();
            ShowTransientStatus("Conversation version restored. The path you left was retained too.");
            await Task.CompletedTask;
        }

        private static BranchNavigationState BuildBranchNavigationState(Conversation conversation)
        {
            IReadOnlyList<ChatMessage> activeMessages = conversation.Messages ?? new List<ChatMessage>();
            List<ConversationBranch> retained = DistinctRetainedBranches(conversation);
            List<ConversationBranch> different = retained
                .Where(branch => !SameConversationPath(branch.Messages, activeMessages))
                .ToList();

            if (different.Count == 0)
            {
                return new BranchNavigationState(
                    new List<BranchPathOption> { new(null, conversation.UpdatedUtc, activeMessages) },
                    0,
                    false);
            }

            int nearestFork = different.Max(branch => CommonConversationPrefix(branch.Messages, activeMessages));
            List<ConversationBranch> alternatives = different
                .Where(branch => CommonConversationPrefix(branch.Messages, activeMessages) == nearestFork)
                .ToList();

            ConversationBranch? retainedActive = retained
                .Where(branch => SameConversationPath(branch.Messages, activeMessages))
                .OrderByDescending(branch => branch.CreatedUtc)
                .FirstOrDefault();

            var paths = alternatives
                .Select(branch => new BranchPathOption(branch.Id, branch.CreatedUtc, branch.Messages))
                .ToList();
            paths.Add(new BranchPathOption(
                null,
                retainedActive?.CreatedUtc ?? conversation.UpdatedUtc,
                activeMessages));
            paths = paths
                .OrderBy(path => path.CreatedUtc)
                .ThenBy(path => path.BranchId ?? "~active", StringComparer.OrdinalIgnoreCase)
                .ToList();

            int activeIndex = paths.FindIndex(path => path.BranchId == null);
            if (activeIndex < 0) activeIndex = paths.Count - 1;
            return new BranchNavigationState(paths, activeIndex, true);
        }

        private static List<ConversationBranch> DistinctRetainedBranches(Conversation conversation)
        {
            var result = new List<ConversationBranch>();
            foreach (ConversationBranch branch in conversation.Branches ?? new List<ConversationBranch>())
            {
                if (branch.Messages == null || branch.Messages.Count == 0) continue;
                if (result.Any(existing => SameConversationPath(existing.Messages, branch.Messages))) continue;
                result.Add(branch);
            }
            return result;
        }

        private static int CommonConversationPrefix(
            IReadOnlyList<ChatMessage>? left,
            IReadOnlyList<ChatMessage>? right)
        {
            if (left == null || right == null) return 0;
            int count = Math.Min(left.Count, right.Count);
            for (int i = 0; i < count; i++)
            {
                if (!SameConversationMessage(left[i], right[i])) return i;
            }
            return count;
        }

        private static bool SameConversationPath(
            IReadOnlyList<ChatMessage>? left,
            IReadOnlyList<ChatMessage>? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!SameConversationMessage(left[i], right[i])) return false;
            }
            return true;
        }

        private static bool SameConversationMessage(ChatMessage a, ChatMessage b)
        {
            if (!string.Equals(a.Role, b.Role, StringComparison.Ordinal)
                || !string.Equals(a.Content, b.Content, StringComparison.Ordinal)
                || !string.Equals(a.Reasoning, b.Reasoning, StringComparison.Ordinal))
                return false;

            IReadOnlyList<ChatAttachment> aa = a.Attachments ?? new List<ChatAttachment>();
            IReadOnlyList<ChatAttachment> bb = b.Attachments ?? new List<ChatAttachment>();
            if (aa.Count != bb.Count) return false;
            for (int j = 0; j < aa.Count; j++)
            {
                if (!string.Equals(aa[j].Id, bb[j].Id, StringComparison.Ordinal)
                    || !string.Equals(aa[j].FileName, bb[j].FileName, StringComparison.Ordinal)
                    || !string.Equals(aa[j].MediaType, bb[j].MediaType, StringComparison.Ordinal)
                    || aa[j].SizeBytes != bb[j].SizeBytes)
                    return false;
            }
            return true;
        }

        private static string BranchLabel(ConversationBranch branch)
        {
            string when = branch.CreatedUtc.ToLocalTime().ToString("g");
            string reason = string.Equals(branch.Reason, "regenerate-or-edit", StringComparison.OrdinalIgnoreCase)
                ? "regenerated / edited"
                : "alternate path";

            ChatMessage? firstDifferent = branch.Messages != null && branch.ForkIndex >= 0
                && branch.ForkIndex < branch.Messages.Count
                    ? branch.Messages[branch.ForkIndex]
                    : branch.Messages?.LastOrDefault();
            string preview = firstDifferent?.Content?.Replace("\r", " ").Replace("\n", " ").Trim()
                ?? string.Empty;
            while (preview.Contains("  ")) preview = preview.Replace("  ", " ");
            if (preview.Length > 72) preview = preview.Substring(0, 72).TrimEnd() + "…";
            if (preview.Length == 0) preview = "conversation path";

            return when + " · " + reason + " · " + preview;
        }

        private sealed record BranchPathOption(
            string? BranchId,
            DateTime CreatedUtc,
            IReadOnlyList<ChatMessage> Messages);

        private sealed record BranchNavigationState(
            List<BranchPathOption> Paths,
            int ActiveIndex,
            bool HasRetainedVersions);
    }
}
