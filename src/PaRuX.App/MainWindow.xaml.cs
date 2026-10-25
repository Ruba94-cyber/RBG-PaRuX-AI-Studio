using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using H.NotifyIcon.Core;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PaRuX
{
    public sealed partial class MainWindow : Window
    {
        private const int MaxVisibleRecentConversations = 50;

        private sealed class ActionCommand : ICommand
        {
            private readonly Action _execute;

            public ActionCommand(Action execute) { _execute = execute; }
            public bool CanExecute(object? parameter) { return true; }
            public void Execute(object? parameter) { _execute(); }
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private readonly DispatcherQueue _dispatcher;
        private List<Conversation> _allConversations = new List<Conversation>();
        private readonly HashSet<string> _runningCodeTaskIds = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _activeIndicatorTimer;
        private string _searchFilter = string.Empty;
        private bool _trayDisposed;
        private readonly BackgroundWorkTracker _backgroundWorkTracker = new();
        private Action? _pendingNotificationOpenAction;

        // Tracks IsBusy transitions so a real Windows notification (tray balloon, visible
        // even while another app has focus or the bubble is hidden/collapsed) can be raised
        // exactly once per finished media job - see OnSessionStateChangedForTrayNotification.
        private bool _wasBusyForTrayNotification;

        public MainWindow()
        {
            InitializeComponent();
            InitializeAccessibilityKeyboardHandling();
            TrayIcon.LeftClickCommand = new ActionCommand(ExecuteNotificationOrOpenMainWindow);
            TrayIcon.DoubleClickCommand = new ActionCommand(ExecuteNotificationOrOpenMainWindow);
            // H.NotifyIcon turns a WinUI ContextFlyout into a native Win32 menu by
            // default. Native menu activation invokes MenuFlyoutItem.Command; XAML
            // Click handlers are not raised in that mode, so wire every tray action
            // through ICommand explicitly.
            TrayOpenItem.Command = new ActionCommand(ExecuteNotificationOrOpenMainWindow);
            TrayQuickChatItem.Command = new ActionCommand(TrayQuickChat);
            TrayHideBubbleItem.Command = new ActionCommand(TrayHideBubble);
            TrayAiAutoItem.Command = new ActionCommand(() => RunAiAction("Auto"));
            TrayAiOnItem.Command = new ActionCommand(() => RunAiAction("On"));
            TrayAiOffItem.Command = new ActionCommand(() => RunAiAction("Off"));
            TraySettingsItem.Command = new ActionCommand(TraySettings);
            TrayExitItem.Command = new ActionCommand(App.ExitApplication);

            _dispatcher = DispatcherQueue.GetForCurrentThread();

            Title = "RBG PaRuX AI Studio";
            SetWindowIcon();

            try
            {
                // Sized/centered first as a sane fallback geometry (used if maximize below
                // cannot run, e.g. an unexpected presenter type), then maximized - the user
                // asked for PaRuX to always come up full screen on launch, not just restore
                // whatever size it happened to be at when last closed. This runs on every
                // launch regardless of how the window was left, unlike BubbleWindow's position,
                // which IS deliberately persisted across sessions (RestorePosition/PersistPosition).
                ResizeForCurrentDisplay();
                MaximizeOnLaunch();
            }
            catch (Exception ex)
            {
                Log.Write("Initial resize failed: " + ex.Message);
            }

            // Closing the window parks PaRuX in the tray; only the tray Exit really quits.
            AppWindow.Closing += OnAppWindowClosing;

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            App.Hotkey.Attach(hwnd);
            App.Hotkey.Pressed += OnHotkeyPressed;
            App.Hotkey.Register(SettingsStore.Current.Data.Hotkey);

            ChatSession.Current.ConversationChanged += OnConversationChanged;
            ChatSession.Current.MessageAdded += _ => OnConversationTouched();
            ChatSession.Current.OpenInMainRequested += OnOpenInMainRequested;
            ChatSession.Current.StateChanged += OnSessionStateChangedForTrayNotification;
            ChatSession.Current.StateChanged += OnSessionStateChangedForGuidedTour;
            ChatSession.Current.StateChanged += () => _dispatcher.TryEnqueue(RefreshActiveIndicators);

            _activeIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _activeIndicatorTimer.Tick += OnActiveIndicatorTimerTick;
            _activeIndicatorTimer.Start();

            OnboardingPage.Completed += OnOnboardingCompleted;
            OnboardingPage.FindModelRequested += OnFirstModelRequested;

            RefreshHistory();

            // First run only (SettingsStore.Current.Data.OnboardingCompleted defaults to
            // false - see AppSettings.cs): show the guided welcome/folder-confirmation
            // screen instead of jumping straight to chat. OnOnboardingCompleted flips
            // this over to ShowChat() once the user presses "Get Started", and it never
            // fires again automatically after that.
            if (SettingsStore.Current.Data.OnboardingCompleted)
            {
#if DEBUG
                string? uiTestPage = Environment.GetEnvironmentVariable("PARUX_UI_TEST_INITIAL_PAGE");
                if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                    && string.Equals(uiTestPage, "firstmodel", StringComparison.Ordinal))
                {
                    ShowOnboarding();
                }
                else if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                    && string.Equals(uiTestPage, "modelhub", StringComparison.Ordinal))
                {
                    ShowStudioWorkspace(StudioWorkspace.ModelHub);
                    ModelHubPage.OnShown();
                }
                else
#endif
                {
                    ShowChat();
                }
            }
            else ShowOnboarding();

            // One best-effort pull from PaRuX Server at startup, off the UI thread, so a
            // conversation started on a paired Android device shows up here too. Never
            // blocks startup and never throws if the server is unavailable.
            _ = SyncHistoryFromServerAsync();
        }

        private async Task SyncHistoryFromServerAsync()
        {
            int pulled = await ConversationSyncClient.Current.PullNewerAsync(CancellationToken.None).ConfigureAwait(false);
            if (pulled > 0) _dispatcher.TryEnqueue(RefreshHistory);
        }

        private void ResizeForCurrentDisplay()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            double scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            int width = Math.Min((int)Math.Round(1180 * scale), (int)(work.Width * 0.92));
            int height = Math.Min((int)Math.Round(820 * scale), (int)(work.Height * 0.92));
            int x = work.X + Math.Max(0, (work.Width - width) / 2);
            int y = work.Y + Math.Max(0, (work.Height - height) / 2);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        /// <summary>Always launch maximized. Mirrors the OverlappedPresenter pattern
        /// BubbleWindow uses (see BubbleWindow.xaml.cs), but the opposite treatment: that
        /// window is deliberately fixed-size/non-resizable, while MainWindow keeps its normal
        /// resizable/maximizable/minimizable presenter and simply starts in the Maximized
        /// state every time.</summary>
        private void MaximizeOnLaunch()
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null) presenter.Maximize();
        }

        private void SetWindowIcon()
        {
            try
            {
                string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "parux.ico");
                if (File.Exists(icon)) AppWindow.SetIcon(icon);
            }
            catch (Exception ex)
            {
                Log.Write("SetWindowIcon failed: " + ex.Message);
            }
        }

        public void BringToFront()
        {
            try
            {
                SetForegroundWindow(WindowNative.GetWindowHandle(this));
            }
            catch (Exception ex)
            {
                Log.Write("BringToFront failed: " + ex.Message);
            }
        }

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (App.IsShuttingDown) return;
            args.Cancel = true;
            ChatSession.Current.Persist();
            sender.Hide();
        }

        private void OnHotkeyPressed()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (App.BubbleWin != null) App.BubbleWin.ToggleFromHotkey();
            });
        }

        private void OnOpenInMainRequested()
        {
            _dispatcher.TryEnqueue(() =>
            {
                App.ShowMainWindow();
                ShowChat();
            });
        }

        private void OnConversationChanged()
        {
            _dispatcher.TryEnqueue(RefreshHistory);
        }

        /// <summary>A message landed, so the sidebar title/order may have moved.</summary>
        private void OnConversationTouched()
        {
            _dispatcher.TryEnqueue(RefreshHistory);
        }

        // ------------------------------------------------------------------
        // Sidebar history
        // ------------------------------------------------------------------

        public void RefreshHistory()
        {
            try
            {
                _allConversations = HistoryStore.LoadAll()
                    .OrderByDescending(c => c.IsFavorite)
                    .ThenByDescending(c => c.UpdatedUtc)
                    .ToList();

                // The live conversation may not be on disk yet (no messages); show it anyway.
                Conversation live = ChatSession.Current.Conversation;
                bool livePresent = false;
                foreach (Conversation c in _allConversations)
                {
                    if (c.Id == live.Id) { livePresent = true; break; }
                }
                if (!livePresent && !live.IsIncognito && live.Messages.Count > 0) _allConversations.Insert(0, live);

                string? selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag as string;
                List<Conversation> matchingConversations = _allConversations.Where(MatchesFilter).ToList();
                List<Conversation> visibleConversations = matchingConversations
                    .Take(MaxVisibleRecentConversations)
                    .ToList();
                Conversation? previouslySelectedConversation = selectedTag == null
                    ? null
                    : matchingConversations.FirstOrDefault(c => string.Equals(c.Id, selectedTag, StringComparison.Ordinal));
                if (previouslySelectedConversation != null
                    && !visibleConversations.Any(c => string.Equals(c.Id, previouslySelectedConversation.Id, StringComparison.Ordinal)))
                {
                    // Keep the currently open older conversation selected when rebuilding the
                    // bounded recent list; the Search box remains the way to find other old chats.
                    visibleConversations.Add(previouslySelectedConversation);
                }

                NavView.MenuItems.Clear();

                NavView.MenuItems.Add(WorkspaceSectionHeader);
                foreach (NavigationViewItem workspaceItem in WorkspaceNavigationItems())
                    NavView.MenuItems.Add(workspaceItem);

                var header = new NavigationViewItemHeader { Content = "Recent" };
                NavView.MenuItems.Add(header);

                int shown = 0;
                NavigationViewItem? liveConversationItem = null;
                foreach (Conversation c in visibleConversations)
                {
                    string title = string.IsNullOrWhiteSpace(c.Title) || c.Title == "New chat"
                        ? HistoryStore.DeriveTitle(c)
                        : c.Title;

                    var item = new NavigationViewItem
                    {
                        Tag = c.Id,
                        SelectsOnInvoked = true
                    };
                    item.Icon = new FontIcon { Glyph = c.IsFavorite ? "\uE734" : "\uE8BD", FontSize = 14 };
                    ToolTipService.SetToolTip(item, title);
                    item.ContextFlyout = BuildItemFlyout(c);

                    var contentGrid = new Grid
                    {
                        ColumnSpacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var titleBlock = new TextBlock
                    {
                        Text = title,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(titleBlock, 0);
                    contentGrid.Children.Add(titleBlock);

                    bool isWorking = IsConversationWorking(c);
                    FrameworkElement workingBadge = BuildWorkingBadge();
                    workingBadge.Visibility = isWorking ? Visibility.Visible : Visibility.Collapsed;
                    Grid.SetColumn(workingBadge, 1);
                    contentGrid.Children.Add(workingBadge);

                    item.Content = contentGrid;
                    // The content is a Grid, so without an explicit name screen readers (and UI
                    // automation) only announce "NavigationViewItem" for every conversation.
                    AutomationProperties.SetName(item, title
                        + (c.IsFavorite ? ", favourite" : string.Empty)
                        + (isWorking ? ", working" : string.Empty));
                    AutomationProperties.SetAutomationId(item, "History_Conversation_Item");

                    NavView.MenuItems.Add(item);
                    if (c.Id == live.Id) liveConversationItem = item;
                    shown++;
                }

                if (shown == 0)
                {
                    // Visual only: an empty sidebar gets a real (if quiet) state
                    // rather than a blank gap under the "Recent" header.
                    var empty = new NavigationViewItem
                    {
                        Content = _searchFilter.Length > 0 ? "No matching chats" : "No chats yet",
                        IsEnabled = false,
                        SelectsOnInvoked = false
                    };
                    empty.Icon = new FontIcon
                    {
                        Glyph = _searchFilter.Length > 0 ? "\uE721" : "\uE8BD",
                        FontSize = 14
                    };
                    NavView.MenuItems.Add(empty);
                }

                if (matchingConversations.Count > shown)
                {
                    var limitNotice = new NavigationViewItem
                    {
                        Content = $"Showing {shown} of {matchingConversations.Count} chats. Search above to find older chats.",
                        IsEnabled = false,
                        SelectsOnInvoked = false
                    };
                    AutomationProperties.SetAutomationId(limitNotice, "History_RecentLimit_Status");
                    NavView.MenuItems.Add(limitNotice);
                }

                NavigationViewItem? previousSelection = selectedTag == null
                    ? null
                    : NavView.MenuItems.OfType<NavigationViewItem>()
                        .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTag, StringComparison.Ordinal));
                NavView.SelectedItem = previousSelection ?? (liveConversationItem ?? ChatNavItem);

                if (ChatNavItem != null)
                {
                    string activeTitle = live != null ? (string.IsNullOrWhiteSpace(live.Title) ? "New chat" : live.Title) : "New chat";
                    bool hasDraft = ChatDraftCoordinator.Current.HasDraft(live?.Id);
                    AutomationProperties.SetName(ChatNavItem, ChatHomeNavigationPolicy.FormatChatNavItemAccessibleName(activeTitle, hasDraft));
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshHistory failed: " + ex.Message);
            }
        }

        private bool IsConversationWorking(Conversation c)
        {
            if (ChatSession.Current.IsBusy && ChatSession.Current.Conversation != null && ChatSession.Current.Conversation.Id == c.Id)
                return true;

            if (c.Messages != null)
            {
                foreach (ChatMessage m in c.Messages)
                {
                    if (!string.IsNullOrWhiteSpace(m.CodeTaskId) && _runningCodeTaskIds.Contains(m.CodeTaskId))
                        return true;
                }
            }

            return false;
        }

        private static FrameworkElement BuildWorkingBadge()
        {
            var badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? accentBrush) && accentBrush is Brush ab)
            {
                badgeBorder.Background = ab;
            }

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var ring = new ProgressRing
            {
                Width = 10,
                Height = 10,
                IsActive = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (Application.Current.Resources.TryGetValue("TextOnAccentFillColorPrimaryBrush", out object? onAccent) && onAccent is Brush oab)
            {
                ring.Foreground = oab;
            }
            stack.Children.Add(ring);

            var label = new TextBlock
            {
                Text = "Active",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (Application.Current.Resources.TryGetValue("TextOnAccentFillColorPrimaryBrush", out object? textBrush) && textBrush is Brush tb)
            {
                label.Foreground = tb;
            }
            stack.Children.Add(label);

            badgeBorder.Child = stack;
            ToolTipService.SetToolTip(badgeBorder, "This conversation is actively processing...");
            return badgeBorder;
        }

        private void RefreshActiveIndicators()
        {
            try
            {
                foreach (object menuItem in NavView.MenuItems)
                {
                    if (menuItem is NavigationViewItem nvi && nvi.Tag is string convId)
                    {
                        Conversation? conv = _allConversations.FirstOrDefault(c => c.Id == convId);
                        if (conv != null && nvi.Content is Grid grid && grid.Children.Count > 1)
                        {
                            bool isWorking = IsConversationWorking(conv);
                            grid.Children[1].Visibility = isWorking ? Visibility.Visible : Visibility.Collapsed;
                        }
                    }
                }
            }
            catch { }
        }

        private async void OnActiveIndicatorTimerTick(object? sender, object e)
        {
            _ = CheckBackgroundWorkNotificationsAsync();
            try
            {
                AgentTaskListResponse list = await AgentOrchestratorClient.Current.ListAsync(CancellationToken.None).ConfigureAwait(false);
                var running = list.Tasks
                    .Where(t => string.Equals(t.Status, AgentTaskStatus.Running, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(t.Status, "running", StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                bool changed = !_runningCodeTaskIds.SetEquals(running);
                if (changed)
                {
                    _runningCodeTaskIds.Clear();
                    foreach (string id in running) _runningCodeTaskIds.Add(id);
                    _dispatcher.TryEnqueue(RefreshActiveIndicators);
                }
            }
            catch { }
        }

        private bool MatchesFilter(Conversation c)
        {
            if (_searchFilter.Length == 0) return true;

            string title = c.Title ?? string.Empty;
            if (title.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            foreach (ChatMessage m in c.Messages)
            {
                if (m.Content != null && m.Content.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private MenuFlyout BuildItemFlyout(Conversation conversation)
        {
            var flyout = new MenuFlyout();

            var rename = new MenuFlyoutItem { Text = "Rename" };
            rename.Click += async (s, e) => await RenameConversationAsync(conversation);
            flyout.Items.Add(rename);

            var favorite = new MenuFlyoutItem { Text = conversation.IsFavorite ? "Remove from favorites" : "Add to favorites" };
            favorite.Click += (s, e) =>
            {
                conversation.IsFavorite = !conversation.IsFavorite;
                HistoryStore.Save(conversation);
                RefreshHistory();
            };
            flyout.Items.Add(favorite);

            var duplicate = new MenuFlyoutItem { Text = "Duplicate" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(duplicate, "Chat_History_Duplicate_Item");
            duplicate.Click += (s, e) => { ChatSession.Current.DuplicateConversation(conversation.Id); RefreshHistory(); };
            flyout.Items.Add(duplicate);

            var exportMarkdown = new MenuFlyoutItem { Text = "Export as Markdown" };
            exportMarkdown.Click += async (s, e) => await ExportConversationAsync(conversation, false);
            flyout.Items.Add(exportMarkdown);

            var exportJson = new MenuFlyoutItem { Text = "Export as JSON" };
            exportJson.Click += async (s, e) => await ExportConversationAsync(conversation, true);
            flyout.Items.Add(exportJson);

            var delete = new MenuFlyoutItem { Text = "Delete" };
            delete.Click += async (s, e) => await DeleteConversationAsync(conversation);
            flyout.Items.Add(delete);

            return flyout;
        }

        private async Task ExportConversationAsync(Conversation conversation, bool json)
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedFileName = SafeFileName(conversation.Title),
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeChoices.Add(json ? "JSON" : "Markdown", new List<string> { json ? ".json" : ".md" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return;
                string content = json
                    ? JsonSerializer.Serialize(conversation, new JsonSerializerOptions { WriteIndented = true })
                    : ToMarkdown(conversation);
                await File.WriteAllTextAsync(file.Path, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("Conversation export failed: " + ex.Message);
            }
        }

        private static string ToMarkdown(Conversation conversation)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# " + (conversation.Title ?? "PaRuX chat")).AppendLine();
            foreach (ChatMessage message in conversation.Messages)
            {
                builder.AppendLine("## " + (message.Role == "user" ? "You" : "PaRuX"));
                builder.AppendLine().AppendLine(message.Content ?? string.Empty).AppendLine();
                foreach (ChatAttachment attachment in message.Attachments ?? new List<ChatAttachment>())
                    builder.AppendLine("- Attachment: `" + attachment.FileName.Replace("`", "") + "`");
                if (message.Attachments != null && message.Attachments.Count > 0) builder.AppendLine();
            }
            return builder.ToString();
        }

        private static string SafeFileName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "PaRuX chat" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }

        private async Task RenameConversationAsync(Conversation conversation)
        {
            var input = new TextBox
            {
                Text = conversation.Title ?? string.Empty,
                SelectionStart = 0,
                PlaceholderText = "Conversation name"
            };

            var dialog = new ContentDialog
            {
                Title = "Rename chat",
                Content = input,
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            string newTitle = (input.Text ?? string.Empty).Trim();
            if (newTitle.Length == 0) return;

            conversation.Title = newTitle;
            if (ChatSession.Current.Conversation.Id == conversation.Id)
                ChatSession.Current.Conversation.Title = newTitle;

            HistoryStore.Save(conversation);
            RefreshHistory();
        }

        private async Task DeleteConversationAsync(Conversation conversation)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete chat?",
                Content = "\"" + (conversation.Title ?? "This chat") + "\" will be permanently deleted. This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            // Leave the open conversation first: NewConversation persists the one being replaced,
            // so deleting before leaving would write the deleted chat straight back to history.
            if (ChatSession.Current.Conversation.Id == conversation.Id)
            {
                ChatSession.Current.NewConversation();
            }
            HistoryStore.Delete(conversation.Id);
            RefreshHistory();
        }

        // ------------------------------------------------------------------
        // Navigation
        // ------------------------------------------------------------------

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            NavigationViewItemBase container = args.InvokedItemContainer;
            if (container == null) return;

            string? tag = container.Tag as string;
            if (tag == null) return;

            if (TryNavigateWorkspace(tag))
            {
                return;
            }

            if (ChatSession.Current.OpenConversation(tag))
            {
                ShowChat();
            }
        }

        public void ShowChat()
        {
            NavView.IsPaneVisible = true;
            if (NavView.SelectedItem is NavigationViewItem selectedItem
                && selectedItem.Tag is string selectedTag
                && selectedTag.StartsWith("__", StringComparison.Ordinal)
                && !string.Equals(selectedTag, ChatHomeNavigationPolicy.ChatWorkspaceTag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = ChatNavItem;
            }
            else if (NavView.SelectedItem == null)
            {
                NavView.SelectedItem = ChatNavItem;
            }
            HideAllWorkspacePages();
            ChatPage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
            OnboardingPage.Visibility = Visibility.Collapsed;
            // Quick Chat is a separate always-on-top window. It is useful over other apps,
            // but must not sit over the main shell when returning from Settings.
            App.BubbleWin?.ApplyVisibilityMode();
            ChatPage.OnWorkspaceActivated();
            PlayPageEntrance(ChatPage);
        }

        public void ShowSettings()
        {
            NavView.IsPaneVisible = true;
            SelectWorkspaceNavigationItem(SettingsNavItem);
            // Workspace pages are siblings in the shell Grid, not a Frame. Hide the previous
            // workspace before the Settings entrance animation starts; otherwise both visual
            // trees paint during the animation and Model Hub text/cards bleed through Settings.
            HideAllWorkspacePages();
            ChatPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            OnboardingPage.Visibility = Visibility.Collapsed;
            // Settings is part of the main shell; hiding the always-on-top bubble prevents
            // its separate HWND from being composited over the category rail/content area.
            App.BubbleWin?.HideBubble();
            SettingsPage.OnShown();
            PlayPageEntrance(SettingsPage);
        }

        /// <summary>First run only - see the OnboardingPage.Completed wiring in the
        /// constructor. Hides the nav pane too, so Settings/history aren't reachable as a
        /// side door around the guided folder-confirmation step.</summary>
        public void ShowOnboarding()
        {
            if (OnboardingReturnBanner != null) OnboardingReturnBanner.IsOpen = false;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
            NavView.IsPaneVisible = false;
            ChatPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            HideAllWorkspacePages();
            OnboardingPage.Visibility = Visibility.Visible;
            OnboardingPage.OnReturnedFromModelHub();
            PlayPageEntrance(OnboardingPage);
        }

        private void OnOnboardingCompleted()
        {
            _onboardingHandoffActive = false;
            if (OnboardingReturnBanner != null) OnboardingReturnBanner.IsOpen = false;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
            NavView.IsPaneVisible = true;
            ShowChat();
            // Queued rather than called inline: the tour points at sidebar controls, and they need
            // to be laid out on screen before a TeachingTip can position itself against them.
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateGetStarted(
                SettingsStore.Current.Data.GuidedTourCompleted);
            if (decision.ShouldStart)
            {
                _dispatcher.TryEnqueue(MaybeStartGuidedTour);
            }
        }

        /// <summary>
        /// Visual only. A 180 ms fade plus a 6 px rise when the content area swaps
        /// pages, because NavigationView's content host is not a Frame and so has
        /// no built-in transition. Always ends with the page fully opaque, even if
        /// the Storyboard cannot run.
        /// </summary>
        private static void PlayPageEntrance(FrameworkElement page)
        {
            if (page == null) return;

            // Before the shell is loaded there is nothing to transition into.
            if (!page.IsLoaded)
            {
                page.Opacity = 1;
                return;
            }

            try
            {
                var translate = new TranslateTransform { Y = 6 };
                page.RenderTransform = translate;
                page.Opacity = 0;

                var board = new Storyboard();

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, page);
                Storyboard.SetTargetProperty(fade, "Opacity");
                board.Children.Add(fade);

                var rise = new DoubleAnimation
                {
                    From = 6,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(rise, translate);
                Storyboard.SetTargetProperty(rise, "Y");
                board.Children.Add(rise);

                board.Completed += (s, e) =>
                {
                    page.Opacity = 1;
                    page.RenderTransform = null;
                };

                board.Begin();
            }
            catch (Exception ex)
            {
                Log.Write("Page transition failed: " + ex.Message);
                page.Opacity = 1;
                page.RenderTransform = null;
            }
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            ChatSession.Current.NewConversation();
            // A brand-new empty chat has no history item to select. Select the Chat home item.
            NavView.SelectedItem = ChatNavItem;
            ShowChat();
        }

        private void NewIncognitoChat_Click(object sender, RoutedEventArgs e)
        {
            // This button doubles as a toggle: pressed once it opens a fresh incognito
            // conversation; pressed again while already inside one it should leave incognito
            // mode rather than just replace it with yet another incognito conversation.
            if (ChatSession.Current.Conversation != null && ChatSession.Current.Conversation.IsIncognito)
                ChatSession.Current.NewConversation();
            else
                ChatSession.Current.NewIncognitoConversation();
            NavView.SelectedItem = null;
            ShowChat();
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            _searchFilter = (sender.Text ?? string.Empty).Trim();
            RefreshHistory();
        }

        // ------------------------------------------------------------------
        // Tray
        // ------------------------------------------------------------------

        private static void TrayQuickChat()
        {
            if (App.BubbleWin != null) App.BubbleWin.ShowExpanded();
        }

        /// <summary>The collapsed pill itself has no close affordance (by design - it has no
        /// title bar or system buttons), so this tray item is the one guaranteed way to dismiss
        /// it without changing the Settings > Bubble mode or quitting the whole app.</summary>
        private static void TrayHideBubble()
        {
            App.BubbleWin?.HideBubble();
        }

        private void TraySettings()
        {
            App.ShowMainWindow();
            ShowSettings();
        }

        /// <summary>
        /// Real gap this closes: the bubble's own "Job done" pill text (see
        /// BubbleWindow.OnSessionStateChanged) only helps if the bubble happens to be visible
        /// and collapsed on screen right when a long image/video job finishes - useless the
        /// moment the user has alt-tabbed to another app, or set Bubble mode to Disabled/HotkeyOnly
        /// so the bubble is not shown at all. A tray balloon (H.NotifyIcon.WinUI, already wired up
        /// for the context menu above) is a real OS-level notification: it shows and lands in
        /// Action Center regardless of which app has focus or whether the bubble/main window are
        /// even visible. Fires off ChatSession's existing IsBusy/StateChanged pattern - the same
        /// signal BubbleWindow already watches - so no new plumbing is needed in ChatSession or
        /// MediaJobClient. RaiseState() can run its continuation off the UI thread (the media job
        /// awaits use ConfigureAwait(false)), so this only decides here and marshals the actual
        /// notification/UI touch via _dispatcher.
        /// </summary>
        private bool _wasBusyForGuidedTour;
        private bool _hasObservedSuccessfulAnswer;

        /// <summary>
        /// Starts the guided tour after the first successful chat answer if not already seen or skipped.
        /// </summary>
        private void OnSessionStateChangedForGuidedTour()
        {
            bool busy = ChatSession.Current.IsBusy;
            bool justFinished = _wasBusyForGuidedTour && !busy;
            _wasBusyForGuidedTour = busy;
            if (!justFinished) return;

            ChatMessage? last = ChatSession.Current.Conversation?.Messages
                .LastOrDefault(m => m.Role == "assistant");

            if (last == null) return;

            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                SettingsStore.Current.Data.GuidedTourCompleted,
                _hasObservedSuccessfulAnswer,
                last.Role,
                last.Content,
                last.IsError,
                last.IsStreaming);

            if (decision.ShouldStart)
            {
                _hasObservedSuccessfulAnswer = true;
                _dispatcher.TryEnqueue(MaybeStartGuidedTour);
            }
            else if (!last.IsError && !string.IsNullOrWhiteSpace(last.Content))
            {
                _hasObservedSuccessfulAnswer = true;
            }
        }

        private bool IsMainWindowHidden()
        {
            try
            {
                if (AppWindow == null) return true;
                if (!AppWindow.IsVisible) return true;
                if (AppWindow.Presenter is OverlappedPresenter overlapped
                    && overlapped.State == OverlappedPresenterState.Minimized)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private BackgroundNotificationPreferences GetNotificationPreferences()
        {
            AppSettings s = SettingsStore.Current.Data;
            return new BackgroundNotificationPreferences(
                NotifyOnGeneration: s.NotifyOnGeneration,
                NotifyOnDownloads: s.NotifyOnDownloads,
                NotifyOnInstalls: s.NotifyOnInstalls);
        }

        private void RaiseBackgroundNotification(BackgroundWorkItem item)
        {
            if (_trayDisposed) return;

            _pendingNotificationOpenAction = item.OpenTarget switch
            {
                BackgroundWorkOpenTarget.Chat => () =>
                {
                    App.ShowMainWindow();
                    ShowChat();
                },
                BackgroundWorkOpenTarget.ModelHub => () =>
                {
                    App.ShowMainWindow();
                    ShowStudioWorkspace(StudioWorkspace.ModelHub);
                    ModelHubPage.OnShown();
                },
                BackgroundWorkOpenTarget.RuntimeManager => () =>
                {
                    App.ShowMainWindow();
                    ShowStudioWorkspace(StudioWorkspace.Runtime);
                    RuntimeManagerPage.OnShown();
                },
                _ => () => App.ShowMainWindow()
            };

            NotificationIcon icon = item.Outcome == BackgroundWorkOutcome.Failed
                ? NotificationIcon.Error
                : NotificationIcon.Info;

            try
            {
                TrayIcon.ShowNotification(item.Title, item.Message, icon);
            }
            catch (Exception ex)
            {
                Log.Write("Background work notification failed: " + ex.Message);
            }
        }

        private void ExecuteNotificationOrOpenMainWindow()
        {
            Action? action = _pendingNotificationOpenAction;
            _pendingNotificationOpenAction = null;
            if (action != null)
            {
                action();
            }
            else
            {
                App.ShowMainWindow();
            }
        }

        private async Task CheckBackgroundWorkNotificationsAsync()
        {
            if (_trayDisposed) return;
            try
            {
                bool isHidden = IsMainWindowHidden();
                BackgroundNotificationPreferences prefs = GetNotificationPreferences();

                if (prefs.NotifyOnDownloads)
                {
                    List<ModelDownloadStatus> downloads = await ModelDownloadClient.Current
                        .ListDownloadsAsync(CancellationToken.None).ConfigureAwait(false);
                    IReadOnlyList<BackgroundNotificationDecision> downloadDecisions =
                        _backgroundWorkTracker.EvaluateDownloads(downloads, isHidden, prefs);

                    foreach (BackgroundNotificationDecision d in downloadDecisions)
                    {
                        if (d.ShouldNotify && d.Item != null)
                        {
                            _dispatcher.TryEnqueue(() => RaiseBackgroundNotification(d.Item));
                        }
                    }
                }

                if (prefs.NotifyOnInstalls)
                {
                    List<RuntimeInstallStatus> installs = await RuntimeManagerClient.Current
                        .ListInstallsAsync(CancellationToken.None).ConfigureAwait(false);
                    IReadOnlyList<BackgroundNotificationDecision> installDecisions =
                        _backgroundWorkTracker.EvaluateInstalls(installs, isHidden, prefs);

                    foreach (BackgroundNotificationDecision d in installDecisions)
                    {
                        if (d.ShouldNotify && d.Item != null)
                        {
                            _dispatcher.TryEnqueue(() => RaiseBackgroundNotification(d.Item));
                        }
                    }
                }
            }
            catch
            {
                // Core offline, starting up or quiescence: ignore poll errors
            }
        }

        private void OnSessionStateChangedForTrayNotification()
        {
            bool busy = ChatSession.Current.IsBusy;
            bool justFinished = _wasBusyForTrayNotification && !busy;
            _wasBusyForTrayNotification = busy;
            if (!justFinished) return;

            RouteKind route = ChatSession.Current.LastRoute;
            if (route != RouteKind.Image && route != RouteKind.ImageEdit && route != RouteKind.Video) return;

            ChatMessage? last = ChatSession.Current.Conversation?.Messages
                .LastOrDefault(m => m.Role == "assistant");
            if (last == null) return;

            bool isHidden = IsMainWindowHidden();
            BackgroundNotificationPreferences prefs = GetNotificationPreferences();
            BackgroundNotificationDecision decision = _backgroundWorkTracker.EvaluateGeneration(
                route, last, isHidden, prefs);

            if (decision.ShouldNotify && decision.Item != null)
            {
                _dispatcher.TryEnqueue(() => RaiseBackgroundNotification(decision.Item));
            }
        }


        private void RunAiAction(string action)
        {
            _ = Task.Run(async () =>
            {
                LocalAiController.ActionResult result =
                    await LocalAiController.Current.RunActionAsync(action, CancellationToken.None);

                _dispatcher.TryEnqueue(() =>
                {
                    ChatPage.ShowTransientStatus(result.Success
                        ? "AI " + action + ": " + result.Message.Trim()
                        : LocalAiController.FriendlyScriptError(result));
                });
            });
        }

        public void DisposeTray()
        {
            if (_trayDisposed) return;
            _trayDisposed = true;
            try
            {
                TrayIcon.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("Tray dispose failed: " + ex.Message);
            }
        }
    }
}
