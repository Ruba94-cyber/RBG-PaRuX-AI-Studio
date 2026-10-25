package com.parux.android.core.network

import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * Exercises the pinning logic against a synthetic self-signed certificate (CN=PaRuX Test Server,
 * SAN parux-test.invalid / 192.0.2.10) stored as parux-lan-cert.der. It is a test fixture, not a
 * real server certificate; realFingerprint below is its SHA-256.
 */
class PinnedCertificateTrustManagerTest {

    private fun loadRealServerCertificate(): X509Certificate {
        val stream = javaClass.classLoader!!.getResourceAsStream("parux-lan-cert.der")!!
        return CertificateFactory.getInstance("X.509").generateCertificate(stream) as X509Certificate
    }

    private val realFingerprint = "0E5EFEF31F9E5E423376B3C27891147DB371E2428D8BCA1E3388FE89E0453F16"

    @Test
    fun `accepts the exact fingerprint captured at pairing`() {
        val cert = loadRealServerCertificate()
        val manager = PinnedCertificateTrustManager(realFingerprint)
        // Must not throw.
        manager.checkServerTrusted(arrayOf(cert), "RSA")
    }

    @Test
    fun `accepts a colon-formatted fingerprint the same way (as shown in the Windows UI)`() {
        val cert = loadRealServerCertificate()
        val colonFormatted = realFingerprint.chunked(2).joinToString(":")
        val manager = PinnedCertificateTrustManager(colonFormatted)
        manager.checkServerTrusted(arrayOf(cert), "RSA")
    }

    @Test
    fun `rejects a certificate that does not match the pinned fingerprint`() {
        val cert = loadRealServerCertificate()
        val wrongPin = "0000000000000000000000000000000000000000000000000000000000000000"
        val manager = PinnedCertificateTrustManager(wrongPin)
        assertThrows(CertificateException::class.java) {
            manager.checkServerTrusted(arrayOf(cert), "RSA")
        }
    }

    @Test
    fun `never trusts client certificates`() {
        val cert = loadRealServerCertificate()
        val manager = PinnedCertificateTrustManager(realFingerprint)
        assertThrows(CertificateException::class.java) {
            manager.checkClientTrusted(arrayOf(cert), "RSA")
        }
    }
}
