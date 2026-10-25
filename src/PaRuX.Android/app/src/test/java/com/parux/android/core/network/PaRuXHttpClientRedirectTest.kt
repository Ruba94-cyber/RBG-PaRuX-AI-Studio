package com.parux.android.core.network

import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetAddress
import java.net.ServerSocket
import java.net.SocketTimeoutException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import okhttp3.Request

class PaRuXHttpClientRedirectTest {

    @Test
    fun `authenticated client sends device token to origin and does not follow redirect`() {
        val loopback = InetAddress.getByName("127.0.0.1")
        val redirectTarget = ServerSocket(0, 1, loopback)
        val origin = ServerSocket(0, 1, loopback)
        redirectTarget.soTimeout = 1_000
        val executor = Executors.newFixedThreadPool(2)
        try {
            val targetAccepted = executor.submit<Boolean> {
                try {
                    redirectTarget.accept().use { true }
                } catch (_: SocketTimeoutException) {
                    false
                }
            }
            val targetPort = redirectTarget.localPort
            val originResponse = executor.submit<String> {
                origin.accept().use { socket ->
                    val request = readRequestHeaders(socket)
                    OutputStreamWriter(socket.getOutputStream(), Charsets.US_ASCII).use { writer ->
                        writer.write(
                            "HTTP/1.1 302 Found\r\n" +
                                "Location: http://127.0.0.1:$targetPort/capture\r\n" +
                                "Content-Length: 0\r\n" +
                                "Connection: close\r\n\r\n"
                        )
                        writer.flush()
                    }
                    request
                }
            }

            val client = PaRuXHttpClient.createAuthenticatedOkHttpClient(
                "unit-test-device-token",
                PinnedCertificateTrustManager("00")
            )
            val request = Request.Builder()
                .url("http://127.0.0.1:${origin.localPort}/api/v1/projects")
                .get()
                .build()
            val response = client.newCall(request).execute().use { it.code }

            assertEquals(302, response)
            assertTrue(originResponse.get(2, TimeUnit.SECONDS).contains("X-PaRuX-Device-Token: unit-test-device-token"))
            assertFalse("Redirect target received the device-token request", targetAccepted.get(2, TimeUnit.SECONDS))
        } finally {
            executor.shutdownNow()
            origin.close()
            redirectTarget.close()
        }
    }

    private fun readRequestHeaders(socket: java.net.Socket): String {
        val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.US_ASCII))
        val request = StringBuilder()
        while (true) {
            val line = reader.readLine() ?: break
            if (line.isEmpty()) break
            request.append(line).append('\n')
        }
        return request.toString()
    }
}
