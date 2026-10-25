package com.parux.android.ui.chat

/**
 * User-facing text for chat failures. Transport exceptions carry messages such as
 * "Failed to connect to /192.168.1.20:51124" that tell a phone user nothing about what to do;
 * the pairing screen already speaks in the user's terms, and chat now does the same.
 */
internal object ChatErrors {
    fun describe(e: Exception): String = when (e) {
        is retrofit2.HttpException -> "PaRuX PC returned an error (HTTP ${e.code()})."
        is javax.net.ssl.SSLException ->
            "Couldn't verify PaRuX PC's certificate. Pair again from PaRuX Windows → Settings → Local Network."
        is java.net.SocketTimeoutException ->
            "PaRuX PC took too long to answer. It may be loading a model; try again in a moment."
        is java.net.ConnectException, is java.net.NoRouteToHostException, is java.net.UnknownHostException ->
            "Can't reach your PaRuX PC. Make sure PaRuX is running on it and both devices are on the same network."
        is java.io.IOException -> e.message ?: "PaRuX PC is not reachable right now."
        else -> e.message ?: "Something went wrong."
    }
}
