package com.parux.android.core.network

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/**
 * Small authenticated control-plane client for the one Core-owned Chat model-routing preference.
 * Android talks to a dedicated sanitized model-list endpoint and therefore never receives a
 * workstation file path merely to render the selector.
 */
class ChatModelRoutingClient(
    private val okHttp: OkHttpClient,
    private val endpoint: String
) {
    suspend fun getPreference(): ChatRoutingPreference = withContext(Dispatchers.IO) {
        get("/api/v1/chat/model-routing", ChatRoutingPreference.serializer())
    }

    suspend fun setPreference(request: SetChatRoutingPreferenceRequest): ChatRoutingPreference =
        withContext(Dispatchers.IO) {
            val body = PaRuXHttpClient.json.encodeToString(SetChatRoutingPreferenceRequest.serializer(), request)
                .toRequestBody("application/json".toMediaType())
            val http = Request.Builder()
                .url(endpoint.trimEnd('/') + "/api/v1/chat/model-routing")
                .post(body)
                .build()
            okHttp.newCall(http).execute().use { response ->
                if (!response.isSuccessful)
                    throw java.io.IOException("PaRuX rejected model routing (HTTP ${response.code}).")
                val text = response.body?.string() ?: throw java.io.IOException("Empty model-routing response.")
                PaRuXHttpClient.json.decodeFromString(ChatRoutingPreference.serializer(), text)
            }
        }

    suspend fun listManagedModels(): List<ManagedChatModelInfo> = withContext(Dispatchers.IO) {
        get(
            "/api/v1/chat/model-routing/models",
            kotlinx.serialization.builtins.ListSerializer(ManagedChatModelInfo.serializer())
        )
    }

    private fun <T> get(path: String, serializer: kotlinx.serialization.KSerializer<T>): T {
        val request = Request.Builder().url(endpoint.trimEnd('/') + path).get().build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful)
                throw java.io.IOException("PaRuX request failed (HTTP ${response.code}).")
            val text = response.body?.string() ?: throw java.io.IOException("Empty PaRuX response.")
            return PaRuXHttpClient.json.decodeFromString(serializer, text)
        }
    }
}

@Serializable
data class ChatRoutingPreference(
    val mode: String = "auto",
    val autoProfile: String = "balanced",
    val manualModelFileName: String? = null,
    val selector: String = "parux-auto:balanced"
)

@Serializable
data class SetChatRoutingPreferenceRequest(
    val mode: String,
    val autoProfile: String? = null,
    val manualModelFileName: String? = null
)

@Serializable
data class ManagedChatModelInfo(
    val fileName: String = "",
    val sizeBytes: Long = 0,
    val quantization: String = "unknown",
    val alias: String? = null,
    val lastBenchmarkTokensPerSecond: Double? = null
)
