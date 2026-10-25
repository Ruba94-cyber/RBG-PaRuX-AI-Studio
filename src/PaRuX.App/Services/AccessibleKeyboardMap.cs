using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaRuX.Services
{
    public enum ShortcutScope
    {
        Global,
        Chat,
        Workspace
    }

    /// <summary>
    /// Key combination representation with modifiers.
    /// </summary>
    public sealed class AccessibleKeyCombo : IEquatable<AccessibleKeyCombo>
    {
        public string Key { get; }
        public bool Ctrl { get; }
        public bool Alt { get; }
        public bool Shift { get; }

        public AccessibleKeyCombo(string key, bool ctrl = false, bool alt = false, bool shift = false)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key cannot be null or empty.", nameof(key));
            }

            Key = key.Trim();
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        public string Formatted
        {
            get
            {
                var sb = new StringBuilder();
                if (Ctrl) sb.Append("Ctrl+");
                if (Alt) sb.Append("Alt+");
                if (Shift) sb.Append("Shift+");
                sb.Append(Key);
                return sb.ToString();
            }
        }

        public override string ToString() => Formatted;

        public bool Equals(AccessibleKeyCombo? other)
        {
            if (other is null) return false;
            return string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
                && Ctrl == other.Ctrl
                && Alt == other.Alt
                && Shift == other.Shift;
        }

        public override bool Equals(object? obj) => Equals(obj as AccessibleKeyCombo);

        public override int GetHashCode()
            => HashCode.Combine(Key.ToUpperInvariant(), Ctrl, Alt, Shift);
    }

    /// <summary>
    /// Accessible definition of an application shortcut with description and associated AutomationId.
    /// </summary>
    public sealed class AccessibleKeyboardShortcut
    {
        public string Id { get; }
        public string Category { get; }
        public AccessibleKeyCombo KeyCombo { get; }
        public ShortcutScope Scope { get; }
        public string Description { get; }
        public string AssociatedAutomationId { get; }

        public AccessibleKeyboardShortcut(
            string id,
            string category,
            AccessibleKeyCombo keyCombo,
            ShortcutScope scope,
            string description,
            string associatedAutomationId)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Category = category ?? "General";
            KeyCombo = keyCombo ?? throw new ArgumentNullException(nameof(keyCombo));
            Scope = scope;
            Description = description ?? string.Empty;
            AssociatedAutomationId = associatedAutomationId ?? string.Empty;
        }

        public override string ToString() =>
            $"{KeyCombo.Formatted} - {Description} [{Category}, {Scope}]";
    }

    /// <summary>
    /// Centralized keyboard map registry for PaRuX.
    /// Provides conflict verification, fast dispatch matching, and screen reader cheat sheets.
    /// </summary>
    public sealed class AccessibleKeyboardRegistry
    {
        private readonly List<AccessibleKeyboardShortcut> _shortcuts = new();

        public static AccessibleKeyboardRegistry Default { get; } = CreateDefaultRegistry();

        public AccessibleKeyboardRegistry()
        {
        }

        public AccessibleKeyboardRegistry(IEnumerable<AccessibleKeyboardShortcut> initial)
        {
            _shortcuts.AddRange(initial);
        }

        public void Register(AccessibleKeyboardShortcut shortcut)
        {
            ArgumentNullException.ThrowIfNull(shortcut);
            _shortcuts.Add(shortcut);
        }

        public IReadOnlyList<AccessibleKeyboardShortcut> GetAll() => _shortcuts.ToList();

        public IReadOnlyList<AccessibleKeyboardShortcut> GetByScope(ShortcutScope scope)
            => _shortcuts.Where(s => s.Scope == scope || s.Scope == ShortcutScope.Global).ToList();

        /// <summary>
        /// Attempts to match an incoming key event against registered shortcuts.
        /// Evaluates scoped matches before global matches.
        /// </summary>
        public bool TryMatch(
            string key,
            bool ctrl,
            bool alt,
            bool shift,
            ShortcutScope currentScope,
            out AccessibleKeyboardShortcut? matched)
        {
            var target = new AccessibleKeyCombo(key, ctrl, alt, shift);

            // 1. Try exact scope match
            matched = _shortcuts.FirstOrDefault(s => s.Scope == currentScope && s.KeyCombo.Equals(target));
            if (matched != null) return true;

            // 2. Fall back to Global scope if not already Global
            if (currentScope != ShortcutScope.Global)
            {
                matched = _shortcuts.FirstOrDefault(s => s.Scope == ShortcutScope.Global && s.KeyCombo.Equals(target));
                if (matched != null) return true;
            }

            matched = null;
            return false;
        }

        /// <summary>
        /// Detects conflicting shortcuts (same key combination in overlapping scopes).
        /// </summary>
        public IReadOnlyList<string> DetectConflicts()
        {
            var conflicts = new List<string>();
            var groups = _shortcuts.GroupBy(s => (s.KeyCombo, s.Scope));

            foreach (var group in groups)
            {
                if (group.Count() > 1)
                {
                    string ids = string.Join(", ", group.Select(s => s.Id));
                    conflicts.Add($"Key combo '{group.Key.KeyCombo.Formatted}' in scope '{group.Key.Scope}' is bound multiple times: {ids}");
                }
            }

            // Also check Global vs Specific Scope overlaps with identical combo
            var globalCombos = _shortcuts
                .Where(s => s.Scope == ShortcutScope.Global)
                .GroupBy(s => s.KeyCombo)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var s in _shortcuts.Where(s => s.Scope != ShortcutScope.Global))
            {
                if (globalCombos.TryGetValue(s.KeyCombo, out var globalMatch))
                {
                    // If same ID, it's fine; otherwise flag shadow/conflict
                    if (!string.Equals(s.Id, globalMatch.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add($"Specific shortcut '{s.Id}' [{s.Scope}] shadows Global shortcut '{globalMatch.Id}' for key combo '{s.KeyCombo.Formatted}'.");
                    }
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Formats an accessible markdown cheat sheet for display or help dialog.
        /// </summary>
        public string GenerateCheatSheetMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Keyboard Shortcuts");
            sb.AppendLine();

            var categories = _shortcuts.GroupBy(s => s.Category);
            foreach (var category in categories)
            {
                sb.AppendLine($"## {category.Key}");
                sb.AppendLine();
                sb.AppendLine("| Shortcut | Action | Scope |");
                sb.AppendLine("|---|---|---|");
                foreach (var s in category)
                {
                    sb.AppendLine($"| `{s.KeyCombo.Formatted}` | {s.Description} | {s.Scope} |");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Plain-language string formatted for screen reader announcement.
        /// </summary>
        public string GenerateScreenReaderAnnouncement()
        {
            var sb = new StringBuilder();
            sb.AppendLine("PaRuX Keyboard Shortcuts summary.");
            foreach (var s in _shortcuts)
            {
                sb.AppendLine($"Press {s.KeyCombo.Formatted} to {s.Description}.");
            }
            return sb.ToString();
        }

        private static AccessibleKeyboardRegistry CreateDefaultRegistry()
        {
            var reg = new AccessibleKeyboardRegistry();

            // General & Navigation
            reg.Register(new AccessibleKeyboardShortcut(
                "App_GoToChatHome",
                "Navigation",
                new AccessibleKeyCombo("H", alt: true),
                ShortcutScope.Global,
                "Return to Chat (Home workspace)",
                "Navigation_Chat_Item"));

            reg.Register(new AccessibleKeyboardShortcut(
                "App_NewChat",
                "Navigation",
                new AccessibleKeyCombo("N", ctrl: true),
                ShortcutScope.Global,
                "Start a new conversation",
                "MainWindow_NewChat_Button"));

            reg.Register(new AccessibleKeyboardShortcut(
                "App_SearchChats",
                "Navigation",
                new AccessibleKeyCombo("F", ctrl: true),
                ShortcutScope.Global,
                "Search chat history",
                "MainWindow_ChatSearch_TextBox"));

            reg.Register(new AccessibleKeyboardShortcut(
                "App_ToggleMode",
                "General",
                new AccessibleKeyCombo("M", ctrl: true),
                ShortcutScope.Global,
                "Toggle between Simple and Advanced interface mode",
                "MainWindow_StudioMode_Toggle"));

            reg.Register(new AccessibleKeyboardShortcut(
                "App_OpenSettings",
                "General",
                new AccessibleKeyCombo(",", ctrl: true),
                ShortcutScope.Global,
                "Open application Settings",
                "Navigation_Settings_Item"));

            reg.Register(new AccessibleKeyboardShortcut(
                "App_ShowKeyboardShortcuts",
                "General",
                new AccessibleKeyCombo("F1"),
                ShortcutScope.Global,
                "Show keyboard shortcuts cheat sheet",
                "MainWindow_Shortcuts_Button"));

            // Overlays
            reg.Register(new AccessibleKeyboardShortcut(
                "Overlay_Close",
                "Overlays",
                new AccessibleKeyCombo("Escape"),
                ShortcutScope.Global,
                "Close active dialog, teaching tip, or flyout",
                "Overlay_Dismiss"));

            // Chat actions
            reg.Register(new AccessibleKeyboardShortcut(
                "Chat_SendMessage",
                "Chat",
                new AccessibleKeyCombo("Enter", ctrl: true),
                ShortcutScope.Chat,
                "Send message to the active model",
                "Chat_Send_Button"));

            reg.Register(new AccessibleKeyboardShortcut(
                "Chat_BranchPrevious",
                "Chat",
                new AccessibleKeyCombo("Left", ctrl: true, alt: true),
                ShortcutScope.Chat,
                "Switch to previous conversation branch",
                "Chat_BranchPrev_Button"));

            reg.Register(new AccessibleKeyboardShortcut(
                "Chat_BranchNext",
                "Chat",
                new AccessibleKeyCombo("Right", ctrl: true, alt: true),
                ShortcutScope.Chat,
                "Switch to next conversation branch",
                "Chat_BranchNext_Button"));

            reg.Register(new AccessibleKeyboardShortcut(
                "Chat_StopGeneration",
                "Chat",
                new AccessibleKeyCombo(".", ctrl: true),
                ShortcutScope.Chat,
                "Stop generating the model response",
                "Chat_Stop_Button"));

            return reg;
        }
    }
}
