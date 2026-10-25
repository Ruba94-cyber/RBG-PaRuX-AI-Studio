package com.parux.android.core.pairing

import android.os.Build
import com.parux.android.core.network.PaRuXHttpClient
import com.parux.android.core.network.PairingCompleteRequest

sealed interface PairingResult {
    data object Success : PairingResult
    data class Failure(val message: String) : PairingResult
}

class PairingRepository(private val credentialStore: DeviceCredentialStore) {

    companion object {
        /**
         * Why an endpoint must not be used, or null when it may be.
         *
         * A named decision rather than an inline `if`, for two reasons. It is applied at two
         * different moments - before the request, and again to the endpoint the server hands back
         * before anything is persisted - and it is the one part of this class that can be tested
         * without an Android Context, since DeviceCredentialStore needs Keystore and
         * SharedPreferences.
         */
        internal fun rejectionReasonFor(endpoint: String?): String? =
            if (LanEndpointPolicy.isAllowedPairingEndpoint(endpoint)) null
            else "That address is not one PaRuX will pair over. Pairing uses HTTPS on a private " +
                "LAN address, exactly as shown in PaRuX Windows → Settings → Local Network."
    }

    suspend fun completePairing(payload: PairingPayload): PairingResult {
        // Enforced here, not only in PairingPayload.parse, because parse is not the only way a
        // payload reaches this method: the "no camera?" manual-entry form constructs a
        // PairingPayload directly from typed text and never goes through the parser. This is the
        // one place every entry point - scanned QR, manual entry, debug paste - has to pass
        // through before anything leaves the device, so it is where the boundary belongs.
        rejectionReasonFor(payload.endpoint)?.let { return PairingResult.Failure(it) }

        return try {
            val api = PaRuXHttpClient.buildForPairing(payload.endpoint, payload.fp)
            val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim().ifBlank { "Android device" }
            val response = api.completePairing(PairingCompleteRequest(code = payload.code, deviceName = deviceName))
            // The endpoint and fingerprint that get persisted come from whatever server answered,
            // and this app then pins to them for every future request. Checking the scanned QR is
            // therefore not enough on its own: the response has to satisfy the same rule, or a
            // server could redirect this device somewhere the policy would never have allowed it
            // to be sent in the first place. A legitimate PaRuX Core always returns
            // "https://{private IPv4}:{port}" (RemoteAccessService.CompletePairing), so this can
            // only ever reject something that was not going to work anyway.
            if (rejectionReasonFor(response.endpoint) != null) {
                return PairingResult.Failure(
                    "PaRuX PC answered with an address this app will not use " +
                        "(pairing is HTTPS on a private LAN address only). Nothing was saved."
                )
            }
            credentialStore.save(
                DeviceCredentialStore.Credential(
                    endpoint = response.endpoint,
                    deviceToken = response.deviceToken,
                    certificateFingerprint = response.certificateFingerprint,
                    deviceName = deviceName
                )
            )
            PairingResult.Success
        } catch (e: retrofit2.HttpException) {
            val message = when (e.code()) {
                401 -> "That pairing code is invalid or has expired. Create a new one from PaRuX Windows."
                403 -> "PaRuX PC is not reachable on this network the way pairing expects (HTTPS, private LAN only)."
                else -> "Pairing failed (HTTP ${e.code()})."
            }
            PairingResult.Failure(message)
        } catch (e: javax.net.ssl.SSLHandshakeException) {
            PairingResult.Failure("Couldn't verify PaRuX PC's certificate. Scan the QR code again from PaRuX Windows.")
        } catch (e: java.io.IOException) {
            PairingResult.Failure("Couldn't reach PaRuX PC. Check that both devices are on the same network.")
        } catch (e: Exception) {
            PairingResult.Failure(e.message ?: "Pairing failed.")
        }
    }

    fun forgetDevice() = credentialStore.clear()

    fun isPaired(): Boolean = credentialStore.isPaired()
}
