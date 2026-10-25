using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaRuX.Services
{
    /// <summary>
    /// Represents an in-app help topic.
    /// </summary>
    public sealed class InAppHelpTopic
    {
        public string Id { get; }
        public string Title { get; }
        public string Category { get; }
        public string Summary { get; }
        public string Content { get; }
        public bool SupportsReplayTour { get; }
        public IReadOnlyList<string> Keywords { get; }

        public InAppHelpTopic(
            string id,
            string title,
            string category,
            string summary,
            string content,
            bool supportsReplayTour = false,
            IEnumerable<string>? keywords = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Category = category ?? "General";
            Summary = summary ?? string.Empty;
            Content = content ?? string.Empty;
            SupportsReplayTour = supportsReplayTour;
            Keywords = keywords != null ? keywords.ToList().AsReadOnly() : Array.Empty<string>();
        }
    }

    /// <summary>
    /// Pure in-app help and documentation catalog for PaRuX AI Studio.
    /// Provides structured guide topics, search, and markdown rendering.
    /// </summary>
    public sealed class InAppHelpCatalog
    {
        private static readonly Lazy<InAppHelpCatalog> _default = new(() => new InAppHelpCatalog(CreateDefaultTopics()));
        public static InAppHelpCatalog Default => _default.Value;

        private readonly List<InAppHelpTopic> _topics;

        public InAppHelpCatalog(IEnumerable<InAppHelpTopic> topics)
        {
            _topics = topics != null ? new List<InAppHelpTopic>(topics) : new List<InAppHelpTopic>();
        }

        public IReadOnlyList<InAppHelpTopic> GetAllTopics() => _topics.AsReadOnly();

        public InAppHelpTopic? GetTopic(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _topics.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<InAppHelpTopic> Search(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return GetAllTopics();
            }

            string term = query.Trim();
            return _topics
                .Where(t => t.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || t.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || t.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || t.Keywords.Any(k => k.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || t.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .AsReadOnly();
        }

        public string GenerateHelpSummaryMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PaRuX AI Studio — Help & Guided Tour");
            sb.AppendLine();
            sb.AppendLine("Welcome to PaRuX AI Studio. Below is a guide to key features and in-app tools.");
            sb.AppendLine("You can replay the interactive tour at any time by selecting **Replay Guided Tour**.");
            sb.AppendLine();

            var groups = _topics.GroupBy(t => t.Category);
            foreach (var group in groups)
            {
                sb.AppendLine($"## {group.Key}");
                sb.AppendLine();
                foreach (var topic in group)
                {
                    sb.AppendLine($"### {topic.Title}");
                    sb.AppendLine(topic.Content.Trim());
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        public string GenerateTopicMarkdown(string id)
        {
            InAppHelpTopic? topic = GetTopic(id);
            if (topic == null)
            {
                return $"# Topic Not Found\n\nNo in-app help topic with ID '{id}' was found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# {topic.Title}");
            sb.AppendLine($"*Category: {topic.Category}*");
            sb.AppendLine();
            sb.AppendLine(topic.Content.Trim());
            return sb.ToString();
        }

        private static List<InAppHelpTopic> CreateDefaultTopics()
        {
            return new List<InAppHelpTopic>
            {
                new(
                    id: "tour",
                    title: "Interactive Guided Tour",
                    category: "Getting Started",
                    summary: "Step-by-step walkthrough of the sidebar, workspace navigation, and model controls.",
                    content: "The guided tour walks you through the primary workspaces in PaRuX: Chat, Model Hub, "
                           + "Create, Projects, Settings, and the Simple vs Advanced interface mode switch.\n\n"
                           + "You can skip the tour at any step by clicking Skip or pressing Escape. "
                           + "To replay the walkthrough at any time, open Help and click 'Replay Guided Tour'.",
                    supportsReplayTour: true,
                    keywords: new[] { "tour", "walkthrough", "onboarding", "intro", "sidebar", "replay" }),

                new(
                    id: "first-start",
                    title: "First Start & Local Chat",
                    category: "Getting Started",
                    summary: "How on-device AI runs 100% privately on your machine without external cloud dependencies.",
                    content: "PaRuX runs entirely on your local PC through llama.cpp. Prompts and documents never leave "
                           + "your machine. You can select profiles (Auto, Standard, Uncensored) in the header. "
                           + "Chat history is stored locally in your app data folder.",
                    supportsReplayTour: false,
                    keywords: new[] { "chat", "local", "privacy", "llama", "profile", "prompts" }),

                new(
                    id: "model-hub",
                    title: "Model Hub & Hardware Fit",
                    category: "Models & Hardware",
                    summary: "Discovering and downloading GGUF models with automatic hardware fit classification.",
                    content: "Model Hub searches Hugging Face and evaluates whether models fit your hardware. "
                           + "Verdicts indicate:\n"
                           + "- **Fits**: Full GPU acceleration in VRAM.\n"
                           + "- **Tight**: Fits with partial CPU/RAM offload.\n"
                           + "- **Big**: Exceeds available memory; recommend smaller quantisation.\n\n"
                           + "Downloads support pause, resume, and integrity checks.",
                    supportsReplayTour: false,
                    keywords: new[] { "model hub", "download", "hugging face", "quant", "gguf", "hardware fit", "vram" }),

                new(
                    id: "workspaces",
                    title: "Simple & Advanced Interface Modes",
                    category: "Workspaces",
                    summary: "Switch between focused everyday creation and advanced orchestration tools.",
                    content: "Chat is the home workspace in PaRuX, listed first at the top of the sidebar and always "
                           + "one click away from any workspace. Returning to Chat keeps your open conversation and unsent draft intact.\n\n"
                           + "The Interface switch lets you toggle modes:\n"
                           + "- **Simple**: Focused on Chat, Create, Projects, and Model Hub.\n"
                           + "- **Advanced**: Unlocks Model Routing, Runtime Manager, Lifecycle Lab, Workflows, "
                           + "Tools & Automations, Observability, and Updates.\n\n"
                           + "Toggling the interface changes what is visible in the sidebar, never how PaRuX answers.",
                    supportsReplayTour: false,
                    keywords: new[] { "workspaces", "interface", "simple", "advanced", "sidebar", "tools", "runtime" }),

                new(
                    id: "shortcuts",
                    title: "Keyboard Shortcuts & Accessibility",
                    category: "Accessibility",
                    summary: "Keyboard navigation shortcuts, accessible automation IDs, and overlay dismissal.",
                    content: "Key keyboard shortcuts:\n"
                           + "- **Alt+H**: Return to Chat (Home workspace).\n"
                           + "- **Ctrl+N**: Start a new conversation.\n"
                           + "- **Escape**: Dismiss overlays, TeachingTips, and dialogs.\n"
                           + "- **? or Ctrl+F1**: Open Keyboard Shortcuts cheat sheet.\n"
                           + "- **Ctrl+Alt+Left / Right**: Navigate alternative branches in conversation history.\n\n"
                           + "All controls maintain stable AutomationIds for assistive technologies.",
                    supportsReplayTour: false,
                    keywords: new[] { "shortcuts", "keyboard", "escape", "accessibility", "automation" }),

                new(
                    id: "troubleshooting",
                    title: "Diagnostics & Troubleshooting",
                    category: "Diagnostics & Support",
                    summary: "Checking runtime health, port allocation, and error recovery guidance.",
                    content: "If local inference stops responding, check:\n"
                           + "1. **Runtime Workspace**: Verify llama-server or ComfyUI health and port allocation.\n"
                           + "2. **Settings**: View diagnostics and local log files.\n"
                           + "3. **Actionable Errors**: Error cards provide root cause and immediate recovery actions.",
                    supportsReplayTour: false,
                    keywords: new[] { "troubleshooting", "diagnostics", "recovery", "health", "ports", "logs", "error" })
            };
        }
    }
}
