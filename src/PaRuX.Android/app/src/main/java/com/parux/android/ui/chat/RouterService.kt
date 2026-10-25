package com.parux.android.ui.chat

/**
 * Faithful Kotlin port of src/PaRuX.App/Services/RouterService.cs. The route decides which
 * system prompt goes out with the request (so DEEP_REASONING genuinely gets the same "think
 * carefully" instruction Windows sends - not just a cosmetic label) and drives the CODE_HANDOFF
 * "Prepare for PC" affordance. It does not decide STANDARD/UNCENSORED/AUTO or trigger image/video
 * generation on its own - those stay explicit user actions against /api/v1/ai/profile and
 * /api/v1/jobs respectively, same as on Windows.
 */
enum class RouteKind { ChatFast, ChatNormal, DeepReasoning, Vision, ComputerUse, Image, Video, CodeHandoff }

object RouterService {
    private val opts = setOf(RegexOption.IGNORE_CASE)

    private val imageRx = Regex(
        "\\b(genera(mi)?\\s+(un[a']?\\s*)?(immagine|foto|illustrazione)|crea(mi)?\\s+(un[a']?\\s*)?(immagine|foto)" +
            "|disegna(mi)?|immagine\\s+di|generate\\s+(an?\\s+)?image|create\\s+(an?\\s+)?(image|picture)" +
            "|make\\s+(me\\s+)?(an?\\s+)?(image|picture)|draw\\s+(me\\s+)?|text[\\s-]?to[\\s-]?image)\\b", opts
    )
    private val videoRx = Regex(
        "\\b(genera(mi)?\\s+(un\\s+)?video|crea(mi)?\\s+(un\\s+)?video|fai\\s+(un\\s+)?video" +
            "|generate\\s+(a\\s+)?video|create\\s+(a\\s+)?video|make\\s+(me\\s+)?(a\\s+)?video" +
            "|text[\\s-]?to[\\s-]?video|anima(mi)?\\s+)\\b", opts
    )
    private val codeHandoffRx = Regex(
        "\\b(creiamo\\s+(?:un[a']?\\s+)?(?:(?:piccol[oa]|nuov[oa]|semplice)\\s+)?(?:app|applicazione|progetto)" +
            "|facciamo\\s+(?:un[a']?\\s+)?(?:(?:piccol[oa]|nuov[oa]|semplice)\\s+)?(?:app|applicazione|progetto)" +
            "|crea(mi)?\\s+(?:un[a']?\\s+)?(?:(?:piccol[oa]|nuov[oa])\\s+)?(?:app|applicazione|progetto)|nuovo\\s+progetto" +
            "|let[\\s']?s\\s+build\\s+an?\\s+app|build\\s+an?\\s+app|new\\s+project|scaffold\\s+" +
            "|debug\\s+(this\\s+)?(repo|repository|project)|refactor\\s+(this\\s+)?(repo|repository|codebase))\\b", opts
    )
    private val computerUseRx = Regex(
        "(^|\\b)(apri|avvia|lancia|esegui|open|launch|start|run)\\s+\\S+" +
            "|\\b(configura(?:mi|la|lo)?\\b|clicca\\b" +
            "|fai\\s+uno\\s+screenshot|screenshot\\b|premi\\s+il\\s+pulsante" +
            "|click\\s+(on\\s+)?the\\b" +
            "|take\\s+a\\s+screenshot|control\\s+my\\s+(pc|computer))\\b", opts
    )
    private val deepRx = Regex(
        "(analizza\\s+in\\s+profondit|analisi\\s+approfondita|approfondisci|in\\s+profondit" +
            "|ragiona\\s+(bene|attentamente)|passo\\s+per\\s+passo|valuta\\s+i\\s+pro\\s+e\\s+i\\s+contro" +
            "|analy[sz]e\\s+in\\s+depth|deep\\s+dive|in[\\s-]depth|think\\s+(carefully|step\\s+by\\s+step)" +
            "|step[\\s-]by[\\s-]step|pros\\s+and\\s+cons|trade[\\s-]?offs?\\b|compare\\s+and\\s+contrast)", opts
    )
    private val explainRx = Regex(
        "\\b(spiega(mi)?|come\\s+funziona|perch[eé]\\b|cosa\\s+significa|dimmi\\s+come" +
            "|explain|how\\s+does|how\\s+do\\s+i|what\\s+is|why\\s+(is|does|do)|walk\\s+me\\s+through)\\b", opts
    )
    private val trivialRx = Regex("^\\s*[-+]?[\\d\\s.,()+\\-*/x×÷^%]+\\s*=?\\s*\\??\\s*$", opts)
    private val quickLookupRx = Regex(
        "^\\s*(traduci|translate|come\\s+si\\s+dice|how\\s+do\\s+you\\s+say|che\\s+ore\\s+sono" +
            "|what\\s+time|meteo|weather|definisci|define)\\b", opts
    )

    fun classify(input: String): RouteKind {
        if (input.isBlank()) return RouteKind.ChatNormal
        val text = input.trim()

        if (imageRx.containsMatchIn(text)) return RouteKind.Image
        if (videoRx.containsMatchIn(text)) return RouteKind.Video
        if (codeHandoffRx.containsMatchIn(text)) return RouteKind.CodeHandoff
        if (computerUseRx.containsMatchIn(text)) return RouteKind.ComputerUse

        if (deepRx.containsMatchIn(text)) return RouteKind.DeepReasoning

        val words = text.split(Regex("[ \\t\\r\\n]+")).count { it.isNotEmpty() }
        if (words > 60) return RouteKind.DeepReasoning
        if (words > 25 && text.count { it == '?' } >= 2) return RouteKind.DeepReasoning

        if (explainRx.containsMatchIn(text)) return RouteKind.ChatNormal

        if (trivialRx.matches(text)) return RouteKind.ChatFast
        if (quickLookupRx.containsMatchIn(text) && words < 14) return RouteKind.ChatFast

        if (words < 8) return RouteKind.ChatFast
        return RouteKind.ChatNormal
    }

    fun label(route: RouteKind): String = when (route) {
        RouteKind.ChatFast -> "Fast"
        RouteKind.ChatNormal -> "Normal"
        RouteKind.DeepReasoning -> "Deep reasoning"
        RouteKind.Vision -> "Vision"
        RouteKind.ComputerUse -> "Computer use"
        RouteKind.Image -> "Image"
        RouteKind.Video -> "Video"
        RouteKind.CodeHandoff -> "Code"
    }

    /** Same system-prompt selection as Windows, so DEEP_REASONING (etc.) is not silently
     * lower-quality just because the request came from the phone. */
    fun systemPrompt(route: RouteKind): String? = when (route) {
        RouteKind.ChatFast -> null
        RouteKind.DeepReasoning -> "Think carefully and thoroughly before answering. " +
            "Work through the problem step by step, weigh the alternatives, " +
            "then give a clear, well-structured answer."
        RouteKind.Vision -> "Analyze the attached image or images carefully. Describe only what is visible, " +
            "state uncertainty explicitly, and answer the user's question directly."
        else -> "You are PaRuX, a helpful local assistant running on the user's computer. " +
            "Answer clearly and concisely, and use Markdown when it aids readability."
    }
}
