package com.parux.android.core.network

import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * PaRuX.Server's LAN listener uses a self-signed certificate (RemoteAccessService.cs generates
 * one 5-year RSA-2048 cert per configured address; there is no CA behind it). The Android system
 * trust store will therefore reject it before OkHttp's own CertificatePinner would ever run, since
 * that store only pins against certs that already chain to a trusted root.
 *
 * Instead Android pins exactly the certificate fingerprint captured during QR pairing
 * (trust-on-first-use, matching docs/PARUX_ANDROID_API.md): this TrustManager trusts a
 * connection if and only if the presented leaf certificate's SHA-256 (over its full DER
 * encoding - the server computes the fingerprint the same way, SHA256(certificate.RawData))
 * matches the pinned value. No system CA is consulted at all.
 */
class PinnedCertificateTrustManager(private val pinnedFingerprintHex: String) : X509TrustManager {

    private val normalizedPin = pinnedFingerprintHex.replace(":", "").trim().uppercase()

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Client certificates are not used by PaRuX.")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull()
            ?: throw CertificateException("No certificate presented by the server.")
        val digest = MessageDigest.getInstance("SHA-256").digest(leaf.encoded)
        val hex = digest.joinToString("") { "%02X".format(it) }
        if (hex != normalizedPin) {
            throw CertificateException(
                "The server's certificate does not match the one pinned during pairing. " +
                    "Re-pair this device if PaRuX Windows's certificate genuinely changed."
            )
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun toSocketFactory(): SSLSocketFactory {
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(this), SecureRandom())
        return context.socketFactory
    }
}
