using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.Media.Core;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace PaRuX.Views
{
    public sealed partial class ChatView : UserControl
    {
        /// <summary>
        /// The per-message chrome the UI owns: avatar row, streaming caret,
        /// action buttons and any running animation. Kept beside _contentPanels
        /// so the streaming path stays exactly as fast as it was.
        /// </summary>
        private sealed class MessageVisuals
        {
            public Border? Bubble;
            public Border? Caret;
            public Storyboard? CaretAnimation;
            public FrameworkElement? Actions;
            /// <summary>Hidden for a Grill Me turn once SkillUsed is known - see SyncStreamingChrome.
            /// Built unconditionally in BuildAssistantActions because SkillUsed is not populated yet
            /// at message-construction time (it arrives later via the skill_selected SSE event), so
            /// the decision has to be re-checked on every render flush, not made once up front.</summary>
            public Button? EditButton;
            public FrameworkElement? SwitchBadge;
            public TextBlock? Sources;
            /// <summary>Model, locality and any fallback that produced this reply. Collapsed until Core reports them.</summary>
            public TextBlock? Provenance;
            public Storyboard? ThinkingAnimation;
            public bool ErrorStyleApplied;
            public FrameworkElement? MediaPanel;
            public TextBlock? MediaProgress;
            public TextBlock? MediaCancelled;
            public Button? MediaCancel;
            public Button? MediaRetry;
            /// <summary>Set once the caret Storyboard has failed, so a broken
            /// animation is not retried on every 80 ms render flush.</summary>
            public bool CaretAnimationFailed;
            /// <summary>User's manual expand/collapse choice for the Thinking panel, remembered
            /// across render ticks (RenderInto rebuilds the Expander from scratch every flush
            /// while streaming, which would otherwise fight the user's own toggle). Null until
            /// they actually click it, at which point it overrides the streaming-phase default.</summary>
            public bool? ReasoningExpandedOverride;
        }

        private readonly DispatcherQueue _dispatcher;
        // Guards RefreshHeaderAsync against out-of-order completion: each call captures the
        // generation it started at and only applies its result if nothing newer has started
        // since - otherwise a slow call (its own real health-check network round trip) can
        // finish and overwrite the header with stale state after a faster, more recent call
        // already wrote the correct one. Real bug this fixes, found via live testing: toggling
        // Incognito twice in quick succession sometimes left the badge showing "Incognito" (or
        // hid it) one click behind the conversation's actual current state.
        private int _headerRefreshGeneration;
        private readonly Dictionary<ChatMessage, Panel> _contentPanels = new Dictionary<ChatMessage, Panel>();
        private readonly Dictionary<ChatMessage, MessageVisuals> _visuals = new Dictionary<ChatMessage, MessageVisuals>();
        private readonly HashSet<ChatMessage> _dirty = new HashSet<ChatMessage>();
        /// <summary>Messages whose content panel currently shows the inline edit surface instead of
        /// rendered markdown. RenderInto must leave these alone until edit/continue is confirmed or
        /// cancelled, or the streaming re-render pipeline would wipe out the user's in-progress edit.</summary>
        private readonly HashSet<ChatMessage> _editingMessages = new HashSet<ChatMessage>();
        private readonly object _dirtyGate = new object();
        private readonly List<ChatAttachment> _pendingAttachments = new List<ChatAttachment>();

        private DispatcherQueueTimer _renderTimer;
        private DispatcherQueueTimer _statusTimer;
        private DispatcherQueueTimer _headerPollTimer;
        private DispatcherQueueTimer _gameMarkerTimer;
        private void OnChatUnloadedStopGameMarkerTimer(object sender, RoutedEventArgs e) => _gameMarkerTimer?.Stop();
        private bool _shownGameActive;
        private bool _isSwitchingProfile;
        private bool _suppressToggleEvents;
        private List<PaRuX.Contracts.SkillManifest> _skills = new List<PaRuX.Contracts.SkillManifest>();
        private bool _suppressSkillSelectorEvents;

        public ChatView()
        {
            InitializeComponent();
            // Non-modal "Tuning for your PC" line: visible only while a model is queued or being tuned.
            TuningStatusHost.Children.Add(new ModelTuningStatusControl(ModelTuningStatusControl.Mode.Line, "text"));

            _dispatcher = DispatcherQueue.GetForCurrentThread();
            ChatSession.Current.ComputerUseConsentPrompt = ShowComputerUseConsentAsync;

            _renderTimer = _dispatcher.CreateTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(80);
            _renderTimer.Tick += OnRenderTick;

            _statusTimer = _dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(7);
            _statusTimer.IsRepeating = false;
            _statusTimer.Tick += (s, e) => HideStatus();

            _headerPollTimer = _dispatcher.CreateTimer();
            _headerPollTimer.Interval = TimeSpan.FromSeconds(1);
            _headerPollTimer.IsRepeating = true;
            _headerPollTimer.Tick += (s, e) => { _ = RefreshHeaderAsync(); };

            // The state-file watcher can miss a Gaming marker change (directory created later, or a
            // dropped event). A File.Exists check every 2 s keeps the "Return to AI" affordance
            // truthful without paying for a full health probe: it refreshes only on a change.
            _gameMarkerTimer = _dispatcher.CreateTimer();
            _gameMarkerTimer.Interval = TimeSpan.FromSeconds(2);
            _gameMarkerTimer.IsRepeating = true;
            _gameMarkerTimer.Tick += (s, e) =>
            {
                if (LocalAiController.Current.IsGameActive() != _shownGameActive) _ = RefreshHeaderAsync();
            };

            ChatSession.Current.ConversationChanged += OnConversationChanged;
            ChatSession.Current.MessageAdded += OnMessageAdded;
            ChatSession.Current.MessageUpdated += OnMessageUpdated;
            ChatSession.Current.StateChanged += OnSessionStateChanged;

            LocalAiController.Current.StateChanged += OnLocalAiStateChanged;

            Loaded += OnLoaded;
            SizeChanged += OnChatViewSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Re-attach (or settle) any media job this conversation was waiting on when the app last closed.
            ChatSession.Current.ReconcileMediaJobs();
            // UserControl.OnApplyTemplate() is not reliably invoked for a plain UserControl that has
            // no Style/ControlTemplate of its own (ChatView has none) - confirmed live via a temporary
            // diagnostic log: at the point right after InitializeComponent() runs in the constructor,
            // SkillSelector is already assigned but SkillSelector.Parent is still null, so calling
            // these initializers from the constructor is too early too. Loaded is the first point in
            // the lifecycle where the element is actually attached to the live visual tree and
            // SkillSelector.Parent is populated. Without this, the conversation branch
            // Previous/Next/Versions navigator (InitializeConversationBranchHistory) never appeared
            // despite correct, persisted branch data, and the bounded chat-rendering windowing
            // (InitializeBoundedChatRendering) was silently inactive too - the app fell back to the
            // original unbounded RebuildMessages() call below for every conversation change.
            // InitializeNativeCodingHandoffUpgrade already had to grow a second, redundant call site
            // from ModelSelector_Loaded for the same reason. All three are idempotent (guarded by
            // their own instance flags), so calling them again here is safe.
            InitializeBoundedChatRendering();
            InitializeNativeCodingHandoffUpgrade();
            InitializeConversationBranchHistory();
            EnsureModelRoutingSubscribed();
            RefreshSimpleModeAnsweringModel();
            RefreshSimpleModeRuntimeHealth();

            SyncProfileToggles(SettingsStore.Current.Data.DefaultProfile);
#if DEBUG
            SeedCompletedCodingTaskUiTestFixture();
#endif
            RebuildMessages();
            _ = RefreshHeaderAsync();
            _gameMarkerTimer.Start();
            Unloaded -= OnChatUnloadedStopGameMarkerTimer;
            Unloaded += OnChatUnloadedStopGameMarkerTimer;
            Unloaded -= OnChatUnloadedStopVoice;
            Unloaded += OnChatUnloadedStopVoice;
            PreviewKeyDown -= OnChatPreviewKeyDownVoice;
            PreviewKeyDown += OnChatPreviewKeyDownVoice;
            _ = LoadSkillsAsync();
        }

        // ------------------------------------------------------------------
        // Skill selector (force AUTO routing to a specific server skill) + slash commands
        // ------------------------------------------------------------------

        private async Task LoadSkillsAsync()
        {
            try
            {
                List<PaRuX.Contracts.SkillManifest> skills = await SkillsClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    _skills = skills.Where(s => s.Enabled && s.Platforms.Contains("Windows"))
                        .OrderBy(s => s.Category).ThenBy(s => s.DisplayName).ToList();

                    _suppressSkillSelectorEvents = true;
                    SkillSelector.Items.Clear();
                    SkillSelector.Items.Add(new ComboBoxItem { Content = "Skill: Auto", Tag = null });
                    foreach (PaRuX.Contracts.SkillManifest skill in _skills)
                        SkillSelector.Items.Add(new ComboBoxItem { Content = skill.DisplayName, Tag = skill.Id });
                    SkillSelector.SelectedIndex = 0;
                    _suppressSkillSelectorEvents = false;
                });
            }
            catch (Exception ex)
            {
                Log.Write("LoadSkillsAsync failed: " + ex.Message);
            }
        }

        private void SkillSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSkillSelectorEvents) return;
            var item = SkillSelector.SelectedItem as ComboBoxItem;
            ChatSession.Current.ForcedSkillId = item?.Tag as string;
        }

        /// <summary>
        /// Recognizes a leading "/slashcommand" against the loaded skill list. Returns the
        /// matched skill id and the text with the command prefix stripped, or (null, text)
        /// unchanged when nothing matches (so an unrecognized "/" is sent as plain text).
        /// </summary>
        private (string? skillId, string text) ParseSlashCommand(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '/') return (null, text);

            int end = 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            string command = text.Substring(0, end);

            PaRuX.Contracts.SkillManifest? match = _skills.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.SlashCommand) && string.Equals(s.SlashCommand, command, StringComparison.OrdinalIgnoreCase));
            if (match == null) return (null, text);

            string rest = end < text.Length ? text.Substring(end).TrimStart() : string.Empty;
            return (match.Id, rest);
        }

        // ------------------------------------------------------------------
        // Resource helpers
        //
        // Styles used by code-built elements live in this control's OWN
        // ResourceDictionary, so these lookups never traverse a merged
        // dictionary. Every one degrades to null rather than throwing.
        // ------------------------------------------------------------------

        private Style? LocalStyle(string key)
        {
            try
            {
                object value;
                if (Resources.TryGetValue(key, out value) && value is Style) return (Style)value;
            }
            catch (Exception ex)
            {
                Log.Write("Style lookup failed for " + key + ": " + ex.Message);
            }
            return null;
        }

        private Brush? LocalBrush(string key)
        {
            try
            {
                object value;
                if (Resources.TryGetValue(key, out value) && value is Brush) return (Brush)value;
            }
            catch (Exception ex)
            {
                Log.Write("Brush lookup failed for " + key + ": " + ex.Message);
            }
            return null;
        }

        private static Style? AppStyle(string key)
        {
            try
            {
                object value;
                if (Application.Current.Resources.TryGetValue(key, out value) && value is Style) return (Style)value;
            }
            catch (Exception ex)
            {
                Log.Write("App style lookup failed for " + key + ": " + ex.Message);
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Motion. Short, eased, and always optional: if a Storyboard fails the
        // element is simply left in its final state.
        // ------------------------------------------------------------------

        private static void PlayEntrance(FrameworkElement element)
        {
            if (element == null) return;
            try
            {
                var translate = new TranslateTransform { Y = 8 };
                element.RenderTransform = translate;
                element.Opacity = 0;

                var board = new Storyboard();

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, "Opacity");
                board.Children.Add(fade);

                var slide = new DoubleAnimation
                {
                    From = 8,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slide, translate);
                Storyboard.SetTargetProperty(slide, "Y");
                board.Children.Add(slide);

                // Whatever happens, the message must not be left half-faded.
                board.Completed += (s, e) =>
                {
                    element.Opacity = 1;
                    element.RenderTransform = null;
                };

                board.Begin();
            }
            catch (Exception ex)
            {
                Log.Write("Entrance animation failed: " + ex.Message);
                element.Opacity = 1;
                element.RenderTransform = null;
            }
        }

        private static Storyboard? PulseForever(DependencyObject target, double from, double to,
                                               int milliseconds, int delayMilliseconds)
        {
            try
            {
                var board = new Storyboard();
                var pulse = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                    BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(pulse, target);
                Storyboard.SetTargetProperty(pulse, "Opacity");
                board.Children.Add(pulse);
                board.Begin();
                return board;
            }
            catch (Exception ex)
            {
                Log.Write("Pulse animation failed: " + ex.Message);
                return null;
            }
        }

        private static void StopBoard(Storyboard? board)
        {
            if (board == null) return;
            try { board.Stop(); } catch { }
        }

        // ------------------------------------------------------------------
        // Session events (may arrive on background threads)
        // ------------------------------------------------------------------

        private void OnConversationChanged()
        {
            _dispatcher.TryEnqueue(() =>
            {
                _lastChatModelRoute = null;
                RefreshSimpleModeAnsweringModel();
                ToolTipService.SetToolTip(
                    ModelSelector,
                    "AUTO chooses by task intelligence; Manual locks the exact model until you change it");
                string? currentId = ChatSession.Current.Conversation?.Id;
                string draft = ChatDraftCoordinator.Current.GetDraft(currentId);
                if (InputBox != null && InputBox.Text != draft)
                {
                    InputBox.Text = draft;
                    InputBox.SelectionStart = draft.Length;
                }
                RebuildMessages();
                ChatSession.Current.ReconcileMediaJobs();
            });
            _dispatcher.TryEnqueue(() => { _ = RefreshHeaderAsync(); });
        }

        private void OnMessageAdded(ChatMessage message)
        {
            _dispatcher.TryEnqueue(() =>
            {
                // Regenerate raises ConversationChanged and immediately adds its replacement.
                // If the queued rebuild already observed that replacement, appending it again
                // creates a second orphaned thinking row. Keep one visual per message instance.
                if (_contentPanels.ContainsKey(message)) return;
                EmptyState.Visibility = Visibility.Collapsed;
                AppendMessageElement(message, true);
                ScrollToBottom(true);
            });
        }

        private void OnMessageUpdated(ChatMessage message)
        {
            lock (_dirtyGate) _dirty.Add(message);

            _dispatcher.TryEnqueue(() =>
            {
                if (!_renderTimer.IsRunning) _renderTimer.Start();
            });
        }

        private void OnSessionStateChanged()
        {
            _dispatcher.TryEnqueue(() =>
            {
                bool busy = ChatSession.Current.IsBusy;
                SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
                StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                InputBox.IsEnabled = !_isSwitchingProfile;

                string? status = ChatSession.Current.Status;
                if (!string.IsNullOrEmpty(status)) ShowStatus(status, true);
                else if (StatusRing.IsActive) HideStatus();

                if (!busy)
                {
                    // Final flush so the last tokens always land.
                    FlushDirty();
                    _renderTimer.Stop();
                    SyncAllStreamingChrome();
                    RefreshSimpleModeAnsweringModel();
                }
            });
        }

        private void OnLocalAiStateChanged(object? sender, EventArgs e)
        {
            _dispatcher.TryEnqueue(() => { _ = RefreshHeaderAsync(); });
        }

        // ------------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------------

        private void OnRenderTick(DispatcherQueueTimer sender, object args)
        {
            FlushDirty();
            if (!ChatSession.Current.IsBusy) _renderTimer.Stop();
        }

        private void FlushDirty()
        {
            List<ChatMessage> pending;
            lock (_dirtyGate)
            {
                if (_dirty.Count == 0) return;
                pending = new List<ChatMessage>(_dirty);
                _dirty.Clear();
            }

            foreach (ChatMessage m in pending)
            {
                Panel? panel;
                if (!_contentPanels.TryGetValue(m, out panel)) continue;
                RenderInto(panel, m);
                SyncStreamingChrome(m);
            }
            ScrollToBottom(false);
        }

        private void RebuildMessages()
        {
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
            foreach (ChatMessage m in messages) AppendMessageElement(m, false);

            EmptyState.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateRouteLabel();
            ScrollToBottom(true);
        }

        /// <summary>
        /// LM Studio-style edit surface: swaps a finished assistant reply's rendered markdown for an
        /// editable TextBox pre-filled with its text. "Continue from here" asks the model to resume
        /// generating from exactly the edited text (ChatSession.ContinueAsync); "Cancel" discards the
        /// edit and restores the normal rendered view. Only reachable while the reply isn't streaming
        /// - BuildAssistantActions is hidden during streaming by SyncStreamingChrome already.
        /// </summary>
        private void BeginEditingMessage(ChatMessage message)
        {
            if (ChatSession.Current.IsBusy) return;
            Panel? panel;
            if (!_contentPanels.TryGetValue(message, out panel)) return;
            if (!_editingMessages.Add(message)) return;

            panel.Children.Clear();

            var box = new TextBox
            {
                Text = message.Content,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80
            };
            panel.Children.Add(box);

            void RestoreRenderedView()
            {
                _editingMessages.Remove(message);
                Panel? target;
                if (_contentPanels.TryGetValue(message, out target)) RenderInto(target, message);
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };

            var continueBtn = new Button { Content = "Continue from here" };
            continueBtn.Click += (s, e) =>
            {
                string edited = box.Text ?? string.Empty;
                _editingMessages.Remove(message);
                _ = ChatSession.Current.ContinueAsync(message, edited);
            };
            row.Children.Add(continueBtn);

            var cancelBtn = new Button { Content = "Cancel" };
            cancelBtn.Click += (s, e) => RestoreRenderedView();
            row.Children.Add(cancelBtn);

            panel.Children.Add(row);
            box.Focus(FocusState.Programmatic);
        }

        /// <summary>True while the server-reported skill for this turn is Grill Me. The reliable
        /// signal for "this turn's content is/was the forced quiz JSON shape, not prose" - SkillUsed
        /// is set from the skill_selected SSE event, which always arrives before any content
        /// streams, so it is available even while message.Content is still incomplete JSON.</summary>
        private static bool IsGrillMeTurn(ChatMessage message) =>
            !string.IsNullOrEmpty(message.SkillUsed)
            && message.SkillUsed.StartsWith("Grill Me", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Renders a parsed Grill Me turn as feedback + question text plus selectable answer
        /// buttons, like Claude Code's AskUserQuestion, instead of requiring the user to type an
        /// answer. Clicking an option sends its exact text through the normal SendAsync pipeline -
        /// no new backend path - which also keeps the sticky ForcedSkillId behavior intact for the
        /// next question. "Other" is offered only when the server said a free-text answer is
        /// genuinely appropriate; the composer itself is never disabled, so a user can always type
        /// instead regardless of interactive/AllowOther.
        /// </summary>
        private FrameworkElement BuildGrillMeTurn(GrillMeTurn quiz, bool interactive)
        {
            var root = new StackPanel();

            if (!string.IsNullOrWhiteSpace(quiz.Feedback))
            {
                var feedback = new TextBlock
                {
                    Text = quiz.Feedback,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                Style? feedbackStyle = LocalStyle("PaRuXMessageRouteTextStyle");
                if (feedbackStyle != null) feedback.Style = feedbackStyle;
                root.Children.Add(feedback);
            }

            var questionPanel = new StackPanel();
            MarkdownRenderer.Render(quiz.Question, questionPanel);
            root.Children.Add(questionPanel);

            var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 6 };
            root.Children.Add(options);

            if (!interactive)
            {
                // History: show what was offered without inviting another answer.
                var summary = new TextBlock
                {
                    Text = "Options: " + string.Join(" · ", quiz.Options) + (quiz.AllowOther ? " · Other" : string.Empty),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7
                };
                options.Children.Add(summary);
                return root;
            }

            void Answer(string text)
            {
                foreach (object child in options.Children)
                {
                    if (child is Button b) b.IsEnabled = false;
                }
                if (ChatSession.Current.IsBusy) return;
                _ = ChatSession.Current.SendAsync(text, null);
            }

            foreach (string option in quiz.Options)
            {
                var button = new Button
                {
                    Content = option,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8)
                };
                string captured = option;
                button.Click += (s, e) => Answer(captured);
                options.Children.Add(button);
            }

            if (quiz.AllowOther)
            {
                var other = new Button
                {
                    Content = "Other…",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8)
                };
                other.Click += (s, e) => InputBox.Focus(FocusState.Programmatic);
                options.Children.Add(other);
            }

            return root;
        }

        private void RenderInto(Panel panel, ChatMessage message)
        {
            // The inline edit surface owns this panel until the user confirms (Continue) or
            // cancels; a streaming re-render arriving mid-edit must not clobber unsaved text.
            if (_editingMessages.Contains(message)) return;

            MessageVisuals? visuals;
            if (_visuals.TryGetValue(message, out visuals) && visuals.ThinkingAnimation != null)
            {
                StopBoard(visuals.ThinkingAnimation);
                visuals.ThinkingAnimation = null;
            }

            if (message.Role == "user")
            {
                panel.Children.Clear();
                AppendAttachmentPreviews(panel, message, HorizontalAlignment.Right);
                var text = new TextBlock
                {
                    Text = message.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                Style? userText = LocalStyle("PaRuXUserTextStyle");
                if (userText != null) text.Style = userText;
                panel.Children.Add(text);
                return;
            }

            if (message.IsStreaming && string.IsNullOrEmpty(message.Content))
            {
                if (string.IsNullOrEmpty(message.Reasoning))
                {
                    // Nothing has arrived yet: three pulsing dots rather than a bare spinner.
                    panel.Children.Clear();
                    Storyboard board;
                    panel.Children.Add(BuildThinkingIndicator(out board));
                    if (visuals != null) visuals.ThinkingAnimation = board;
                    return;
                }

                // Reasoning tokens are arriving before the real answer does - show them live,
                // expanded by default (this is the "watch it think" phase).
                panel.Children.Clear();
                panel.Children.Add(BuildReasoningPanel(message.Reasoning, visuals, defaultExpanded: true));
                return;
            }

            // Grill Me's structured turn streams as raw JSON while still in flight - not valid to
            // parse yet and unreadable as markdown. Show the same "preparing" indicator used before
            // any content has arrived, rather than a broken partial object.
            if (message.IsStreaming && IsGrillMeTurn(message))
            {
                panel.Children.Clear();
                Storyboard grillBoard;
                panel.Children.Add(BuildThinkingIndicator(out grillBoard));
                if (visuals != null) visuals.ThinkingAnimation = grillBoard;
                return;
            }

            // A finished Grill Me turn: render feedback + question + selectable answers instead of
            // raw JSON, like Claude Code's AskUserQuestion. Only the newest message stays
            // interactive - an older, already-answered question should read as history, not offer
            // buttons for a question the user already responded to.
            if (!message.IsStreaming && IsGrillMeTurn(message)
                && GrillMeTurnParser.TryParse(message.Content, out GrillMeTurn? quiz))
            {
                panel.Children.Clear();
                bool interactive = ReferenceEquals(message, ChatSession.Current.Conversation.Messages.LastOrDefault());
                panel.Children.Add(BuildGrillMeTurn(quiz, interactive));
                AppendAttachmentPreviews(panel, message, HorizontalAlignment.Left);
                return;
            }

            // MarkdownRenderer.Render clears the panel itself before rendering, so the
            // reasoning panel has to be inserted AFTER it, not before - adding it first would
            // just get wiped out by Render's own Children.Clear().
            MarkdownRenderer.Render(message.Content, panel);
            if (!string.IsNullOrEmpty(message.Reasoning))
                panel.Children.Insert(0, BuildReasoningPanel(message.Reasoning, visuals, defaultExpanded: false));
            AppendAttachmentPreviews(panel, message, HorizontalAlignment.Left);
        }

        /// <summary>Collapsible "Thinking" block for a reasoning model's chain-of-thought
        /// (ChatMessage.Reasoning). Plain text, not markdown - reasoning_content is prose, not
        /// formatted output meant to be rendered.</summary>
        private FrameworkElement BuildReasoningPanel(string? reasoning, MessageVisuals? visuals, bool defaultExpanded)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new TextBlock { Text = "💭", FontSize = 13 });
            var headerText = new TextBlock { Text = "Thinking", FontSize = 12, Opacity = 0.75 };
            header.Children.Add(headerText);

            var body = new TextBlock
            {
                Text = reasoning,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 13,
                IsTextSelectionEnabled = true
            };

            var expander = new Expander
            {
                Header = header,
                Content = body,
                IsExpanded = visuals?.ReasoningExpandedOverride ?? defaultExpanded,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };
            if (visuals != null)
            {
                expander.Expanding += (s, e) => visuals.ReasoningExpandedOverride = true;
                expander.Collapsed += (s, e) => visuals.ReasoningExpandedOverride = false;
            }
            return expander;
        }

        private void AppendAttachmentPreviews(
            Panel panel, ChatMessage message, HorizontalAlignment alignment)
        {
            if (message.Attachments == null) return;
            foreach (ChatAttachment attachment in message.Attachments)
            {
                if (!File.Exists(attachment.LocalPath)) continue;
                try
                {
                    if (attachment.MediaType != null
                        && attachment.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        panel.Children.Add(new MediaPlayerElement
                        {
                            Source = MediaSource.CreateFromUri(new Uri(attachment.LocalPath)),
                            AreTransportControlsEnabled = true,
                            AutoPlay = false,
                            Width = 520,
                            Height = 320,
                            HorizontalAlignment = alignment,
                            Margin = new Thickness(0, 8, 0, 4)
                        });
                        continue;
                    }
                    if (attachment.MediaType != null
                        && attachment.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        var audioPlayer = new MediaPlayerElement
                        {
                            Source = MediaSource.CreateFromUri(new Uri(attachment.LocalPath)),
                            AreTransportControlsEnabled = true,
                            AutoPlay = false,
                            Width = 520,
                            Height = 110,
                            HorizontalAlignment = alignment,
                            Margin = new Thickness(0, 8, 0, 4)
                        };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(audioPlayer, "Audio player: " + attachment.FileName);
                        panel.Children.Add(audioPlayer);
                        continue;
                    }
                    if (attachment.MediaType == null
                        || !attachment.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        var documentContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        documentContent.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 14 });
                        documentContent.Children.Add(new TextBlock { Text = attachment.FileName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 });
                        var documentButton = new Button
                        {
                            Content = documentContent,
                            MaxWidth = 420,
                            HorizontalAlignment = alignment,
                            Margin = new Thickness(0, 8, 0, 4)
                        };
                        ToolTipService.SetToolTip(documentButton, "Open attached document");
                        documentButton.Click += (s, e) => { _ = OpenMediaExternallyAsync(attachment); };
                        panel.Children.Add(documentButton);
                        continue;
                    }
                    var preview = new Image
                    {
                        // Reserve the preview's layout immediately. BitmapImage decodes
                        // asynchronously; with only MaxWidth/MaxHeight a restored conversation
                        // initially measured this button at zero height, leaving an invisible
                        // image hit target over the action row until another layout pass.
                        Width = 420,
                        Height = 360,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = alignment,
                        Margin = new Thickness(0, 8, 0, 4)
                    };
                    _ = LoadImagePreviewAsync(preview, attachment.LocalPath);
                    var open = new Button
                    {
                        Content = preview,
                        Background = null,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0),
                        HorizontalAlignment = alignment
                    };
                    ToolTipService.SetToolTip(open, "Open image viewer");
                    open.Click += (s, e) => { _ = ShowImageViewerAsync(attachment); };
                    panel.Children.Add(open);
                }
                catch (Exception ex)
                {
                    Log.Write("Image preview failed: " + ex.Message);
                }
            }
        }

        private static async Task LoadImagePreviewAsync(Image target, string path)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                target.Source = bitmap;
            }
            catch (Exception ex)
            {
                Log.Write("Image preview failed: " + ex.Message);
            }
        }

        private FrameworkElement BuildThinkingIndicator(out Storyboard board)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var combined = new Storyboard();
            Style? dotStyle = LocalStyle("PaRuXThinkingDotStyle");

            for (int i = 0; i < 3; i++)
            {
                var dot = new Border { Opacity = 0.25 };
                if (dotStyle != null)
                {
                    dot.Style = dotStyle;
                }
                else
                {
                    dot.Width = 7;
                    dot.Height = 7;
                    dot.CornerRadius = new CornerRadius(4);
                }
                panel.Children.Add(dot);

                try
                {
                    var pulse = new DoubleAnimation
                    {
                        From = 0.25,
                        To = 1.0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(480)),
                        BeginTime = TimeSpan.FromMilliseconds(i * 160),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Storyboard.SetTarget(pulse, dot);
                    Storyboard.SetTargetProperty(pulse, "Opacity");
                    combined.Children.Add(pulse);
                }
                catch (Exception ex)
                {
                    Log.Write("Thinking dot animation failed: " + ex.Message);
                }
            }

            try { combined.Begin(); }
            catch (Exception ex) { Log.Write("Thinking indicator failed: " + ex.Message); }

            board = combined;
            return panel;
        }

        /// <summary>
        /// Shows or hides the streaming caret and the Copy/Regenerate row for one
        /// message, and upgrades the bubble to the error style if the turn failed.
        /// </summary>
        private void SyncStreamingChrome(ChatMessage message)
        {
            MessageVisuals? visuals;
            if (!_visuals.TryGetValue(message, out visuals)) return;

            SyncMediaPanel(message, visuals);
            bool streaming = message.IsStreaming;
            bool hasContent = !string.IsNullOrEmpty(message.Content);

            if (visuals.Caret != null)
            {
                bool showCaret = streaming && hasContent;
                visuals.Caret.Visibility = showCaret ? Visibility.Visible : Visibility.Collapsed;

                if (showCaret && visuals.CaretAnimation == null && !visuals.CaretAnimationFailed)
                {
                    visuals.CaretAnimation = PulseForever(visuals.Caret, 1.0, 0.15, 620, 0);
                    if (visuals.CaretAnimation == null) visuals.CaretAnimationFailed = true;
                }
                else if (!showCaret && visuals.CaretAnimation != null)
                {
                    StopBoard(visuals.CaretAnimation);
                    visuals.CaretAnimation = null;
                    visuals.Caret.Opacity = 1;
                }
            }

            if (visuals.Actions != null)
            {
                visuals.Actions.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
            }

            // Re-checked on every flush, not decided once at message construction: SkillUsed isn't
            // known until the skill_selected SSE event arrives, which is after the button was built.
            if (visuals.EditButton != null)
            {
                visuals.EditButton.Visibility = IsGrillMeTurn(message) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (visuals.SwitchBadge != null)
            {
                visuals.SwitchBadge.Visibility = message.SwitchedToUncensored
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            if (visuals.Sources != null)
            {
                List<string>? sources = message.RagSources;
                bool hasSources = sources is { Count: > 0 };
                bool hasSkill = !string.IsNullOrEmpty(message.SkillUsed);
                if (hasSources && hasSkill)
                    visuals.Sources.Text = "Skill: " + message.SkillUsed + "  ·  Retrieved context: " + string.Join(", ", sources!);
                else if (hasSkill)
                    visuals.Sources.Text = "Skill: " + message.SkillUsed;
                else if (hasSources)
                    visuals.Sources.Text = "Retrieved context: " + string.Join(", ", sources!);
                visuals.Sources.Visibility = (hasSources || hasSkill) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (visuals.Provenance != null)
            {
                string? label = _isSimpleMode
                    ? AnsweringModelLabelFormatter.Format(message.ModelId, message.ManualLock)
                    : ChatProvenance.FormatLabel(
                    message.ModelId, message.Provider, message.Locality, message.FallbackReason);
                visuals.Provenance.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
                if (label != null)
                {
                    string detail = string.IsNullOrWhiteSpace(message.FallbackReason)
                        ? label
                        : label + ". " + message.FallbackReason;
                    visuals.Provenance.Text = label;
                    AutomationProperties.SetName(visuals.Provenance, detail);
                    ToolTipService.SetToolTip(visuals.Provenance, detail);
                }
            }

            // IsError is only set once the turn has failed, i.e. after the bubble
            // was built, so the style has to be swapped in here rather than there.
            if (message.IsError && visuals.Bubble != null && !visuals.ErrorStyleApplied)
            {
                Style? error = AppStyle("PaRuXErrorBubbleStyle");
                if (error != null) visuals.Bubble.Style = error;
                visuals.ErrorStyleApplied = true;
            }
        }

        private void SyncAllStreamingChrome()
        {
            foreach (ChatMessage m in ChatSession.Current.Conversation.Messages) SyncStreamingChrome(m);
        }

        private void AppendMessageElement(ChatMessage message, bool animate)
        {
            var content = new StackPanel();
            _contentPanels[message] = content;

            var visuals = new MessageVisuals();
            _visuals[message] = visuals;

            string styleKey = message.Role == "user"
                ? "PaRuXUserBubbleStyle"
                : (message.IsError ? "PaRuXErrorBubbleStyle" : "PaRuXAssistantBubbleStyle");

            var bubble = new Border { Child = content, ContextFlyout = BuildMessageBranchFlyout(message) };
            Style? bubbleStyle = AppStyle(styleKey);
            if (bubbleStyle != null) bubble.Style = bubbleStyle;
            visuals.Bubble = bubble;
            visuals.ErrorStyleApplied = message.IsError;

            FrameworkElement row;

            if (message.Role == "user")
            {
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                stack.Children.Add(bubble);

                if (!string.IsNullOrEmpty(message.Route))
                {
                    stack.Children.Add(BuildRouteBadge(message.Route));
                }
                stack.Children.Add(BuildTimestampCaption(message.TimestampUtc, HorizontalAlignment.Right));
                row = stack;
            }
            else
            {
                // Avatar column + a borderless reply that blends into the page.
                var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Border avatar = BuildAvatar();
                Grid.SetColumn(avatar, 0);
                grid.Children.Add(avatar);

                var column = new StackPanel();
                Grid.SetColumn(column, 1);

                FrameworkElement switchBadge = BuildSwitchedToUncensoredBadge();
                switchBadge.Visibility = message.SwitchedToUncensored ? Visibility.Visible : Visibility.Collapsed;
                visuals.SwitchBadge = switchBadge;
                column.Children.Add(switchBadge);

                column.Children.Add(bubble);

                var sources = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
                Style? sourcesStyle = LocalStyle("PaRuXMessageRouteTextStyle");
                if (sourcesStyle != null) sources.Style = sourcesStyle;
                visuals.Sources = sources;
                column.Children.Add(sources);

                var provenance = new TextBlock { Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
                Style? provenanceStyle = LocalStyle("PaRuXMessageRouteTextStyle");
                if (provenanceStyle != null) provenance.Style = provenanceStyle;
                AutomationProperties.SetAutomationId(provenance, "parux-message-provenance");
                visuals.Provenance = provenance;
                column.Children.Add(provenance);

                var caret = new Border { Visibility = Visibility.Collapsed };
                Style? caretStyle = LocalStyle("PaRuXStreamCaretStyle");
                if (caretStyle != null)
                {
                    caret.Style = caretStyle;
                }
                else
                {
                    caret.Width = 7;
                    caret.Height = 16;
                    caret.CornerRadius = new CornerRadius(4);
                }
                visuals.Caret = caret;
                column.Children.Add(caret);

                FrameworkElement actions = BuildAssistantActions(message, visuals);
                visuals.Actions = actions;
                column.Children.Add(actions);
                column.Children.Add(BuildMediaPanel(message, visuals));
                column.Children.Add(BuildTimestampCaption(message.TimestampUtc, HorizontalAlignment.Left));

                grid.Children.Add(column);
                row = grid;
            }

            RenderInto(content, message);
            MessagesPanel.Children.Add(row);
            SyncStreamingChrome(message);

            if (animate) PlayEntrance(row);
        }

        private Border BuildAvatar()
        {
            var avatar = new Border();
            Style? avatarStyle = LocalStyle("PaRuXAvatarStyle");
            if (avatarStyle != null)
            {
                avatar.Style = avatarStyle;
            }
            else
            {
                avatar.Width = 26;
                avatar.Height = 26;
                avatar.CornerRadius = new CornerRadius(13);
                avatar.Margin = new Thickness(0, 2, 12, 0);
                avatar.VerticalAlignment = VerticalAlignment.Top;
            }

            var glyph = new TextBlock { Text = "N" };
            Style? glyphStyle = LocalStyle("PaRuXAvatarGlyphStyle");
            if (glyphStyle != null) glyph.Style = glyphStyle;
            avatar.Child = glyph;

            return avatar;
        }

        /// <summary>
        /// Small local-time caption shown under each message bubble. The chat had no way to tell
        /// when a turn actually happened - real value for a long-running native coding task, where
        /// distinguishing "posted a minute ago" from "posted an hour ago" matters and the transcript
        /// otherwise reads as an undated wall of text.
        /// </summary>
        private FrameworkElement BuildTimestampCaption(DateTime timestampUtc, HorizontalAlignment alignment)
        {
            DateTime local = timestampUtc.ToLocalTime();
            var text = new TextBlock
            {
                Text = local.ToString("HH:mm"),
                Margin = new Thickness(0, 2, 2, 0),
                HorizontalAlignment = alignment
            };
            Style? textStyle = LocalStyle("PaRuXMessageRouteTextStyle");
            if (textStyle != null) text.Style = textStyle;
            ToolTipService.SetToolTip(text, local.ToString("dddd d MMMM yyyy, HH:mm:ss"));
            return text;
        }

        private FrameworkElement BuildRouteBadge(string route)
        {
            var badge = new Border();
            Style? badgeStyle = LocalStyle("PaRuXMessageRoutePillStyle");
            if (badgeStyle != null)
            {
                badge.Style = badgeStyle;
            }
            else
            {
                badge.CornerRadius = new CornerRadius(999);
                badge.Padding = new Thickness(8, 1, 8, 1);
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.Margin = new Thickness(0, 4, 2, 0);
            }

            var text = new TextBlock { Text = route };
            Style? textStyle = LocalStyle("PaRuXMessageRouteTextStyle");
            if (textStyle != null) text.Style = textStyle;
            badge.Child = text;

            ToolTipService.SetToolTip(badge, "PaRuX routed this turn to: " + route);
            return badge;
        }

        private FrameworkElement BuildSwitchedToUncensoredBadge()
        {
            var badge = new Border { Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            Style? badgeStyle = LocalStyle("PaRuXMessageRoutePillStyle");
            if (badgeStyle != null)
            {
                badge.Style = badgeStyle;
            }
            else
            {
                badge.CornerRadius = new CornerRadius(999);
                badge.Padding = new Thickness(8, 1, 8, 1);
            }

            var text = new TextBlock { Text = "Switched to Uncensored" };
            Style? textStyle = LocalStyle("PaRuXMessageRouteTextStyle");
            if (textStyle != null) text.Style = textStyle;
            badge.Child = text;

            ToolTipService.SetToolTip(badge,
                "Standard declined or fell short here, so PaRuX automatically retried on the local Uncensored model.");
            return badge;
        }

        private FrameworkElement BuildAssistantActions(ChatMessage message, MessageVisuals visuals)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 4, 0, 0)
            };

            Button copy = BuildActionButton("\uE8C8", "Copy");
            copy.Click += (s, e) =>
            {
                try
                {
                    // A Grill Me turn's raw Content is the forced JSON shape, not something a user
                    // would want on their clipboard - copy the readable question text instead.
                    string toCopy = message.Content ?? string.Empty;
                    if (IsGrillMeTurn(message) && GrillMeTurnParser.TryParse(toCopy, out GrillMeTurn? quiz))
                    {
                        toCopy = string.IsNullOrWhiteSpace(quiz.Feedback)
                            ? quiz.Question
                            : quiz.Feedback + "\n\n" + quiz.Question;
                    }
                    var package = new DataPackage();
                    package.SetText(toCopy);
                    Clipboard.SetContent(package);
                    ShowTransientStatus("Copied to clipboard.");
                }
                catch (Exception ex)
                {
                    Log.Write("Copy failed: " + ex.Message);
                    ShowTransientStatus("Couldn't copy to the clipboard.");
                }
            };
            actions.Children.Add(copy);

            Button readAloud = BuildActionButton("", "Read aloud");
            ConfigureReadAloudButton(readAloud, message);
            actions.Children.Add(readAloud);

            Button regenerate = BuildActionButton("\uE72C", "Regenerate");
            // Regenerate the reply whose button was selected. Using the conversation's latest
            // assistant message made buttons on older media silently replay an unrelated newer
            // reply instead of resubmitting the original image/video job.
            regenerate.Click += (s, e) => { _ = ChatSession.Current.RegenerateAsync(message); };
            actions.Children.Add(regenerate);
            actions.Children.Add(BuildBranchActionButton(message));

            // Not offered for a Grill Me turn: its Content is the forced quiz JSON, not prose a
            // user would hand-edit, and continuing a truncated JSON prefix isn't guaranteed to
            // still satisfy the schema the way a fresh generation call does. Built unconditionally
            // and hidden via SyncStreamingChrome rather than skipped here: at this point (message
            // construction) SkillUsed has not been set yet - it arrives later via the
            // skill_selected SSE event - so IsGrillMeTurn(message) would always read false here and
            // the button would show for every turn regardless of skill.
            Button edit = BuildActionButton("\uE70F", "Edit and continue");
            edit.Click += (s, e) => BeginEditingMessage(message);
            actions.Children.Add(edit);
            visuals.EditButton = edit;

            Brush? ratingActiveBrush = LocalBrush("PaRuXBrandTextBrush");
            Brush? ratingQuietBrush = LocalBrush("PaRuXDotOffBrush");

            Button thumbsUp = BuildActionButton("\uE8E1", "Good reply");
            Button thumbsDown = BuildActionButton("\uE8E2", "Not useful");
            void SyncRatingButtons()
            {
                thumbsUp.Foreground = message.FeedbackRating == "up" ? ratingActiveBrush : ratingQuietBrush;
                thumbsDown.Foreground = message.FeedbackRating == "down" ? ratingActiveBrush : ratingQuietBrush;
            }
            thumbsUp.Click += (s, e) =>
            {
                message.FeedbackRating = message.FeedbackRating == "up" ? null : "up";
                SyncRatingButtons();
                ChatSession.Current.Persist();
            };
            thumbsDown.Click += (s, e) =>
            {
                message.FeedbackRating = message.FeedbackRating == "down" ? null : "down";
                SyncRatingButtons();
                ChatSession.Current.Persist();
            };
            SyncRatingButtons();
            actions.Children.Add(thumbsUp);
            actions.Children.Add(thumbsDown);

            Button trainingCandidate = BuildActionButton("\uE734", message.TrainingCandidate ? "Marked as a training candidate" : "Save as a training candidate");
            trainingCandidate.Foreground = message.TrainingCandidate ? ratingActiveBrush : ratingQuietBrush;
            trainingCandidate.Click += (s, e) =>
            {
                message.TrainingCandidate = !message.TrainingCandidate;
                trainingCandidate.Foreground = message.TrainingCandidate ? ratingActiveBrush : ratingQuietBrush;
                ToolTipService.SetToolTip(trainingCandidate, message.TrainingCandidate ? "Marked as a training candidate" : "Save as a training candidate");
                ChatSession.Current.Persist();
                ShowTransientStatus(message.TrainingCandidate ? "Saved as a training candidate." : "Removed from training candidates.");
            };
            actions.Children.Add(trainingCandidate);

            // Answering "here is what you should change" and stopping there leaves the person to
            // carry the answer somewhere else by hand. Any reply can become a coding task against
            // the configured workspace, with the request and the proposal as its goal - the same
            // isolated worktree, verification and repair path every other coding task gets.
            Button applyAsCodingTask = BuildActionButton("", "Apply this as a coding task");
            applyAsCodingTask.Click += async (s, e) =>
            {
                string workspace = (SettingsStore.Current.Data.AgentCodingWorkspaceRoot ?? string.Empty).Trim();
                if (workspace.Length == 0)
                {
                    ShowTransientStatus("Set the Coding agent workspace in Settings › Agents first: "
                        + "a coding task runs in its own worktree of a repository you name.");
                    return;
                }

                string request = ChatSession.Current.Conversation.Messages
                    .LastOrDefault(m => m.Role == "user")?.Content?.Trim() ?? string.Empty;
                string proposal = message.Content?.Trim() ?? string.Empty;
                if (proposal.Length == 0) { ShowTransientStatus("There is nothing in this reply to apply."); return; }

                static string Bound(string value, int max) =>
                    value.Length <= max ? value : value.Substring(0, max) + "\n… (truncated)";

                string goal = "Apply the following change in this repository."
                    + (request.Length > 0 ? "\n\nWhat was asked:\n" + Bound(request, 2_000) : string.Empty)
                    + "\n\nWhat PaRuX proposed:\n" + Bound(proposal, 6_000);

                applyAsCodingTask.IsEnabled = false;
                try
                {
                    PaRuX.Contracts.AgentTaskDto task = await AgentOrchestratorClient.Current.CreateCodingTaskAsync(
                        goal,
                        workspace,
                        SettingsStore.Current.Data.CodingPermissionProfile ?? "offline",
                        2,
                        CancellationToken.None,
                        codingRunPermissionMode: CodingRunPermissionModes.Normalize(
                            ChatSession.Current.Conversation.CodingRunPermissionMode));
                    ShowTransientStatus("Coding task " + task.Id[..8] + " created on " + workspace
                        + ". Follow it in Settings › Agents.");
                }
                catch (Exception ex)
                {
                    Log.Write("Apply-as-coding-task failed: " + ex.Message);
                    ShowTransientStatus(UserFailureRecoveryMapper.MapCodingTaskLaunch(ex).FormatStatusLine());
                }
                finally { applyAsCodingTask.IsEnabled = true; }
            };
            actions.Children.Add(applyAsCodingTask);

            if (message.ActionKind == "computer-launch" && !string.IsNullOrWhiteSpace(message.ActionPayload))
            {
                // "Allow once": the card authorizes exactly one execution, consumed by its first click.
                bool spent = new ComputerUseApprovalState(message.ActionConsumed).Consumed;
                var runComputerAction = new Button
                {
                    Content = spent ? ComputerUseApprovalState.UsedLabel
                        : string.IsNullOrWhiteSpace(message.ActionLabel) ? "Run action" : message.ActionLabel,
                    IsEnabled = !spent,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(12, 4, 12, 4)
                };
                runComputerAction.Click += async (s, e) =>
                {
                    // Gaming Mode refuses before the approval is spent, so a refused click leaves the card usable.
                    string? gamingRefusal = ComputerUseAuthorizationPolicy.GamingRefusal(GamingManager.Current.IsActive);
                    if (gamingRefusal != null)
                    {
                        ShowTransientStatus(gamingRefusal);
                        return;
                    }

                    (ComputerUseApprovalState next, bool mayExecute) =
                        new ComputerUseApprovalState(message.ActionConsumed).TryExecute(message.ActionPayload);
                    if (!mayExecute)
                    {
                        runComputerAction.IsEnabled = false;
                        runComputerAction.Content = ComputerUseApprovalState.UsedLabel;
                        return;
                    }

                    // Consume and persist before the first await, so a second click or a restart cannot replay it.
                    message.ActionConsumed = next.Consumed;
                    runComputerAction.IsEnabled = false;
                    ChatSession.Current.Persist();
                    ShowTransientStatus("Running the approved local action…");
                    ComputerActionResult result = await ComputerUseService.ExecuteAsync(message.ActionPayload);
                    ShowTransientStatus(result.Message);
                    runComputerAction.Content = result.Message;
                };
                actions.Children.Add(runComputerAction);
            }

            if (message.ActionKind == "computer-agent" && !string.IsNullOrWhiteSpace(message.ActionPayload))
            {
                // "Allow once": the card authorizes one run. Its first click consumes the approval, and the button stays
                // disabled afterwards, so the same goal cannot be run again from this card.
                bool spent = new ComputerUseApprovalState(message.ActionConsumed).Consumed;
                var runAgent = new Button
                {
                    Content = spent ? ComputerUseApprovalState.UsedLabel
                        : string.IsNullOrWhiteSpace(message.ActionLabel) ? "Let PaRuX control the screen" : message.ActionLabel,
                    IsEnabled = !spent,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(12, 4, 12, 4)
                };
                CancellationTokenSource? agentCts = null;
                runAgent.Click += async (s, e) =>
                {
                    if (agentCts != null)
                    {
                        // A run is already in progress, so this click stops it. It is not a new approval.
                        agentCts.Cancel();
                        return;
                    }

                    // The approval is spent only once the run holds the shared mouse and keyboard and Gaming Mode is off. A refused
                    // attempt leaves the card usable and runs nothing. The spend is persisted before the run starts, so neither a
                    // second click nor a restart can replay it.
                    ComputerUseApprovalAdmission.Refusal refusal = await ComputerUseApprovalAdmission.RunOnceAsync(
                        message.ActionConsumed,
                        message.ActionPayload,
                        () => ComputerUseRunGate.Shared.TryEnter(),
                        () => GamingManager.Current.IsActive,
                        () =>
                        {
                            message.ActionConsumed = true;
                            ChatSession.Current.Persist();
                        },
                        async gateLease =>
                        {
                            agentCts = new CancellationTokenSource();
                            Guid computerUseRegistrationId = LocalAgentCancellationRegistry.Register(agentCts);
                            runAgent.Content = "Stop";
                            ChatMessage live = ChatSession.Current.AppendLiveAssistantMessage();
                            var stepLog = new System.Text.StringBuilder();
                            ScrollToBottom(true);
                            try
                            {
                                ComputerUseAgentResult result = await ComputerUseAgentService.RunAsync(
                                    message.ActionPayload,
                                    line =>
                                    {
                                        _dispatcher.TryEnqueue(() =>
                                        {
                                            if (stepLog.Length > 0) stepLog.Append('\n');
                                            stepLog.Append("- ").Append(line);
                                            live.Content = stepLog.ToString();
                                            ChatSession.Current.NotifyMessageUpdated(live);
                                        });
                                    },
                                    agentCts.Token,
                                    heldLease: gateLease);

                                stepLog.Append("\n\n**").Append(result.Cancelled ? "Stopped" : result.GoalReported ? "Done" : "Stopped")
                                    .Append(":** ").Append(result.Summary);
                                live.Content = stepLog.ToString();
                            }
                            catch (Exception ex)
                            {
                                Log.Write("Computer Use agent failed: " + ex);
                                live.Content = (stepLog.Length > 0 ? stepLog.ToString() + "\n\n" : string.Empty)
                                    + "**Failed:** " + ex.Message;
                                live.IsError = stepLog.Length == 0;
                            }
                            finally
                            {
                                live.IsStreaming = false;
                                ChatSession.Current.NotifyMessageUpdated(live);
                                ChatSession.Current.Persist();
                                LocalAgentCancellationRegistry.Unregister(computerUseRegistrationId);
                                agentCts.Dispose();
                                agentCts = null;
                                runAgent.Content = ComputerUseApprovalState.UsedLabel;
                                runAgent.IsEnabled = false;
                            }
                        });

                    if (refusal == ComputerUseApprovalAdmission.Refusal.GateBusy)
                    {
                        ShowTransientStatus("Another Computer Use task is controlling the mouse and keyboard. This approval was not used, so try again when it finishes.");
                    }
                    else if (refusal == ComputerUseApprovalAdmission.Refusal.GamingActive)
                    {
                        ShowTransientStatus("Gaming Mode is active, so Computer Use did not start. This approval was not used.");
                    }
                    else if (refusal == ComputerUseApprovalAdmission.Refusal.Unavailable)
                    {
                        runAgent.IsEnabled = false;
                        runAgent.Content = ComputerUseApprovalState.UsedLabel;
                    }
                };
                actions.Children.Add(runAgent);
            }

            ChatAttachment? image = message.Attachments == null
                ? null
                : message.Attachments.FirstOrDefault(a =>
                    a.MediaType != null && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(a.LocalPath));
            if (image != null)
            {
                Button view = BuildActionButton("\uE890", "Open image viewer");
                view.Click += (s, e) => { _ = ShowImageViewerAsync(image); };
                actions.Children.Add(view);

                Button save = BuildActionButton("\uE74E", "Save image as");
                save.Click += (s, e) => { _ = SaveMediaAsAsync(image); };
                actions.Children.Add(save);

                Button folder = BuildActionButton("\uE8B7", "Open containing folder");
                folder.Click += (s, e) => { _ = OpenMediaFolderAsync(image); };
                actions.Children.Add(folder);
            }

            ChatAttachment? video = message.Attachments == null
                ? null
                : message.Attachments.FirstOrDefault(a =>
                    a.MediaType != null && a.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(a.LocalPath));
            if (video != null)
            {
                Button saveVideo = BuildActionButton("\uE74E", "Save video as");
                saveVideo.Click += (s, e) => { _ = SaveMediaAsAsync(video); };
                actions.Children.Add(saveVideo);
                Button openVideo = BuildActionButton("\uE768", "Open video externally");
                openVideo.Click += (s, e) => { _ = OpenMediaExternallyAsync(video); };
                actions.Children.Add(openVideo);
                Button videoFolder = BuildActionButton("\uE8B7", "Open containing folder");
                videoFolder.Click += (s, e) => { _ = OpenMediaFolderAsync(video); };
                actions.Children.Add(videoFolder);
            }

            return actions;
        }

        private async Task ShowImageViewerAsync(ChatAttachment initial)
        {
            if (initial == null || !File.Exists(initial.LocalPath))
            {
                ShowTransientStatus("The image file is no longer available.");
                return;
            }

            List<ChatAttachment> images = ChatSession.Current.Conversation.Messages
                .Where(m => m.Attachments != null)
                .SelectMany(m => m.Attachments)
                .Where(a => a.MediaType != null
                    && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(a.LocalPath))
                .ToList();
            int index = Math.Max(0, images.FindIndex(a => a.Id == initial.Id));

            var viewerImage = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var scroll = new ScrollViewer
            {
                Content = viewerImage,
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = 0.25f,
                MaxZoomFactor = 8.0f,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 560
            };
            var caption = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.72,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var navigationRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 6
            };
            var fileRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 6
            };
            Button previous = new Button { Content = "Previous" };
            Button next = new Button { Content = "Next" };
            Button fit = new Button { Content = "Fit" };
            Button actual = new Button { Content = "100%" };
            Button save = new Button { Content = "Save as…" };
            Button external = new Button { Content = "Open" };
            Button folder = new Button { Content = "Folder" };
            navigationRow.Children.Add(previous);
            navigationRow.Children.Add(next);
            navigationRow.Children.Add(fit);
            navigationRow.Children.Add(actual);
            fileRow.Children.Add(save);
            fileRow.Children.Add(external);
            fileRow.Children.Add(folder);
            toolbar.Children.Add(navigationRow);
            toolbar.Children.Add(fileRow);

            // ContentDialog is intentionally bounded so every command remains
            // reachable on high-DPI and smaller displays; the ScrollViewer
            // provides zoom/pan for inspecting the full-resolution image.
            var content = new StackPanel { Width = 500 };
            content.Children.Add(toolbar);
            content.Children.Add(scroll);
            content.Children.Add(caption);

            Action refresh = () =>
            {
                ChatAttachment current = images[index];
                viewerImage.Source = new BitmapImage(new Uri(current.LocalPath));
                viewerImage.Stretch = Stretch.Uniform;
                scroll.ChangeView(null, null, 1.0f, true);
                caption.Text = current.FileName + "   ·   " + (index + 1) + " / " + images.Count;
                previous.IsEnabled = index > 0;
                next.IsEnabled = index < images.Count - 1;
            };
            previous.Click += (s, e) => { if (index > 0) { index--; refresh(); } };
            next.Click += (s, e) => { if (index < images.Count - 1) { index++; refresh(); } };
            fit.Click += (s, e) =>
            {
                viewerImage.Stretch = Stretch.Uniform;
                scroll.ChangeView(0, 0, 1.0f, false);
            };
            actual.Click += (s, e) =>
            {
                viewerImage.Stretch = Stretch.None;
                scroll.ChangeView(0, 0, 1.0f, false);
            };
            save.Click += async (s, e) => await SaveMediaAsAsync(images[index]);
            external.Click += async (s, e) => await OpenMediaExternallyAsync(images[index]);
            folder.Click += async (s, e) => await OpenMediaFolderAsync(images[index]);
            refresh();

            var dialog = new ContentDialog
            {
                Title = "Image viewer",
                Content = content,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            try { await dialog.ShowAsync(); }
            catch (Exception ex)
            {
                Log.Write("Image viewer failed: " + ex.Message);
                ShowTransientStatus("Couldn't open the image viewer.");
            }
        }

        private async Task SaveMediaAsAsync(ChatAttachment image)
        {
            try
            {
                StorageFile source = await StorageFile.GetFileFromPathAsync(image.LocalPath);
                var picker = new FileSavePicker
                {
                    SuggestedFileName = Path.GetFileNameWithoutExtension(image.FileName)
                };
                string extension = Path.GetExtension(image.FileName);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
                picker.FileTypeChoices.Add(image.MediaType != null && image.MediaType.StartsWith("video/")
                    ? "Video" : "Image", new List<string> { extension });
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                StorageFile destination = await picker.PickSaveFileAsync();
                if (destination == null) return;
                await source.CopyAndReplaceAsync(destination);
                ShowTransientStatus("Media saved.");
            }
            catch (Exception ex)
            {
                Log.Write("Save media failed: " + ex.Message);
                ShowTransientStatus("Couldn't save the media file.");
            }
        }

        private async Task OpenMediaExternallyAsync(ChatAttachment image)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(image.LocalPath);
                if (!await Launcher.LaunchFileAsync(file))
                    ShowTransientStatus("No app is available to open this image.");
            }
            catch (Exception ex)
            {
                Log.Write("Open image failed: " + ex.Message);
                ShowTransientStatus("Couldn't open the image.");
            }
        }

        private async Task OpenMediaFolderAsync(ChatAttachment image)
        {
            try
            {
                string? folder = Path.GetDirectoryName(image.LocalPath);
                if (string.IsNullOrWhiteSpace(folder) || !await Launcher.LaunchFolderPathAsync(folder))
                    ShowTransientStatus("Couldn't open the image folder.");
            }
            catch (Exception ex)
            {
                Log.Write("Open image folder failed: " + ex.Message);
                ShowTransientStatus("Couldn't open the image folder.");
            }
        }

        /// <summary>
        /// A quiet 32x28 icon button. Properties are set inline rather than through
        /// a Style, so the control keeps its stock WinUI template and therefore its
        /// hover / pressed / focus visuals.
        /// </summary>
        private Button BuildActionButton(string glyph, string tooltip)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 13 },
                Background = null,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Width = 32,
                Height = 28
            };

            Brush? quiet = LocalBrush("PaRuXDotOffBrush");
            if (quiet != null) button.Foreground = quiet;

            ToolTipService.SetToolTip(button, tooltip);
            AutomationProperties.SetName(button, tooltip);
            return button;
        }

        private void ScrollToBottom(bool force)
        {
            try
            {
                if (!force)
                {
                    double distance = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset;
                    // Leave the user alone if they've scrolled up to read.
                    if (distance > 140) return;
                }
                MessagesScroll.UpdateLayout();
                MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, true);
            }
            catch (Exception ex)
            {
                Log.Write("ScrollToBottom failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Composer
        // ------------------------------------------------------------------

        private async void Attach_Click(object sender, RoutedEventArgs e)
        {
            if (ChatSession.Current.IsBusy) return;
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".webm");
                picker.FileTypeFilter.Add(".mov");
                picker.FileTypeFilter.Add(".m4v");
                foreach (string extension in new[] { ".pdf", ".docx", ".pptx", ".xls", ".xlsx", ".xlsb", ".csv", ".tsv", ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".log", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".sql", ".html", ".css" })
                    picker.FileTypeFilter.Add(extension);
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));

                IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                int maximumImages = Math.Max(1, SettingsStore.Current.Data.MaxImagesPerMessage);
                int imageCount = _pendingAttachments.Count(a => a.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
                int videoCount = _pendingAttachments.Count(a => a.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);
                int documentCount = _pendingAttachments.Count(a => a.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                    && a.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true
                    && !ChatAudioAttachmentPolicy.IsAudioMediaType(a.MediaType));
                long imageMaximumBytes = Math.Max(1, SettingsStore.Current.Data.MaxImageMegabytes) * 1024L * 1024L;
                long videoMaximumBytes = Math.Max(1, SettingsStore.Current.Data.MaxVideoMegabytes) * 1024L * 1024L;
                long documentMaximumBytes = Math.Max(1, SettingsStore.Current.Data.MaxDocumentMegabytes) * 1024L * 1024L;
                StorageFolder destination = await StorageFolder.GetFolderFromPathAsync(AppPaths.AttachmentsDir);

                foreach (StorageFile file in files)
                {
                    Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
                    string extension = Path.GetExtension(file.Name).ToLowerInvariant();
                    bool isVideo = extension == ".mp4" || extension == ".webm" || extension == ".mov" || extension == ".m4v";
                    bool isDocument = extension == ".pdf" || DocumentTextExtractor.CanExtract(file.Path);
                    long maximumBytes = isVideo ? videoMaximumBytes : (isDocument ? documentMaximumBytes : imageMaximumBytes);
                    if (isVideo && videoCount >= 1)
                    {
                        ShowTransientStatus("Attach one video per message.");
                        continue;
                    }
                    if (!isVideo && !isDocument && imageCount >= maximumImages)
                    {
                        ShowTransientStatus("The configured image count limit has been reached.");
                        continue;
                    }
                    if (isDocument && documentCount >= Math.Max(1, SettingsStore.Current.Data.MaxDocumentsPerMessage))
                    {
                        ShowTransientStatus("The configured document count limit has been reached.");
                        continue;
                    }
                    if ((long)properties.Size > maximumBytes)
                    {
                        ShowTransientStatus(file.Name + " is larger than the configured media limit.");
                        continue;
                    }

                    try { StorageQuotaService.EnsureCapacity((long)properties.Size); }
                    catch (IOException ex) { ShowTransientStatus(ex.Message); continue; }

                    StorageFile stored = await file.CopyAsync(
                        destination, Guid.NewGuid().ToString("N") + extension, NameCollisionOption.FailIfExists);
                    _pendingAttachments.Add(new ChatAttachment
                    {
                        FileName = file.Name,
                        LocalPath = stored.Path,
                        MediaType = MediaTypeFor(extension),
                        SizeBytes = (long)properties.Size
                    });
                    if (isVideo) videoCount++; else if (isDocument) documentCount++; else imageCount++;
                }

                RefreshAttachmentPanel();
                UpdateRouteLabel();
            }
            catch (Exception ex)
            {
                Log.Write("Attach media failed: " + ex);
                ShowTransientStatus("Couldn't attach that media: " + ex.Message);
            }
        }

        private async void AttachAudio_Click(object sender, RoutedEventArgs e)
        {
            if (ChatSession.Current.IsBusy) return;
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.MusicLibrary,
                    ViewMode = PickerViewMode.List
                };
                foreach (string extension in ChatAudioAttachmentPolicy.Extensions) picker.FileTypeFilter.Add(extension);
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null) return;

                Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
                int attachedAudio = _pendingAttachments.Count(a => ChatAudioAttachmentPolicy.IsAudioMediaType(a.MediaType));
                string? problem = ChatAudioAttachmentPolicy.Validate(file.Name, (long)properties.Size, attachedAudio);
                if (problem != null) { ShowTransientStatus(problem); return; }

                try { StorageQuotaService.EnsureCapacity((long)properties.Size); }
                catch (IOException ex) { ShowTransientStatus(ex.Message); return; }

                string extensionName = Path.GetExtension(file.Name).ToLowerInvariant();
                StorageFolder destination = await StorageFolder.GetFolderFromPathAsync(AppPaths.AttachmentsDir);
                StorageFile stored = await file.CopyAsync(
                    destination, Guid.NewGuid().ToString("N") + extensionName, NameCollisionOption.FailIfExists);

                TimeSpan? duration = null;
                if (extensionName == ".wav")
                {
                    try
                    {
                        using var fs = new FileStream(stored.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        duration = ChatAudioAttachmentPolicy.TryReadWavDuration(fs);
                    }
                    catch (IOException) { }
                }

                var attachment = new ChatAttachment
                {
                    FileName = file.Name,
                    LocalPath = stored.Path,
                    MediaType = ChatAudioAttachmentPolicy.MediaTypeFor(extensionName)!,
                    SizeBytes = (long)properties.Size
                };
                _audioDurations[attachment.Id] = duration;
                _pendingAttachments.Add(attachment);
                RefreshAttachmentPanel();
                UpdateRouteLabel();
            }
            catch (Exception ex)
            {
                Log.Write("Attach audio failed: " + ex);
                ShowTransientStatus("Couldn't attach that audio: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Voice input (microphone) and Read aloud. Policy lives in VoiceRecordingSession,
        // WavSilenceDetector, SpeechTextPreparer and VoiceMessages; this is only UI wiring.
        // ------------------------------------------------------------------
        private readonly VoiceRecordingSession _voice = new VoiceRecordingSession();
        private MicrophoneRecorder? _recorder;
        private DispatcherQueueTimer? _voiceTimer;
        private bool _voiceStarting;

        private readonly WavPlayer _wavPlayer = new WavPlayer();
        private CancellationTokenSource? _readAloudCts;
        private Button? _readAloudButton;
        private bool _readAloudWired;

        private async void Mic_Click(object sender, RoutedEventArgs e)
        {
            if (_voiceStarting) return;
            if (_voiceConversation != null) { ShowTransientStatus("Voice mode is on. Turn it off (Esc) to dictate a single message."); return; }
            switch (_voice.Phase)
            {
                case VoiceRecordingPhase.Recording:
                    await StopVoiceAndTranscribeAsync(limitReached: false);
                    return;
                case VoiceRecordingPhase.Transcribing:
                    ShowTransientStatus("Still transcribing the previous recording.");
                    return;
            }

            if (GamingManager.Current.IsActive) { ShowTransientStatus(VoiceMessages.GamingMode("voice input")); return; }

            _voiceStarting = true;
            try
            {
                try
                {
                    SpeechEngineStatus engine = await SpeechClient.Current.GetStatusAsync(CancellationToken.None);
                    if (!engine.SttInstalled) { ShowTransientStatus(VoiceMessages.SttNotInstalled()); return; }
                }
                catch (Exception ex)
                {
                    Log.Write("Voice input engine check failed: " + ex.Message);
                    ShowTransientStatus("Couldn't check the local speech engine: " + ex.Message);
                    return;
                }

                var recorder = new MicrophoneRecorder();
                try { await recorder.StartAsync(); }
                catch (VoiceCaptureException ex) { ShowTransientStatus(ex.Message); return; }

                _recorder = recorder;
                WarmSpeechEngineOnce();
                _voice.TryStart(DateTime.UtcNow);
                SetVoiceUi(recording: true);
                _voiceTimer ??= CreateVoiceTimer();
                _voiceTimer.Start();
            }
            finally { _voiceStarting = false; }
        }

        private static bool _sttWarmed;
        private static Task? _sttWarmTask;

        /// <summary>Loads the speech model while the user is still talking, so the first transcription is not a cold start.</summary>
        private static void WarmSpeechEngineOnce()
        {
            if (_sttWarmed) return;
            _sttWarmed = true;
            _sttWarmTask = Task.Run(async () =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                    await SpeechClient.Current.TranscribeAsync(
                        Convert.ToBase64String(WavSilenceDetector.BuildSilence(500)), "warmup.wav", cts.Token);
                    Log.Write("Voice: speech engine warmed in " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex) { _sttWarmed = false; Log.Write("Voice: warm-up skipped: " + ex.Message); }
            });
        }

        private DispatcherQueueTimer CreateVoiceTimer()
        {
            DispatcherQueueTimer timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.Tick += async (s, e) =>
            {
                if (_voice.Phase != VoiceRecordingPhase.Recording) { timer.Stop(); return; }
                DateTime now = DateTime.UtcNow;
                VoiceIndicator.Text = "● " + VoiceRecordingSession.FormatElapsed(_voice.Elapsed(now));
                if (_voice.IsLimitReached(now))
                {
                    timer.Stop();
                    await StopVoiceAndTranscribeAsync(limitReached: true);
                }
            };
            return timer;
        }

        private void SetVoiceUi(bool recording)
        {
            VoiceIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording) VoiceIndicator.Text = "● " + VoiceRecordingSession.FormatElapsed(TimeSpan.Zero);
            MicIcon.Glyph = recording ? "" : "";
            MicButton.Foreground = recording
                ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                : LocalBrush("PaRuXTextTertiaryBrush");
            ToolTipService.SetToolTip(MicButton, recording
                ? "Stop recording and transcribe (Esc cancels)"
                : "Dictate: click to start recording, click again to stop (Esc cancels, 2 min max)");
            AutomationProperties.SetName(MicButton, recording ? "Stop recording" : "Record voice message");
        }

        private async Task StopVoiceAndTranscribeAsync(bool limitReached)
        {
            VoiceStopOutcome outcome = _voice.Stop(DateTime.UtcNow);
            _voiceTimer?.Stop();
            MicrophoneRecorder? recorder = _recorder;
            _recorder = null;
            SetVoiceUi(recording: false);
            if (recorder == null) { _voice.Finish(); return; }

            if (outcome != VoiceStopOutcome.Accepted)
            {
                await recorder.CancelAsync();
                if (outcome == VoiceStopOutcome.TooShort) ShowTransientStatus(VoiceMessages.TooShort());
                return;
            }

            try
            {
                if (limitReached) ShowTransientStatus(VoiceMessages.LimitReached());
                byte[] wav = await recorder.StopAsync();
                WavAnalysis analysis = WavSilenceDetector.Analyze(wav);
                if (!analysis.Valid) { ShowTransientStatus(VoiceMessages.InvalidAudio(analysis.Problem)); return; }
                if (analysis.IsSilent) { ShowTransientStatus(VoiceMessages.Silent()); return; }

                ShowPersistentVoiceStatus("Transcribing locally… (the first use loads the speech model and can take about a minute)");
                string header = BitConverter.ToString(wav, 0, Math.Min(48, wav.Length));
                Log.Write("Voice: " + wav.Length + " bytes, " + analysis.DurationSeconds.ToString("0.00")
                    + " s, peak " + analysis.Peak.ToString("0.0000") + ", rms " + analysis.Rms.ToString("0.0000")
                    + ", variation " + analysis.Variation.ToString("0.0") + ", steadyNoise " + analysis.LooksLikeSteadyNoise + ", header " + header);
                // Cold start: the model load (about 100 s while the LLM also loads) is a separate, bounded phase,
                // so the transcription itself keeps its own 75 s limit.
                Task? warming = _sttWarmTask;
                if (warming != null && !warming.IsCompleted)
                {
                    ShowPersistentVoiceStatus("Loading the local speech model (first use only, up to two minutes)…");
                    await Task.WhenAny(warming, Task.Delay(VoiceMessages.ModelLoadWait));
                    ShowPersistentVoiceStatus("Transcribing locally…");
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string text;
                try
                {
                    using var timeout = new CancellationTokenSource(VoiceMessages.TranscribeTimeout);
                    TranscribeResponse result = await SpeechClient.Current.TranscribeAsync(
                        Convert.ToBase64String(wav), "voice-message.wav", timeout.Token);
                    text = (result.Text ?? string.Empty).Trim();
                    Log.Write("Voice transcription ok in " + sw.ElapsedMilliseconds + " ms: " + text.Length + " chars");
                }
                catch (OperationCanceledException)
                {
                    Log.Write("Voice transcription timed out after " + sw.ElapsedMilliseconds + " ms");
                    ShowPersistentVoiceStatus(VoiceMessages.TranscribeTimedOut());
                    return;
                }
                if (text.Length == 0) { ShowPersistentVoiceStatus(VoiceMessages.NoSpeechRecognized(analysis.LooksLikeSteadyNoise)); return; }

                string existing = InputBox.Text ?? string.Empty;
                InputBox.Text = existing.Length == 0 || char.IsWhiteSpace(existing[existing.Length - 1])
                    ? existing + text
                    : existing + " " + text;
                InputBox.SelectionStart = InputBox.Text.Length;
                InputBox.Focus(FocusState.Programmatic);

                if (SettingsStore.Current.Data.SendVoiceMessagesAutomatically)
                    Send();
                else
                    ShowTransientStatus("Voice transcribed. Review the text and press Enter to send.");
            }
            catch (Exception ex)
            {
                Log.Write("Voice transcription failed: " + ex.GetType().Name + ": " + ex.Message);
                if (GamingManager.Current.IsActive) ShowTransientStatus(VoiceMessages.GamingMode("voice input"));
                else if (ex.Message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0)
                    ShowPersistentVoiceStatus(VoiceMessages.SttNotInstalled());
                else ShowPersistentVoiceStatus("Transcription failed: " + ex.Message);
            }
            finally { _voice.Finish(); }
        }

        /// <summary>Voice outcomes stay visible longer than the usual transient status so a failure is never missed.</summary>
        private void ShowPersistentVoiceStatus(string text)
        {
            ShowTransientStatus(text);
            _dispatcher.TryEnqueue(() =>
            {
                _statusTimer.Stop();
                _statusTimer.Interval = TimeSpan.FromSeconds(20);
                _statusTimer.Start();
            });
        }

        private async void CancelVoiceRecording()
        {
            if (!_voice.Cancel()) return;
            _voiceTimer?.Stop();
            MicrophoneRecorder? recorder = _recorder;
            _recorder = null;
            SetVoiceUi(recording: false);
            if (recorder != null) await recorder.CancelAsync();
            ShowTransientStatus("Recording cancelled.");
        }

        private void OnChatPreviewKeyDownVoice(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && _voiceConversation != null)
            {
                e.Handled = true;
                _voiceConversation.Stop();
                return;
            }
            if (e.Key == VirtualKey.Escape && _voice.Phase == VoiceRecordingPhase.Recording)
            {
                e.Handled = true;
                CancelVoiceRecording();
            }
        }

        private void OnChatUnloadedStopVoice(object sender, RoutedEventArgs e)
        {
            try
            {
                _voiceConversation?.Stop();
                if (_voice.Phase == VoiceRecordingPhase.Recording) CancelVoiceRecording();
                StopReadAloud();
            }
            catch (Exception ex) { Log.Write("Voice cleanup failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Voice mode (hands-free conversation). All policy lives in VoiceConversationController.
        // ------------------------------------------------------------------
        private VoiceConversationController? _voiceConversation;

        private async void VoiceMode_Click(object sender, RoutedEventArgs e)
        {
            VoiceConversationController? active = _voiceConversation;
            if (active != null)
            {
                // Press while the reply is read aloud (or while paused) = barge-in / resume; otherwise leave the mode.
                if (!active.BargeIn()) active.Stop();
                return;
            }
            if (_voiceStarting || _voice.Phase != VoiceRecordingPhase.Idle) { ShowTransientStatus("Finish the current voice recording first."); return; }
            if (GamingManager.Current.IsActive) { ShowTransientStatus(VoiceMessages.GamingMode("voice conversation")); return; }
            if (ChatSession.Current.IsBusy) { ShowTransientStatus("Wait for the current reply to finish, then turn Voice mode on."); return; }

            StopReadAloud();
            var controller = new VoiceConversationController(
                new SystemVoiceClock(), new WinRtVoiceMicrophone(), new LocalVoiceTranscriber(),
                new ChatSessionVoiceChat(), new KokoroVoiceSpeaker(), new LocalVoiceEnvironment());
            controller.Changed += (state, text) => _dispatcher.TryEnqueue(() => ApplyVoiceModeUi(state, text));
            _voiceConversation = controller;
            ApplyVoiceModeUi(VoiceConversationState.Starting, "Starting voice conversation…");
            VoiceConversationEnd end;
            try { end = await controller.RunAsync(); }
            catch (Exception ex)
            {
                Log.Write("Voice conversation crashed: " + ex);
                end = new VoiceConversationEnd(VoiceConversationEndReason.Error, VoiceConversationMessages.Unexpected(ex.Message));
            }
            _voiceConversation = null;
            ApplyVoiceModeUi(VoiceConversationState.Off, string.Empty);
            Log.Write("Voice conversation ended: " + end.Reason + " - " + end.Message);
            if (end.Reason != VoiceConversationEndReason.UserStopped) ShowPersistentVoiceStatus(end.Message);
            else ShowTransientStatus(end.Message);
        }

        private void ApplyVoiceModeUi(VoiceConversationState state, string text)
        {
            bool on = state != VoiceConversationState.Off;
            VoiceModeStatus.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            string label = VoiceConversationController.StateLabel(state);
            VoiceModeStatus.Text = on ? label + (text.StartsWith(label, StringComparison.OrdinalIgnoreCase) ? text.Substring(label.Length) : " — " + text) : string.Empty;
            ToolTipService.SetToolTip(VoiceModeStatus, on ? text : null);
            VoiceModeIcon.Glyph = on ? "" : "";
            VoiceModeButton.Foreground = on
                ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                : LocalBrush("PaRuXTextTertiaryBrush");
            string tip = !on ? "Voice mode: hands-free conversation. PaRuX listens, answers aloud, then listens again (Esc leaves)"
                : state == VoiceConversationState.Speaking ? "Interrupt the reply and speak (Esc leaves Voice mode)"
                : state == VoiceConversationState.Paused ? "Resume listening (Esc leaves Voice mode)"
                : "Turn Voice mode off (Esc)";
            ToolTipService.SetToolTip(VoiceModeButton, tip);
            AutomationProperties.SetName(VoiceModeButton, !on ? "Start voice conversation"
                : state == VoiceConversationState.Speaking ? "Interrupt and speak"
                : state == VoiceConversationState.Paused ? "Resume listening" : "Stop voice conversation");
            AutomationProperties.SetHelpText(VoiceModeStatus, VoiceModeStatus.Text);
        }

        private void ConfigureReadAloudButton(Button button, ChatMessage message)
        {
            AutomationProperties.SetAutomationId(button, "Chat_ReadAloud_Button");
            SetReadAloudButtonState(button, active: false);
            button.Click += async (s, e) => await ToggleReadAloudAsync(button, message);
        }

        private static void SetReadAloudButtonState(Button button, bool active)
        {
            if (button.Content is FontIcon icon) icon.Glyph = active ? "" : "";
            string label = active ? "Stop reading" : "Read aloud";
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, label);
        }

        private void StopReadAloud()
        {
            try { _readAloudCts?.Cancel(); } catch { }
            _readAloudCts = null;
            _wavPlayer.Stop();
            if (_readAloudButton != null) SetReadAloudButtonState(_readAloudButton, active: false);
            _readAloudButton = null;
        }

        private async Task ToggleReadAloudAsync(Button button, ChatMessage message)
        {
            if (ReferenceEquals(_readAloudButton, button)) { StopReadAloud(); return; }
            StopReadAloud();

            if (GamingManager.Current.IsActive) { ShowTransientStatus(VoiceMessages.GamingMode("Read aloud")); return; }
            string text = SpeechTextPreparer.Prepare(message.Content);
            if (text.Length == 0) { ShowTransientStatus(VoiceMessages.NothingToRead()); return; }

            var cts = new CancellationTokenSource();
            _readAloudCts = cts;
            _readAloudButton = button;
            SetReadAloudButtonState(button, active: true);
            if (!_readAloudWired)
            {
                _readAloudWired = true;
                _wavPlayer.Ended += () => _dispatcher.TryEnqueue(StopReadAloud);
            }

            try
            {
                SpeechEngineStatus engine = await SpeechClient.Current.GetStatusAsync(cts.Token);
                if (!engine.TtsInstalled) { ShowTransientStatus(VoiceMessages.TtsNotInstalled()); StopReadAloudIf(cts); return; }
                ShowTransientStatus("Preparing speech locally…");
                byte[] wav = await SpeechClient.Current.SynthesizeBytesAsync(text, null, cts.Token);
                if (cts.IsCancellationRequested) return;
                await _wavPlayer.PlayAsync(wav);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("Read aloud failed: " + ex.GetType().Name + ": " + ex.Message);
                if (ex.Message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0)
                    ShowTransientStatus(VoiceMessages.TtsNotInstalled());
                else if (GamingManager.Current.IsActive) ShowTransientStatus(VoiceMessages.GamingMode("Read aloud"));
                else ShowTransientStatus("Couldn't read this message aloud: " + ex.Message);
                StopReadAloudIf(cts);
            }
        }

        /// <summary>Resets the Read aloud state only if this attempt is still the current one.</summary>
        private void StopReadAloudIf(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_readAloudCts, cts)) StopReadAloud();
        }

        private readonly Dictionary<string, TimeSpan?> _audioDurations = new Dictionary<string, TimeSpan?>();

        /// <summary>Shows the Computer Use consent dialog. Cancel is the default button; only an explicit Allow once returns true.</summary>
        private Task<bool> ShowComputerUseConsentAsync(string task)
        {
            var completion = new TaskCompletionSource<bool>();
            if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var body = new TextBlock { Text = ComputerUseConsentText.Body(task), TextWrapping = TextWrapping.Wrap };
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = ComputerUseConsentText.Title,
                        Content = body,
                        PrimaryButtonText = ComputerUseConsentText.Confirm,
                        CloseButtonText = ComputerUseConsentText.Cancel,
                        DefaultButton = ContentDialogButton.Close
                    };
                    AutomationProperties.SetAutomationId(dialog, "Chat_ComputerUse_Consent_Dialog");
                    // Template parts are not reachable via FindName; walk the realized visual tree once the dialog is open.
                    dialog.Opened += (s, e) =>
                    {
                        Button? primary = FindDialogButton(dialog, ComputerUseConsentText.Confirm);
                        if (primary != null)
                            AutomationProperties.SetAutomationId(primary, "Chat_ComputerUse_Consent_Confirm_Button");
                    };
                    ContentDialogResult result = await dialog.ShowAsync();
                    completion.TrySetResult(result == ContentDialogResult.Primary);
                }
                catch (Exception ex)
                {
                    Log.Write("Computer Use consent dialog failed: " + ex.Message);
                    completion.TrySetResult(false);
                }
            }))
            {
                completion.TrySetResult(false);
            }
            return completion.Task;
        }

        private static Button? FindDialogButton(DependencyObject root, string label)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Button b && string.Equals(b.Content as string, label, StringComparison.Ordinal))
                    return b;
                Button? nested = FindDialogButton(child, label);
                if (nested != null) return nested;
            }
            return null;
        }

        private static string MediaTypeFor(string extension)
        {
            switch (extension)
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".m4v": return "video/x-m4v";
                case ".pdf": return "application/pdf";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xlsb": return "application/vnd.ms-excel.sheet.binary.macroenabled.12";
                case ".csv": return "text/csv";
                case ".tsv": return "text/tab-separated-values";
                case ".json": return "application/json";
                case ".xml": return "application/xml";
                case ".md": return "text/markdown";
                case ".txt": return "text/plain";
                case ".yaml":
                case ".yml": return "application/yaml";
                case ".log":
                case ".cs":
                case ".js":
                case ".ts":
                case ".tsx":
                case ".jsx":
                case ".py":
                case ".ps1":
                case ".sql":
                case ".html":
                case ".css": return "text/plain";
                default: return "application/octet-stream";
            }
        }

        private void RefreshAttachmentPanel()
        {
            AttachmentList.Children.Clear();
            foreach (ChatAttachment attachment in _pendingAttachments)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                bool isAudio = ChatAudioAttachmentPolicy.IsAudioMediaType(attachment.MediaType);
                TimeSpan? audioDuration = null;
                if (isAudio) _audioDurations.TryGetValue(attachment.Id, out audioDuration);
                row.Children.Add(new FontIcon { Glyph = isAudio ? "\uE720" : "\uEB9F", FontSize = 13 });
                row.Children.Add(new TextBlock
                {
                    Text = isAudio ? ChatAudioAttachmentPolicy.ChipText(attachment.FileName, audioDuration) : attachment.FileName,
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var remove = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                    Tag = attachment,
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = null,
                    BorderThickness = new Thickness(0)
                };
                remove.Click += (s, e) =>
                {
                    var button = s as Button;
                    var selected = button == null ? null : button.Tag as ChatAttachment;
                    if (selected != null) _pendingAttachments.Remove(selected);
                    RefreshAttachmentPanel();
                    UpdateRouteLabel();
                };
                row.Children.Add(remove);

                var pill = new Border { Child = row };
                if (isAudio)
                {
                    AutomationProperties.SetAutomationId(pill, "Chat_Audio_Chip");
                    AutomationProperties.SetName(pill, "Audio: " + ChatAudioAttachmentPolicy.ChipText(attachment.FileName, audioDuration));
                }
                Style? style = LocalStyle("PaRuXQuietPillStyle");
                if (style != null) pill.Style = style;
                AttachmentList.Children.Add(pill);
            }
            AttachmentPanel.Visibility = _pendingAttachments.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;

            CoreVirtualKeyStates shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift);
            bool shiftDown = (shift & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (shiftDown) return;   // Shift+Enter inserts a newline

            e.Handled = true;
            Send();
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ChatDraftCoordinator.Current.SaveDraft(ChatSession.Current.Conversation?.Id, InputBox.Text);
            UpdateRouteLabel();
        }

        /// <summary>Brand-coloured focus ring on the composer card.</summary>
        private void InputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                Brush? focus = null;
                object value;
                if (Application.Current.Resources.TryGetValue("PaRuXFocusBrush", out value)) focus = value as Brush;
                if (focus != null) ComposerBorder.BorderBrush = focus;
                ComposerBorder.BorderThickness = new Thickness(2);
                ComposerBorder.Padding = new Thickness(7);
            }
            catch (Exception ex)
            {
                Log.Write("Composer focus visual failed: " + ex.Message);
            }
        }

        private void InputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                Brush? stroke = null;
                object value;
                if (Application.Current.Resources.TryGetValue("PaRuXStrokeStrongBrush", out value)) stroke = value as Brush;
                if (stroke != null) ComposerBorder.BorderBrush = stroke;
                ComposerBorder.BorderThickness = new Thickness(1);
                ComposerBorder.Padding = new Thickness(8);
            }
            catch (Exception ex)
            {
                Log.Write("Composer focus visual failed: " + ex.Message);
            }
        }

        /// <summary>Empty-state starter: prefills the composer, never sends.</summary>
        private void Suggestion_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            string? seed = button.Tag as string;
            if (string.IsNullOrEmpty(seed)) return;

            InputBox.Text = seed + " ";
            UpdateRouteLabel();

            try
            {
                InputBox.Focus(FocusState.Programmatic);
                InputBox.SelectionStart = InputBox.Text.Length;
            }
            catch (Exception ex)
            {
                Log.Write("Suggestion focus failed: " + ex.Message);
            }
        }

        private void UpdateRouteLabel()
        {
            string text = InputBox.Text ?? string.Empty;
            bool hasAudio = _pendingAttachments.Any(a => ChatAudioAttachmentPolicy.IsAudioMediaType(a.MediaType));
            string? modalityLabel = RouterService.ModalityLabel(text, hasAudio);
            ToolTipService.SetToolTip(RoutePill,
                ModalityIntent.ExpectedRouteText(RouterService.ModalityRole(text), hasAudio));
            if (modalityLabel != null && (hasAudio || RouterService.Classify(text) != RouteKind.ComputerUse))
            {
                SetRoutePreview(modalityLabel);
                return;
            }
            if (_pendingAttachments.Count > 0)
            {
                SetRoutePreview(RouterService.Label(RouterService.ClassifyWithAttachment(text)));
                return;
            }
            if (text.Trim().Length == 0)
            {
                SetRoutePreview(null);
                return;
            }
            RouteKind route = RouterService.Classify(text);
            SetRoutePreview(RouterService.Label(route));
        }

        /// <summary>Drives the route pill, so an empty label never leaves an empty chip.</summary>
        private void SetRoutePreview(string? label)
        {
            if (string.IsNullOrEmpty(label))
            {
                RouteLabel.Text = string.Empty;
                RoutePill.Visibility = Visibility.Collapsed;
                return;
            }
            RouteLabel.Text = label;
            RoutePill.Visibility = Visibility.Visible;
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Send();
        }

        private void Send()
        {
            string text = InputBox.Text ?? string.Empty;
            if (text.Trim().Length == 0 && _pendingAttachments.Count == 0) return;
            if (ChatSession.Current.IsBusy || _isSwitchingProfile) return;

            var attachments = new List<ChatAttachment>(_pendingAttachments);
            _pendingAttachments.Clear();
            ChatDraftCoordinator.Current.ClearDraft(ChatSession.Current.Conversation?.Id);
            InputBox.Text = string.Empty;
            RefreshAttachmentPanel();
            SetRoutePreview(null);

            (string? skillId, string stripped) slash = ParseSlashCommand(text.TrimStart());
            if (slash.skillId != null)
                _ = ChatSession.Current.SendAsync(slash.stripped, attachments, slash.skillId);
            else
                _ = ChatSession.Current.SendAsync(text, attachments);
        }

        /// <summary>
        /// Explicit user action: leaves Gaming Mode. Nothing is loaded here; the local model
        /// starts on the user's next message, so this is the only "resume" a user ever needs.
        /// </summary>
        private async void ReturnToAi_Click(object sender, RoutedEventArgs e)
        {
            ReturnToAiButton.IsEnabled = false;
            Log.Write("Return to AI requested by the user (GamingManager.ExitAsync)");
            try
            {
                // The result carries the server's reason for a refusal, so the message can say why Gaming Mode did not end.
                LocalAiController.ActionResult exit = await GamingManager.Current.ExitAsync(CancellationToken.None);
                if (!exit.Success)
                    ShowTransientStatus("Couldn't leave Gaming Mode: "
                        + (string.IsNullOrWhiteSpace(exit.Message) ? "PaRuX Core did not confirm." : exit.Message)
                        + " Try again from Settings > Gaming.");
            }
            catch (Exception ex) { Log.Write("Return to AI failed: " + ex.GetType().Name + ": " + ex.Message); }
            finally { ReturnToAiButton.IsEnabled = true; }
            await RefreshHeaderAsync();
        }

        private void ChatHelpButton_Click(object sender, RoutedEventArgs e)
        {
            _ = App.MainWin?.ShowInAppHelpDialogAsync();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            ChatSession.Current.Cancel();
        }

        // ------------------------------------------------------------------
        // Status strip
        // ------------------------------------------------------------------

        private void ShowStatus(string text, bool spinning)
        {
            StatusText.Text = text;
            StatusRing.IsActive = spinning;
            StatusRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            StatusRing.IsActive = false;
        }

        /// <summary>Shows a short-lived note (tray action result, copy confirmation, ...).</summary>
        public void OnWorkspaceActivated()
        {
            try
            {
                string? currentId = ChatSession.Current.Conversation?.Id;
                if (ChatDraftCoordinator.Current.HasDraft(currentId) && InputBox != null && string.IsNullOrEmpty(InputBox.Text))
                {
                    InputBox.Text = ChatDraftCoordinator.Current.GetDraft(currentId);
                }
                InputBox?.Focus(FocusState.Programmatic);
                if (InputBox != null && !string.IsNullOrEmpty(InputBox.Text))
                {
                    InputBox.SelectionStart = InputBox.Text.Length;
                }
                RefreshSimpleModeRuntimeHealth();
            }
            catch (Exception ex)
            {
                Log.Write("ChatView.OnWorkspaceActivated failed: " + ex.Message);
            }
        }

        public void ShowTransientStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _dispatcher.TryEnqueue(() =>
            {
                ShowStatus(text, false);
                _statusTimer.Stop();
                _statusTimer.Interval = TimeSpan.FromSeconds(7);
                _statusTimer.Start();
            });
        }

        // ------------------------------------------------------------------
        // Profile toggle (AUTO / STANDARD / UNCENSORED)
        // ------------------------------------------------------------------

        private void SyncProfileToggles(string selected)
        {
            _suppressToggleEvents = true;
            AutoToggle.IsChecked = selected == "AUTO";
            StandardToggle.IsChecked = selected == "STANDARD";
            UncensoredToggle.IsChecked = selected == "UNCENSORED";
            _suppressToggleEvents = false;
        }

        private void ProfileToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents) return;

            var button = sender as ToggleButton;
            if (button == null) return;
            string? choice = button.Tag as string;
            if (choice == null) return;

            SyncProfileToggles(choice);

            if (choice == "AUTO")
            {
                // Phase 1 AUTO means "don't force a profile" - deliberately no runtime action call,
                // because the "Auto" action would stop the model rather than leave it be.
                SettingsStore.Current.Data.DefaultProfile = choice;
                SettingsStore.Current.Save();
                ShowTransientStatus("Auto: PaRuX will use whichever profile is already loaded.");
                return;
            }

            _ = SwitchProfileAsync(choice);
        }

        private async Task SwitchProfileAsync(string choice)
        {
            _isSwitchingProfile = true;
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            ShowStatus(choice == "UNCENSORED"
                ? "Switching to the Uncensored profile — the model is reloading, this takes a moment…"
                : "Switching to the Standard profile — the model is reloading, this takes a moment…", true);

            AutoToggle.IsEnabled = false;
            StandardToggle.IsEnabled = false;
            UncensoredToggle.IsEnabled = false;

            try
            {
                LocalAiState current = await LocalAiController.Current
                    .GetStateAsync(CancellationToken.None);
                if (current.Healthy && string.Equals(current.TextProfile, choice, StringComparison.Ordinal))
                {
                    SettingsStore.Current.Data.DefaultProfile = choice;
                    SettingsStore.Current.Save();
                    HideStatus();
                    ShowTransientStatus((choice == "UNCENSORED" ? "Uncensored" : "Standard")
                        + " is already loaded and ready.");
                    return;
                }

                string action = choice == "UNCENSORED" ? "Uncensored" : "Standard";
                LocalAiController.ActionResult result =
                    await Task.Run(() => LocalAiController.Current.RunActionAsync(action, CancellationToken.None));

                if (result.Success)
                {
                    // A requested profile becomes the persisted default only after Core proves it
                    // actually loaded. Previously a failed Uncensored switch left the toggle and
                    // future vision/Computer Use requests pinned to a model that never started.
                    SettingsStore.Current.Data.DefaultProfile = choice;
                    SettingsStore.Current.Save();
                    HideStatus();
                    ShowTransientStatus("Now running the " + (choice == "UNCENSORED" ? "Uncensored" : "Standard") + " profile.");
                }
                else
                {
                    HideStatus();
                    ShowTransientStatus(LocalAiController.FriendlyScriptError(result));
                    // The switch failed, so reflect what is actually loaded.
                    SyncProfileToggles(LocalAiController.Current.GetTextProfile());
                }
            }
            catch (Exception ex)
            {
                Log.Write("SwitchProfile failed: " + ex.Message);
                HideStatus();
                ShowTransientStatus("Couldn't switch profile: " + ex.Message);
                SyncProfileToggles(LocalAiController.Current.GetTextProfile());
            }
            finally
            {
                _isSwitchingProfile = false;
                InputBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                AutoToggle.IsEnabled = true;
                StandardToggle.IsEnabled = true;
                UncensoredToggle.IsEnabled = true;
                await RefreshHeaderAsync();
            }
        }

        /// <summary>Stage of the running native coding task, or null when none runs. Lets the header
        /// say what is happening before any model has been asked for anything (repository indexing).</summary>
        private static async Task<string?> RunningCodingTaskStageAsync()
        {
            try
            {
                AgentTaskListResponse tasks = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false);
                AgentTaskDto? running = tasks.Tasks.FirstOrDefault(task =>
                    string.Equals(task.Role, "coding", StringComparison.Ordinal)
                    && string.Equals(task.Status, AgentTaskStatus.Running, StringComparison.Ordinal));
                return running == null ? null : (running.Stage ?? string.Empty);
            }
            catch { return null; }
        }

        private async Task RefreshHeaderAsync()
        {
            int generation = ++_headerRefreshGeneration;
            try
            {
                LocalAiState state = await Task.Run(() =>
                    LocalAiController.Current.GetStateAsync(CancellationToken.None));

                // A newer refresh already started (and, quite possibly, already finished and
                // written the header) while this one was awaiting its health check - applying
                // this now-stale result would silently overwrite the correct, more recent state.
                if (generation != _headerRefreshGeneration) return;

                string? codingStage = await RunningCodingTaskStageAsync().ConfigureAwait(true);
                if (generation != _headerRefreshGeneration) return;
                ChatHeaderStatusPresenter.Header header = ChatHeaderStatusPresenter.Describe(state, codingStage);
                ProfileText.Text = header.Text;
                bool mediaAssistant = state.ActiveProfile?.Contains("media assistant", StringComparison.OrdinalIgnoreCase) == true;
                ToolTipService.SetToolTip(ProfileText, mediaAssistant
                    ? "Temporary 4B prompt assistant. Image and video pixels are generated by the selected diffusion checkpoint; your chat model restores after the media burst."
                    : "The model currently serving this chat profile.");

                GameBadge.Visibility = state.GameActive ? Visibility.Visible : Visibility.Collapsed;
                ReturnToAiButton.Visibility = state.GameActive ? Visibility.Visible : Visibility.Collapsed;
                _shownGameActive = state.GameActive;
                IncognitoBadge.Visibility = ChatSession.Current.Conversation != null && ChatSession.Current.Conversation.IsIncognito
                    ? Visibility.Visible : Visibility.Collapsed;

                // Colour the header dot to match: green loaded, amber gaming, grey idle.
                string dotKey = header.Dot switch
                {
                    ChatHeaderDot.Ok => "PaRuXDotOkBrush",
                    ChatHeaderDot.Warn => "PaRuXDotWarnBrush",
                    _ => "PaRuXDotOffBrush"
                };
                Brush? dot = LocalBrush(dotKey);
                if (dot != null) ProfileDot.Background = dot;

                // A state-file notification happens when the process starts, before llama.cpp's
                // /health endpoint is ready. Poll only during that short transition, then stop;
                // otherwise the header can remain on "Loading model…" indefinitely.
                if ((!state.Healthy && state.ServerProcessPresent && !state.GameActive) || codingStage != null)
                    _headerPollTimer.Start();
                else
                    _headerPollTimer.Stop();

                if (SimpleModeRuntimeHealthPanel != null && SimpleModeRuntimeHealthPanel.Visibility == Visibility.Visible)
                {
                    RefreshSimpleModeRuntimeHealth();
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshHeader failed: " + ex.Message);
            }
        }
    }
}
