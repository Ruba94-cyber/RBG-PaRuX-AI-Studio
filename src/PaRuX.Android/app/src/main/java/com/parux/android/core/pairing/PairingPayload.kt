package com.parux.android.core.pairing

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** The QR payload PaRuX Windows renders in Settings > Local Network > Pair a device. */
@Serializable
data class PairingPayload(
    val v: Int = 1,
    val endpoint: String,
    val code: String,
    val fp: String
) {
    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        /** Returns null for anything that is not a PaRuX pairing QR - callers show a generic
         * "not a PaRuX pairing code" message rather than a parser error.
         *
         * The endpoint is checked against [LanEndpointPolicy] here rather than later: this is the
         * boundary where untrusted scanned text becomes something the app will connect to, and a
         * payload whose endpoint is not `https://<private IPv4>` is not a PaRuX pairing code at
         * all. Rejecting it at parse time means no caller can reach the network with one. */
        fun parse(scannedText: String): PairingPayload? = try {
            val payload = json.decodeFromString<PairingPayload>(scannedText)
            if (payload.code.isBlank()
                || payload.fp.isBlank()
                || !LanEndpointPolicy.isAllowedPairingEndpoint(payload.endpoint)) null
            else payload
        } catch (_: Exception) {
            null
        }
    }
}
