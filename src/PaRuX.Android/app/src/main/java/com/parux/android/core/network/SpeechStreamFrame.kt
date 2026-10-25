package com.parux.android.core.network

import kotlinx.serialization.Serializable

/**
 * One NDJSON frame from PaRuX Core continuous Kokoro synthesis. Audio payloads are little-endian
 * signed PCM16; start/done/error frames carry no audio bytes. requestId + sequence are mandatory
 * client-side replay/stale-frame guards, not merely diagnostics.
 */
@Serializable
data class SpeechStreamFrame(
    val type: String = "",
    val requestId: String = "",
    val sequence: Int = -1,
    val sampleRate: Int = 24_000,
    val channels: Int = 1,
    val format: String = "pcm_s16le",
    val base64Data: String = "",
    val samples: Int = 0,
    val ttfaMilliseconds: Long = 0,
    val durationSeconds: Double = 0.0,
    val error: String? = null
)
