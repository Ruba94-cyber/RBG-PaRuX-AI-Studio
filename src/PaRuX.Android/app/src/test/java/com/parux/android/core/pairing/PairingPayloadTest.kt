package com.parux.android.core.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairingPayloadTest {

    @Test
    fun `parses the exact payload shape PaRuX Windows renders in its QR`() {
        // Matches BuildPairingQrImageAsync in SettingsView.xaml.cs field-for-field.
        val json = """{"v":1,"endpoint":"https://192.168.1.50:51124","code":"380511","fp":"0E5EFEF3"}"""
        val payload = PairingPayload.parse(json)
        assertEquals("https://192.168.1.50:51124", payload?.endpoint)
        assertEquals("380511", payload?.code)
        assertEquals("0E5EFEF3", payload?.fp)
    }

    @Test
    fun `rejects a QR that is not a PaRuX pairing code instead of throwing`() {
        assertNull(PairingPayload.parse("https://example.com/not-parux"))
        assertNull(PairingPayload.parse("{\"unrelated\":true}"))
        assertNull(PairingPayload.parse(""))
    }

    @Test
    fun `rejects a payload missing a required field`() {
        assertNull(PairingPayload.parse("""{"v":1,"endpoint":"https://192.168.1.5:1","code":""}"""))
    }

    @Test
    fun `rejects a hostile QR that would send the pairing code over plaintext http`() {
        // The payload is otherwise perfectly well-formed - only the scheme differs - and this is
        // the whole attack: on an http URL the pinned socket factory is never used, so the code
        // would have left the device in the clear.
        assertNull(
            PairingPayload.parse(
                """{"v":1,"endpoint":"http://192.168.1.50:51124","code":"380511","fp":"0E5EFEF3"}"""
            )
        )
    }

    @Test
    fun `rejects a hostile QR pointing at a host outside the private LAN`() {
        // The attacker also chooses fp, so certificate pinning proves nothing here.
        assertNull(
            PairingPayload.parse(
                """{"v":1,"endpoint":"https://attacker.example","code":"380511","fp":"DEADBEEF"}"""
            )
        )
        assertNull(
            PairingPayload.parse(
                """{"v":1,"endpoint":"https://192.168.1.5@evil.test","code":"380511","fp":"DEADBEEF"}"""
            )
        )
    }
}
