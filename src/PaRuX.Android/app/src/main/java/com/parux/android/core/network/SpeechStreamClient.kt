package com.parux.android.core.network

import android.util.Base64
import java.io.BufferedReader
import java.io.IOException
import java.io.InputStreamReader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/**
 * Paired-device continuous TTS transport. The authenticated/pinned OkHttpClient supplied by
 * PaRuXHttpClient remains the sole network authority; this class only adds streaming framing.
 * Every frame is validated before reaching AudioTrack so a stale prior turn or malformed PCM
 * packet cannot be played by Live Voice.
 */
class SpeechStreamClient(private val okHttp: OkHttpClient, private val endpoint: String) {
    companion object {
        const val SampleRate = 24_000
        const val Channels = 1
        const val Format = "pcm_s16le"
    }

    private val json = Json { ignoreUnknownKeys = true }
    @Volatile private var activeCall: Call? = null

    fun stream(request: SynthesizeRequest): Flow<SpeechStreamFrame> = callbackFlow {
        val body = PaRuXHttpClient.json.encodeToString(SynthesizeRequest.serializer(), request)
            .toRequestBody("application/json".toMediaType())
        val httpRequest = Request.Builder()
            .url(endpoint.trimEnd('/') + "/api/v2/speech/synthesize/stream")
            .post(body)
            .build()
        val call = okHttp.newCall(httpRequest)
        activeCall = call

        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (call.isCanceled()) { close(); return }
                close(IOException(e.message ?: "Continuous speech network error", e))
            }

            override fun onResponse(call: Call, response: Response) {
                response.use { resp ->
                    if (!resp.isSuccessful) {
                        val detail = runCatching { resp.body?.string().orEmpty() }.getOrDefault("")
                        close(IOException("PaRuX continuous speech rejected the request (HTTP ${resp.code})${if (detail.isBlank()) "." else ": $detail"}"))
                        return
                    }

                    val headerRequestId = resp.header("X-PaRuX-Request-Id").orEmpty()
                    if (headerRequestId.isBlank()) {
                        close(IOException("PaRuX continuous speech response omitted its request id."))
                        return
                    }
                    val source = resp.body?.byteStream()
                    if (source == null) {
                        close(IOException("PaRuX continuous speech returned an empty response body."))
                        return
                    }

                    var expectedSequence = 0
                    var sawStart = false
                    var terminal = false
                    try {
                        BufferedReader(InputStreamReader(source, Charsets.UTF_8)).use { reader ->
                            while (!terminal) {
                                val line = reader.readLine()
                                    ?: throw IOException("Continuous speech ended without a terminal frame.")
                                if (line.isBlank()) continue
                                val frame = try { json.decodeFromString<SpeechStreamFrame>(line) }
                                catch (e: Exception) { throw IOException("Continuous speech returned invalid NDJSON: ${e.message}", e) }

                                if (frame.requestId != headerRequestId)
                                    throw IOException("Continuous speech returned a frame for a stale/different request.")
                                if (frame.sequence != expectedSequence)
                                    throw IOException("Continuous speech sequence mismatch: expected $expectedSequence, received ${frame.sequence}.")
                                if (frame.sampleRate != SampleRate || frame.channels != Channels || frame.format != Format)
                                    throw IOException("Continuous speech changed PCM format mid-stream.")

                                when (frame.type) {
                                    "start" -> {
                                        if (sawStart || expectedSequence != 0)
                                            throw IOException("Continuous speech emitted a duplicate/out-of-order start frame.")
                                        if (frame.base64Data.isNotEmpty())
                                            throw IOException("Continuous speech start frame unexpectedly carried audio.")
                                        sawStart = true
                                    }
                                    "audio" -> {
                                        if (!sawStart) throw IOException("Continuous speech emitted audio before start.")
                                        if (frame.samples <= 0 || frame.base64Data.isBlank())
                                            throw IOException("Continuous speech emitted an empty audio frame.")
                                        val pcm = try { Base64.decode(frame.base64Data, Base64.DEFAULT) }
                                        catch (e: IllegalArgumentException) { throw IOException("Continuous speech returned invalid Base64 PCM.", e) }
                                        if (pcm.size != frame.samples * 2)
                                            throw IOException("Continuous speech PCM byte count does not match its declared samples.")
                                    }
                                    "done" -> {
                                        if (!sawStart) throw IOException("Continuous speech completed before start.")
                                        if (frame.base64Data.isNotEmpty())
                                            throw IOException("Continuous speech terminal frame unexpectedly carried audio.")
                                        terminal = true
                                    }
                                    "error" -> {
                                        if (!sawStart) throw IOException(frame.error ?: "Continuous speech failed before start.")
                                        terminal = true
                                    }
                                    else -> throw IOException("Unsupported continuous speech frame type '${frame.type}'.")
                                }

                                expectedSequence++
                                if (trySend(frame).isFailure) {
                                    call.cancel()
                                    return
                                }
                            }
                        }
                        close()
                    } catch (e: Exception) {
                        if (!call.isCanceled()) close(e)
                        else close()
                    }
                }
            }
        })

        awaitClose {
            call.cancel()
            if (activeCall === call) activeCall = null
        }
    }.flowOn(Dispatchers.IO)

    fun cancel() {
        activeCall?.cancel()
        activeCall = null
    }
}
