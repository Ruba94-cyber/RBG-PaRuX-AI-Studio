using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Immutable snapshot representing the draft state of a conversation.
    /// </summary>
    public sealed record ChatDraftSnapshot(
        string ConversationKey,
        string DraftText,
        bool HasDraft,
        DateTimeOffset Timestamp);

    /// <summary>
    /// Pure coordinator managing unsent chat composer drafts per conversation.
    /// Preserves drafts across workspace switches (e.g. Chat -> Model Hub -> Chat)
    /// and between distinct conversation histories.
    /// </summary>
    public sealed class ChatDraftCoordinator
    {
        public const string NewChatDraftKey = "__new_chat_draft__";

        private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public static ChatDraftCoordinator Current { get; } = new();

        public void SaveDraft(string? conversationId, string? draftText)
        {
            string key = ResolveKey(conversationId);
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(draftText))
                {
                    _drafts.Remove(key);
                }
                else
                {
                    _drafts[key] = draftText;
                }
            }
        }

        public string GetDraft(string? conversationId)
        {
            string key = ResolveKey(conversationId);
            lock (_lock)
            {
                return _drafts.TryGetValue(key, out string? draft) ? draft : string.Empty;
            }
        }

        public bool HasDraft(string? conversationId)
        {
            string key = ResolveKey(conversationId);
            lock (_lock)
            {
                return _drafts.TryGetValue(key, out string? draft) && !string.IsNullOrWhiteSpace(draft);
            }
        }

        public void ClearDraft(string? conversationId)
        {
            string key = ResolveKey(conversationId);
            lock (_lock)
            {
                _drafts.Remove(key);
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                _drafts.Clear();
            }
        }

        public IReadOnlyDictionary<string, string> GetAllDrafts()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_drafts);
            }
        }

        public ChatDraftSnapshot CaptureState(string? conversationId, string? text)
        {
            string key = ResolveKey(conversationId);
            string effectiveText = text ?? string.Empty;
            bool hasDraft = !string.IsNullOrWhiteSpace(effectiveText);
            return new ChatDraftSnapshot(key, effectiveText, hasDraft, DateTimeOffset.UtcNow);
        }

        public static string ResolveKey(string? conversationId) =>
            string.IsNullOrWhiteSpace(conversationId) ? NewChatDraftKey : conversationId.Trim();
    }
}
