using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class ChatHomeNavigationTests
    {
        [Fact]
        public void Chat_Is_Always_First_Item_In_Sidebar_In_Simple_Mode()
        {
            IReadOnlyList<WorkspaceDescriptor> workspaces = ChatHomeNavigationPolicy.GetWorkspaces(advancedMode: false);

            Assert.NotEmpty(workspaces);
            WorkspaceDescriptor first = workspaces[0];
            Assert.Equal(ChatHomeNavigationPolicy.ChatWorkspaceTag, first.Tag);
            Assert.Equal("Chat", first.DisplayTitle);
            Assert.Equal("Navigation_Chat_Item", first.AutomationId);
            Assert.True(first.IsHome);
            Assert.False(first.IsAdvancedOnly);
            Assert.Equal(0, first.SortOrder);
        }

        [Fact]
        public void Chat_Is_Always_First_Item_In_Sidebar_In_Advanced_Mode()
        {
            IReadOnlyList<WorkspaceDescriptor> workspaces = ChatHomeNavigationPolicy.GetWorkspaces(advancedMode: true);

            Assert.NotEmpty(workspaces);
            WorkspaceDescriptor first = workspaces[0];
            Assert.Equal(ChatHomeNavigationPolicy.ChatWorkspaceTag, first.Tag);
            Assert.Equal("Chat", first.DisplayTitle);
            Assert.Equal("Navigation_Chat_Item", first.AutomationId);
            Assert.True(first.IsHome);
            Assert.Equal(0, first.SortOrder);
        }

        [Fact]
        public void Workspace_Order_In_Simple_Mode_Contains_Expected_Workspaces()
        {
            IReadOnlyList<WorkspaceDescriptor> workspaces = ChatHomeNavigationPolicy.GetWorkspaces(advancedMode: false);

            var tags = workspaces.Select(w => w.Tag).ToList();
            Assert.Equal(new[]
            {
                "__chat__",
                "__create__",
                "__projects__",
                "__modelhub__",
                "__settings__",
                "__help__"
            }, tags);
        }

        [Fact]
        public void Workspace_Order_In_Advanced_Mode_Contains_All_Workspaces()
        {
            IReadOnlyList<WorkspaceDescriptor> workspaces = ChatHomeNavigationPolicy.GetWorkspaces(advancedMode: true);

            Assert.Equal(13, workspaces.Count);
            Assert.Equal("__chat__", workspaces[0].Tag);
            Assert.Equal("__create__", workspaces[1].Tag);
            Assert.Equal("__projects__", workspaces[2].Tag);
            Assert.Equal("__modelhub__", workspaces[3].Tag);
            Assert.Equal("__modelrouting__", workspaces[4].Tag);
            Assert.Equal("__lifecycle__", workspaces[5].Tag);
            Assert.Equal("__workflows__", workspaces[6].Tag);
            Assert.Equal("__tools__", workspaces[7].Tag);
            Assert.Equal("__observability__", workspaces[8].Tag);
            Assert.Equal("__updates__", workspaces[9].Tag);
            Assert.Equal("__runtime__", workspaces[10].Tag);
            Assert.Equal("__settings__", workspaces[11].Tag);
            Assert.Equal("__help__", workspaces[12].Tag);
        }

        [Theory]
        [InlineData("__chat__", true)]
        [InlineData("__create__", false)]
        [InlineData("__modelhub__", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsHomeWorkspace_Identifies_Chat_Workspace_Correctly(string? tag, bool expected)
        {
            Assert.Equal(expected, ChatHomeNavigationPolicy.IsHomeWorkspace(tag));
        }

        [Theory]
        [InlineData("__chat__", true)]
        [InlineData("__create__", true)]
        [InlineData("__projects__", true)]
        [InlineData("__modelhub__", true)]
        [InlineData("__settings__", true)]
        [InlineData("__help__", true)]
        [InlineData("__unknown__", false)]
        [InlineData("conversation-1234", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsWorkspaceTag_Identifies_Known_Workspaces(string? tag, bool expected)
        {
            Assert.Equal(expected, ChatHomeNavigationPolicy.IsWorkspaceTag(tag));
        }

        [Theory]
        [InlineData("conv-uuid-123", true)]
        [InlineData("12345", true)]
        [InlineData("__chat__", false)]
        [InlineData("__modelhub__", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsConversationTag_Distinguishes_Conversation_Ids(string? tag, bool expected)
        {
            Assert.Equal(expected, ChatHomeNavigationPolicy.IsConversationTag(tag));
        }

        [Fact]
        public void EvaluateNavigation_From_ModelHub_To_ChatHome_Preserves_Conversation_And_Draft()
        {
            const string activeConvId = "conversation-alpha";
            NavigationDecision decision = ChatHomeNavigationPolicy.EvaluateNavigation(
                targetTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                currentWorkspaceTag: ChatHomeNavigationPolicy.ModelHubWorkspaceTag,
                openConversationId: activeConvId,
                hasDraft: true);

            Assert.Equal(NavigationDestinationKind.ChatHome, decision.DestinationKind);
            Assert.Equal(ChatHomeNavigationPolicy.ChatWorkspaceTag, decision.DestinationTag);
            Assert.True(decision.IsHomeNavigation);
            Assert.True(decision.ShouldPreserveOpenConversation);
            Assert.True(decision.ShouldPreserveDraft);
            Assert.Equal(activeConvId, decision.TargetConversationId);
        }

        [Fact]
        public void EvaluateNavigation_From_Settings_To_ChatHome_Preserves_Conversation_And_Draft()
        {
            const string activeConvId = "conversation-beta";
            NavigationDecision decision = ChatHomeNavigationPolicy.EvaluateNavigation(
                targetTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                currentWorkspaceTag: ChatHomeNavigationPolicy.SettingsWorkspaceTag,
                openConversationId: activeConvId,
                hasDraft: true);

            Assert.Equal(NavigationDestinationKind.ChatHome, decision.DestinationKind);
            Assert.True(decision.IsHomeNavigation);
            Assert.True(decision.ShouldPreserveOpenConversation);
            Assert.True(decision.ShouldPreserveDraft);
            Assert.Equal(activeConvId, decision.TargetConversationId);
        }

        [Fact]
        public void EvaluateNavigation_From_Create_To_ChatHome_Preserves_Conversation_And_Draft()
        {
            NavigationDecision decision = ChatHomeNavigationPolicy.EvaluateNavigation(
                targetTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                currentWorkspaceTag: ChatHomeNavigationPolicy.CreateWorkspaceTag,
                openConversationId: "conv-create-return",
                hasDraft: true);

            Assert.Equal(NavigationDestinationKind.ChatHome, decision.DestinationKind);
            Assert.True(decision.IsHomeNavigation);
            Assert.True(decision.ShouldPreserveOpenConversation);
            Assert.True(decision.ShouldPreserveDraft);
        }

        [Fact]
        public void EvaluateNavigation_To_Specific_Conversation_Switches_Conversation()
        {
            NavigationDecision decision = ChatHomeNavigationPolicy.EvaluateNavigation(
                targetTag: "conversation-target-42",
                currentWorkspaceTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                openConversationId: "conversation-source-1",
                hasDraft: true);

            Assert.Equal(NavigationDestinationKind.SpecificConversation, decision.DestinationKind);
            Assert.False(decision.IsHomeNavigation);
            Assert.False(decision.ShouldPreserveOpenConversation);
            Assert.True(decision.ShouldPreserveDraft);
            Assert.Equal("conversation-target-42", decision.TargetConversationId);
        }

        [Fact]
        public void EvaluateNavigation_To_Other_Workspace_Preserves_Chat_State_In_Background()
        {
            NavigationDecision decision = ChatHomeNavigationPolicy.EvaluateNavigation(
                targetTag: ChatHomeNavigationPolicy.ModelHubWorkspaceTag,
                currentWorkspaceTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                openConversationId: "conv-before-modelhub",
                hasDraft: true);

            Assert.Equal(NavigationDestinationKind.Workspace, decision.DestinationKind);
            Assert.False(decision.IsHomeNavigation);
            Assert.True(decision.ShouldPreserveOpenConversation);
            Assert.True(decision.ShouldPreserveDraft);
            Assert.Equal("conv-before-modelhub", decision.TargetConversationId);
        }

        [Fact]
        public void ResolveSidebarSelectionTag_Returns_ConversationId_When_In_Recent()
        {
            string selected = ChatHomeNavigationPolicy.ResolveSidebarSelectionTag(
                activeWorkspaceTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                openConversationId: "conv-in-recent",
                isConversationInRecentList: true);

            Assert.Equal("conv-in-recent", selected);
        }

        [Fact]
        public void ResolveSidebarSelectionTag_Falls_Back_To_Chat_When_Conversation_Not_In_Recent()
        {
            string selected = ChatHomeNavigationPolicy.ResolveSidebarSelectionTag(
                activeWorkspaceTag: ChatHomeNavigationPolicy.ChatWorkspaceTag,
                openConversationId: "conv-brand-new-unsaved",
                isConversationInRecentList: false);

            Assert.Equal(ChatHomeNavigationPolicy.ChatWorkspaceTag, selected);
        }

        [Fact]
        public void ResolveSidebarSelectionTag_Returns_WorkspaceTag_When_In_Another_Workspace()
        {
            string selected = ChatHomeNavigationPolicy.ResolveSidebarSelectionTag(
                activeWorkspaceTag: ChatHomeNavigationPolicy.ModelHubWorkspaceTag,
                openConversationId: "conv-bg",
                isConversationInRecentList: true);

            Assert.Equal(ChatHomeNavigationPolicy.ModelHubWorkspaceTag, selected);
        }

        [Fact]
        public void FormatChatNavItemAccessibleName_Includes_Home_And_Draft_Details()
        {
            string nameWithDraft = ChatHomeNavigationPolicy.FormatChatNavItemAccessibleName(
                activeConversationTitle: "Rust Concurrency",
                hasDraft: true);
            Assert.Contains("Chat, home workspace", nameWithDraft);
            Assert.Contains("active conversation: Rust Concurrency", nameWithDraft);
            Assert.Contains("with unsent draft", nameWithDraft);

            string nameNoDraft = ChatHomeNavigationPolicy.FormatChatNavItemAccessibleName(
                activeConversationTitle: "New chat",
                hasDraft: false);
            Assert.Equal("Chat, home workspace", nameNoDraft);
        }

        [Fact]
        public void ChatDraftCoordinator_Saves_And_Retrieves_Draft_Per_Conversation()
        {
            var coordinator = new ChatDraftCoordinator();
            coordinator.SaveDraft("conv-1", "Draft for conversation 1");
            coordinator.SaveDraft("conv-2", "Draft for conversation 2");

            Assert.Equal("Draft for conversation 1", coordinator.GetDraft("conv-1"));
            Assert.Equal("Draft for conversation 2", coordinator.GetDraft("conv-2"));
            Assert.True(coordinator.HasDraft("conv-1"));
            Assert.True(coordinator.HasDraft("conv-2"));
            Assert.False(coordinator.HasDraft("conv-3"));
            Assert.Empty(coordinator.GetDraft("conv-3"));
        }

        [Fact]
        public void ChatDraftCoordinator_Maintains_Separate_Draft_For_New_Chat()
        {
            var coordinator = new ChatDraftCoordinator();
            coordinator.SaveDraft(null, "Unsent new chat prompt");
            coordinator.SaveDraft("conv-saved", "Saved chat draft");

            Assert.True(coordinator.HasDraft(null));
            Assert.True(coordinator.HasDraft(string.Empty));
            Assert.Equal("Unsent new chat prompt", coordinator.GetDraft(null));
            Assert.Equal("Saved chat draft", coordinator.GetDraft("conv-saved"));
        }

        [Fact]
        public void ChatDraftCoordinator_Clears_Draft_On_Send()
        {
            var coordinator = new ChatDraftCoordinator();
            coordinator.SaveDraft("conv-send", "Message about to be sent");
            Assert.True(coordinator.HasDraft("conv-send"));

            coordinator.ClearDraft("conv-send");
            Assert.False(coordinator.HasDraft("conv-send"));
            Assert.Empty(coordinator.GetDraft("conv-send"));
        }

        [Fact]
        public void ChatDraftCoordinator_Handles_Null_And_Whitespace_Drafts()
        {
            var coordinator = new ChatDraftCoordinator();
            coordinator.SaveDraft("conv-ws", "Valid text");
            Assert.True(coordinator.HasDraft("conv-ws"));

            coordinator.SaveDraft("conv-ws", "   ");
            Assert.False(coordinator.HasDraft("conv-ws"));
            Assert.Empty(coordinator.GetDraft("conv-ws"));
        }

        [Fact]
        public void ChatDraftCoordinator_Preserves_Draft_Across_Simulated_Workspace_Switch()
        {
            var coordinator = new ChatDraftCoordinator();
            const string convId = "conv-active-session";
            const string typedDraft = "Can you help me profile this C# program?";

            // 1. User types draft in Chat
            coordinator.SaveDraft(convId, typedDraft);

            // 2. User switches to Model Hub workspace (Chat page collapsed)
            ChatDraftSnapshot snapshot = coordinator.CaptureState(convId, coordinator.GetDraft(convId));
            Assert.True(snapshot.HasDraft);
            Assert.Equal(typedDraft, snapshot.DraftText);

            // 3. User returns to Chat workspace (Chat page activated)
            string restoredDraft = coordinator.GetDraft(convId);
            Assert.Equal(typedDraft, restoredDraft);
            Assert.True(coordinator.HasDraft(convId));
        }

        [Fact]
        public void AccessibleKeyboardRegistry_Registers_ChatHome_Shortcut()
        {
            AccessibleKeyboardRegistry registry = AccessibleKeyboardRegistry.Default;
            IReadOnlyList<AccessibleKeyboardShortcut> shortcuts = registry.GetAll();

            AccessibleKeyboardShortcut? chatShortcut = shortcuts.FirstOrDefault(s => s.Id == "App_GoToChatHome");
            Assert.NotNull(chatShortcut);
            Assert.Equal("Alt+H", chatShortcut.KeyCombo.Formatted);
            Assert.Equal("Navigation_Chat_Item", chatShortcut.AssociatedAutomationId);
            Assert.Equal(ShortcutScope.Global, chatShortcut.Scope);

            // Ensure no conflicts with existing shortcuts
            IReadOnlyList<string> conflicts = registry.DetectConflicts();
            Assert.Empty(conflicts);
        }

        [Fact]
        public void InAppHelpCatalog_Documents_Chat_Home_Workspace_And_Draft_Preservation()
        {
            InAppHelpCatalog catalog = InAppHelpCatalog.Default;
            InAppHelpTopic? workspacesTopic = catalog.GetTopic("workspaces");

            Assert.NotNull(workspacesTopic);
            Assert.Contains("Chat is the home workspace", workspacesTopic.Content);
            Assert.Contains("listed first at the top of the sidebar", workspacesTopic.Content);
            Assert.Contains("one click away", workspacesTopic.Content);
            Assert.Contains("unsent draft", workspacesTopic.Content);

            InAppHelpTopic? shortcutsTopic = catalog.GetTopic("shortcuts");
            Assert.NotNull(shortcutsTopic);
            Assert.Contains("Alt+H", shortcutsTopic.Content);
            Assert.Contains("Return to Chat", shortcutsTopic.Content);
        }
    }
}
