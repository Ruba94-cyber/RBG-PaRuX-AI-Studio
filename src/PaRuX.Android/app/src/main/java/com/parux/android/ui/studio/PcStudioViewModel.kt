package com.parux.android.ui.studio

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.core.AppContainer
import com.parux.android.core.network.AgentTaskDto
import com.parux.android.core.network.AgentTaskLogEntryDto
import com.parux.android.core.network.AgentUserQuestionDto
import com.parux.android.core.network.AnswerAgentUserQuestionRequest
import com.parux.android.core.network.AttachProjectTaskRequest
import com.parux.android.core.network.CreateAgentTaskRequest
import com.parux.android.core.network.CreateProjectRequest
import com.parux.android.core.network.MobileArtifactDto
import com.parux.android.core.network.PcComputerUseApproval
import com.parux.android.core.network.ProjectDto
import com.parux.android.core.network.ProjectKnowledgePackDto
import com.parux.android.core.network.RunWorkflowRequest
import com.parux.android.core.network.TimelineEntry
import com.parux.android.core.network.WorkflowDto
import com.parux.android.core.supervision.PcTaskSupervisor
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull

/** A human-readable projection of an AgentTask's resultJson for mobile review. */
data class PcTaskResultView(
    val taskId: String,
    val role: String,
    val goal: String,
    val status: String,
    val stage: String?,
    val verificationStatus: String?,
    val summary: String?,
    val branch: String?,
    val worktree: String?,
    val diff: String?,
    val stepsTaken: Int?,
    val rawFallback: String?
)

data class PcStudioUiState(
    val projects: List<ProjectDto> = emptyList(),
    val selectedProjectId: String? = null,
    val projectSelectionInitialized: Boolean = false,
    val tasks: List<AgentTaskDto> = emptyList(),
    val pendingApprovals: List<AgentTaskDto> = emptyList(),
    val approvalTask: AgentTaskDto? = null,
    val userQuestionTask: AgentTaskDto? = null,
    val userQuestion: AgentUserQuestionDto? = null,
    val workflows: List<WorkflowDto> = emptyList(),
    val artifacts: List<MobileArtifactDto> = emptyList(),
    val knowledge: ProjectKnowledgePackDto? = null,
    val knowledgeLoading: Boolean = false,
    val timeline: List<TimelineEntry> = emptyList(),
    val selectedTaskId: String? = null,
    val selectedTaskLogs: List<AgentTaskLogEntryDto> = emptyList(),
    val showTaskLogs: Boolean = false,
    val taskResult: PcTaskResultView? = null,
    val continuationParent: AgentTaskDto? = null,
    val downloadingArtifactId: String? = null,
    val loading: Boolean = false,
    val actionInFlight: Boolean = false,
    val message: String? = null,
    val error: String? = null
)

class PcStudioViewModel(private val container: AppContainer) : ViewModel() {
    companion object {
        const val UserInputWaitingStage = "Waiting for user input"
        fun isUserInputPending(task: AgentTaskDto): Boolean =
            task.status == "waiting" && task.stage == UserInputWaitingStage
    }

    private val _state = MutableStateFlow(PcStudioUiState())
    val state: StateFlow<PcStudioUiState> = _state.asStateFlow()

    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }
    private var pollingJob: Job? = null

    fun startPolling() {
        if (pollingJob?.isActive == true) return
        pollingJob = viewModelScope.launch {
            while (isActive) {
                refresh(silent = _state.value.tasks.isNotEmpty() || _state.value.projects.isNotEmpty())
                delay(3_000)
            }
        }
    }

    fun refresh(silent: Boolean = false) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            if (!silent) _state.value = _state.value.copy(loading = true, error = null)
            try {
                val api = client.api
                val projects = api.listProjects().projects.sortedByDescending { it.updatedUtc }
                val previous = _state.value
                val active = container.activeProjectStore.selection.value
                val selectedProject = when {
                    active.initialized && active.projectId == null -> null
                    active.initialized && projects.any { it.id == active.projectId } -> active.projectId
                    active.initialized -> null
                    !previous.projectSelectionInitialized -> projects.firstOrNull()?.id
                    previous.selectedProjectId == null -> null
                    projects.any { it.id == previous.selectedProjectId } -> previous.selectedProjectId
                    else -> projects.firstOrNull()?.id
                }
                if (!active.initialized && selectedProject != null) {
                    container.activeProjectStore.select(selectedProject)
                }

                val tasks = api.listAgentTasks().tasks.sortedByDescending { it.updatedUtc }
                tasks.asSequence()
                    .filter { it.status !in setOf("completed", "failed", "cancelled", "blocked") }
                    .forEach { PcTaskSupervisor.monitor(container.appContext, it.id) }

                val pendingApprovals = tasks.filter(PcComputerUseApproval::isPending)
                val workflows = api.listWorkflows().sortedWith(
                    compareBy<WorkflowDto> { it.name.lowercase() }.thenByDescending { it.versionNumber })
                val artifacts = api.listMobileArtifacts(selectedProject).artifacts.sortedByDescending { it.updatedUtc }
                val timeline = api.getObservabilityTimeline(100).entries

                val selectedTaskId = previous.selectedTaskId
                val logs = if (selectedTaskId != null && tasks.any { it.id == selectedTaskId }) {
                    runCatching { api.getAgentTaskLogs(selectedTaskId).logs }.getOrDefault(previous.selectedTaskLogs)
                } else emptyList()

                val openResultId = previous.taskResult?.taskId
                val refreshedResult = openResultId?.let { id -> tasks.firstOrNull { it.id == id }?.let(::toResultView) }
                val openApprovalId = previous.approvalTask?.id
                val refreshedApproval = openApprovalId?.let { id -> pendingApprovals.firstOrNull { it.id == id } }
                val openQuestionTaskId = previous.userQuestionTask?.id
                val refreshedQuestionTask = openQuestionTaskId?.let { id ->
                    tasks.firstOrNull { it.id == id && isUserInputPending(it) }
                }

                _state.value = previous.copy(
                    projects = projects,
                    selectedProjectId = selectedProject,
                    projectSelectionInitialized = true,
                    tasks = tasks,
                    pendingApprovals = pendingApprovals,
                    approvalTask = refreshedApproval,
                    userQuestionTask = refreshedQuestionTask,
                    userQuestion = previous.userQuestion.takeIf { refreshedQuestionTask != null },
                    workflows = workflows,
                    artifacts = artifacts,
                    knowledge = previous.knowledge?.takeIf { it.projectId == selectedProject },
                    timeline = timeline,
                    selectedTaskLogs = logs,
                    taskResult = refreshedResult ?: previous.taskResult?.takeIf { openResultId == null },
                    loading = false,
                    actionInFlight = false,
                    error = null
                )
            } catch (e: Exception) {
                _state.value = _state.value.copy(loading = false, actionInFlight = false, error = e.message ?: "Could not refresh PC state.")
            }
        }
    }

    fun selectProject(id: String?) {
        container.activeProjectStore.select(id)
        _state.value = _state.value.copy(
            selectedProjectId = id,
            projectSelectionInitialized = true,
            knowledge = null,
            knowledgeLoading = false
        )
        refresh(silent = true)
    }

    fun refreshKnowledge() {
        val projectId = _state.value.selectedProjectId
        if (projectId.isNullOrBlank()) {
            _state.value = _state.value.copy(knowledge = null, knowledgeLoading = false)
            return
        }
        val client = container.client.value ?: return
        viewModelScope.launch {
            _state.value = _state.value.copy(knowledgeLoading = true, error = null)
            try {
                val pack = client.api.getProjectKnowledge(projectId)
                if (_state.value.selectedProjectId == projectId) {
                    _state.value = _state.value.copy(knowledge = pack, knowledgeLoading = false)
                }
            } catch (e: Exception) {
                if (_state.value.selectedProjectId == projectId) {
                    _state.value = _state.value.copy(
                        knowledgeLoading = false,
                        error = e.message ?: "Could not load Project knowledge."
                    )
                }
            }
        }
    }

    fun createProject(goal: String) {
        if (goal.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction("Creating Project on the PC…")
            try {
                val project = client.api.createProject(CreateProjectRequest(goal.trim()))
                container.activeProjectStore.select(project.id)
                _state.value = _state.value.copy(
                    selectedProjectId = project.id,
                    projectSelectionInitialized = true,
                    knowledge = null,
                    actionInFlight = false,
                    message = "Project created and queued on the PC."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun startPcTask(goal: String, role: String, projectId: String? = _state.value.selectedProjectId) {
        createTask(goal = goal, role = role, projectId = projectId, parentId = null)
    }

    fun requestContinuation(task: AgentTaskDto) {
        _state.value = _state.value.copy(continuationParent = task, taskResult = null)
    }

    fun dismissContinuation() {
        _state.value = _state.value.copy(continuationParent = null)
    }

    fun continueTask(parent: AgentTaskDto, instruction: String) {
        if (instruction.isBlank()) return
        val projectId = projectContainingTask(parent.id) ?: _state.value.selectedProjectId
        val role = parent.role?.takeIf { it.isNotBlank() } ?: "coding"
        _state.value = _state.value.copy(continuationParent = null)
        createTask(
            goal = "Continue from task ${parent.id.take(8)} (${parent.goal}):\n${instruction.trim()}",
            role = role,
            projectId = projectId,
            parentId = parent.id
        )
    }

    private fun createTask(goal: String, role: String, projectId: String?, parentId: String?) {
        if (goal.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction(
                when (role) {
                    "computer-use" -> "Sending Computer Use task to Windows…"
                    "coding" -> "Sending coding task to Windows…"
                    else -> "Sending task to PaRuX…"
                }
            )
            try {
                val task = client.api.createAgentTask(
                    CreateAgentTaskRequest(
                        goal = goal.trim(),
                        parentId = parentId,
                        role = role,
                        maxRepairAttempts = if (role == "coding") 1 else 0
                    )
                )
                if (!projectId.isNullOrBlank()) client.api.attachProjectTask(projectId, AttachProjectTaskRequest(task.id))
                PcTaskSupervisor.monitor(container.appContext, task.id)

                _state.value = _state.value.copy(
                    selectedTaskId = task.id,
                    knowledge = _state.value.knowledge?.takeIf { it.projectId != projectId },
                    actionInFlight = false,
                    message = when (role) {
                        "coding" -> "Coding task queued. PaRuX Windows will work in its configured repository."
                        "computer-use" -> "Computer Use task queued. If Windows needs consent, it will appear here as an Approve once / Deny request."
                        else -> "Task queued."
                    }
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    private fun projectContainingTask(taskId: String): String? =
        _state.value.projects.firstOrNull { taskId in it.taskIds }?.id

    fun downloadArtifact(context: Context, artifact: MobileArtifactDto, destination: Uri) {
        if (!artifact.downloadable || artifact.versionNumber <= 0) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            _state.value = _state.value.copy(downloadingArtifactId = artifact.id, error = null)
            try {
                withContext(Dispatchers.IO) {
                    client.api.downloadMobileArtifact(artifact.id, artifact.versionNumber).use { body ->
                        val output = context.contentResolver.openOutputStream(destination, "w")
                            ?: throw IllegalStateException("Android could not open the selected destination.")
                        output.use { stream -> body.byteStream().use { input -> input.copyTo(stream) } }
                    }
                }
                _state.value = _state.value.copy(
                    downloadingArtifactId = null,
                    message = "Saved ${artifact.fileName ?: artifact.title} on this device."
                )
            } catch (e: Exception) {
                _state.value = _state.value.copy(downloadingArtifactId = null, error = e.message ?: "Artifact download failed.")
            }
        }
    }

    fun requestComputerUseApproval(task: AgentTaskDto) {
        if (!PcComputerUseApproval.isPending(task)) {
            _state.value = _state.value.copy(error = "This Computer Use request is no longer awaiting approval.")
            return
        }
        _state.value = _state.value.copy(
            approvalTask = task,
            userQuestionTask = null,
            userQuestion = null,
            taskResult = null,
            showTaskLogs = false
        )
    }

    fun dismissComputerUseApproval() { _state.value = _state.value.copy(approvalTask = null) }

    fun approveComputerUseOnce(task: AgentTaskDto) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction("Approving Computer Use for this task only…")
            try {
                val current = client.api.getAgentTask(task.id)
                if (!PcComputerUseApproval.isPending(current)) {
                    _state.value = _state.value.copy(
                        actionInFlight = false,
                        approvalTask = null,
                        error = "This request changed before approval and was not authorized."
                    )
                    refresh(silent = true)
                    return@launch
                }

                // Core owns and atomically validates the actual transition + audit. Android sends
                // only the task id; it cannot choose an arbitrary target status/stage or append a
                // forged audit record through a generic task-state mutation endpoint.
                client.api.approveComputerUseOnce(current.id)
                PcTaskSupervisor.monitor(container.appContext, current.id)
                _state.value = _state.value.copy(
                    approvalTask = null,
                    actionInFlight = false,
                    message = "Approved once. PaRuX Windows may execute this Computer Use task now."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun denyComputerUse(task: AgentTaskDto) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction("Denying Computer Use…")
            try {
                val current = client.api.getAgentTask(task.id)
                if (!PcComputerUseApproval.isPending(current)) {
                    _state.value = _state.value.copy(
                        actionInFlight = false,
                        approvalTask = null,
                        error = "This request changed before the decision; no denial was applied."
                    )
                    refresh(silent = true)
                    return@launch
                }
                client.api.cancelAgentTask(current.id)
                PcTaskSupervisor.markHandled(container.appContext, current.id, "cancelled")
                _state.value = _state.value.copy(
                    approvalTask = null,
                    actionInFlight = false,
                    message = "Computer Use denied. The task was cancelled without controlling the PC."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun requestUserQuestion(task: AgentTaskDto) {
        if (!isUserInputPending(task)) {
            _state.value = _state.value.copy(error = "This task is no longer waiting for user input.")
            return
        }
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val questionState = client.api.getAgentUserQuestion(task.id)
                val question = questionState.pendingQuestion
                if (question == null) {
                    _state.value = _state.value.copy(error = "The pending question changed before it could be opened.")
                    refresh(silent = true)
                    return@launch
                }
                _state.value = _state.value.copy(
                    userQuestionTask = task,
                    userQuestion = question,
                    approvalTask = null,
                    taskResult = null,
                    showTaskLogs = false,
                    error = null
                )
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun dismissUserQuestion() {
        _state.value = _state.value.copy(userQuestionTask = null, userQuestion = null)
    }

    fun answerUserQuestion(answer: String) {
        val task = _state.value.userQuestionTask ?: return
        val question = _state.value.userQuestion ?: return
        val trimmed = answer.trim()
        if (trimmed.isBlank()) return
        if (!question.allowOther && trimmed !in question.options) {
            _state.value = _state.value.copy(error = "Choose one of the options offered by the agent.")
            return
        }
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction("Sending your answer to the paused agent…")
            try {
                // Re-read the durable pending question immediately before mutation. This prevents a
                // stale mobile dialog from answering a newer question after another surface acted.
                val latest = client.api.getAgentUserQuestion(task.id).pendingQuestion
                if (latest == null || latest.id != question.id) {
                    _state.value = _state.value.copy(
                        actionInFlight = false,
                        userQuestionTask = null,
                        userQuestion = null,
                        error = "This question changed before your answer was submitted. Nothing was applied."
                    )
                    refresh(silent = true)
                    return@launch
                }
                client.api.answerAgentUserQuestion(
                    task.id,
                    AnswerAgentUserQuestionRequest(questionId = question.id, answer = trimmed)
                )
                PcTaskSupervisor.monitor(container.appContext, task.id)
                _state.value = _state.value.copy(
                    userQuestionTask = null,
                    userQuestion = null,
                    actionInFlight = false,
                    message = "Answer recorded. The same PaRuX task is resuming from its saved checkpoint."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun cancelTask(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                client.api.cancelAgentTask(id)
                PcTaskSupervisor.markHandled(container.appContext, id, "cancelled")
                _state.value = _state.value.copy(
                    userQuestionTask = _state.value.userQuestionTask?.takeIf { it.id != id },
                    userQuestion = _state.value.userQuestion.takeIf { _state.value.userQuestionTask?.id != id },
                    message = "Task cancellation requested."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun showTaskLogs(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val logs = client.api.getAgentTaskLogs(id).logs
                _state.value = _state.value.copy(
                    selectedTaskId = id,
                    selectedTaskLogs = logs,
                    showTaskLogs = true,
                    taskResult = null,
                    approvalTask = null,
                    userQuestionTask = null,
                    userQuestion = null,
                    error = null
                )
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun dismissTaskLogs() { _state.value = _state.value.copy(showTaskLogs = false) }
    fun showTaskResult(task: AgentTaskDto) {
        _state.value = _state.value.copy(
            taskResult = toResultView(task),
            showTaskLogs = false,
            approvalTask = null,
            userQuestionTask = null,
            userQuestion = null
        )
    }
    fun dismissTaskResult() { _state.value = _state.value.copy(taskResult = null) }

    fun runWorkflow(workflowId: String, input: String?) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            beginAction("Starting Workflow…")
            try {
                val result = client.api.runWorkflow(workflowId, RunWorkflowRequest(input?.takeIf { it.isNotBlank() }))
                result.createdTaskIds.forEach { PcTaskSupervisor.monitor(container.appContext, it) }
                _state.value = _state.value.copy(
                    selectedTaskId = result.createdTaskIds.firstOrNull(),
                    actionInFlight = false,
                    message = "Workflow started ${result.createdTaskIds.size} task(s)."
                )
                refresh(silent = true)
            } catch (e: Exception) { failAction(e) }
        }
    }

    fun dismissMessage() { _state.value = _state.value.copy(message = null) }
    fun dismissError() { _state.value = _state.value.copy(error = null) }

    private fun beginAction(message: String) {
        _state.value = _state.value.copy(actionInFlight = true, message = message, error = null)
    }
    private fun failAction(e: Exception) {
        _state.value = _state.value.copy(actionInFlight = false, error = e.message ?: "PC action failed.")
    }

    private fun toResultView(task: AgentTaskDto): PcTaskResultView {
        var summary: String? = null
        var branch: String? = null
        var worktree: String? = task.worktreePath
        var diff: String? = null
        var stepsTaken: Int? = null
        var rawFallback: String? = null
        val raw = task.resultJson
        if (!raw.isNullOrBlank()) {
            runCatching {
                val root = json.parseToJsonElement(raw)
                if (root is JsonObject) {
                    summary = stringValue(root["summary"])
                        ?: stringValue(root["response"])
                        ?: stringValue(root["result"])
                    branch = stringValue(root["branch"])
                    worktree = stringValue(root["worktree"]) ?: worktree
                    diff = stringValue(root["diff"])
                    stepsTaken = primitiveValue(root["stepsTaken"])?.intOrNull
                    if (summary == null && branch == null && diff == null && stepsTaken == null)
                        rawFallback = json.encodeToString(JsonElement.serializer(), root)
                } else rawFallback = raw
            }.onFailure { rawFallback = raw }
        }
        return PcTaskResultView(
            taskId = task.id,
            role = task.role ?: "auto",
            goal = task.goal,
            status = task.status,
            stage = task.stage,
            verificationStatus = task.verificationStatus,
            summary = summary,
            branch = branch,
            worktree = worktree,
            diff = diff,
            stepsTaken = stepsTaken,
            rawFallback = rawFallback
        )
    }

    private fun stringValue(element: JsonElement?): String? = primitiveValue(element)?.contentOrNull
    private fun primitiveValue(element: JsonElement?): JsonPrimitive? = element as? JsonPrimitive
}