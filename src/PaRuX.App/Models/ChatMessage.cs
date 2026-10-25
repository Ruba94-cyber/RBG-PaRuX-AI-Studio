using System;
using System.Collections.Generic;

namespace PaRuX.Models
{
    public class ChatMessage
    {
        /// <summary>"user" | "assistant" | "system"</summary>
        public string Role { get; set; } = "user";

        public string Content { get; set; } = string.Empty;

        /// <summary>Stable id assigned lazily (ConversationBranching.EnsureMessageId) so a branch can reference its fork point.
        /// Null in documents saved before branching existed.</summary>
        public string? MessageId { get; set; }

        /// <summary>Chain-of-thought text from a reasoning model's reasoning_content stream
        /// (DeepReasoning route, --reasoning auto on the server). Empty for routes/models that
        /// don't emit it. Rendered separately from Content, in a collapsible panel.</summary>
        public string Reasoning { get; set; } = string.Empty;

        public List<ChatAttachment> Attachments { get; set; } = new List<ChatAttachment>();

        /// <summary>Route label recorded for user turns, e.g. "Fast". Null for assistant turns.</summary>
        public string? Route { get; set; }

        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        /// <summary>True while this message is still being streamed. Not persisted meaningfully.</summary>
        public bool IsStreaming { get; set; }

        /// <summary>Set when the turn failed, so the UI can style it as an error.</summary>
        public bool IsError { get; set; }

        /// <summary>Optional local action rendered below an assistant message.</summary>
        public string? ActionKind { get; set; }
        public string? ActionLabel { get; set; }
        public string? ActionPayload { get; set; }

        /// <summary>Set when the one-time "Allow once" approval on this card has been used. It is persisted with the
        /// conversation, so a re-opened conversation cannot replay the approval.</summary>
        public bool ActionConsumed { get; set; }

        /// <summary>Set when PaRuX auto-escalated this reply from Standard to Uncensored.</summary>
        public bool SwitchedToUncensored { get; set; }

        /// <summary>Reviewable local path for a native coding handoff. It is stored only in the
        /// owner's local conversation document; Core's remotely visible AgentTaskDto never contains
        /// workstation paths.</summary>
        public string? CodeWorkspacePath { get; set; }

        /// <summary>Durable Core task created by the native PaRuX coding handoff. Persisting the id
        /// lets a re-opened conversation reconnect to the same checkpoint/verifier/worktree instead
        /// of creating another disconnected coding session.</summary>
        public string? CodeTaskId { get; set; }

        /// <summary>File names of local documents RAG retrieved for this turn, for the source citation line.</summary>
        public List<string>? RagSources { get; set; }

        /// <summary>Server-reported skill that actually ran this turn (from the "skill_selected" SSE
        /// event), e.g. "Deep Research (forced)". Null when the default General skill ran in AUTO mode.</summary>
        public string? SkillUsed { get; set; }

        /// <summary>Model that produced an assistant reply, as Core reported it (a model file name or a remote model id).
        /// Null for user turns, for replies that never reached a model, and for conversations saved before this field.</summary>
        public string? ModelId { get; set; }

        /// <summary>True when the model was chosen via a manual lock rather than auto routing.</summary>
        public bool ManualLock { get; set; }

        /// <summary>Provider that produced an assistant reply: "local" for PaRuX's managed runtime, otherwise the provider id.</summary>
        public string? Provider { get; set; }

        /// <summary>"local" or "remote": where Core ran the reply. Null when Core did not report it.</summary>
        public string? Locality { get; set; }

        /// <summary>Why the reply did not run on the route first requested, when Core reported a fallback. Null otherwise.</summary>
        public string? FallbackReason { get; set; }

        /// <summary>"up" | "down" | null. User feedback on an assistant reply.</summary>
        public string? FeedbackRating { get; set; }

        /// <summary>Optional short tags alongside a rating, e.g. "Too verbose", "Wrong tool".</summary>
        public List<string> FeedbackTags { get; set; } = new();

        /// <summary>Explicitly marked by the user as worth keeping for a future training dataset.</summary>
        public bool TrainingCandidate { get; set; }

        // ---- Inline media job state (additive: chats saved before these fields load with all-null/false). ----

        /// <summary>Server media job id of the step this reply is currently waiting on or last ran.</summary>
        public string? MediaJobId { get; set; }

        /// <summary>"image" | "video" | "edit" | "img2img" for the step in <see cref="MediaJobId"/>.</summary>
        public string? MediaJobKind { get; set; }

        /// <summary>"running" | "completed" | "failed" | "cancelled". Null for ordinary text replies.</summary>
        public string? MediaJobState { get; set; }

        /// <summary>Last failure text of the media job, shown with the Retry action.</summary>
        public string? MediaJobError { get; set; }

        /// <summary>Last server-reported progress, for a job re-attached after a restart.</summary>
        public int MediaProgressPercent { get; set; }

        public string? MediaProgressStage { get; set; }

        /// <summary>True while an image-then-video chat request still owes its video step.</summary>
        public bool MediaChainVideoPending { get; set; }
    }
}
