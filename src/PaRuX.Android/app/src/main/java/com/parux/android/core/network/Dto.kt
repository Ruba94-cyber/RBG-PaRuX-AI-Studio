package com.parux.android.core.network

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Wire types for PaRuX.Server's /api/v1 surface. Field names mirror
 * src/PaRuX.Contracts/ApiContracts.cs exactly; the server serializes with
 * System.Text.Json's camelCase policy, so plain Kotlin property names line up
 * without needing @SerialName here.
 */

@Serializable
data class ChatCompletionRequest(
    val model: String = "local-default",
    val messages: List<ChatCompletionMessage>,
    val stream: Boolean = true,
    val skillId: String? = null
)

@Serializable
data class ChatCompletionMessage(
    val role: String,
    val content: String,
    val images: List<ChatImageContent> = emptyList()
)

@Serializable
data class ChatImageContent(
    val fileName: String,
    val mediaType: String,
    val base64Data: String
)

@Serializable
data class ServerHealth(
    val status: String = "ok",
    val version: String = "1.0.0",
    val providerReady: Boolean = false,
    val gamingMode: Boolean = false
)

@Serializable
data class PairingCompleteRequest(
    val code: String,
    val deviceName: String
)

@Serializable
data class PairingCompleteResponse(
    val deviceId: String,
    val deviceToken: String,
    val endpoint: String,
    val certificateFingerprint: String
)

@Serializable
data class AiProfileStatus(
    val mode: String = "AUTO",
    val textProfile: String = "STANDARD",
    val activeProfile: String = "",
    val gamingMode: Boolean = false
)

@Serializable
data class AiProfileRequest(
    val textProfile: String
)

@Serializable
data class ConversationSummary(
    val id: String,
    val title: String,
    val createdUtc: String,
    val updatedUtc: String
)

@Serializable
data class ConversationDocument(
    val id: String,
    val title: String,
    val createdUtc: String,
    val updatedUtc: String,
    val documentJson: String
)

@Serializable
data class ConversationUpsertRequest(
    val title: String,
    val createdUtc: String,
    val updatedUtc: String,
    val documentJson: String
)

@Serializable
data class MediaJobRequest(
    val type: String,
    val prompt: String,
    val width: Int = 1024,
    val height: Int = 1024,
    val durationSeconds: Double = 5.0,
    val fps: Int = 24,
    val steps: Int = 30,
    val seed: Long = -1,
    /** "edit" only: base64-encoded source image to edit. */
    val sourceImageBase64: String = ""
)

@Serializable
data class MediaAsset(
    val fileName: String,
    val mediaType: String,
    val base64Data: String = "",
    val seed: Long = 0,
    val width: Int = 0,
    val height: Int = 0,
    val durationSeconds: Double = 0.0,
    val fps: Int = 0,
    val steps: Int = 0
)

@Serializable
data class MediaJobStatus(
    val id: String,
    val type: String,
    val status: String,
    val stage: String,
    val progressPercent: Int,
    val etaSeconds: Int? = null,
    val error: String? = null,
    val createdUtc: String,
    val updatedUtc: String,
    val asset: MediaAsset? = null
)

@Serializable
data class ApiError(val error: String)

/**
 * The conversation *document* stored opaquely by the server (ConversationUpsertRequest.documentJson)
 * is not one of the camelCase API DTOs above - it is the exact JSON shape
 * src/PaRuX.App/Services/HistoryStore.cs already writes to its local SQLite blob, which uses
 * System.Text.Json's default (PascalCase, matching the C# property names) rather than the
 * API's camelCase policy. Android has to write and read this exact shape for a conversation to
 * open correctly on Windows and vice versa - @SerialName maps idiomatic Kotlin names onto it.
 *
 * Known 1.0 gap, documented rather than silently glossed over: ChatAttachmentDoc.localPath is a
 * Windows filesystem path with no meaning on Android and vice versa, so an attachment synced from
 * one platform will not resolve to a real file on the other. Android intentionally never writes a
 * populated localPath; live image/video understanding still works because /api/v1/chat/completions
 * takes inline base64 image data directly, independent of this history blob.
 */
@Serializable
data class ConversationDoc(
    @SerialName("Id") val id: String,
    @SerialName("Title") val title: String = "New chat",
    @SerialName("CreatedUtc") val createdUtc: String,
    @SerialName("UpdatedUtc") val updatedUtc: String,
    @SerialName("IsFavorite") val isFavorite: Boolean = false,
    @SerialName("Messages") val messages: List<ChatMessageDoc> = emptyList()
)

@Serializable
data class ChatMessageDoc(
    @SerialName("Role") val role: String,
    @SerialName("Content") val content: String = "",
    @SerialName("Attachments") val attachments: List<ChatAttachmentDoc> = emptyList(),
    @SerialName("Route") val route: String? = null,
    @SerialName("TimestampUtc") val timestampUtc: String,
    @SerialName("IsStreaming") val isStreaming: Boolean = false,
    @SerialName("IsError") val isError: Boolean = false,
    @SerialName("ActionKind") val actionKind: String? = null,
    @SerialName("ActionLabel") val actionLabel: String? = null,
    @SerialName("ActionPayload") val actionPayload: String? = null
)

@Serializable
data class ChatAttachmentDoc(
    @SerialName("Id") val id: String,
    @SerialName("FileName") val fileName: String = "",
    @SerialName("MediaType") val mediaType: String = "image/png",
    @SerialName("LocalPath") val localPath: String = "",
    @SerialName("SizeBytes") val sizeBytes: Long = 0
)

// ---- Internet toggle (needed for Research below) ----

@Serializable
data class InternetStatus(val enabled: Boolean = false)

@Serializable
data class InternetEnabledRequest(val enabled: Boolean)

// ---- Memory ----

@Serializable
data class MemoryEntryDto(
    val id: String,
    val scope: String = "User",
    val scopeKey: String? = null,
    val content: String,
    val createdUtc: String = "",
    val updatedUtc: String = "",
    val enabled: Boolean = true
)

@Serializable
data class MemoryAddRequest(val scope: String = "User", val scopeKey: String? = null, val content: String)

// ---- RAG ----

@Serializable
data class RagSourceDto(
    val id: String,
    val folderPath: String,
    val addedUtc: String = "",
    val enabled: Boolean = true,
    val documentCount: Int = 0,
    val chunkCount: Int = 0
)

// ---- Research ----

@Serializable
data class ResearchRequest(val question: String, val mode: String = "balanced")

@Serializable
data class ResearchSource(
    val title: String = "",
    val url: String = "",
    val domain: String = "",
    val snippet: String = "",
    val retrievedUtc: String = ""
)

@Serializable
data class ResearchResult(
    val answer: String = "",
    val sources: List<ResearchSource> = emptyList(),
    val queriesUsed: List<String> = emptyList()
)

// ---- Presentations ----

@Serializable
data class PresentationRequest(val topic: String, val slideCount: Int = 6)

@Serializable
data class PresentationSlideDto(val title: String = "", val bullets: List<String> = emptyList(), val speakerNotes: String = "")

@Serializable
data class PresentationResponse(
    val fileName: String = "",
    val slides: List<PresentationSlideDto> = emptyList(),
    val base64Data: String = ""
)

// ---- Speech ----

@Serializable
data class SpeechEngineStatus(val sttInstalled: Boolean = false, val ttsInstalled: Boolean = false)

@Serializable
data class SynthesizeRequest(val text: String, val voice: String = "")

@Serializable
data class SynthesizeResponse(val fileName: String = "", val mediaType: String = "audio/wav", val base64Data: String = "", val durationSeconds: Double = 0.0)

@Serializable
data class TranscribeRequest(val audioBase64: String, val fileName: String = "audio.wav", val language: String = "")

@Serializable
data class TranscribeResponse(val text: String = "", val languageDetected: String = "", val durationSeconds: Double = 0.0)

// ---- Model Manager (search/download) ----

@Serializable
data class ModelFileOption(val fileName: String = "", val sizeBytes: Long = 0, val downloadUrl: String = "")

@Serializable
data class ModelSearchResult(val repoId: String = "", val author: String = "", val downloads: Int = 0, val likes: Int = 0, val ggufFiles: List<ModelFileOption> = emptyList())

@Serializable
data class ModelDownloadRequest(val repoId: String, val fileName: String, val downloadUrl: String, val sizeBytes: Long)

@Serializable
data class ModelDownloadStatus(
    val id: String,
    val status: String = "queued",
    val fileName: String = "",
    val bytesDownloaded: Long = 0,
    val totalBytes: Long = 0,
    val error: String? = null
)

@Serializable
data class AiStorageBudgetStatus(val usedBytes: Long = 0, val limitBytes: Long = 0, val withinBudget: Boolean = true)

@Serializable
data class ModelBenchmarkRequest(val modelPath: String, val label: String = "")

@Serializable
data class ModelBenchmarkRun(
    val id: String = "", val status: String = "queued", val stage: String = "Queued",
    val modelPath: String = "", val label: String = "", val automatic: Boolean = false,
    val tokensPerSecond: Double? = null, val generatedTokens: Int = 0, val repetitions: Int = 0,
    val backend: String? = null, val hardwareKey: String? = null, val error: String? = null
)

@Serializable
data class ModelBenchmarkSummary(
    val modelPath: String = "", val label: String = "", val usage: String = "installed", val sizeBytes: Long = 0,
    val completedRunCount: Int = 0, val averageTokensPerSecond: Double? = null,
    val backend: String? = null, val hardwareKey: String? = null,
    val lastRun: ModelBenchmarkRun? = null
)

// ---- Training Lab ----

@Serializable
data class TrainingEngineStatus(val installed: Boolean = false, val torchVersion: String? = null, val cudaVerified: Boolean = false)

@Serializable
data class TrainingJobRequest(
    val trainJsonlPath: String,
    val baseModel: String = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
    val loraRank: Int = 16,
    val maxSteps: Int = 60,
    val learningRate: Double = 0.0002
)

@Serializable
data class TrainingJobStatus(
    val id: String,
    val status: String = "queued",
    val stage: String = "Queued",
    val currentStep: Int = 0,
    val totalSteps: Int = 0,
    val lastLoss: Double? = null,
    val error: String? = null,
    val adapterPath: String? = null
)

// ---- Avatar Studio ----

@Serializable
data class AvatarProviderStatus(val configured: Boolean = false, val providerName: String? = null, val reachable: Boolean = false)

@Serializable
data class AvatarGenerateRequest(val text: String, val avatarId: String = "")

@Serializable
data class AvatarGenerateResponse(val fileName: String = "", val mediaType: String = "video/mp4", val base64Data: String = "")

// ---- Skills ----

@Serializable
data class SkillPermissionsDto(val readFiles: Boolean = false, val writeFiles: Boolean = false, val shell: Boolean = false, val computerUse: Boolean = false)

@Serializable
data class SkillManifest(
    val id: String = "",
    val name: String = "",
    val displayName: String = "",
    val description: String = "",
    val category: String = "General",
    val version: String = "1.0.0",
    val source: String = "builtin",
    val license: String = "",
    val enabled: Boolean = true,
    val platforms: List<String> = listOf("Windows", "Android"),
    val preferredModelRole: String = "MAIN_LLM",
    val allowedModelRoles: List<String> = emptyList(),
    val allowedTools: List<String> = emptyList(),
    val internetPolicy: String = "off",
    val memoryPolicy: String = "optional",
    val ragPolicy: String = "optional",
    val slashCommand: String = "",
    val workflow: List<String> = emptyList(),
    val dependencies: List<String> = emptyList(),
    val permissions: SkillPermissionsDto = SkillPermissionsDto(),
    val timeoutSeconds: Int = 120,
    val confirmationPolicy: String = "never"
)

@Serializable
data class SkillCreateRequest(val manifest: SkillManifest)

@Serializable
data class SkillEnabledRequest(val enabled: Boolean)

// ---- 3D Studio (TRELLIS image-to-3D) ----

@Serializable
data class ThreedEngineStatus(val installed: Boolean = false, val torchVersion: String? = null, val cudaVerified: Boolean = false, val lastError: String? = null)

@Serializable
data class ThreedGenerateRequest(val sourceImageBase64: String, val fileName: String = "source.png", val seed: Int = -1)

@Serializable
data class ThreedGenerateResponse(val fileName: String = "", val mediaType: String = "model/gltf-binary", val base64Data: String = "", val durationSeconds: Double = 0.0)

@Serializable
data class ThreedInstallResponse(val installed: Boolean = false, val log: String = "")

// ---- Multi-Agent Orchestrator + Autopilot + Universal STOP (vNext Phase 9/49, mirrors
// PaRuX.Contracts.OrchestrationContracts/AutopilotContracts/StopAllContracts) ----

@Serializable
data class AgentTaskDto(
    val id: String = "",
    val parentId: String? = null,
    val goal: String = "",
    val role: String? = null,
    val status: String = "",
    val stage: String? = null,
    val progressPercent: Int = 0,
    val worktreePath: String? = null,
    val resultJson: String? = null,
    val error: String? = null,
    val verificationStatus: String? = null,
    val createdUtc: String = "",
    val updatedUtc: String = "",
    val dependsOnTaskIds: List<String> = emptyList()
)

@Serializable
data class AgentTaskListResponse(val tasks: List<AgentTaskDto> = emptyList())

@Serializable
data class AutopilotRunDto(
    val id: String = "",
    val goal: String = "",
    val permissionMode: String = "",
    val status: String = "",
    val projectId: String = "",
    val taskIds: List<String> = emptyList(),
    val finalReportJson: String? = null,
    val createdUtc: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class StartAutopilotRunRequest(val goal: String, val permissionMode: String = "balanced")

@Serializable
data class StopAllResult(
    val tasksCancelled: Int = 0,
    val autopilotRunsCancelled: Int = 0,
    val automationsPaused: Boolean = false,
    val timestampUtc: String = ""
)

@Serializable
data class StopAllStatus(val automationsPaused: Boolean = false)

// ---- Automation Engine (vNext Phase 16, mirrors PaRuX.Contracts.AutomationContracts) ----

@Serializable
data class AutomationDto(
    val id: String = "",
    val name: String = "",
    val enabled: Boolean = true,
    val triggerType: String = "",
    val triggerConfigJson: String = "",
    val actionType: String = "",
    val actionConfigJson: String = "",
    val createdUtc: String = "",
    val updatedUtc: String = "",
    val lastTriggeredUtc: String? = null,
    val lastResult: String? = null,
    val lastCreatedTaskId: String? = null
)

@Serializable
data class AutomationListResponse(val automations: List<AutomationDto> = emptyList())

@Serializable
data class SetAutomationEnabledRequest(val enabled: Boolean)

// ---- Local Context Graph (vNext Phase 23, mirrors PaRuX.Contracts.ContextGraphContracts) ----

@Serializable
data class ContextEntityDto(
    val id: String = "",
    val entityType: String = "",
    val name: String = "",
    val metadataJson: String? = null,
    val source: String? = null,
    val createdUtc: String = "",
    val updatedUtc: String = ""
)

@Serializable
data class ContextRelationDto(
    val id: String = "",
    val fromEntityId: String = "",
    val relationType: String = "",
    val toEntityId: String = "",
    val metadataJson: String? = null,
    val source: String? = null,
    val createdUtc: String = ""
)

@Serializable
data class ContextEntityListResponse(val entities: List<ContextEntityDto> = emptyList())

@Serializable
data class ContextNeighborhoodResponse(
    val center: ContextEntityDto = ContextEntityDto(),
    val outgoingRelations: List<ContextRelationDto> = emptyList(),
    val incomingRelations: List<ContextRelationDto> = emptyList(),
    val neighborEntities: List<ContextEntityDto> = emptyList()
)
