package com.parux.android.core.network

import kotlinx.serialization.Serializable

/**
 * Narrow write contracts used by Android PC Studio for the shared AgentTask state machine.
 * They mirror PaRuX.Contracts.UpdateAgentTaskStatusRequest / AppendAgentTaskLogRequest and are
 * intentionally task-scoped: Android never receives a raw Windows mouse/keyboard/shell endpoint.
 */
@Serializable
data class UpdateAgentTaskStatusRequest(
    val status: String,
    val stage: String? = null,
    val progressPercent: Int? = null,
    val resultJson: String? = null,
    val error: String? = null,
    val verificationStatus: String? = null
)

@Serializable
data class AppendAgentTaskLogRequest(val message: String)

@Serializable
data class AttachProjectTaskRequest(val taskId: String)

/** Sanitized mobile Artifact projection: intentionally contains no Windows ContentRef/path. */
@Serializable
data class MobileArtifactDto(
    val id: String = "",
    val artifactType: String = "",
    val title: String = "",
    val status: String = "",
    val versionNumber: Int = 0,
    val fileName: String? = null,
    val mediaType: String? = null,
    val sizeBytes: Long? = null,
    val downloadable: Boolean = false,
    val updatedUtc: String = ""
)

@Serializable
data class MobileArtifactListResponse(val artifacts: List<MobileArtifactDto> = emptyList())

/**
 * Bounded Project context produced by Core from authoritative TaskStore/ArtifactStore identities.
 * Identity is opaque and Content is already safe/bounded; no Windows path is present.
 */
@Serializable
data class ProjectKnowledgeItemDto(
    val kind: String = "",
    val identity: String = "",
    val title: String = "",
    val status: String? = null,
    val content: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class ProjectKnowledgePackDto(
    val projectId: String = "",
    val goal: String = "",
    val updatedUtc: String = "",
    val items: List<ProjectKnowledgeItemDto> = emptyList(),
    val promptText: String = "",
    val usedCharacters: Int = 0,
    val maxCharacters: Int = 0,
    val truncated: Boolean = false
)

/** Shared wire values also used by ComputerUseWorkerService on Windows. */
object PcComputerUseApproval {
    const val RequiredStage = "Computer Use approval required"
    const val GrantedStage = "Computer Use approval granted remotely"

    fun isPending(task: AgentTaskDto): Boolean =
        task.role == "computer-use" && task.status == "waiting" && task.stage == RequiredStage
}
