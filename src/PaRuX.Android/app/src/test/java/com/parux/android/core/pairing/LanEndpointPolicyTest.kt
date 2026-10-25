package com.parux.android.core.pairing

import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * A QR code is untrusted input - anyone can print one - and nothing between the scanner and the
 * network call used to check what it contained. These pin the boundary that now does.
 *
 * The attack this closes: a QR carrying `http://attacker.example` made the phone POST the user's
 * pairing code in plaintext to an attacker's host. Certificate pinning did not help, because
 * OkHttp only uses the pinned socket factory for an `https` URL - on `http` the pinning built for
 * pairing is silently inert - and the attacker controls the pinned fingerprint in the QR anyway.
 * The persisted result was the attacker's endpoint and certificate, so the hijack outlived the
 * scan.
 */
class LanEndpointPolicyTest {

    @Test
    fun `accepts the exact endpoint shape PaRuX Windows renders into its QR`() {
        // RemoteAccessService always returns "https://{private IPv4}:{port}".
        assertTrue(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.50:51124"))
        assertTrue(LanEndpointPolicy.isAllowedPairingEndpoint("https://10.0.0.7:51124"))
        assertTrue(LanEndpointPolicy.isAllowedPairingEndpoint("https://172.16.4.2:51124"))
        assertTrue(LanEndpointPolicy.isAllowedPairingEndpoint("https://172.31.255.254:51124"))
        // A trailing slash and no explicit port are still the same endpoint.
        assertTrue(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.5/"))
    }

    @Test
    fun `refuses plaintext http, which is where pinning silently stops applying`() {
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("http://192.168.1.50:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("HTTP://192.168.1.50:51124"))
    }

    @Test
    fun `refuses a public address even over https`() {
        // Pairing is private-LAN only. The attacker picks the fingerprint in the QR too, so TLS
        // alone proves nothing about who is on the other end.
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://attacker.example"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://8.8.8.8:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://172.32.0.1:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://172.15.255.255:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://193.168.1.5:51124"))
    }

    @Test
    fun `refuses a url whose userinfo disguises the real host`() {
        // "https://192.168.1.5@evil.test" connects to evil.test, not to 192.168.1.5.
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.5@evil.test/"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://user:pw@192.168.1.5:51124"))
    }

    @Test
    fun `refuses anything carrying a path, query or fragment`() {
        // PaRuX's own QR never contains these; accepting them widens what a crafted QR can steer.
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.5/redirect"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.5?next=evil"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://192.168.1.5#evil"))
    }

    @Test
    fun `refuses hostnames, including ones that merely look local`() {
        // PaRuX only ever advertises a literal private IPv4, so a DNS name is out of scope -
        // and a name is exactly what an attacker controls the resolution of.
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://parux.local:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://localhost:51124"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://127.0.0.1:51124"))
    }

    @Test
    fun `refuses blank, malformed and non-url input without throwing`() {
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint(null))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint(""))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("   "))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("not a url at all"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("https://"))
        assertFalse(LanEndpointPolicy.isAllowedPairingEndpoint("ftp://192.168.1.5"))
    }

    @Test
    fun `private ipv4 matching rejects malformed octets rather than parsing them leniently`() {
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.1"))
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.1.5.6"))
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.1.256"))
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.1."))
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.-1.5"))
        assertFalse(LanEndpointPolicy.isPrivateIpv4("192.168.0x1.5"))
        assertTrue(LanEndpointPolicy.isPrivateIpv4("10.255.255.255"))
    }
}

/**
 * The QR parser is not the only door. The "no camera?" manual-entry form builds a PairingPayload
 * straight from typed text and never calls parse, so a check that lived only in the parser would
 * have left that path open - which is exactly what a first version of this fix did. The guard was
 * moved to PairingRepository, the one place every entry point passes through before anything
 * leaves the device.
 */
class PairingRepositoryEndpointGuardTest {

    @Test
    fun `an endpoint that never went through the parser is still refused`() {
        for (endpoint in listOf(
            "http://192.168.1.5:51124",
            "https://attacker.example",
            "https://192.168.1.5@evil.test",
            ""
        )) {
            assertNotNull(
                "Expected a refusal reason for $endpoint",
                PairingRepository.rejectionReasonFor(endpoint)
            )
        }
    }

    @Test
    fun `a real PaRuX endpoint is not refused`() {
        assertNull(PairingRepository.rejectionReasonFor("https://192.168.1.50:51124"))
    }
}
