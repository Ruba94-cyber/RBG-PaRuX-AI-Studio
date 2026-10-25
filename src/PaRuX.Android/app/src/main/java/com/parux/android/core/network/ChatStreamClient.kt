package com.parux.android.core.network

import java.io.BufferedReader
import java.io.IOException
import java.io.InputStreamReader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.contentOrNull
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/** A content delta, or a terminal/control-plane event. */
sealed interface ChatStreamEvent {
    data class Delta(val text: String) : ChatStreamEvent
    data object Done : ChatStreamEvent
    data class Error(val message: String) : ChatStreamEvent
    data object SwitchedToUncensored : ChatStreamEvent
    data class RagSources(val files: List<String>) : ChatStreamEvent
    data class SkillSelected(val displayName: String, val mode: String) : ChatStreamEvent
    /**
     * This turn could not be served by the PC's local runtime and was answered by the cloud
     * provider the user configured as a fallback, so it left the device. The PC announces it before
     * the first token; Android has to show it, or the guarantee that using the fallback is always
     * visible would only hold on one of the two clients.
     */
    data class CloudFallback(val provider: String, val model: String, val reason: String?) : ChatStreamEvent
    /** Sanitized explanation of the exact text-model routing decision for this turn. */
    data class ModelRouted(
        val selectedModel: String?,
        val mode: String,
        val autoProfile: String?,
        val requiredIntelligence: Double?,
        val selectedIntelligence: Double?,
        val tokensPerSecond: Double?,
        val manualLock: Boolean,
        val reason: String?
    ) : ChatStreamEvent
}

/**
 * Streams /api/v1/chat/completions exactly the way the Windows Chat client does. Synthetic PaRuX
 * control-plane events are consumed before normal choices[0].delta.content frames. Conversation
 * and Project identities are added as Core control metadata: the Project id lives under a
 * reserved SkillParameters key that ChatProxy never serializes to the model provider, while
 * ConversationId is an existing server contract field. This keeps Android in the same bounded KV
 * cache scope as Windows without sending Project text or Windows state to the phone/model.
 */
class ChatStreamClient(private val okHttp: OkHttpClient, private val endpoint: String) {

    companion object {
        private const val PROJECT_SCOPE_PARAMETER = "__parux_project_scope"
    }

    private val json = Json { ignoreUnknownKeys = true }
    private var activeCall: Call? = null

    fun stream(
        request: ChatCompletionRequest,
        conversationId: String? = null,
        projectId: String? = null
    ): Flow<ChatStreamEvent> = callbackFlow {
        val base = PaRuXHttpClient.json
            .encodeToJsonElement(ChatCompletionRequest.serializer(), request)
            .jsonObject
        val fields = base.toMutableMap()
        conversationId?.trim()?.takeIf { it.isNotEmpty() }?.let {
            fields["conversationId"] = JsonPrimitive(it)
        }
        projectId?.trim()?.takeIf { it.isNotEmpty() }?.let {
            fields["skillParameters"] = JsonObject(mapOf(PROJECT_SCOPE_PARAMETER to JsonPrimitive(it)))
        }
        val body = JsonObject(fields).toString().toRequestBody("application/json".toMediaType())
        val httpRequest = Request.Builder()
            .url(endpoint.trimEnd('/') + "/api/v1/chat/completions")
            .post(body)
            .build()

        val call = okHttp.newCall(httpRequest)
        activeCall = call

        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (call.isCanceled()) { close(); return }
                trySend(ChatStreamEvent.Error(e.message ?: "Network error"))
                close()
            }

            override fun onResponse(call: Call, response: Response) {
                response.use { resp ->
                    if (!resp.isSuccessful) {
                        trySend(ChatStreamEvent.Error("PaRuX Server rejected the request (HTTP ${resp.code})."))
                        close()
                        return
                    }
                    val source = resp.body?.byteStream()
                    if (source == null) {
                        trySend(ChatStreamEvent.Error("Empty response body."))
                        close()
                        return
                    }
                    try {
                        BufferedReader(InputStreamReader(source, Charsets.UTF_8)).use { reader ->
                            while (true) {
                                val line = reader.readLine() ?: break
                                if (line.isEmpty() || !line.startsWith("data:")) continue
                                val data = line.substring(5).trim()
                                if (data.isEmpty()) continue
                                if (data == "[DONE]") { trySend(ChatStreamEvent.Done); break }
                                val modelRouted = extractModelRouted(data)
                                val ragSources = extractRagSources(data)
                                val skillSelected = extractSkillSelected(data)
                                val cloudFallback = parseCloudFallbackEvent(data)
                                when {
                                    modelRouted != null -> trySend(modelRouted)
                                    cloudFallback != null -> trySend(cloudFallback)
                                    isSwitchedToUncensoredEvent(data) -> trySend(ChatStreamEvent.SwitchedToUncensored)
                                    ragSources != null -> trySend(ChatStreamEvent.RagSources(ragSources))
                                    skillSelected != null -> trySend(skillSelected)
                                    else -> extractContent(data)?.let { fragment -> trySend(ChatStreamEvent.Delta(fragment)) }
                                }
                            }
                        }
                        close()
                    } catch (e: Exception) {
                        if (!call.isCanceled()) trySend(ChatStreamEvent.Error(e.message ?: "Stream interrupted"))
                        close()
                    }
                }
            }
        })

        awaitClose { call.cancel() }
    }.flowOn(Dispatchers.IO)

    fun cancel() {
        activeCall?.cancel()
        activeCall = null
    }

    private fun isSwitchedToUncensoredEvent(data: String): Boolean {
        val root = parseObject(data) ?: return false
        val marker = root["parux_event"]?.jsonPrimitive ?: return false
        return marker.isString && marker.content == "switched_uncensored"
    }

    private fun extractModelRouted(data: String): ChatStreamEvent.ModelRouted? {
        val root = parseObject(data) ?: return null
        if (root["parux_event"]?.jsonPrimitive?.contentOrNull != "model_routed") return null
        fun text(name: String): String? = root[name]?.jsonPrimitive?.contentOrNull
        fun number(name: String): Double? = text(name)?.toDoubleOrNull()
        val manualLock = text("manualLock")?.equals("true", ignoreCase = true) == true
        return ChatStreamEvent.ModelRouted(
            selectedModel = text("selectedModel"),
            mode = text("mode") ?: "auto",
            autoProfile = text("autoProfile"),
            requiredIntelligence = number("requiredIntelligence"),
            selectedIntelligence = number("selectedIntelligence"),
            tokensPerSecond = number("tokensPerSecond"),
            manualLock = manualLock,
            reason = text("reason")
        )
    }

    private fun extractRagSources(data: String): List<String>? {
        val root = parseObject(data) ?: return null
        val marker = root["parux_event"]?.jsonPrimitive ?: return null
        if (!marker.isString || marker.content != "rag_sources") return null
        val files = root["files"] as? JsonArray ?: return null
        return files.mapNotNull { it.jsonPrimitive.contentOrNull }
    }

    private fun extractSkillSelected(data: String): ChatStreamEvent.SkillSelected? {
        val root = parseObject(data) ?: return null
        val marker = root["parux_event"]?.jsonPrimitive ?: return null
        if (!marker.isString || marker.content != "skill_selected") return null
        val name = root["displayName"]?.jsonPrimitive?.contentOrNull ?: "General"
        val mode = root["mode"]?.jsonPrimitive?.contentOrNull ?: "auto"
        return ChatStreamEvent.SkillSelected(name, mode)
    }

    private fun extractContent(data: String): String? {
        val root = parseObject(data) ?: return null
        val choices = root["choices"] as? JsonArray ?: return null
        val first = choices.firstOrNull()?.jsonObject ?: return null
        val delta = first["delta"]?.jsonObject ?: return null
        val content = delta["content"] ?: return null
        return if (content.jsonPrimitive.isString) content.jsonPrimitive.content else null
    }

    private fun parseObject(data: String): JsonObject? =
        try { json.parseToJsonElement(data) as? JsonObject } catch (_: Exception) { null }
}

/**
 * Top-level and internal rather than a private method, so the one parser that decides whether a
 * user is told their turn left the device is directly testable. A missing provider or model name
 * degrades to a still-honest notice; it never degrades to no notice at all.
 */
internal fun parseCloudFallbackEvent(data: String): ChatStreamEvent.CloudFallback? {
    val root = try { Json.parseToJsonElement(data) as? JsonObject } catch (_: Exception) { null } ?: return null
    val marker = root["parux_event"] as? JsonPrimitive ?: return null
    if (!marker.isString || marker.content != "cloud_fallback") return null
    return ChatStreamEvent.CloudFallback(
        provider = (root["provider"] as? JsonPrimitive)?.contentOrNull?.takeIf { it.isNotBlank() } ?: "a cloud provider",
        model = (root["model"] as? JsonPrimitive)?.contentOrNull.orEmpty(),
        reason = (root["reason"] as? JsonPrimitive)?.contentOrNull
    )
}
