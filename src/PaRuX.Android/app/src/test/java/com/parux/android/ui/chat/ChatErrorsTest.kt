package com.parux.android.ui.chat

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChatErrorsTest {
    @Test
    fun unreachablePcIsExplainedInTheUsersTermsWithoutTheRawAddress() {
        val text = ChatErrors.describe(java.net.ConnectException("Failed to connect to /192.168.1.20:51124"))
        assertTrue(text.startsWith("Can't reach your PaRuX PC"))
        assertFalse(text.contains("192.168.1"))
    }

    @Test
    fun timeoutsAndCertificateFailuresHaveTheirOwnGuidance() {
        assertTrue(ChatErrors.describe(java.net.SocketTimeoutException("timeout")).contains("took too long"))
        assertTrue(ChatErrors.describe(javax.net.ssl.SSLHandshakeException("bad cert")).contains("Pair again"))
    }

    @Test
    fun otherFailuresKeepTheirMessage() {
        assertEquals("stream closed", ChatErrors.describe(java.io.IOException("stream closed")))
        assertEquals("Something went wrong.", ChatErrors.describe(IllegalStateException()))
    }
}
