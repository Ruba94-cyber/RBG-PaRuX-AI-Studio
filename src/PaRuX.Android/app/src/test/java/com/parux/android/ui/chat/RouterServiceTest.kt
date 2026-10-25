package com.parux.android.ui.chat

import org.junit.Assert.assertEquals
import org.junit.Test

/** Same canonical cases as tests/PaRuX.Server.Tests/RouterTests.cs, so the Kotlin port of
 * RouterService.cs is proven to classify identically to Windows, not just structurally similar. */
class RouterServiceTest {

    @Test
    fun `canonical intents match PaRuX Windows RouterTests_cs exactly`() {
        val cases = listOf(
            "4+4" to RouteKind.ChatFast,
            "Spiegami una VLAN" to RouteKind.ChatNormal,
            "Analizza in profondità questa architettura" to RouteKind.DeepReasoning,
            "Apri questa app e configurala" to RouteKind.ComputerUse,
            "Apri Blocco note" to RouteKind.ComputerUse,
            "Avvia la calcolatrice" to RouteKind.ComputerUse,
            "Open Notepad" to RouteKind.ComputerUse,
            "Genera un'immagine" to RouteKind.Image,
            "Genera un video di 30 secondi" to RouteKind.Video,
            "Creiamo una piccola app Windows" to RouteKind.CodeHandoff,
            "Crea una nuova applicazione" to RouteKind.CodeHandoff
        )
        cases.forEach { (input, expected) ->
            assertEquals("input: $input", expected, RouterService.classify(input))
        }
    }
}
