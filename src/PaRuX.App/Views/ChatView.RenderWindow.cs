using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private int _renderedMessageBudget = ChatRenderWindowPolicy.InitialMessageBudget;
        private bool _boundedChatRenderingInitialized;

        /// <summary>
        /// Replaces only the three lifecycle handlers that can materialize message rows. Everything
        /// else in ChatView keeps the existing implementation. This is called from the top of
        /// ChatView.OnLoaded (see ChatView.xaml.cs), so the very first Loaded firing still runs the
        /// original unbounded OnLoaded/RebuildMessages once, but every subsequent MessageAdded and
        /// ConversationChanged goes through the windowed handlers.
        /// </summary>
        private void InitializeBoundedChatRendering()
        {
            if (_boundedChatRenderingInitialized) return;
            _boundedChatRenderingInitialized = true;

            Loaded -= OnLoaded;
            Loaded += OnLoadedWindowed;

            ChatSession.Current.ConversationChanged -= OnConversationChanged;
            ChatSession.Current.ConversationChanged += OnConversationChangedWindowed;

            ChatSession.Current.MessageAdded -= OnMessageAdded;
            ChatSession.Current.MessageAdded += OnMessageAddedWindowed;
        }

        private void OnLoadedWindowed(object sender, RoutedEventArgs e)
        {
            SyncProfileToggles(SettingsStore.Current.Data.DefaultProfile);
            RebuildMessagesWindowed(resetBudget: true, preserveViewport: false);
            _ = RefreshHeaderAsync();
            _ = LoadSkillsAsync();
        }

        private void OnConversationChangedWindowed()
        {
            _dispatcher.TryEnqueue(() => RebuildMessagesWindowed(resetBudget: true, preserveViewport: false));
            _dispatcher.TryEnqueue(() => { _ = RefreshHeaderAsync(); });
        }

        private void OnMessageAddedWindowed(ChatMessage message)
        {
            _dispatcher.TryEnqueue(() =>
            {
                // Regenerate can queue ConversationChanged and MessageAdded back-to-back. If the
                // windowed rebuild already materialized this exact message instance, do not add a
                // duplicate row.
                if (_contentPanels.ContainsKey(message)) return;
                EmptyState.Visibility = Visibility.Collapsed;

                if (ChatRenderWindowPolicy.NeedsLiveCompaction(
                        _contentPanels.Count + 1,
                        _renderedMessageBudget))
                {
                    // The complete conversation remains in ChatSession/Core. Only drop old XAML
                    // rows after a small slack window so a long active session stays bounded without
                    // rebuilding on every turn.
                    RebuildMessagesWindowed(resetBudget: false, preserveViewport: false);
                    return;
                }

                AppendMessageElement(message, true);
                ScrollToBottom(true);
            });
        }

        private void RebuildMessagesWindowed(bool resetBudget, bool preserveViewport)
        {
            double oldScrollableHeight = 0;
            double oldOffset = 0;
            if (preserveViewport)
            {
                try
                {
                    MessagesScroll.UpdateLayout();
                    oldScrollableHeight = MessagesScroll.ScrollableHeight;
                    oldOffset = MessagesScroll.VerticalOffset;
                }
                catch { preserveViewport = false; }
            }

            foreach (KeyValuePair<ChatMessage, MessageVisuals> pair in _visuals)
            {
                StopBoard(pair.Value.CaretAnimation);
                StopBoard(pair.Value.ThinkingAnimation);
            }
            _visuals.Clear();

            MessagesPanel.Children.Clear();
            _contentPanels.Clear();
            _editingMessages.Clear();
            lock (_dirtyGate) _dirty.Clear();

            List<ChatMessage> messages = ChatSession.Current.Conversation.Messages;
            _renderedMessageBudget = resetBudget
                ? ChatRenderWindowPolicy.InitialBudget(messages.Count)
                : ChatRenderWindowPolicy.ClampBudget(messages.Count, _renderedMessageBudget);

            int hidden = ChatRenderWindowPolicy.HiddenCount(messages.Count, _renderedMessageBudget);
            if (hidden > 0) MessagesPanel.Children.Add(BuildLoadEarlierMessagesButton(hidden));

            int start = ChatRenderWindowPolicy.StartIndex(messages.Count, _renderedMessageBudget);
            for (int index = start; index < messages.Count; index++)
                AppendMessageElement(messages[index], false);

            EmptyState.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateRouteLabel();

            if (!preserveViewport)
            {
                ScrollToBottom(true);
                return;
            }

            // Loading an older page inserts content above the current first visible row. Preserve
            // that row's apparent screen position rather than jumping the reader to the top/bottom.
            try
            {
                MessagesScroll.UpdateLayout();
                double addedHeight = Math.Max(0, MessagesScroll.ScrollableHeight - oldScrollableHeight);
                MessagesScroll.ChangeView(null, oldOffset + addedHeight, null, true);
            }
            catch (Exception ex)
            {
                Log.Write("Preserving chat history viewport failed: " + ex.Message);
            }
        }

        private Button BuildLoadEarlierMessagesButton(int hiddenCount)
        {
            int nextCount = Math.Min(ChatRenderWindowPolicy.HistoryPageSize, hiddenCount);
            var button = new Button
            {
                Content = "Load " + nextCount + " earlier messages · " + hiddenCount + " hidden",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 6, 12, 6)
            };
            ToolTipService.SetToolTip(button,
                "Only the chat visual tree is paged. The complete conversation remains saved and available to PaRuX.");
            button.Click += (s, e) =>
            {
                List<ChatMessage> messages = ChatSession.Current.Conversation.Messages;
                _renderedMessageBudget = ChatRenderWindowPolicy.GrowBudget(
                    messages.Count,
                    _renderedMessageBudget);
                RebuildMessagesWindowed(resetBudget: false, preserveViewport: true);
            };
            return button;
        }
    }
}
