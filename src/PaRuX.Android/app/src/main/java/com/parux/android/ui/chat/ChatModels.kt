package com.parux.android.ui.chat

import java.time.Instant
import java.util.UUID
import com.parux.android.core.network.ChatAttachmentDoc
import com.parux.android.core.network.ChatMessageDoc
import com.parux.android.core.network.ConversationDoc

/** In-memory chat UI state. Converts to/from ConversationDoc for server sync. */
data class UiMessage(
    val id: String = UUID.randomUUID().toString(),
    val role: String,
    val content: String,
    val route: String? = null,
    val timestampUtc: String = Instant.now().toString(),
    val isStreaming: Boolean = false,
    val isError: Boolean = false,
    val imageAttachment: PendingImage? = null,
    val switchedToUncensored: Boolean = false,
    val ragSources: List<String> = emptyList(),
    val skillUsed: String? = null,
    /** Sanitized per-turn text model decision; never contains a Windows path. */
    val modelRouteLabel: String? = null,
    val modelRouteReason: String? = null,
    /** Set when local inference could not serve this turn and the configured cloud fallback did,
     *  meaning this one turn left the PC. Null for every ordinary local turn. */
    val cloudFallbackNotice: String? = null
)

data class PendingImage(val fileName: String, val mediaType: String, val base64Data: String)

data class UiConversation(
    val id: String = UUID.randomUUID().toString(),
    val title: String = "New chat",
    val createdUtc: String = Instant.now().toString(),
    val updatedUtc: String = Instant.now().toString(),
    val messages: List<UiMessage> = emptyList()
)

fun UiConversation.toDoc(): ConversationDoc = ConversationDoc(
    id = id,
    title = title,
    createdUtc = createdUtc,
    updatedUtc = updatedUtc,
    messages = messages.filter { !it.isStreaming }.map { m ->
        ChatMessageDoc(
            role = m.role,
            content = m.content,
            attachments = if (m.imageAttachment == null) emptyList() else listOf(
                ChatAttachmentDoc(
                    id = UUID.randomUUID().toString(),
                    fileName = m.imageAttachment.fileName,
                    mediaType = m.imageAttachment.mediaType,
                    localPath = "",
                    sizeBytes = 0
                )
            ),
            route = m.route,
            timestampUtc = m.timestampUtc,
            isError = m.isError
        )
    }
)

fun ConversationDoc.toUi(): UiConversation = UiConversation(
    id = id,
    title = title,
    createdUtc = createdUtc,
    updatedUtc = updatedUtc,
    messages = messages.map { m ->
        UiMessage(
            role = m.role,
            content = m.content,
            route = m.route,
            timestampUtc = m.timestampUtc,
            isError = m.isError
        )
    }.toMutableList()
)

fun deriveTitle(conversation: UiConversation): String {
    val firstUser = conversation.messages.firstOrNull { it.role == "user" && it.content.isNotBlank() }
        ?: return "New chat"
    val flattened = firstUser.content.trim().replace(Regex("\\s+"), " ")
    return if (flattened.length > 48) flattened.take(48).trimEnd() + "…" else flattened
}
