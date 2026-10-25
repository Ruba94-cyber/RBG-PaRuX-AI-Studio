using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Coordinates background work observation and idempotency for tray notifications.
    /// Manages state transitions for downloads, runtime installs, and media generation.
    /// Ensures that:
    /// 1. Only jobs that finish/fail while the window is hidden raise a tray notification.
    /// 2. Exactly one notification is raised per job (never duplicate or re-raise).
    /// 3. Startup lists of already-completed/failed jobs do not trigger notifications.
    /// 4. Jobs cancelled by the user do not trigger notifications.
    /// </summary>
    public sealed class BackgroundWorkTracker
    {
        private readonly HashSet<string> _activeJobIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenTerminalJobIds = new(StringComparer.OrdinalIgnoreCase);
        private bool _downloadsInitialized;
        private bool _installsInitialized;
        private readonly object _gate = new();

        /// <summary>
        /// Resets all internal state (for testing and session resets).
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _activeJobIds.Clear();
                _seenTerminalJobIds.Clear();
                _downloadsInitialized = false;
                _installsInitialized = false;
            }
        }

        /// <summary>
        /// Evaluates a poll of model downloads.
        /// </summary>
        public IReadOnlyList<BackgroundNotificationDecision> EvaluateDownloads(
            IReadOnlyList<ModelDownloadStatus>? downloads,
            bool isWindowHidden,
            BackgroundNotificationPreferences preferences)
        {
            if (downloads == null || downloads.Count == 0)
            {
                return Array.Empty<BackgroundNotificationDecision>();
            }

            var decisions = new List<BackgroundNotificationDecision>();

            lock (_gate)
            {
                if (!_downloadsInitialized)
                {
                    // First poll: record active downloads as active, and already-finished ones as seen,
                    // so we do not flood the user with notifications for yesterday's downloads.
                    foreach (ModelDownloadStatus d in downloads)
                    {
                        if (string.IsNullOrWhiteSpace(d.Id)) continue;
                        string status = (d.Status ?? string.Empty).Trim().ToLowerInvariant();
                        if (status is "queued" or "downloading")
                        {
                            _activeJobIds.Add(d.Id);
                        }
                        else
                        {
                            _seenTerminalJobIds.Add(d.Id);
                        }
                    }
                    _downloadsInitialized = true;
                    return Array.Empty<BackgroundNotificationDecision>();
                }

                foreach (ModelDownloadStatus d in downloads)
                {
                    if (string.IsNullOrWhiteSpace(d.Id)) continue;

                    string status = (d.Status ?? string.Empty).Trim().ToLowerInvariant();
                    if (status is "queued" or "downloading")
                    {
                        _activeJobIds.Add(d.Id);
                        continue;
                    }

                    // Terminal state reached ("completed", "failed", "cancelled")
                    BackgroundWorkItem? item = BackgroundWorkNotificationPolicy.FormatDownload(d);
                    if (item == null) continue;

                    // If it was already seen as terminal, skip
                    if (_seenTerminalJobIds.Contains(d.Id)) continue;

                    // If it was tracked as an active job, or if it's newly arrived and finished:
                    BackgroundNotificationDecision decision = BackgroundWorkNotificationPolicy.Evaluate(
                        item, isWindowHidden, preferences, _seenTerminalJobIds);

                    _activeJobIds.Remove(d.Id);
                    _seenTerminalJobIds.Add(d.Id);

                    if (decision.ShouldNotify)
                    {
                        decisions.Add(decision);
                    }
                }
            }

            return decisions;
        }

        /// <summary>
        /// Evaluates a poll of runtime engine installs.
        /// </summary>
        public IReadOnlyList<BackgroundNotificationDecision> EvaluateInstalls(
            IReadOnlyList<RuntimeInstallStatus>? installs,
            bool isWindowHidden,
            BackgroundNotificationPreferences preferences)
        {
            if (installs == null || installs.Count == 0)
            {
                return Array.Empty<BackgroundNotificationDecision>();
            }

            var decisions = new List<BackgroundNotificationDecision>();

            lock (_gate)
            {
                if (!_installsInitialized)
                {
                    // First poll: record active installs as active, and already-finished ones as seen.
                    foreach (RuntimeInstallStatus inst in installs)
                    {
                        if (string.IsNullOrWhiteSpace(inst.Id)) continue;
                        string stage = (inst.Stage ?? string.Empty).Trim().ToLowerInvariant();
                        if (stage is "queued" or "downloading" or "verifying" or "extracting" or "swapping" or "performance-check")
                        {
                            _activeJobIds.Add(inst.Id);
                        }
                        else
                        {
                            _seenTerminalJobIds.Add(inst.Id);
                        }
                    }
                    _installsInitialized = true;
                    return Array.Empty<BackgroundNotificationDecision>();
                }

                foreach (RuntimeInstallStatus inst in installs)
                {
                    if (string.IsNullOrWhiteSpace(inst.Id)) continue;

                    string stage = (inst.Stage ?? string.Empty).Trim().ToLowerInvariant();
                    if (stage is "queued" or "downloading" or "verifying" or "extracting" or "swapping" or "performance-check")
                    {
                        _activeJobIds.Add(inst.Id);
                        continue;
                    }

                    // Terminal stage reached ("completed", "failed", "cancelled")
                    BackgroundWorkItem? item = BackgroundWorkNotificationPolicy.FormatInstall(inst);
                    if (item == null) continue;

                    if (_seenTerminalJobIds.Contains(inst.Id)) continue;

                    BackgroundNotificationDecision decision = BackgroundWorkNotificationPolicy.Evaluate(
                        item, isWindowHidden, preferences, _seenTerminalJobIds);

                    _activeJobIds.Remove(inst.Id);
                    _seenTerminalJobIds.Add(inst.Id);

                    if (decision.ShouldNotify)
                    {
                        decisions.Add(decision);
                    }
                }
            }

            return decisions;
        }

        /// <summary>
        /// Evaluates a finished media generation job turn.
        /// </summary>
        public BackgroundNotificationDecision EvaluateGeneration(
            RouteKind route,
            ChatMessage? lastAssistantMessage,
            bool isWindowHidden,
            BackgroundNotificationPreferences preferences)
        {
            if (route != RouteKind.Image && route != RouteKind.ImageEdit && route != RouteKind.Video)
            {
                return BackgroundNotificationDecision.Suppress("NotMediaRoute");
            }

            if (lastAssistantMessage == null)
            {
                return BackgroundNotificationDecision.Suppress("NoAssistantMessage");
            }

            string jobId = "gen_" + route + "_" + lastAssistantMessage.TimestampUtc.Ticks + "_" + (lastAssistantMessage.Content?.GetHashCode() ?? 0);

            bool isUserCancelled = lastAssistantMessage.Content != null &&
                                   lastAssistantMessage.Content.Contains("_Stopped._", StringComparison.Ordinal);

            string kindName = route == RouteKind.Video
                ? "Video"
                : route == RouteKind.ImageEdit
                    ? "Image edit"
                    : "Image";

            BackgroundWorkItem item = BackgroundWorkNotificationPolicy.FormatGeneration(
                kindName,
                lastAssistantMessage.IsError,
                lastAssistantMessage.Content,
                jobId,
                isUserCancelled);

            lock (_gate)
            {
                BackgroundNotificationDecision decision = BackgroundWorkNotificationPolicy.Evaluate(
                    item, isWindowHidden, preferences, _seenTerminalJobIds);

                _seenTerminalJobIds.Add(jobId);
                return decision;
            }
        }
    }
}
