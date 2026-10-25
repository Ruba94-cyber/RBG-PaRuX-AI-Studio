using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    public enum NavigationDestinationKind
    {
        ChatHome,
        SpecificConversation,
        Workspace,
        Unknown
    }

    /// <summary>
    /// Metadata descriptor for a sidebar workspace item.
    /// </summary>
    public sealed record WorkspaceDescriptor(
        string Tag,
        string DisplayTitle,
        string AutomationId,
        string ToolTip,
        bool IsHome,
        bool IsAdvancedOnly,
        int SortOrder);

    /// <summary>
    /// Decision outcome from evaluating a navigation transition.
    /// </summary>
    public sealed record NavigationDecision(
        NavigationDestinationKind DestinationKind,
        string DestinationTag,
        bool IsHomeNavigation,
        bool ShouldPreserveOpenConversation,
        bool ShouldPreserveDraft,
        string? TargetConversationId);

    /// <summary>
    /// Pure policy governing sidebar workspace navigation and the Chat Home workspace contract.
    /// Guarantees that Chat is always listed first in the sidebar, is one click away from every workspace,
    /// and that returning to Chat preserves the open conversation and unsent draft.
    /// </summary>
    public static class ChatHomeNavigationPolicy
    {
        public const string ChatWorkspaceTag = "__chat__";
        public const string CreateWorkspaceTag = "__create__";
        public const string ProjectsWorkspaceTag = "__projects__";
        public const string ModelHubWorkspaceTag = "__modelhub__";
        public const string ModelRoutingWorkspaceTag = "__modelrouting__";
        public const string LifecycleWorkspaceTag = "__lifecycle__";
        public const string WorkflowWorkspaceTag = "__workflows__";
        public const string ToolsWorkspaceTag = "__tools__";
        public const string ObservabilityWorkspaceTag = "__observability__";
        public const string UpdatesWorkspaceTag = "__updates__";
        public const string RuntimeWorkspaceTag = "__runtime__";
        public const string SettingsWorkspaceTag = "__settings__";
        public const string HelpWorkspaceTag = "__help__";

        public const string ChatAutomationId = "Navigation_Chat_Item";
        public const string ChatDisplayName = "Chat";
        public const string ChatToolTip = "Chat (Home workspace) - Return to current conversation and draft";

        private static readonly IReadOnlyList<WorkspaceDescriptor> AllWorkspaces = new List<WorkspaceDescriptor>
        {
            new(ChatWorkspaceTag, ChatDisplayName, ChatAutomationId, ChatToolTip, IsHome: true, IsAdvancedOnly: false, SortOrder: 0),
            new(CreateWorkspaceTag, "Create", "Navigation_Create_Item", "Generate images, video, speech and 3D assets", IsHome: false, IsAdvancedOnly: false, SortOrder: 1),
            new(ProjectsWorkspaceTag, "Projects", "Navigation_Projects_Item", "Manage project contexts and document knowledge bases", IsHome: false, IsAdvancedOnly: false, SortOrder: 2),
            new(ModelHubWorkspaceTag, "Model Hub", "Navigation_ModelHub_Item", "Discover, evaluate hardware fit, and download models", IsHome: false, IsAdvancedOnly: false, SortOrder: 3),
            new(ModelRoutingWorkspaceTag, "Model Intelligence", "Navigation_ModelRouting_Item", "Configure automatic routing policies and task classification", IsHome: false, IsAdvancedOnly: true, SortOrder: 4),
            new(LifecycleWorkspaceTag, "Lifecycle Lab", "Navigation_Lifecycle_Item", "Evaluate fine-tuning, training datasets, and model lifecycle", IsHome: false, IsAdvancedOnly: true, SortOrder: 5),
            new(WorkflowWorkspaceTag, "Workflows", "Navigation_Workflows_Item", "Chain multi-step prompts and agentic automations", IsHome: false, IsAdvancedOnly: true, SortOrder: 6),
            new(ToolsWorkspaceTag, "Tools & Automations", "Navigation_Tools_Item", "Configure tools, skills, and desktop automation integrations", IsHome: false, IsAdvancedOnly: true, SortOrder: 7),
            new(ObservabilityWorkspaceTag, "Observability", "Navigation_Observability_Item", "Monitor token latency, resource metrics, and trace logs", IsHome: false, IsAdvancedOnly: true, SortOrder: 8),
            new(UpdatesWorkspaceTag, "Updates", "Navigation_Updates_Item", "Review verified release updates and signature integrity", IsHome: false, IsAdvancedOnly: true, SortOrder: 9),
            new(RuntimeWorkspaceTag, "Runtime", "Navigation_Runtime_Item", "Monitor llama.cpp engine health, backend allocations, and VRAM", IsHome: false, IsAdvancedOnly: true, SortOrder: 10),
            new(SettingsWorkspaceTag, "Settings", "Navigation_Settings_Item", "Application settings, storage budgets, and system configuration", IsHome: false, IsAdvancedOnly: false, SortOrder: 11),
            new(HelpWorkspaceTag, "Help", "Navigation_Help_Item", "In-app documentation guides, tour replay, and keyboard shortcuts", IsHome: false, IsAdvancedOnly: false, SortOrder: 12)
        }.AsReadOnly();

        /// <summary>
        /// Gets the ordered list of workspace descriptors for the given interface mode.
        /// Chat is strictly listed first (index 0) in both Simple and Advanced modes.
        /// </summary>
        public static IReadOnlyList<WorkspaceDescriptor> GetWorkspaces(bool advancedMode)
        {
            return AllWorkspaces
                .Where(w => advancedMode || !w.IsAdvancedOnly)
                .OrderBy(w => w.SortOrder)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Returns true if the tag represents the Home workspace (Chat).
        /// </summary>
        public static bool IsHomeWorkspace(string? tag)
        {
            return string.Equals(tag, ChatWorkspaceTag, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true if the tag matches any known workspace item tag.
        /// </summary>
        public static bool IsWorkspaceTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return AllWorkspaces.Any(w => string.Equals(w.Tag, tag.Trim(), StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns true if the tag represents an individual conversation in history.
        /// </summary>
        public static bool IsConversationTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return !tag.StartsWith("__", StringComparison.Ordinal);
        }

        /// <summary>
        /// Evaluates a navigation transition to determine destination, preservation behavior, and target ID.
        /// </summary>
        public static NavigationDecision EvaluateNavigation(
            string? targetTag,
            string? currentWorkspaceTag,
            string? openConversationId,
            bool hasDraft)
        {
            if (string.IsNullOrWhiteSpace(targetTag))
            {
                return new NavigationDecision(
                    DestinationKind: NavigationDestinationKind.Unknown,
                    DestinationTag: string.Empty,
                    IsHomeNavigation: false,
                    ShouldPreserveOpenConversation: true,
                    ShouldPreserveDraft: hasDraft,
                    TargetConversationId: openConversationId);
            }

            string cleanTarget = targetTag.Trim();

            if (IsHomeWorkspace(cleanTarget))
            {
                return new NavigationDecision(
                    DestinationKind: NavigationDestinationKind.ChatHome,
                    DestinationTag: ChatWorkspaceTag,
                    IsHomeNavigation: true,
                    ShouldPreserveOpenConversation: true,
                    ShouldPreserveDraft: true,
                    TargetConversationId: openConversationId);
            }

            if (IsConversationTag(cleanTarget))
            {
                return new NavigationDecision(
                    DestinationKind: NavigationDestinationKind.SpecificConversation,
                    DestinationTag: cleanTarget,
                    IsHomeNavigation: false,
                    ShouldPreserveOpenConversation: false,
                    ShouldPreserveDraft: true,
                    TargetConversationId: cleanTarget);
            }

            if (IsWorkspaceTag(cleanTarget))
            {
                return new NavigationDecision(
                    DestinationKind: NavigationDestinationKind.Workspace,
                    DestinationTag: cleanTarget,
                    IsHomeNavigation: false,
                    ShouldPreserveOpenConversation: true,
                    ShouldPreserveDraft: true,
                    TargetConversationId: openConversationId);
            }

            return new NavigationDecision(
                DestinationKind: NavigationDestinationKind.Unknown,
                DestinationTag: cleanTarget,
                IsHomeNavigation: false,
                ShouldPreserveOpenConversation: true,
                ShouldPreserveDraft: hasDraft,
                TargetConversationId: openConversationId);
        }

        /// <summary>
        /// Resolves which navigation item tag should be highlighted/selected in the sidebar.
        /// </summary>
        public static string ResolveSidebarSelectionTag(
            string? activeWorkspaceTag,
            string? openConversationId,
            bool isConversationInRecentList)
        {
            // If currently in a non-chat workspace (Model Hub, Settings, Projects, etc.), select that workspace
            if (!string.IsNullOrWhiteSpace(activeWorkspaceTag)
                && IsWorkspaceTag(activeWorkspaceTag)
                && !IsHomeWorkspace(activeWorkspaceTag))
            {
                return activeWorkspaceTag;
            }

            // If in Chat and the active conversation is listed in Recent, highlight that conversation item
            if (!string.IsNullOrWhiteSpace(openConversationId) && isConversationInRecentList)
            {
                return openConversationId;
            }

            // Otherwise, select the Chat home workspace item
            return ChatWorkspaceTag;
        }

        /// <summary>
        /// Generates an accessible name for screen readers and UI automation on the Chat navigation item.
        /// </summary>
        public static string FormatChatNavItemAccessibleName(string? activeConversationTitle, bool hasDraft)
        {
            string baseName = "Chat, home workspace";
            if (!string.IsNullOrWhiteSpace(activeConversationTitle) && activeConversationTitle != "New chat")
            {
                baseName += $", active conversation: {activeConversationTitle.Trim()}";
            }
            if (hasDraft)
            {
                baseName += ", with unsent draft";
            }
            return baseName;
        }
    }
}
