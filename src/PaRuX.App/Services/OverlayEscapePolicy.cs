using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    public enum OverlayKind
    {
        Dialog,
        TeachingTip,
        Flyout,
        ModalSheet,
        Drawer
    }

    /// <summary>
    /// Describes an active overlay (dialog, teaching tip, flyout, sheet) for keyboard and escape management.
    /// Preserves triggering control automation identity to restore focus after dismissal.
    /// </summary>
    public sealed class OverlayDescriptor
    {
        public string OverlayId { get; }
        public OverlayKind Kind { get; }
        public bool IsDismissibleOnEscape { get; }
        public string? TriggeringControlAutomationId { get; }
        public string? AccessibleLabel { get; }

        public OverlayDescriptor(
            string overlayId,
            OverlayKind kind,
            bool isDismissibleOnEscape = true,
            string? triggeringControlAutomationId = null,
            string? accessibleLabel = null)
        {
            if (string.IsNullOrWhiteSpace(overlayId))
            {
                throw new ArgumentException("OverlayId cannot be null or whitespace.", nameof(overlayId));
            }

            OverlayId = overlayId;
            Kind = kind;
            IsDismissibleOnEscape = isDismissibleOnEscape;
            TriggeringControlAutomationId = triggeringControlAutomationId;
            AccessibleLabel = accessibleLabel;
        }

        public override string ToString() =>
            $"Overlay[{OverlayId}, Kind={Kind}, Dismissible={IsDismissibleOnEscape}, ReturnFocus={TriggeringControlAutomationId ?? "none"}]";
    }

    /// <summary>
    /// Result of evaluating an Escape key press against the active overlay stack.
    /// </summary>
    public sealed class EscapeHandlingResult
    {
        public bool Handled { get; init; }
        public bool Dismissed { get; init; }
        public string? DismissedOverlayId { get; init; }
        public string? RestoreFocusAutomationId { get; init; }
        public string Reason { get; init; } = string.Empty;

        public static EscapeHandlingResult NotHandled() => new()
        {
            Handled = false,
            Dismissed = false,
            Reason = "No active overlay on stack."
        };

        public static EscapeHandlingResult Blocked(string overlayId, string reason = "Overlay is non-dismissible.") => new()
        {
            Handled = true,
            Dismissed = false,
            DismissedOverlayId = overlayId,
            Reason = reason
        };

        public static EscapeHandlingResult DismissedSuccess(string overlayId, string? restoreFocusAutomationId) => new()
        {
            Handled = true,
            Dismissed = true,
            DismissedOverlayId = overlayId,
            RestoreFocusAutomationId = restoreFocusAutomationId,
            Reason = $"Dismissed overlay '{overlayId}'. Focus restored to '{restoreFocusAutomationId ?? "default"}'."
        };
    }

    /// <summary>
    /// Pure stateful policy managing active overlays in a LIFO stack.
    /// Ensures deterministic Escape behaviour, overlay nesting, and focus return.
    /// </summary>
    public sealed class OverlayEscapeManager
    {
        private readonly object _lock = new();
        private readonly List<OverlayDescriptor> _overlayStack = new();

        public static OverlayEscapeManager Instance { get; } = new();

        public int ActiveCount
        {
            get
            {
                lock (_lock) return _overlayStack.Count;
            }
        }

        public void Push(OverlayDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            lock (_lock)
            {
                // Remove if already present (e.g. re-registering)
                _overlayStack.RemoveAll(o => string.Equals(o.OverlayId, descriptor.OverlayId, StringComparison.OrdinalIgnoreCase));
                _overlayStack.Add(descriptor);
            }
        }

        public bool Pop(string overlayId)
        {
            if (string.IsNullOrWhiteSpace(overlayId)) return false;
            lock (_lock)
            {
                int index = _overlayStack.FindLastIndex(o => string.Equals(o.OverlayId, overlayId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _overlayStack.RemoveAt(index);
                    return true;
                }
                return false;
            }
        }

        public OverlayDescriptor? PeekTopmost()
        {
            lock (_lock)
            {
                return _overlayStack.Count > 0 ? _overlayStack[^1] : null;
            }
        }

        public IReadOnlyList<OverlayDescriptor> GetActiveOverlays()
        {
            lock (_lock)
            {
                return _overlayStack.ToList();
            }
        }

        /// <summary>
        /// Handles an Escape key press.
        /// If an overlay is active and dismissible, dismisses it and returns its return-focus target.
        /// If the topmost overlay is non-dismissible (blocking confirmation modal), marks Escape as handled/blocked.
        /// If no overlay is open, returns Handled=false so downstream handlers can process it.
        /// </summary>
        public EscapeHandlingResult HandleEscape()
        {
            lock (_lock)
            {
                if (_overlayStack.Count == 0)
                {
                    return EscapeHandlingResult.NotHandled();
                }

                OverlayDescriptor topmost = _overlayStack[^1];
                if (!topmost.IsDismissibleOnEscape)
                {
                    return EscapeHandlingResult.Blocked(topmost.OverlayId, "Topmost overlay requires explicit user decision and cannot be dismissed by Escape.");
                }

                _overlayStack.RemoveAt(_overlayStack.Count - 1);
                return EscapeHandlingResult.DismissedSuccess(topmost.OverlayId, topmost.TriggeringControlAutomationId);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _overlayStack.Clear();
            }
        }
    }
}
