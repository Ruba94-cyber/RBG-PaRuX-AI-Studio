using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Native durable PaRuX coding task surface. Review and integration are separate trust decisions:
    /// autonomous work stays in isolated single- or multi-repository worktrees; applying reviewed
    /// changes to original repositories requires an explicit button plus confirmation dialog and
    /// never commits/pushes automatically. Durable user questions use the same task checkpoint and
    /// may be answered here without turning the chat transcript into an execution-state database.
    /// </summary>
    public sealed partial class ChatView
    {
        private bool _nativeCodingHandoffUpgradeInitialized;
#if DEBUG
        private bool _completedCodingTaskUiTestFixtureSeeded;
#endif
        /// <summary>Stable UI-automation identity for the coding-task card (localized text is not a test API).</summary>
        private static T AutoId<T>(T element, string id) where T : FrameworkElement
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);
            return element;
        }

        private readonly Dictionary<ChatMessage, Border> _codingTaskCards = new();
        private readonly Dictionary<string, CancellationTokenSource> _activeCodingMonitors = new(StringComparer.Ordinal);
        private readonly HashSet<ChatMessage> _autoLaunchingCodingTasks = new();
        private readonly Dictionary<ChatMessage, CodingTaskReviewModel> _codingTaskReviewModels = new();

#if DEBUG
        private void SeedCompletedCodingTaskUiTestFixture()
        {
            string? fixture = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_TASK_CARD");
            if (_completedCodingTaskUiTestFixtureSeeded
                || !string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                || (fixture != "completed" && fixture != "completed-apply"))
                return;

            _completedCodingTaskUiTestFixtureSeeded = true;
            ChatSession.Current.Conversation.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Completed coding task UI fixture",
                ActionKind = "code-handoff",
                CodeTaskId = "ui-test-completed-coding-task"
            });
        }

        private static AgentTaskDto? GetCompletedCodingTaskUiTestFixture(ChatMessage message)
        {
            string? fixture = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_TASK_CARD");
            if (!string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                || (fixture != "completed" && fixture != "completed-apply")
                || !string.Equals(message.CodeTaskId, "ui-test-completed-coding-task", StringComparison.Ordinal))
                return null;

            DateTime now = DateTime.UtcNow;
            return new AgentTaskDto
            {
                Id = message.CodeTaskId!,
                Goal = "Isolated FlaUI coding-task card fixture",
                Role = "coding",
                Status = AgentTaskStatus.Completed,
                Stage = "verified",
                ProgressPercent = 100,
                VerificationStatus = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION") is { Length: > 0 } overrideStatus ? overrideStatus : "pass",
                ResultJson = "{\"goalReported\":true,\"verifiedByCommand\":true,\"verificationPending\":false,\"browserPostconditionPending\":false,\"checkpointSessionId\":\"ui-test-completed-coding-task\",\"verifiedWorkspaceFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}",
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }
#endif
        // RebuildMessages() (main renderer, ChatView.xaml.cs OnConversationChanged) and
        // UpgradeAllNativeCodingHandoffs() are both enqueued off the same ConversationChanged/
        // MessageAdded events, but the main renderer's own dispatcher work can still be mid-flight
        // (or not yet queued) when ours runs, so _visuals has no entry yet for a brand-new or
        // just-switched-to conversation. Confirmed live: a real /code message reproducibly got
        // "hasVisuals=false" on first attempt and, with no retry, the coding-task card (and the
        // whole autonomous run) never appeared - the chat was left showing only the static intro
        // line forever. Bounded retry closes that race without guessing at the renderer's internal
        // batching; it self-cancels once a message's card is built or after a generous cap.
        private const int MaxVisualsWaitAttempts = 40;
        private readonly Dictionary<ChatMessage, int> _pendingVisualsWaitAttempts = new();

        private void InitializeNativeCodingHandoffUpgrade()
        {
            if (_nativeCodingHandoffUpgradeInitialized) return;
            _nativeCodingHandoffUpgradeInitialized = true;
            ChatSession.Current.MessageAdded += OnNativeCodingMessageAdded;
            ChatSession.Current.ConversationChanged += OnNativeCodingConversationChanged;
            _dispatcher.TryEnqueue(UpgradeAllNativeCodingHandoffs);
        }

        private void OnNativeCodingMessageAdded(ChatMessage message) =>
            _dispatcher.TryEnqueue(() => UpgradeNativeCodingHandoff(message));

        private void OnNativeCodingConversationChanged() =>
            _dispatcher.TryEnqueue(UpgradeAllNativeCodingHandoffs);

        private void UpgradeAllNativeCodingHandoffs()
        {
            foreach (ChatMessage message in ChatSession.Current.Conversation.Messages)
                UpgradeNativeCodingHandoff(message);
        }

        private void UpgradeNativeCodingHandoff(ChatMessage message)
        {
            if (!string.Equals(message.ActionKind, "code-handoff", StringComparison.Ordinal)) return;
            if (!_visuals.TryGetValue(message, out MessageVisuals? visuals)
                || visuals.Actions is not StackPanel actions
                || actions.Parent is not StackPanel column)
            {
                // Visuals for this message aren't built yet - wait for the main renderer instead of
                // giving up permanently. Each retry is a fresh dispatcher hop, so it costs nothing
                // while idle and naturally yields to whatever render work is still queued ahead of it.
                int attempts = _pendingVisualsWaitAttempts.TryGetValue(message, out int n) ? n : 0;
                if (attempts >= MaxVisualsWaitAttempts)
                {
                    Log.Write("Coding handoff card gave up waiting for message visuals after "
                        + MaxVisualsWaitAttempts + " attempts; the message stayed a plain bubble.");
                    _pendingVisualsWaitAttempts.Remove(message);
                    return;
                }
                _pendingVisualsWaitAttempts[message] = attempts + 1;
                _dispatcher.TryEnqueue(() => UpgradeNativeCodingHandoff(message));
                return;
            }
            _pendingVisualsWaitAttempts.Remove(message);

            actions.Visibility = Visibility.Collapsed;

            if (!_codingTaskCards.TryGetValue(message, out Border? card) || card.Parent == null)
            {
                card = BuildCodingTaskCard(message);
                _codingTaskCards[message] = card;
                int actionIndex = column.Children.IndexOf(actions);
                if (actionIndex >= 0)
                    column.Children.Insert(actionIndex, card);
                else
                    column.Children.Add(card);
            }

#if DEBUG
            AgentTaskDto? uiTestTask = GetCompletedCodingTaskUiTestFixture(message);
            RefreshCodingTaskCardUi(message, card, uiTestTask,
                diffStat: uiTestTask == null ? null : "3 files changed");

            // The UI fixture is compiled only into Debug builds. Release always uses Core task state.
            if (uiTestTask != null) return;
#else
            RefreshCodingTaskCardUi(message, card);
#endif

            if (string.IsNullOrWhiteSpace(message.CodeTaskId))
            {
                string? existingRoot = ResolvePreferredCodingRoot();

                if (!string.IsNullOrWhiteSpace(existingRoot) && Directory.Exists(existingRoot))
                {
                    if (_autoLaunchingCodingTasks.Add(message))
                    {
                        _ = AutoLaunchCodingTaskAsync(message, card);
                    }
                }
            }
            else
            {
                StartCodingTaskMonitoring(message, card);
            }
        }

        private Border BuildCodingTaskCard(ChatMessage message)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 8, 0, 8),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 720
            };

            if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out object? bgBrush) && bgBrush is Brush bg)
                card.Background = bg;
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? strokeBrush) && strokeBrush is Brush stroke)
                card.BorderBrush = stroke;

            var panel = new StackPanel { Spacing = 10 };
            card.Child = panel;

            return card;
        }

        private void RefreshCodingTaskCardUi(
            ChatMessage message,
            Border card,
            AgentTaskDto? task = null,
            AgentTaskLogListResponse? logs = null,
            AgentUserQuestionStateDto? userQuestion = null,
            string? diffStat = null)
        {
            if (card.Child is not StackPanel panel) return;
            panel.Children.Clear();

            string currentRoot = !string.IsNullOrWhiteSpace(message.CodeWorkspacePath)
                ? message.CodeWorkspacePath
                : ResolvePreferredCodingRoot() ?? string.Empty;

            bool isSandbox = CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(currentRoot);
            string folderName = CodingTaskSandboxWorkspacePolicy.GetWorkspaceDisplayName(currentRoot);



            bool hasTask = !string.IsNullOrWhiteSpace(message.CodeTaskId);
            string statusStr = task?.Status ?? (hasTask ? "running" : "not-started");

            // 1. Header row
            var headerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(new FontIcon { Glyph = "\uE943", FontSize = 18 });
            var titleStack = new StackPanel { Spacing = 1 };
            titleStack.Children.Add(new TextBlock
            {
                Text = folderName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14
            });
            string taskDisplay = !string.IsNullOrWhiteSpace(message.CodeTaskId)
                ? ("Task " + message.CodeTaskId[..Math.Min(8, message.CodeTaskId.Length)])
                : "PaRuX Coding Agent";
            titleStack.Children.Add(AutoId(new TextBlock
            {
                Text = taskDisplay,
                FontSize = 11,
                Opacity = 0.65
            }, "Chat_CodingTask_Id_Text"));
            if (isSandbox)
            {
                var bindButton = AutoId(new HyperlinkButton
                {
                    Content = "Bind repository…",
                    FontSize = 11,
                    Padding = new Thickness(0, 2, 0, 0)
                }, "Chat_CodingTask_BindRepository_Button");
                bindButton.Click += async (_, _) => await PickCodeFolderAndBindAsync(message, card);
                titleStack.Children.Add(bindButton);
            }
            titlePanel.Children.Add(titleStack);
            Grid.SetColumn(titlePanel, 0);
            headerGrid.Children.Add(titlePanel);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = AutoId(new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                "Chat_CodingTask_Status_Text");
            badge.Child = badgeText;

            if (!hasTask)
            {
                badgeText.Text = string.IsNullOrWhiteSpace(currentRoot) ? "Waiting for folder" : "Starting...";
                if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? b) && b is Brush bb)
                    badge.Background = bb;
            }
            else if (string.Equals(statusStr, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase))
            {
                // "Verified" is claimed only when Core recorded a pass backed by executed checks AND the
                // task result carries the complete, admissible verification receipt. Anything less is
                // shown as unverified/unapproved, never as verified.
                CodingVerificationPresentation verification = CodingVerificationPresentation.For(task?.VerificationStatus);
                bool isAccepted = verification.IsVerified && CodingTaskApplyAdmissionPolicy.CanApply(task, message.CodeTaskId);
                badgeText.Text = isAccepted ? verification.Badge
                    : verification.IsVerified ? "Completed · evidence not approved" : verification.Badge;
                string brush = isAccepted ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCautionBackgroundBrush";
                if (Application.Current.Resources.TryGetValue(brush, out object? b) && b is Brush bb)
                    badge.Background = bb;
            }
            else if (string.Equals(statusStr, "awaiting-user-input", StringComparison.OrdinalIgnoreCase))
            {
                badgeText.Text = "Waiting for answer";
                if (Application.Current.Resources.TryGetValue("SystemFillColorCautionBackgroundBrush", out object? b) && b is Brush bb)
                    badge.Background = bb;
            }
            else if (string.Equals(statusStr, AgentTaskStatus.Failed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusStr, AgentTaskStatus.Blocked, StringComparison.OrdinalIgnoreCase))
            {
                badgeText.Text = "Failed";
                if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBackgroundBrush", out object? b) && b is Brush bb)
                    badge.Background = bb;
            }
            else if (string.Equals(statusStr, AgentTaskStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                badgeText.Text = "Cancelled";
                if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? b) && b is Brush bb)
                    badge.Background = bb;
            }
            else
            {
                badgeText.Text = "Running";
                if (Application.Current.Resources.TryGetValue("SystemFillColorAttentionBackgroundBrush", out object? b) && b is Brush bb)
                    badge.Background = bb;
            }

            Grid.SetColumn(badge, 1);
            headerGrid.Children.Add(badge);
            panel.Children.Add(headerGrid);

            // 2. Body / Progress section
            if (!hasTask)
            {
                if (string.IsNullOrWhiteSpace(currentRoot))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Choose the code folder or repository PaRuX should work in so it can start the autonomous task.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8
                    });

                    var pickButton = new Button
                    {
                        Content = "Choose code folder…",
                        Padding = new Thickness(14, 6, 14, 6)
                    };
                    if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? ab) && ab is Style as_)
                        pickButton.Style = as_;
                    pickButton.Click += async (_, _) => await PickCodeFolderAndLaunchAsync(message, card);
                    panel.Children.Add(pickButton);
                }
                else
                {
                    panel.Children.Add(new ProgressBar { IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Stretch });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Preparing the isolated workspace and starting the task…",
                        FontSize = 12,
                        Opacity = 0.75
                    });
                }
                return;
            }

            string stage = !string.IsNullOrWhiteSpace(task?.Stage) ? task.Stage : (task?.Status ?? "Running");
            panel.Children.Add(AutoId(new TextBlock
            {
                Text = stage,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            }, "Chat_CodingTask_Stage_Text"));

            if (string.Equals(statusStr, AgentTaskStatus.Running, StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusStr, "queued", StringComparison.OrdinalIgnoreCase))
            {
                panel.Children.Add(new ProgressBar
                {
                    IsIndeterminate = (task?.ProgressPercent ?? 0) <= 0,
                    Value = task?.ProgressPercent ?? 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                });
            }
            else if (string.Equals(statusStr, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase))
            {
                panel.Children.Add(new ProgressBar
                {
                    IsIndeterminate = false,
                    Value = 100,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                });
            }

            // 3. Live Logs / Execution Evidence Tail
            if (logs != null && logs.Logs.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Live activity · plan, actions and evidence",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 12
                });
                var logBox = new Border
                {
                    Padding = new Thickness(10),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                if (Application.Current.Resources.TryGetValue("LayerFillColorAltBrush", out object? logBg) && logBg is Brush lbg)
                    logBox.Background = lbg;

                var logStack = new StackPanel { Spacing = 4 };
                foreach (AgentTaskLogEntryDto entry in logs.Logs.TakeLast(16))
                {
                    logStack.Children.Add(AutoId(new TextBlock
                    {
                        Text = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + entry.Message,
                        FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
                        FontSize = 11,
                        Opacity = 0.85,
                        TextWrapping = TextWrapping.Wrap
                    }, "Chat_CodingTask_Activity_Item"));
                }
                var logScroll = new ScrollViewer
                {
                    Content = logStack,
                    MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                // The card is rebuilt whenever fresh task evidence arrives. A newly-created
                // ScrollViewer starts at offset zero, which made every update jump back to the
                // oldest visible line. Defer until layout has measured the content, then pin the
                // compact activity view to the newest line at the bottom.
                logScroll.Loaded += (_, _) => _dispatcher.TryEnqueue(() =>
                    logScroll.ChangeView(null, logScroll.ScrollableHeight, null, disableAnimation: true));
                logBox.Child = logScroll;
                panel.Children.Add(logBox);
                panel.Children.Add(new TextBlock
                {
                    Text = "Shows the operating plan, commands and verifiable results. The model's private internal reasoning is not exposed.",
                    FontSize = 10,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // 4. Pending user question (if waiting for user input)
            if (userQuestion?.PendingQuestion != null)
            {
                AgentUserQuestionDto q = userQuestion.PendingQuestion;
                var questionCard = new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                if (Application.Current.Resources.TryGetValue("SystemFillColorCautionBackgroundBrush", out object? qb) && qb is Brush qbg)
                    questionCard.Background = qbg;

                var qStack = new StackPanel { Spacing = 8 };
                qStack.Children.Add(new TextBlock
                {
                    Text = "The agent needs your feedback:",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                qStack.Children.Add(new TextBlock { Text = q.Question, TextWrapping = TextWrapping.Wrap });

                ComboBox? optionsCombo = null;
                if (q.Options.Count > 0)
                {
                    optionsCombo = new ComboBox
                    {
                        Header = "Select an option",
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    foreach (string opt in q.Options) optionsCombo.Items.Add(new ComboBoxItem { Content = opt, Tag = opt });
                    optionsCombo.SelectedIndex = 0;
                    qStack.Children.Add(optionsCombo);
                }

                TextBox? otherText = null;
                if (q.AllowOther)
                {
                    otherText = new TextBox
                    {
                        Header = q.Options.Count > 0 ? "Or type another answer" : "Your answer",
                        PlaceholderText = "Type your answer here…",
                        AcceptsReturn = true,
                        MinHeight = 54
                    };
                    qStack.Children.Add(otherText);
                }

                var sendAnswerButton = new Button
                {
                    Content = "Send answer",
                    Padding = new Thickness(12, 4, 12, 4)
                };
                if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? ab) && ab is Style as_)
                    sendAnswerButton.Style = as_;
                sendAnswerButton.Click += async (_, _) =>
                {
                    string typed = (otherText?.Text ?? string.Empty).Trim();
                    string selected = optionsCombo?.SelectedItem is ComboBoxItem selItem ? (selItem.Tag?.ToString() ?? string.Empty) : string.Empty;
                    string finalAnswer = typed.Length > 0 ? typed : selected;
                    if (string.IsNullOrWhiteSpace(finalAnswer))
                    {
                        ShowTransientStatus("Enter an answer before sending.");
                        return;
                    }

                    sendAnswerButton.IsEnabled = false;
                    try
                    {
                        await AgentOrchestratorClient.Current.AnswerUserQuestionAsync(
                            message.CodeTaskId!,
                            new AnswerAgentUserQuestionRequest { QuestionId = q.Id, Answer = finalAnswer },
                            CancellationToken.None);
                        ShowTransientStatus("Answer sent; the agent has resumed work.");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Send answer to coding task question failed: " + ex);
                        ShowTransientStatus(UserFailureRecoveryMapper.Map(ex, "Sending the answer failed").FormatStatusLine());
                        sendAnswerButton.IsEnabled = true;
                    }
                };
                AutomationProperties.SetAutomationId(sendAnswerButton, "parux-coding-answer-user");
                qStack.Children.Add(sendAnswerButton);

                questionCard.Child = qStack;
                panel.Children.Add(questionCard);
            }

            // 5. Completed Review & Apply Section
            if (string.Equals(statusStr, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase)
                && !CodingTaskApplyAdmissionPolicy.CanApply(task, message.CodeTaskId))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Apply is disabled: there is not enough evidence from independent verification or tests. Review the worktree before deciding how to proceed.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8
                });
                var reviewUnverified = new Button
                {
                    Content = "Review unverified changes in VS Code",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                AutomationProperties.SetAutomationId(
                    reviewUnverified, "parux-coding-review-unverified");
                reviewUnverified.Click += async (_, _) =>
                {
                    string? reviewRoot = await ResolveTaskWorktreeAsync(message.CodeTaskId!);
                    if (string.IsNullOrWhiteSpace(reviewRoot)
                        || !Directory.Exists(reviewRoot)
                        || !StartConfiguredVsCode(reviewRoot))
                        ShowTransientStatus("Could not open the worktree for review.");
                };
                panel.Children.Add(reviewUnverified);
            }
            if (string.Equals(statusStr, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase)
                && CodingTaskApplyAdmissionPolicy.CanApply(task, message.CodeTaskId))
            {
                var reviewPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };

                if (!string.IsNullOrWhiteSpace(diffStat))
                {
                    var diffBox = new Border
                    {
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(6)
                    };
                    if (Application.Current.Resources.TryGetValue("LayerFillColorAltBrush", out object? dbg) && dbg is Brush dbgBrush)
                        diffBox.Background = dbgBrush;

                    diffBox.Child = AutoId(new TextBlock
                    {
                        Text = diffStat,
                        FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }, "Chat_CodingTask_DiffStat_Text");
                    reviewPanel.Children.Add(diffBox);
                }

                CodingTaskReviewModel reviewModel = CodingTaskReviewModel.Parse(diffStat);
                _codingTaskReviewModels[message] = reviewModel;

                Button? applyButtonRef = null;
                if (reviewModel.TotalFiles > 0)
                {
                    var filesReviewStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
                    var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
                    var statusText = AutoId(new TextBlock
                    {
                        Text = reviewModel.FormatReviewStatus(),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    }, "Chat_CodingTask_ReviewStatus_Text");
                    headerRow.Children.Add(statusText);

                    var selectAllLink = AutoId(new HyperlinkButton
                    {
                        Content = "Accept all",
                        FontSize = 11,
                        Padding = new Thickness(2, 0, 2, 0)
                    }, "Chat_CodingTask_AcceptAll_Button");

                    var deselectAllLink = AutoId(new HyperlinkButton
                    {
                        Content = "Reject all",
                        FontSize = 11,
                        Padding = new Thickness(2, 0, 2, 0)
                    }, "Chat_CodingTask_RejectAll_Button");

                    headerRow.Children.Add(selectAllLink);
                    headerRow.Children.Add(deselectAllLink);
                    filesReviewStack.Children.Add(headerRow);

                    var checkBoxes = new List<CheckBox>();

                    void UpdateApplyEnabled()
                    {
                        statusText.Text = reviewModel.FormatReviewStatus();
                        if (applyButtonRef != null)
                            applyButtonRef.IsEnabled = reviewModel.HasAcceptedFiles;
                    }

                    selectAllLink.Click += (_, _) =>
                    {
                        reviewModel.AcceptAll();
                        foreach (var cb in checkBoxes) cb.IsChecked = true;
                        UpdateApplyEnabled();
                    };

                    deselectAllLink.Click += (_, _) =>
                    {
                        reviewModel.RejectAll();
                        foreach (var cb in checkBoxes) cb.IsChecked = false;
                        UpdateApplyEnabled();
                    };

                    foreach (var fileItem in reviewModel.Files)
                    {
                        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                        var cb = AutoId(new CheckBox
                        {
                            IsChecked = fileItem.IsAccepted,
                            Content = $"{fileItem.RelativePath} ({fileItem.ChangeKind})",
                            FontSize = 12
                        }, "Chat_CodingTask_ReviewFile_" + fileItem.AutomationIdSafePath);
                        cb.Checked += (_, _) =>
                        {
                            fileItem.IsAccepted = true;
                            UpdateApplyEnabled();
                        };
                        cb.Unchecked += (_, _) =>
                        {
                            fileItem.IsAccepted = false;
                            UpdateApplyEnabled();
                        };
                        checkBoxes.Add(cb);
                        fileRow.Children.Add(cb);
                        filesReviewStack.Children.Add(fileRow);
                    }

                    reviewPanel.Children.Add(filesReviewStack);
                }

                var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                var applyButton = AutoId(new Button
                {
                    Content = "Apply changes to project",
                    Padding = new Thickness(16, 6, 16, 6),
                    IsEnabled = reviewModel.TotalFiles == 0 || reviewModel.HasAcceptedFiles
                }, "Chat_CodingTask_Apply_Button");
                if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? ab) && ab is Style as_)
                    applyButton.Style = as_;
                applyButton.Click += async (_, _) => await ApplyReviewedChangesAsync(message, applyButton);
                AutomationProperties.SetAutomationId(applyButton, "parux-coding-apply");
                buttonRow.Children.Add(applyButton);
                applyButtonRef = applyButton;

                var vsCodeButton = AutoId(new Button
                {
                    Content = "Review in VS Code",
                    Padding = new Thickness(12, 6, 12, 6)
                }, "Chat_CodingTask_ReviewInVsCode_Button");
                vsCodeButton.Click += async (_, _) =>
                {
                    string? worktree = await ResolveTaskWorktreeAsync(message.CodeTaskId!);
                    if (!string.IsNullOrWhiteSpace(worktree) && Directory.Exists(worktree))
                    {
                        if (!StartConfiguredVsCode(worktree))
                            ShowTransientStatus("Unable to launch VS Code.");
                    }
                };
                AutomationProperties.SetAutomationId(vsCodeButton, "parux-coding-review-vscode");
                buttonRow.Children.Add(vsCodeButton);

                reviewPanel.Children.Add(buttonRow);
                panel.Children.Add(reviewPanel);
            }
            else if (string.Equals(statusStr, AgentTaskStatus.Running, StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusStr, "queued", StringComparison.OrdinalIgnoreCase))
            {
                var runButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                var stopButton = AutoId(new Button
                {
                    Content = "Stop now",
                    Padding = new Thickness(12, 4, 12, 4)
                }, "Chat_CodingTask_Stop_Button");
                stopButton.Click += async (_, _) => await StopCodingTaskAsync(message, stopButton);
                AutomationProperties.SetAutomationId(stopButton, "parux-coding-stop");
                runButtons.Children.Add(stopButton);

                var vsCodeLive = new Button
                {
                    Content = "Open in VS Code",
                    Padding = new Thickness(12, 4, 12, 4)
                };
                vsCodeLive.Click += async (_, _) =>
                {
                    string? worktree = await ResolveTaskWorktreeAsync(message.CodeTaskId!);
                    if (!string.IsNullOrWhiteSpace(worktree) && Directory.Exists(worktree))
                        StartConfiguredVsCode(worktree);
                    else
                        ShowTransientStatus("Worktree not ready yet.");
                };
                AutomationProperties.SetAutomationId(vsCodeLive, "parux-coding-live-vscode");
                runButtons.Children.Add(vsCodeLive);

                panel.Children.Add(runButtons);
            }

            AppendCodingReviewStatusUi(panel, message, task);
        }

        /// <summary>
        /// Review findings with per-finding state, the parent/child timeline and the "Revert my changes"
        /// action. All three come from the task's additive review status document; nothing is shown when the
        /// task has published none of them. Revert is requested through the desktop-owner API and executed by
        /// the desktop worker on exactly the files the agent wrote.
        /// </summary>
        private void AppendCodingReviewStatusUi(StackPanel panel, ChatMessage message, AgentTaskDto task)
        {
            if (string.IsNullOrWhiteSpace(task.ReviewStatusJson)) return;
            AgentTaskReviewStatusDto? status;
            try
            {
                status = System.Text.Json.JsonSerializer.Deserialize<AgentTaskReviewStatusDto>(
                    task.ReviewStatusJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            catch (System.Text.Json.JsonException) { return; }
            if (status == null) return;

            if (status.Findings.Count > 0)
            {
                var findingsStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
                findingsStack.Children.Add(new TextBlock
                {
                    Text = "Review findings (" + status.Findings.Count(f => f.State == "open") + " open of " + status.Findings.Count + ")",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 12
                });
                foreach (AgentReviewFindingStatusDto finding in status.Findings.Take(30))
                {
                    string line = "[" + finding.State + "] " + finding.Severity + "  " + finding.File + ":" + finding.StartLine
                        + (finding.EndLine > finding.StartLine ? "-" + finding.EndLine : string.Empty) + "  " + finding.Message;
                    if (!string.IsNullOrWhiteSpace(finding.OutdatedReason)) line += "  (" + finding.OutdatedReason + ")";
                    findingsStack.Children.Add(new TextBlock
                    {
                        Text = line,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = finding.State == "fixed" ? 0.6 : 0.9
                    });
                }
                panel.Children.Add(AutoId(findingsStack, "CodingTask_Findings_List"));
            }

            if (status.ChildTimeline.Count > 0)
            {
                var timelineStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
                timelineStack.Children.Add(new TextBlock
                {
                    Text = "Delegated work timeline",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 12
                });
                foreach (AgentChildTimelineLineDto entry in status.ChildTimeline.TakeLast(20))
                {
                    timelineStack.Children.Add(new TextBlock
                    {
                        Text = entry.AtUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + entry.Kind + "  " + entry.Subject
                            + (string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : " - " + entry.Detail),
                        FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.85
                    });
                }
                panel.Children.Add(AutoId(timelineStack, "CodingTask_Timeline"));
            }

            AgentOwnChangeRevertStateDto? revert = status.Revert;
            if (revert is { Offered: true } || revert is { Completed: true })
            {
                if (revert.Completed)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Agent changes reverted: " + string.Join(", ", revert.Items.Select(i => i.Path + " (" + i.Outcome + ")")),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.85
                    });
                }
                else
                {
                    var revertButton = AutoId(new Button
                    {
                        Content = revert.Requested ? "Reverting..." : "Revert my changes",
                        Padding = new Thickness(12, 4, 12, 4),
                        IsEnabled = !revert.Requested
                    }, "CodingTask_Revert_Button");
                    ToolTipService.SetToolTip(revertButton,
                        "Verification failed. Undo only the " + revert.OwnChangedFiles
                        + " file(s) the agent changed; your own edits are never touched.");
                    revertButton.Click += async (_, _) =>
                    {
                        revertButton.IsEnabled = false;
                        try
                        {
                            await AgentOrchestratorClient.Current.RequestRevertOwnChangesAsync(message.CodeTaskId!, CancellationToken.None);
                            revertButton.Content = "Reverting...";
                            ShowTransientStatus("Reverting the agent's changes.");
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Revert agent changes request failed: " + ex);
                            ShowTransientStatus(UserFailureRecoveryMapper.Map(ex, "Reverting the agent's changes failed").FormatStatusLine());
                            revertButton.IsEnabled = true;
                        }
                    };
                    panel.Children.Add(revertButton);
                }
            }
        }

        private async Task PickCodeFolderAndLaunchAsync(ChatMessage message, Border card)
        {
            try
            {
                string? root = await CodeHandoffService.ChooseRepositoryAsync(ProjectWorkspaceSession.Current.ProjectId);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    RefreshCodeFolderLabel();
                    RefreshCodingTaskCardUi(message, card);
                    await AutoLaunchCodingTaskAsync(message, card);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Pick code folder failed: " + ex);
                ShowTransientStatus(UserFailureRecoveryMapper.MapFolderSelection(ex).FormatStatusLine());
            }
        }

        private async Task PickCodeFolderAndBindAsync(ChatMessage message, Border card)
        {
            try
            {
                string? chosen = await CodeHandoffService.ChooseRepositoryAsync(ProjectWorkspaceSession.Current.ProjectId);
                if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen))
                {
                    CodingWorkspaceBindingResult result = CodingTaskSandboxWorkspacePolicy.BindSandboxTaskToRepository(message, chosen);
                    if (result.Success)
                    {
                        ChatSession.Current.Persist();
                        RefreshCodeFolderLabel();
                        RefreshCodingTaskCardUi(message, card);
                        ShowTransientStatus(result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Bind code folder failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapFolderSelection(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
            }
        }

        

        

        private static string? ResolvePreferredCodingRoot()
        {
            string? objective = ChatSession.Current.Conversation.Messages
                .LastOrDefault(candidate => string.Equals(candidate.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content;
            string? explicitPath = CodingTaskIntent.ExtractExplicitWindowsPath(objective);
            if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
                return explicitPath;

            string? projectRoot = !string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId)
                ? ProjectWorkspaceSession.Current.RepositoryRoot
                : null;
            string? configured = SettingsStore.Current.Data.AgentCodingWorkspaceRoot;

            return CodingTaskSandboxWorkspacePolicy.ResolveEffectiveCodingRoot(
                explicitPath,
                projectRoot,
                configured,
                fallbackToDefaultSandbox: true);


        }

        private async Task AutoLaunchCodingTaskAsync(ChatMessage message, Border card)
        {
            try
            {
                ShowTransientStatus("Starting PaRuX Coding Agent on the project…");
                Conversation conversation = ChatSession.Current.Conversation;
                string? permissionMode = await ChooseCodingRunPermissionModeAsync(conversation);
                if (permissionMode == null)
                {
                    ShowTransientStatus("Coding task cancelled.");
                    _autoLaunchingCodingTasks.Remove(message);
                    RefreshCodingTaskCardUi(message, card);
                    return;
                }
                CodeHandoffResult result = await CodeHandoffService
                    .PrepareAndLaunchAsync(conversation, permissionMode);
                if (result.Cancelled)
                {
                    ShowTransientStatus("Coding task cancelled.");
                    _autoLaunchingCodingTasks.Remove(message);
                    RefreshCodingTaskCardUi(message, card);
                    return;
                }

                message.CodeTaskId = result.TaskId;
                message.CodeWorkspacePath = result.WorkspacePath;
                ChatSession.Current.Persist();
                ShowTransientStatus("PaRuX Coding Agent started.");

                StartCodingTaskMonitoring(message, card);
            }
            catch (Exception ex)
            {
                Log.Write("Auto-launch coding task failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskLaunch(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
                _autoLaunchingCodingTasks.Remove(message);
                RefreshCodingTaskCardUi(message, card);
            }
        }

        private async Task<string?> ChooseCodingRunPermissionModeAsync(Conversation conversation)
        {
            var picker = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
            picker.Items.Add(new ComboBoxItem { Content = "Bypass — keep current behavior", Tag = CodingAgentPermissionMode.Bypass });
            picker.Items.Add(new ComboBoxItem { Content = "Ask — approve commands and file changes", Tag = CodingAgentPermissionMode.Ask });
            picker.Items.Add(new ComboBoxItem { Content = "Plan — read-only actions", Tag = CodingAgentPermissionMode.Plan });
            string current = CodingAgentPermissionMode.Normalize(conversation.CodingRunPermissionMode);
            picker.SelectedIndex = current switch
            {
                CodingAgentPermissionMode.Ask => 1,
                CodingAgentPermissionMode.Plan => 2,
                _ => 0
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "Choose permissions for coding tasks in this conversation. This choice does not change other conversations or global settings.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(picker);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Coding run permissions",
                Content = content,
                PrimaryButtonText = "Start coding",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            AutomationProperties.SetAutomationId(dialog, "parux-coding-permission-mode-dialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || picker.SelectedItem is not ComboBoxItem item
                || item.Tag is not string selected)
                return null;

            string normalized = CodingAgentPermissionMode.Normalize(selected);
            conversation.CodingRunPermissionMode = normalized;
            ChatSession.Current.Persist();
            return normalized;
        }

        private void StartCodingTaskMonitoring(ChatMessage message, Border card)
        {
            if (string.IsNullOrWhiteSpace(message.CodeTaskId)) return;
            string taskId = message.CodeTaskId;

            if (_activeCodingMonitors.ContainsKey(taskId)) return;
            var cts = new CancellationTokenSource();
            _activeCodingMonitors[taskId] = cts;

            _ = Task.Run(async () =>
            {
                string? cachedDiff = null;
                string? lastUiSignature = null;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        AgentTaskDto task = await AgentOrchestratorClient.Current.GetAsync(taskId, cts.Token).ConfigureAwait(false);
                        AgentTaskLogListResponse logs = await AgentOrchestratorClient.Current.GetLogsAsync(taskId, cts.Token).ConfigureAwait(false);
                        AgentUserQuestionStateDto? question = null;

                        if (string.Equals(task.Status, "awaiting-user-input", StringComparison.OrdinalIgnoreCase))
                        {
                            question = await AgentOrchestratorClient.Current.GetUserQuestionStateAsync(taskId, cts.Token).ConfigureAwait(false);
                        }

                        if (string.Equals(task.Status, AgentTaskStatus.Completed, StringComparison.OrdinalIgnoreCase) && cachedDiff == null)
                        {
                            string? worktree = await ResolveTaskWorktreeAsync(taskId).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(worktree) && Directory.Exists(worktree))
                            {
                                CodeHandoffCheckResult check = await CodeHandoffService.CheckResultAsync(worktree).ConfigureAwait(false);
                                cachedDiff = check.DiffStat;
                            }
                        }

                        AgentTaskLogEntryDto? lastLog = logs.Logs.LastOrDefault();
                        string uiSignature = string.Join("|",
                            task.Status,
                            task.Stage,
                            task.ProgressPercent,
                            logs.Logs.Count,
                            lastLog?.TimestampUtc.Ticks ?? 0,
                            lastLog?.Message ?? string.Empty,
                            question?.PendingQuestion?.Id ?? string.Empty,
                            cachedDiff ?? string.Empty);
                        if (!string.Equals(uiSignature, lastUiSignature, StringComparison.Ordinal))
                        {
                            lastUiSignature = uiSignature;
                            _dispatcher.TryEnqueue(() => RefreshCodingTaskCardUi(message, card, task, logs, question, cachedDiff));
                        }

                        if (AgentTaskStatus.Terminal.Contains(task.Status))
                        {
                            _activeCodingMonitors.Remove(taskId);
                            break;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Write("Coding task monitor error for " + taskId + ": " + ex.Message);
                    }

                    try { await Task.Delay(1500, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        private async Task StopCodingTaskAsync(ChatMessage message, Button stopButton)
        {
            if (string.IsNullOrWhiteSpace(message.CodeTaskId)) return;
            stopButton.IsEnabled = false;
            try
            {
                await AgentOrchestratorClient.Current.CancelAsync(message.CodeTaskId, CancellationToken.None);
                ShowTransientStatus("Cancellation requested for task " + message.CodeTaskId + ".");
            }
            catch (Exception ex)
            {
                Log.Write("Stop coding task failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskCancel(ex, message.CodeTaskId);
                ShowTransientStatus(guidance.FormatStatusLine());
            }
            finally
            {
                stopButton.IsEnabled = true;
            }
        }

        private static async Task<string?> FingerprintReviewedTaskDiffAsync(
            AgentRepositorySetDto authorities, string worktree, bool multiMode,
            CancellationToken cancellationToken)
        {
            if (authorities == null || string.IsNullOrWhiteSpace(worktree))
                return null;
            if (multiMode)
            {
                if (!CodingWorkspaceGitLayoutPolicy.TrySelect(worktree, 16,
                        out IReadOnlyList<(string Alias, string Root)> selected)
                    || selected.Count != authorities.Repositories.Count)
                    return null;
                var owned = new HashSet<string>(
                    authorities.Repositories.Select(item => item.Alias),
                    StringComparer.OrdinalIgnoreCase);
                if (owned.Count != authorities.Repositories.Count
                    || selected.Any(item => !owned.Contains(item.Alias)))
                    return null;
            }
            else if (authorities.Repositories.Count != 1)
                return null;

            // This is the exact state receipt used by the agent and Core
            // handoff, including each repository's HEAD and bounded untracked
            // content. Recompute again *inside* ApplyAsync, not only here.
            return await CodingWorkspaceVerificationFingerprint.CaptureAsync(
                worktree, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> IsFreshCodingTaskApplyApprovedAsync(string taskId) =>
            (await GetFreshCodingTaskApplyApprovalAsync(taskId).ConfigureAwait(false)).Approved;

        /// <summary>
        /// Core approval plus the Git-state fingerprint that Core verified for this task. The reviewed diff
        /// must reproduce that fingerprint before any user repository is written.
        /// </summary>
        private async Task<(bool Approved, string? VerifiedFingerprint)> GetFreshCodingTaskApplyApprovalAsync(string taskId)
        {
#if DEBUG
            if (IsCompletedCodingTaskApplyUiTestFixture(taskId))
                return (true, null); // Explicit, compiled-out Release-only UI fixture.
#endif
            AgentTaskDto current = await AgentOrchestratorClient.Current
                .GetAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            return (CodingTaskApplyAdmissionPolicy.CanApply(current, taskId),
                CodingTaskApplyAdmissionPolicy.TryGetVerifiedWorkspaceFingerprint(current, taskId));
        }

        private async Task ApplyReviewedChangesAsync(ChatMessage message, Button applyButton)
        {
            if (string.IsNullOrWhiteSpace(message.CodeTaskId)) return;
            applyButton.IsEnabled = false;
            bool applied = false;
            try
            {
                if (!await IsFreshCodingTaskApplyApprovedAsync(message.CodeTaskId))
                {
                    ShowTransientStatus("Apply blocked: the task is not verified by Core with accepted tests.");
                    return;
                }
#if DEBUG
                bool applyUiTestFixture = IsCompletedCodingTaskApplyUiTestFixture(message.CodeTaskId);
#endif
                string? worktree = await ResolveTaskWorktreeAsync(message.CodeTaskId);
                if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
                {
                    ShowTransientStatus("No worktree available to apply for this task.");
                    return;
                }

                AgentRepositorySetDto repositories;
#if DEBUG
                string? fixtureRepository = applyUiTestFixture
                    ? Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_APPLY_REPOSITORY")
                    : null;
                if (applyUiTestFixture && !string.IsNullOrWhiteSpace(fixtureRepository))
                    repositories = new AgentRepositorySetDto
                    {
                        TaskId = message.CodeTaskId,
                        Repositories = new List<AgentRepositoryAuthorityDto>
                        {
                            new() { Alias = "primary", WorkspaceRoot = fixtureRepository, IsPrimary = true }
                        }
                    };
                else
#endif
                    repositories = await AgentOrchestratorClient.Current
                        .GetRepositorySetAsync(message.CodeTaskId, CancellationToken.None);
                bool multiMode = repositories.Repositories.Count > 1;
                string reviewText;

                if (multiMode)
                {
                    MultiRepositoryReviewResult multiReview = await MultiRepositoryApplyService.ReviewAsync(
                        repositories, worktree, CancellationToken.None);
                    if (!multiReview.Success)
                    {
                        ShowTransientStatus("Multi-repository review incomplete: " + (multiReview.Error ?? multiReview.Summary));
                        return;
                    }
                    if (!multiReview.HasChanges)
                    {
                        ShowTransientStatus("No changes to apply.");
                        return;
                    }
                    reviewText = multiReview.DiffStat;
                }
                else
                {
                    CodeHandoffCheckResult check = await CodeHandoffService.CheckResultAsync(worktree);
                    if (!check.HasChanges)
                    {
                        ShowTransientStatus(string.IsNullOrWhiteSpace(check.Error)
                            ? "No changes to apply."
                            : "Review incomplete: " + check.Error);
                        return;
                    }
                    reviewText = check.DiffStat;
                }

                string? reviewedDiffFingerprint = await FingerprintReviewedTaskDiffAsync(
                    repositories, worktree, multiMode, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(reviewedDiffFingerprint))
                {
                    ShowTransientStatus("The diff is no longer available for a verifiable review.");
                    return;
                }

                bool isShadow = false;
                string targetName = "original repository";
                string? repositoryRoot = null;
                if (!multiMode)
                {
                    if (!string.IsNullOrWhiteSpace(message.CodeWorkspacePath)
                        && Directory.Exists(message.CodeWorkspacePath)
                        && !CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(message.CodeWorkspacePath))
                    {
                        repositoryRoot = message.CodeWorkspacePath;
                    }
                    else
                    {
                        repositoryRoot = AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(repositories, message.CodeTaskId);
                    }

                    if (Directory.Exists(repositoryRoot)
                        && ShadowWorkspaceService.IsShadowRepository(repositoryRoot))
                    {
                        isShadow = true;
                        string? orig = ShadowWorkspaceService.TryGetOriginalPath(repositoryRoot);
                        targetName = !string.IsNullOrWhiteSpace(orig) ? System.IO.Path.GetFileName(orig) : "target folder";
                    }
                    else if (Directory.Exists(repositoryRoot))
                    {
                        targetName = CodingTaskSandboxWorkspacePolicy.IsDefaultSandboxWorkspace(repositoryRoot)
                            ? "sandbox workspace"
                            : System.IO.Path.GetFileName(repositoryRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                    }
                }

                string verificationWarning = string.Empty;
                try
                {
                    AgentTaskDto reviewed = await AgentOrchestratorClient.Current.GetAsync(message.CodeTaskId!, CancellationToken.None);
                    verificationWarning = CodingVerificationPresentation.For(reviewed.VerificationStatus).ApplyWarning;
                }
                catch (Exception ex)
                {
                    Log.Write("Could not read verification status before apply confirmation: " + ex.Message);
                    verificationWarning = CodingVerificationPresentation.For(null).ApplyWarning;
                }

                CodingTaskReviewModel? reviewModel = _codingTaskReviewModels.TryGetValue(message, out CodingTaskReviewModel? rm) ? rm : null;
                if (reviewModel != null && reviewModel.TotalFiles > 0 && reviewModel.NoneAccepted)
                {
                    ShowTransientStatus("No changes selected to apply.");
                    return;
                }

                string confirmationText = reviewModel != null && reviewModel.TotalFiles > 0
                    ? reviewModel.FormatConfirmationSummary(targetName, multiMode, isShadow)
                    : (multiMode
                        ? "PaRuX will apply reviewed changes as uncommitted local modifications. No git commit or push will be executed.\n\n" + reviewText
                        : isShadow


                            ? "Changes will be applied directly to folder '" + targetName + "'. No git repository will be created in the folder.\n\n" + reviewText
                            : "Changes will be applied to the repository as uncommitted local modifications. No git commit or push will be executed.\n\n" + reviewText);

                var confirmation = new ContentDialog
                {
                    XamlRoot = applyButton.XamlRoot,
                    Title = multiMode ? "Apply the changes to all repositories?" : "Apply the changes to the project?",
                    Content = verificationWarning + confirmationText,
                    PrimaryButtonText = "Apply changes",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                AutoId(confirmation, "Chat_CodingTask_ApplyConfirm_Dialog");
                ContentDialogResult decision = await confirmation.ShowAsync();
                Log.Write("Reviewed coding-task apply confirmation " + message.CodeTaskId + ": " + decision);
                if (decision != ContentDialogResult.Primary) return;
                var acceptedAtConfirmation = reviewModel != null && reviewModel.TotalFiles > 0 ? reviewModel.GetAcceptedPaths() : null; (bool approvedNow, string? verifiedFingerprint) =
                    await GetFreshCodingTaskApplyApprovalAsync(message.CodeTaskId);
                if (!approvedNow)
                {
                    ShowTransientStatus("Apply blocked: task verification status changed.");
                    return;
                }
                string? currentFingerprint = await FingerprintReviewedTaskDiffAsync(
                    repositories, worktree, multiMode, CancellationToken.None);
                if (currentFingerprint == null
                    || !string.Equals(currentFingerprint, reviewedDiffFingerprint, StringComparison.Ordinal))
                {
                    ShowTransientStatus("Diff changed after review. Please re-review before applying.");
                    return;
                }

                if (multiMode)
                {
                    MultiRepositoryApplyResult result = await MultiRepositoryApplyService.ApplyAsync(
                        repositories, worktree, CancellationToken.None, reviewedDiffFingerprint);
                    string stats = string.Join("\n\n", result.Repositories
                        .Where(item => item.HasChanges)
                        .Select(item => "[" + item.Alias + "]\n" + item.DiffStat));
                    ChatSession.Current.AppendAssistantNotice(
                        "**PaRuX multi-repository apply** — " + result.Message
                        + (string.IsNullOrWhiteSpace(stats) ? string.Empty : "\n\n```\n" + stats + "\n```"));
                    // Derived from the typed counts, so a partial set is never reported as nothing having changed.
                    ShowTransientStatus(!result.Success
                        ? UserFailureRecoveryMapper.MapCodingApplyResult(result.Message).FormatStatusLine()
                        : result.AppliedCount == 0 && result.AlreadyPresentCount > 0
                            ? "Changes were already present across repositories: nothing was modified."
                            : result.AppliedCount > 0 && result.AlreadyPresentCount > 0
                                ? "Changes applied to " + result.AppliedCount + " repositories; "
                                  + result.AlreadyPresentCount + " already present, not modified."
                                : "Changes applied successfully as uncommitted local modifications.");
                    // Latch the card after any successful multi-repository apply, as the single-repository path does, so
                    // a second click cannot re-run a patch that is already present.
                    applied = result.Success;
                }
                else
                {
                    if (repositoryRoot == null || !Directory.Exists(repositoryRoot))
                    {
                        ShowTransientStatus("Repository authority is unavailable.");
                        return;
                    }
                    CodeApplyResult result = await CodeWorktreeApplyService.ApplyAsync(
                        repositoryRoot, worktree, CancellationToken.None, reviewedDiffFingerprint, verifiedFingerprint,
                        acceptedAtConfirmation);
                    Log.Write("Reviewed coding-task apply " + message.CodeTaskId + ": success=" + result.Success
                        + "; " + result.Message);
                    ChatSession.Current.AppendAssistantNotice(
                        "**PaRuX apply** — " + result.Message
                        + (!string.IsNullOrWhiteSpace(result.DiffStat) ? "\n\n```\n" + result.DiffStat + "\n```" : string.Empty));
                    bool alreadyPresent = result.Success
                        && result.Message.Contains("already present", StringComparison.OrdinalIgnoreCase);
                    ShowTransientStatus(!result.Success
                        ? UserFailureRecoveryMapper.MapCodingApplyResult(result.Message).FormatStatusLine()
                        : alreadyPresent
                            ? "Changes were already present in the project: nothing was changed."
                            : "Changes applied successfully to the project as uncommitted local changes.");
                    applied = result.Success;
                    if (!result.Success && !string.IsNullOrWhiteSpace(result.UnresolvedRecoveryDirectory))
                    {
                        string? originalRoot = ShadowWorkspaceService.TryGetOriginalPath(repositoryRoot);
                        if (string.IsNullOrWhiteSpace(originalRoot))
                            ShowTransientStatus("Recovery is unavailable: original folder cannot be verified.");
                        else
                            await ResolveUnresolvedShadowRecoveryAsync(applyButton.XamlRoot, originalRoot,
                                result.UnresolvedRecoveryDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Apply reviewed changes failed: " + ex);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskApply(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
            }
            finally
            {
                // After a successful apply the card shows the final state instead of inviting a re-apply.
                applyButton.IsEnabled = !applied;
                if (applied) applyButton.Content = "Changes applied to project";
            }
        }

        /// <summary>
        /// Explicit recovery for a non-Git apply refused because an earlier journal is unresolved. The dialog offers
        /// only the actions the recovery service can verify, and each one is re-checked under the project lease before
        /// anything is written or discarded. Closing the dialog changes nothing; the apply button stays available for
        /// a new attempt once the caller finishes.
        /// </summary>
        private async Task ResolveUnresolvedShadowRecoveryAsync(XamlRoot? xamlRoot, string originalRoot,
            string journalDirectory)
        {
            IReadOnlyList<ShadowApplyRecoveryInspector.RecoveryAssessment> pending =
                await Task.Run(() => ShadowApplyRecoveryService.ListPendingJournals(originalRoot));
            ShadowApplyRecoveryInspector.RecoveryAssessment? assessment = pending.FirstOrDefault(item =>
                string.Equals(item.RecoveryDirectory, journalDirectory, StringComparison.OrdinalIgnoreCase));
            if (assessment == null)
            {
                ShowTransientStatus("The recovery journal is no longer present. You can try applying the changes again.");
                return;
            }

            ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(assessment);
            string folder = assessment.RecoveryDirectory;
            ShadowRecoveryDialogModel.RecoveryChoice? choice = null;
            var content = new StackPanel { Spacing = 10 };
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = plan.Title,
                Content = content,
                CloseButtonText = plan.OffersDecision
                    ? ShadowRecoveryDialogModel.CancelButtonText
                    : ShadowRecoveryDialogModel.CloseButtonText,
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "parux-recovery-dialog");
            dialog.Resources["ContentDialogMaxWidth"] = 720d;

            content.Children.Add(new TextBlock { Text = plan.Explanation, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock
            {
                Text = plan.FileSummary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.82
            });
            content.Children.Add(new TextBlock
            {
                Text = "Recovery folder: " + folder,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72,
                IsTextSelectionEnabled = true
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (plan.OffersDecision)
            {
                var restore = new Button
                {
                    Content = ShadowRecoveryDialogModel.RestoreButtonText,
                    IsEnabled = plan.RestoreEnabled
                };
                AutomationProperties.SetAutomationId(restore, "parux-recovery-restore");
                restore.Click += (_, _) =>
                {
                    choice = ShadowRecoveryDialogModel.RecoveryChoice.Restore;
                    dialog.Hide();
                };

                var keep = new Button
                {
                    Content = ShadowRecoveryDialogModel.KeepButtonText,
                    IsEnabled = plan.KeepEnabled
                };
                AutomationProperties.SetAutomationId(keep, "parux-recovery-keep");
                keep.Click += (_, _) =>
                {
                    choice = ShadowRecoveryDialogModel.RecoveryChoice.KeepCurrentFiles;
                    dialog.Hide();
                };
                actions.Children.Add(restore);
                actions.Children.Add(keep);
            }
            else
            {
                var openFolder = new Button { Content = ShadowRecoveryDialogModel.OpenFolderButtonText };
                AutomationProperties.SetAutomationId(openFolder, "parux-recovery-open-folder");
                openFolder.Click += async (_, _) => await OpenShadowRecoveryFolderAsync(folder);
                actions.Children.Add(openFolder);
            }
            content.Children.Add(actions);

            await dialog.ShowAsync();
            if (choice == null) return; // Cancel, Close or Escape: nothing was written or discarded.
            ShadowRecoveryDialogModel.RecoveryChoice selected = choice.Value;
            if (selected == ShadowRecoveryDialogModel.RecoveryChoice.KeepCurrentFiles
                && !await ConfirmKeepCurrentFilesAsync(xamlRoot))
            {
                ShowTransientStatus(ShadowRecoveryDialogModel.KeepDeclinedText);
                return;
            }

            ShadowApplyRecoveryService.RestoreOutcome outcome = await Task.Run(() =>
                selected == ShadowRecoveryDialogModel.RecoveryChoice.Restore
                    ? ShadowApplyRecoveryService.Restore(folder, originalRoot)
                    : ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(folder, originalRoot));
            string text = ShadowRecoveryDialogModel.DescribeOutcome(outcome, selected);
            Log.Write("Shadow apply recovery " + selected + ": success=" + outcome.Success + "; " + outcome.Message);
            ChatSession.Current.AppendAssistantNotice("**PaRuX recovery** — " + text);
            ShowTransientStatus(text);
        }

        private async Task OpenShadowRecoveryFolderAsync(string folder)
        {
            try
            {
                if (!await global::Windows.System.Launcher.LaunchFolderPathAsync(folder))
                    ShowTransientStatus("Could not open the recovery folder.");
            }
            catch (Exception ex)
            {
                Log.Write("Open shadow recovery folder failed: " + ex.Message);
                ShowTransientStatus("Could not open the recovery folder.");
            }
        }

        /// <summary>
        /// The second, explicit step before the backups are deleted for good. Cancel is the default button, so Enter
        /// or Escape keeps every backup. Returns true only when the user chooses the confirm button.
        /// </summary>
        private static async Task<bool> ConfirmKeepCurrentFilesAsync(XamlRoot? xamlRoot)
        {
            ShadowRecoveryDialogModel.KeepConfirmation confirmation = ShadowRecoveryDialogModel.DescribeKeepConfirmation();
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = confirmation.Title,
                Content = new TextBlock { Text = confirmation.Message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirmation.ConfirmButtonText,
                CloseButtonText = confirmation.CancelButtonText,
                DefaultButton = confirmation.DefaultIsCancel ? ContentDialogButton.Close : ContentDialogButton.Primary
            };
            AutomationProperties.SetAutomationId(dialog, "parux-recovery-keep-confirm");
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static async Task<string?> ResolveTaskWorktreeAsync(string taskId)
        {
#if DEBUG
            if (IsCompletedCodingTaskApplyUiTestFixture(taskId))
                return Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_APPLY_WORKTREE");
#endif

            AgentDesktopWorktreeDto desktop = await AgentOrchestratorClient.Current
                .GetDesktopWorktreeAsync(taskId, CancellationToken.None);
            return desktop.WorktreePath;
        }

#if DEBUG
        private static bool IsCompletedCodingTaskApplyUiTestFixture(string taskId) =>
            string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
            && string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_TASK_CARD"),
                "completed-apply", StringComparison.Ordinal)
            && string.Equals(taskId, "ui-test-completed-coding-task", StringComparison.Ordinal);
#endif

        private static bool StartConfiguredVsCode(string worktree)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = SettingsStore.Current.Data.VsCodeCommand,
                    WorkingDirectory = worktree,
                    UseShellExecute = true // parux:shell-execute-reviewed: launches configured VS Code in target worktree
                };
                start.ArgumentList.Add("--new-window");
                start.ArgumentList.Add(worktree);
                return Process.Start(start) != null;
            }
            catch (Exception ex)
            {
                Log.Write("VS Code launch for PaRuX task failed: " + ex.Message);
                return false;
            }
        }
    }
}
