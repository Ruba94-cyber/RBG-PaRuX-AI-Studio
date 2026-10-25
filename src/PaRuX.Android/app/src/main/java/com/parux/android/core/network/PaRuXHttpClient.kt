package com.parux.android.core.network

import com.parux.android.core.pairing.DeviceCredentialStore
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * Builds the one authenticated, certificate-pinned HTTP client PaRuX Android uses to talk to
 * PaRuX Server. There is deliberately no other network path in this app: no direct connection
 * to a model server, media runtime, or filesystem share (PARUX_ANDROID_API.md).
 */
object PaRuXHttpClient {

    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    /** Null when the app is not paired yet - callers route to the pairing screen instead. */
    fun buildAuthenticated(credentialStore: DeviceCredentialStore): AuthenticatedClient? {
        val credential = credentialStore.load() ?: return null
        val trustManager = PinnedCertificateTrustManager(credential.certificateFingerprint)

        val okHttp = createAuthenticatedOkHttpClient(credential.deviceToken, trustManager)

        val contentType = "application/json".toMediaType()
        val retrofit = Retrofit.Builder()
            .baseUrl(credential.endpoint.trimEnd('/') + "/")
            .client(okHttp)
            .addConverterFactory(json.asConverterFactory(contentType))
            .build()

        return AuthenticatedClient(
            okHttp = okHttp,
            api = retrofit.create(PaRuXApi::class.java),
            endpoint = credential.endpoint,
            deviceToken = credential.deviceToken
        )
    }

    internal fun createAuthenticatedOkHttpClient(
        deviceToken: String,
        trustManager: PinnedCertificateTrustManager
    ): OkHttpClient = OkHttpClient.Builder()
        .sslSocketFactory(trustManager.toSocketFactory(), trustManager)
        .followRedirects(false)
        .followSslRedirects(false)
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.SECONDS)   // streaming chat/video responses can run for minutes
        .writeTimeout(60, TimeUnit.SECONDS)
        .addInterceptor { chain ->
            val original = chain.request()
            MobileEndpointBoundary.desktopOnlyReason(original.method, original.url.encodedPath)?.let { reason ->
                throw IOException(reason)
            }
            val request: Request = original.newBuilder()
                .addHeader("X-PaRuX-Device-Token", deviceToken)
                .build()
            chain.proceed(request)
        }
        .build()

    /** Unauthenticated client used only for the one pre-auth call: pairing completion. It still
     * pins the certificate fingerprint shown in the QR - the pairing code is single-use and
     * short-lived, but the TLS connection carrying it is not exempt from pinning. */
    fun buildForPairing(endpoint: String, certificateFingerprintHex: String): PaRuXApi {
        val trustManager = PinnedCertificateTrustManager(certificateFingerprintHex)
        val okHttp = OkHttpClient.Builder()
            .sslSocketFactory(trustManager.toSocketFactory(), trustManager)
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .build()
        val contentType = "application/json".toMediaType()
        val retrofit = Retrofit.Builder()
            .baseUrl(endpoint.trimEnd('/') + "/")
            .client(okHttp)
            .addConverterFactory(json.asConverterFactory(contentType))
            .build()
        return retrofit.create(PaRuXApi::class.java)
    }

    data class AuthenticatedClient(
        val okHttp: OkHttpClient,
        val api: PaRuXApi,
        val endpoint: String,
        val deviceToken: String
    )
}
