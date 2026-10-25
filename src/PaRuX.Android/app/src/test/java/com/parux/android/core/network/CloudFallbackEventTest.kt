package com.parux.android.core.network

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The PC announces a cloud-answered turn once, in the stream, before the first token. If this
 * parser returns null the user sees an ordinary-looking local answer that in fact left their
 * machine - so these cases are about the notice never being lost, not about field fidelity.
 */
class CloudFallbackEventTest {

    @Test
    fun `a cloud fallback event is recognised with its provider model and reason`() {
        val event = parseCloudFallbackEvent(
            """{"parux_event":"cloud_fallback","provider":"OpenRouter","model":"openai/gpt-oss-120b","reason":"The local model did not become ready."}"""
        )

        assertEquals("OpenRouter", event?.provider)
        assertEquals("openai/gpt-oss-120b", event?.model)
        assertEquals("The local model did not become ready.", event?.reason)
    }

    @Test
    fun `a missing provider name still produces a notice`() {
        val event = parseCloudFallbackEvent("""{"parux_event":"cloud_fallback"}""")

        assertEquals("a cloud provider", event?.provider)
        assertEquals("", event?.model)
        assertNull(event?.reason)
    }

    @Test
    fun `a blank provider name is treated as missing rather than shown as empty`() {
        assertEquals("a cloud provider", parseCloudFallbackEvent(
            """{"parux_event":"cloud_fallback","provider":"   "}""")?.provider)
    }

    @Test
    fun `ordinary stream frames are not mistaken for a fallback notice`() {
        assertNull(parseCloudFallbackEvent("""{"choices":[{"delta":{"content":"hello"}}]}"""))
        assertNull(parseCloudFallbackEvent("""{"parux_event":"model_routed","provider":"OpenRouter"}"""))
        assertNull(parseCloudFallbackEvent("""{"parux_event":123}"""))
        assertNull(parseCloudFallbackEvent("[DONE]"))
        assertNull(parseCloudFallbackEvent("not json at all"))
    }
}
