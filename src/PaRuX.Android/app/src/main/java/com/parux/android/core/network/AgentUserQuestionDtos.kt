package com.parux.android.core.network

import kotlinx.serialization.Serializable

/**
 * Mobile-safe projection of a durable Core-owned human-in-the-loop question. Android may inspect
 * and answer an existing question but never creates one or selects an arbitrary task transition.
 */
@Serializable
data class AgentUserQuestionDto(
    val id: String = "",
    val question: String = "",
    val options: List<String> = emptyList(),
    val allowOther: Boolean = true,
    val askedUtc: String = ""
)

@Serializable
data class AgentUserQuestionExchangeDto(
    val questionId: String = "",
    val question: String = "",
    val options: List<String> = emptyList(),
    val allowOther: Boolean = true,
    val answer: String = "",
    val askedUtc: String = "",
    val answeredUtc: String = ""
)

@Serializable
data class AgentUserQuestionStateDto(
    val pendingQuestion: AgentUserQuestionDto? = null,
    val recentExchanges: List<AgentUserQuestionExchangeDto> = emptyList()
)

@Serializable
data class AnswerAgentUserQuestionRequest(
    val questionId: String,
    val answer: String
)
