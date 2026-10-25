using System;
using System.Collections.Generic;

namespace PaRuX.Models
{
    /// <summary>
    /// Immutable-at-rest snapshot of a conversation path that was superseded by regeneration,
    /// edit-and-continue, or an explicit branch restore. It lives inside Conversation.document_json
    /// so old databases need no schema migration and Android/server sync can round-trip it together
    /// with the owning conversation.
    /// </summary>
    public sealed class ConversationBranch
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Index of the first message that differs from the path that replaced this one.</summary>
        public int ForkIndex { get; set; }

        /// <summary>Stable content fingerprint used to avoid storing the same superseded path twice.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Diagnostic origin such as regenerate-or-edit, divergence, or restore.</summary>
        public string Reason { get; set; } = "divergence";

        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    }
}
