package com.parux.android.core.network

import kotlinx.serialization.Serializable

/**
 * Wire contracts used by Android's PC/Studio control surface. These intentionally mirror the
 * existing server-owned Project/Orchestration/Workflow/Observability contracts rather than
 * introducing a mobile-specific backend. That is what makes work started on Windows resumable
 * from Android and vice versa: both clients are reading and mutating the same durable Core state.
 */

@Serializable
data class CreateAgentTaskRequest(
    val goal: String,
    val parentId: String? = null,
    val role: String? = null,
    val dependsOnTaskIds: List<String>? = null,
    val maxRepairAttempts: Int = 0,
    val createdByAutomationId: String? = null
)

@Serializable
data class AgentTaskLogEntryDto(
    val id: String = "",
    val taskId: String = "",
    val timestampUtc: String = "",
    val message: String = ""
)

@Serializable
data class AgentTaskLogListResponse(val logs: List<AgentTaskLogEntryDto> = emptyList())

@Serializable
data class CreateProjectRequest(val goal: String)

@Serializable
data class ProjectDto(
    val id: String = "",
    val goal: String = "",
    val taskIds: List<String> = emptyList(),
    val createdUtc: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class ProjectListResponse(val projects: List<ProjectDto> = emptyList())

@Serializable
data class CreateContextEntityRequest(
    val entityType: String,
    val name: String,
    val metadataJson: String? = null,
    val source: String? = null
)

@Serializable
data class CreateContextRelationRequest(
    val fromEntityId: String,
    val relationType: String,
    val toEntityId: String,
    val metadataJson: String? = null,
    val source: String? = null
)

@Serializable
data class WorkflowStepTemplate(
    val name: String = "",
    val goal: String = "",
    val role: String? = null,
    val dependsOnStepIndexes: List<Int> = emptyList()
)

@Serializable
data class WorkflowDto(
    val id: String = "",
    val name: String = "",
    val description: String? = null,
    val steps: List<WorkflowStepTemplate> = emptyList(),
    val versionNumber: Int = 1,
    val previousVersionId: String? = null,
    val createdUtc: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class RunWorkflowRequest(val input: String? = null)

@Serializable
data class WorkflowRunResult(
    val workflowId: String = "",
    val createdTaskIds: List<String> = emptyList()
)

@Serializable
data class TimelineEntry(
    val category: String = "",
    val title: String = "",
    val detail: String? = null,
    val relatedId: String = "",
    val timestamp: String = ""
)

@Serializable
data class TimelineResponse(val entries: List<TimelineEntry> = emptyList())
