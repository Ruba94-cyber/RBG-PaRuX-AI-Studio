package com.parux.android.core.network

import kotlinx.serialization.Serializable

/**
 * Mobile-safe projections of PaRuX V2 state. These types intentionally contain no workstation
 * model/worktree paths, plugin/runtime installer fields or caller-controlled capability grants.
 */

@Serializable
data class ExecutionResourceBudgetDto(
    val maxWallClockMinutes: Int = 0,
    val maxToolSteps: Int = 0,
    val maxDirectSubagents: Int = 0,
    val maxDelegationDepth: Int = 0,
    val contextFraction: Double = 0.0,
    val verificationPasses: Int = 0,
    val maxParallelTasks: Int = 0,
    val maxGpuTemperatureC: Int = 0,
    val maxSystemRamPercent: Int = 0,
    val maxVramPercent: Int = 0
)

@Serializable
data class MobileExecutionProfileDto(
    val activeQualityProfile: String = "balanced",
    val preferredContextTokens: Int = 0,
    val reasoningEffort: Int = 2,
    val structuredOutputStrategy: String = "schema-first",
    val toolStrategy: String = "adaptive",
    val evidenceSource: String = "defaults",
    val updatedUtc: String = "",
    val budget: ExecutionResourceBudgetDto = ExecutionResourceBudgetDto()
)

@Serializable
data class SetExecutionQualityProfileRequestDto(val qualityProfile: String)

@Serializable
data class ObservabilityMetricDto(
    val id: String = "",
    val label: String = "",
    val value: String = "",
    val detail: String? = null
)

@Serializable
data class ObservabilityStatusCountDto(
    val name: String = "",
    val count: Int = 0
)

@Serializable
data class EngineRaceSummaryDto(
    val total: Int = 0,
    val completed: Int = 0,
    val media: Int = 0,
    val runtime: Int = 0
)

@Serializable
data class ObservabilityDashboardDto(
    val capturedUtc: String = "",
    val executionProfile: MobileExecutionProfileDto = MobileExecutionProfileDto(),
    val metrics: List<ObservabilityMetricDto> = emptyList(),
    val taskStatuses: List<ObservabilityStatusCountDto> = emptyList(),
    val trainingStatuses: List<ObservabilityStatusCountDto> = emptyList(),
    val recentActivity: List<TimelineEntry> = emptyList(),
    val engineRaceSummary: EngineRaceSummaryDto = EngineRaceSummaryDto()
)

@Serializable
data class GoalAcceptanceCriterionDto(
    val id: String = "",
    val text: String = "",
    val required: Boolean = true,
    val order: Int = 0
)

@Serializable
data class GoalSpecificationVersionDto(
    val goalId: String = "",
    val version: Int = 0,
    val goal: String = "",
    val acceptanceCriteria: List<GoalAcceptanceCriterionDto> = emptyList(),
    val contentSha256: String = "",
    val previousContentSha256: String? = null,
    val changeReason: String = "",
    val createdUtc: String = ""
)

@Serializable
data class CreateGoalSpecificationRequestDto(
    val goal: String,
    val acceptanceCriteria: List<String>,
    val changeReason: String = "initial"
)

@Serializable
data class CreateGoalSpecificationVersionRequestDto(
    val expectedPreviousVersion: Int,
    val goal: String,
    val acceptanceCriteria: List<String>,
    val changeReason: String
)

@Serializable
data class GoalTaskBindingDto(
    val taskId: String = "",
    val goalId: String = "",
    val goalVersion: Int = 0,
    val contentSha256: String = "",
    val boundUtc: String = ""
)

@Serializable
data class CreateGoalModeTaskRequestDto(
    val goalId: String,
    val goalVersion: Int = 0,
    val role: String? = null,
    val maxRepairAttempts: Int = 2
)

@Serializable
data class MobileGoalTaskDto(
    val id: String = "",
    val parentId: String? = null,
    val goal: String = "",
    val role: String? = null,
    val status: String = "",
    val stage: String? = null,
    val progressPercent: Int = 0,
    val error: String? = null,
    val verificationStatus: String? = null,
    val createdUtc: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class GoalModeTaskResponseDto(
    val specification: GoalSpecificationVersionDto = GoalSpecificationVersionDto(),
    val binding: GoalTaskBindingDto = GoalTaskBindingDto(),
    val task: MobileGoalTaskDto = MobileGoalTaskDto()
)

@Serializable
data class GoalTaskDetailDto(
    val binding: GoalTaskBindingDto = GoalTaskBindingDto(),
    val specification: GoalSpecificationVersionDto = GoalSpecificationVersionDto()
)

@Serializable
data class GoalAcceptanceCriterionVerificationDto(
    val criterionId: String = "",
    val satisfied: Boolean = false,
    val evidence: String = ""
)

@Serializable
data class GoalVerificationRecordDto(
    val id: String = "",
    val taskId: String = "",
    val goalId: String = "",
    val goalVersion: Int = 0,
    val contentSha256: String = "",
    val verificationPass: Int = 0,
    val allRequiredSatisfied: Boolean = false,
    val criteria: List<GoalAcceptanceCriterionVerificationDto> = emptyList(),
    val createdUtc: String = ""
)
