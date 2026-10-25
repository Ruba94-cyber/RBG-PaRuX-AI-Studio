using System;
using System.Text.Json;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure logic for creating a NEW conversation from an existing one (branch at a message, or full duplicate).
    /// Attachments are copied as metadata only: they keep pointing at the same files on disk (referenced, never duplicated).
    /// </summary>
    public static class ConversationBranching
    {
        public const string BranchTitlePrefix = "Branch of ";
        public const string DuplicateTitlePrefix = "Copy of ";
        private const int MaxTitleLength = 80;

        public static string EnsureMessageId(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.MessageId)) message.MessageId = Guid.NewGuid().ToString("N");
            return message.MessageId!;
        }

        /// <summary>Branch containing messages [0..messageIndex] inclusive. Returns null for an out-of-range index.</summary>
        public static Conversation? BranchAt(Conversation source, int messageIndex)
        {
            if (source == null || messageIndex < 0 || messageIndex >= source.Messages.Count) return null;
            string parentMessageId = EnsureMessageId(source.Messages[messageIndex]);
            return Build(source, messageIndex + 1, BranchTitlePrefix, parentMessageId);
        }

        public static Conversation? BranchAt(Conversation source, ChatMessage message)
        {
            if (source == null || message == null) return null;
            return BranchAt(source, source.Messages.IndexOf(message));
        }

        public static Conversation Duplicate(Conversation source) =>
            Build(source, source.Messages.Count, DuplicateTitlePrefix, null);

        private static Conversation Build(Conversation source, int count, string prefix, string? parentMessageId)
        {
            var copy = new Conversation
            {
                Title = MakeTitle(prefix, source.Title),
                CodingRunPermissionMode = source.CodingRunPermissionMode,
                IsIncognito = source.IsIncognito,
                ParentConversationId = source.Id,
                ParentMessageId = parentMessageId
            };
            for (int i = 0; i < count; i++) copy.Messages.Add(CloneMessage(source.Messages[i]));
            return copy;
        }

        private static ChatMessage CloneMessage(ChatMessage message)
        {
            // JSON round trip: the shape the history store persists, so future fields clone without extra code.
            ChatMessage clone = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message)) ?? new ChatMessage();
            clone.IsStreaming = false;
            return clone;
        }

        public static string MakeTitle(string prefix, string? sourceTitle)
        {
            string baseTitle = string.IsNullOrWhiteSpace(sourceTitle) ? Conversation.DefaultTitle : sourceTitle.Trim();
            string title = prefix + baseTitle;
            return title.Length <= MaxTitleLength ? title : title.Substring(0, MaxTitleLength - 1) + "…";
        }
    }
}
