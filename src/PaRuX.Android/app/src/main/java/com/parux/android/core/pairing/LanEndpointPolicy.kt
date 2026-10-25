package com.parux.android.core.pairing

/**
 * The endpoints this app is willing to talk to at all.
 *
 * A QR code is untrusted input by definition - anyone can print one - and before this nothing
 * between the scanner and the network call checked what it contained. `PairingPayload.parse`
 * accepted any non-blank endpoint string, so a hostile QR carrying
 * `{"endpoint":"http://attacker.example","code":"...","fp":"..."}` would have made the phone POST
 * the user's pairing code to an attacker's host in plaintext. Certificate pinning does not save
 * that case: OkHttp only uses the pinned `sslSocketFactory` for an `https` URL, so on an `http`
 * URL the pinning built for pairing is silently inert. The attacker also controls `fp`, so pinning
 * offers nothing against a hostile QR even over TLS.
 *
 * The consequence was worse than leaking a single-use code: `PairingRepository` persisted the
 * endpoint and fingerprint returned by whatever server answered, so a hostile QR could
 * permanently repoint this app - pinned to the attacker's own certificate.
 *
 * The rule enforced here is not invented for this file. It is exactly what PaRuX Windows itself
 * produces and permits: `RemoteAccessService` binds only to an `IsPrivateAssignedAddress` IPv4 and
 * always returns `https://{that address}:{port}`, and Core already answers 403 with
 * "HTTPS, private LAN only" for anything else. This makes the client refuse before sending, rather
 * than trusting the server to refuse after.
 *
 * Deliberately no hostnames: PaRuX only ever advertises a literal private IPv4, so accepting a DNS
 * name would widen this surface past anything PaRuX itself creates.
 */
object LanEndpointPolicy {

    /** Mirrors PaRuX.Server's RemoteAccessService.IsPrivateAddress: RFC 1918 IPv4 only. */
    fun isPrivateIpv4(host: String): Boolean {
        val parts = host.split('.')
        if (parts.size != 4) return false
        val octets = parts.map { part ->
            // "01" and "1_0" and "" must not sneak through a lenient parse.
            if (part.isEmpty() || part.length > 3 || !part.all { it.isDigit() }) return false
            part.toInt().also { if (it > 255) return false }
        }
        return when {
            octets[0] == 10 -> true
            octets[0] == 172 && octets[1] in 16..31 -> true
            octets[0] == 192 && octets[1] == 168 -> true
            else -> false
        }
    }

    /**
     * True only for `https://<private IPv4>[:port]`, with no userinfo, no path beyond `/`, and no
     * query or fragment - the exact shape PaRuX Windows renders into its pairing QR.
     */
    fun isAllowedPairingEndpoint(endpoint: String?): Boolean {
        val value = endpoint?.trim().orEmpty()
        if (value.isEmpty()) return false

        val uri = try { java.net.URI(value) } catch (_: Exception) { return false }
        if (!"https".equals(uri.scheme, ignoreCase = true)) return false

        // Userinfo is how a crafted URL disguises its real host ("https://192.168.1.5@evil.test").
        if (uri.userInfo != null) return false
        if (!uri.query.isNullOrEmpty() || !uri.fragment.isNullOrEmpty()) return false
        if (!(uri.path.isNullOrEmpty() || uri.path == "/")) return false

        val host = uri.host ?: return false
        if (uri.port != -1 && uri.port !in 1..65535) return false
        return isPrivateIpv4(host)
    }
}
