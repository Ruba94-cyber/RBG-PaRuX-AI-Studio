package com.parux.android.core.network

import okhttp3.ResponseBody
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Path
import retrofit2.http.Query
import retrofit2.http.Streaming

/**
 * Every non-streaming PaRuX Android call. This interface intentionally mirrors only the paired-
 * device control plane: desktop filesystem paths, runtime installers, manual benchmarks, generic
 * task-state mutation and privileged Context Graph/Visual Context authoring do not exist here.
 */
interface PaRuXApi {
    @GET("api/v1/health") suspend fun health(): ServerHealth
    @POST("api/v1/remote/pair/complete") suspend fun completePairing(@Body request: PairingCompleteRequest): PairingCompleteResponse

    @GET("api/v1/ai/profile") suspend fun getAiProfile(): AiProfileStatus
    @POST("api/v1/ai/profile") suspend fun setAiProfile(@Body request: AiProfileRequest): AiProfileStatus

    @GET("api/v1/conversations") suspend fun listConversations(): List<ConversationSummary>
    @GET("api/v1/conversations/{id}") suspend fun getConversation(@Path("id") id: String): ConversationDocument
    @PUT("api/v1/conversations/{id}") suspend fun saveConversation(@Path("id") id: String, @Body request: ConversationUpsertRequest): ConversationDocument
    @DELETE("api/v1/conversations/{id}") suspend fun deleteConversation(@Path("id") id: String)

    @POST("api/v1/jobs") suspend fun createJob(@Body request: MediaJobRequest): MediaJobStatus
    @GET("api/v1/jobs") suspend fun listJobs(): List<MediaJobStatus>
    @GET("api/v1/jobs/{id}") suspend fun getJob(@Path("id") id: String): MediaJobStatus
    @POST("api/v1/jobs/{id}/cancel") suspend fun cancelJob(@Path("id") id: String)

    @GET("api/v1/internet") suspend fun getInternet(): InternetStatus
    @POST("api/v1/internet") suspend fun setInternet(@Body request: InternetEnabledRequest): InternetStatus

    @GET("api/v1/memory") suspend fun listMemory(): List<MemoryEntryDto>
    @POST("api/v1/memory") suspend fun addMemory(@Body request: MemoryAddRequest): MemoryEntryDto
    @DELETE("api/v1/memory/{id}") suspend fun deleteMemory(@Path("id") id: String)

    @GET("api/v1/rag/sources") suspend fun listRagSources(): List<RagSourceDto>
    @POST("api/v1/rag/sources/{id}/reindex") suspend fun reindexRagSource(@Path("id") id: String): RagSourceDto
    @DELETE("api/v1/rag/sources/{id}") suspend fun removeRagSource(@Path("id") id: String)

    @POST("api/v1/research") suspend fun research(@Body request: ResearchRequest): ResearchResult
    @POST("api/v1/presentations") suspend fun generatePresentation(@Body request: PresentationRequest): PresentationResponse

    @GET("api/v1/speech/engine") suspend fun getSpeechEngineStatus(): SpeechEngineStatus
    @POST("api/v1/speech/synthesize") suspend fun synthesize(@Body request: SynthesizeRequest): SynthesizeResponse
    @POST("api/v1/speech/transcribe") suspend fun transcribe(@Body request: TranscribeRequest): TranscribeResponse

    @GET("api/v1/models/search") suspend fun searchModels(@Query("query") query: String, @Query("limit") limit: Int = 10): List<ModelSearchResult>
    @GET("api/v1/models/budget") suspend fun getModelBudget(): AiStorageBudgetStatus
    @GET("api/v1/models/downloads") suspend fun listModelDownloads(): List<ModelDownloadStatus>
    @POST("api/v1/models/downloads") suspend fun startModelDownload(@Body request: ModelDownloadRequest): ModelDownloadStatus
    @POST("api/v1/models/downloads/{id}/cancel") suspend fun cancelModelDownload(@Path("id") id: String)

    @GET("api/v1/avatar/status") suspend fun getAvatarStatus(): AvatarProviderStatus
    @POST("api/v1/avatar/generate") suspend fun generateAvatar(@Body request: AvatarGenerateRequest): AvatarGenerateResponse

    @GET("api/v1/skills") suspend fun listSkills(): List<SkillManifest>
    @POST("api/v1/mobile/skills") suspend fun createSkill(@Body request: SkillCreateRequest): SkillManifest
    @POST("api/v1/skills/{id}/enabled") suspend fun setSkillEnabled(@Path("id") id: String, @Body request: SkillEnabledRequest)
    @DELETE("api/v1/skills/{id}") suspend fun deleteSkill(@Path("id") id: String)

    @GET("api/v1/threed/engine") suspend fun getThreedEngineStatus(): ThreedEngineStatus
    @POST("api/v1/threed/generate") suspend fun generateThreed(@Body request: ThreedGenerateRequest): ThreedGenerateResponse

    @GET("api/v1/orchestrator/tasks") suspend fun listAgentTasks(): AgentTaskListResponse
    @GET("api/v1/orchestrator/tasks/{id}") suspend fun getAgentTask(@Path("id") id: String): AgentTaskDto
    @POST("api/v1/orchestrator/tasks") suspend fun createAgentTask(@Body request: CreateAgentTaskRequest): AgentTaskDto
    @GET("api/v1/orchestrator/tasks/{id}/logs") suspend fun getAgentTaskLogs(@Path("id") id: String): AgentTaskLogListResponse
    @POST("api/v1/orchestrator/tasks/{id}/cancel") suspend fun cancelAgentTask(@Path("id") id: String)
    @POST("api/v1/orchestrator/tasks/{id}/computer-use/approve-once") suspend fun approveComputerUseOnce(@Path("id") id: String)
    @GET("api/v1/orchestrator/tasks/{id}/user-question") suspend fun getAgentUserQuestion(@Path("id") id: String): AgentUserQuestionStateDto
    @POST("api/v1/orchestrator/tasks/{id}/user-question/answer") suspend fun answerAgentUserQuestion(
        @Path("id") id: String,
        @Body request: AnswerAgentUserQuestionRequest
    ): AgentTaskDto

    @POST("api/v1/autopilot/runs") suspend fun startAutopilotRun(@Body request: StartAutopilotRunRequest): AutopilotRunDto
    @GET("api/v1/autopilot/runs/{id}") suspend fun getAutopilotRun(@Path("id") id: String): AutopilotRunDto
    @POST("api/v1/autopilot/runs/{id}/approve") suspend fun approveAutopilotRun(@Path("id") id: String)

    @POST("api/v1/system/stop-all") suspend fun stopAllAgents(): StopAllResult
    @POST("api/v1/system/resume-automations") suspend fun resumeAutomations()
    @GET("api/v1/system/stop-all/status") suspend fun getStopAllStatus(): StopAllStatus

    @GET("api/v1/projects") suspend fun listProjects(): ProjectListResponse
    @POST("api/v1/projects") suspend fun createProject(@Body request: CreateProjectRequest): ProjectDto
    @POST("api/v1/projects/{id}/tasks") suspend fun attachProjectTask(@Path("id") id: String, @Body request: AttachProjectTaskRequest): ProjectDto
    @GET("api/v1/projects/{id}/knowledge") suspend fun getProjectKnowledge(@Path("id") id: String, @Query("maxCharacters") maxCharacters: Int = 12_000): ProjectKnowledgePackDto

    @GET("api/v1/mobile/artifacts") suspend fun listMobileArtifacts(@Query("projectId") projectId: String? = null): MobileArtifactListResponse
    @Streaming
    @GET("api/v1/mobile/artifacts/{id}/content") suspend fun downloadMobileArtifact(@Path("id") id: String, @Query("version") version: Int): ResponseBody

    @GET("api/v1/workflows") suspend fun listWorkflows(): List<WorkflowDto>
    @POST("api/v1/workflows/{id}/run") suspend fun runWorkflow(@Path("id") id: String, @Body request: RunWorkflowRequest): WorkflowRunResult

    @GET("api/v1/observability/timeline") suspend fun getObservabilityTimeline(@Query("limit") limit: Int = 100): TimelineResponse

    @GET("api/v1/automations") suspend fun listAutomations(): AutomationListResponse
    @POST("api/v1/automations/{id}/enabled") suspend fun setAutomationEnabled(@Path("id") id: String, @Body request: SetAutomationEnabledRequest): AutomationDto
    @DELETE("api/v1/automations/{id}") suspend fun deleteAutomation(@Path("id") id: String)

    @GET("api/v1/context/entities") suspend fun listContextEntities(@Query("type") type: String? = null): ContextEntityListResponse
    @GET("api/v1/context/entities/{id}/neighborhood") suspend fun getContextNeighborhood(@Path("id") id: String): ContextNeighborhoodResponse

    // Safe V2 projections. The server strips path-bearing/per-model/admin details before these
    // responses cross the paired-device boundary.
    @GET("api/v2/mobile/execution-profile") suspend fun getExecutionProfile(): MobileExecutionProfileDto
    @PUT("api/v2/mobile/execution-profile/quality") suspend fun setExecutionQuality(
        @Body request: SetExecutionQualityProfileRequestDto
    ): MobileExecutionProfileDto

    @GET("api/v2/mobile/observability/dashboard") suspend fun getObservabilityDashboard(
        @Query("recentLimit") recentLimit: Int = 30,
        @Query("benchmarkLimit") benchmarkLimit: Int = 12
    ): ObservabilityDashboardDto

    @POST("api/v2/mobile/goals") suspend fun createGoal(@Body request: CreateGoalSpecificationRequestDto): GoalSpecificationVersionDto
    @GET("api/v2/mobile/goals/{goalId}") suspend fun getGoal(
        @Path("goalId") goalId: String,
        @Query("version") version: Int = 0
    ): GoalSpecificationVersionDto
    @GET("api/v2/mobile/goals/{goalId}/versions") suspend fun listGoalVersions(@Path("goalId") goalId: String): List<GoalSpecificationVersionDto>
    @POST("api/v2/mobile/goals/{goalId}/versions") suspend fun createGoalVersion(
        @Path("goalId") goalId: String,
        @Body request: CreateGoalSpecificationVersionRequestDto
    ): GoalSpecificationVersionDto
    @POST("api/v2/mobile/goals/tasks") suspend fun createGoalTask(@Body request: CreateGoalModeTaskRequestDto): GoalModeTaskResponseDto
    @GET("api/v2/mobile/goals/tasks/{taskId}") suspend fun getGoalTaskDetail(@Path("taskId") taskId: String): GoalTaskDetailDto
    @GET("api/v2/mobile/goals/tasks/{taskId}/verification-records") suspend fun getGoalVerificationRecords(
        @Path("taskId") taskId: String
    ): List<GoalVerificationRecordDto>
}