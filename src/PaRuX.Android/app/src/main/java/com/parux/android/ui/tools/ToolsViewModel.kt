package com.parux.android.ui.tools

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.core.AppContainer
import com.parux.android.core.network.AiStorageBudgetStatus
import com.parux.android.core.network.AvatarGenerateRequest
import com.parux.android.core.network.AvatarProviderStatus
import com.parux.android.core.network.CreateAgentTaskRequest
import com.parux.android.core.network.MemoryAddRequest
import com.parux.android.core.network.MemoryEntryDto
import com.parux.android.core.network.ModelDownloadRequest
import com.parux.android.core.network.ModelDownloadStatus
import com.parux.android.core.network.ModelSearchResult
import com.parux.android.core.network.PresentationResponse
import com.parux.android.core.network.RagSourceDto
import com.parux.android.core.network.ResearchRequest
import com.parux.android.core.network.ResearchResult
import com.parux.android.core.network.SkillManifest
import com.parux.android.core.network.SpeechEngineStatus
import com.parux.android.core.network.SynthesizeRequest
import com.parux.android.core.network.ThreedEngineStatus
import com.parux.android.core.network.ThreedGenerateRequest
import com.parux.android.core.network.TranscribeRequest
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class ToolsUiState(
    val internetEnabled: Boolean = false,
    val memory: List<MemoryEntryDto> = emptyList(),
    val ragSources: List<RagSourceDto> = emptyList(),
    val skills: List<SkillManifest> = emptyList(),
    val researchAnswer: ResearchResult? = null,
    val researchLoading: Boolean = false,
    val presentation: PresentationResponse? = null,
    val presentationLoading: Boolean = false,
    val speechEngine: SpeechEngineStatus = SpeechEngineStatus(),
    val ttsResult: String? = null,
    val ttsAudioBase64: String? = null,
    val sttResult: String? = null,
    val sttLoading: Boolean = false,
    val modelBudget: AiStorageBudgetStatus? = null,
    val modelResults: List<ModelSearchResult> = emptyList(),
    val modelSearchLoading: Boolean = false,
    val modelDownloads: List<ModelDownloadStatus> = emptyList(),
    val avatarStatus: AvatarProviderStatus = AvatarProviderStatus(),
    val avatarLoading: Boolean = false,
    val threedEngine: ThreedEngineStatus = ThreedEngineStatus(),
    val threedGenerating: Boolean = false,
    val threedResultBase64: String? = null,
    val threedResultInfo: String? = null,
    val computerUseRunning: Boolean = false,
    val computerUseLog: List<String> = emptyList(),
    val computerUseSummary: String? = null,
    val agentTasks: List<com.parux.android.core.network.AgentTaskDto> = emptyList(),
    val automations: List<com.parux.android.core.network.AutomationDto> = emptyList(),
    val contextEntities: List<com.parux.android.core.network.ContextEntityDto> = emptyList(),
    val contextNeighborhood: com.parux.android.core.network.ContextNeighborhoodResponse? = null,
    val autopilotRun: com.parux.android.core.network.AutopilotRunDto? = null,
    val autopilotStatusMessage: String? = null,
    val stopAllMessage: String? = null,
    val errorMessage: String? = null
)

/**
 * Mobile Tools intentionally exposes only Core-owned, path-free control surfaces. Operations that
 * require a Windows filesystem path or install/modify a workstation runtime (manual benchmarks,
 * Training Lab job creation, engine installation) are configured from PaRuX Windows instead of
 * sending desktop paths through the paired-device API. Computer Use is also workstation-side:
 * Android creates/supervises a durable `computer-use` AgentTask and never gains phone Accessibility
 * or raw Windows input privileges.
 */
class ToolsViewModel(private val container: AppContainer) : ViewModel() {
    private val _state = MutableStateFlow(ToolsUiState())
    val state: StateFlow<ToolsUiState> = _state.asStateFlow()

    private fun fail(e: Exception) {
        _state.value = _state.value.copy(errorMessage = e.message ?: "Something went wrong.")
    }

    fun dismissError() { _state.value = _state.value.copy(errorMessage = null) }

    fun refreshAll() {
        refreshInternet(); refreshMemory(); refreshRag(); refreshSkills()
        refreshSpeechEngine(); refreshModelBudget(); refreshAvatarStatus(); refreshThreedEngine()
    }

    // ---- Internet ----
    fun refreshInternet() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(internetEnabled = client.api.getInternet().enabled) } catch (_: Exception) {}
        }
    }
    fun setInternet(enabled: Boolean) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(internetEnabled = client.api.setInternet(com.parux.android.core.network.InternetEnabledRequest(enabled)).enabled) }
            catch (e: Exception) { fail(e) }
        }
    }

    // ---- Memory ----
    fun refreshMemory() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(memory = client.api.listMemory()) } catch (e: Exception) { fail(e) }
        }
    }
    fun addMemory(content: String) {
        if (content.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.addMemory(MemoryAddRequest(content = content)); refreshMemory() } catch (e: Exception) { fail(e) }
        }
    }
    fun deleteMemory(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.deleteMemory(id); refreshMemory() } catch (e: Exception) { fail(e) }
        }
    }

    // ---- RAG: source paths are selected on Windows; Android only manages existing Core ids. ----
    fun refreshRag() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(ragSources = client.api.listRagSources()) } catch (e: Exception) { fail(e) }
        }
    }
    fun reindexRag(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.reindexRagSource(id); refreshRag() } catch (e: Exception) { fail(e) }
        }
    }
    fun removeRag(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.removeRagSource(id); refreshRag() } catch (e: Exception) { fail(e) }
        }
    }

    // ---- Skills: mobile creation is sanitized again by Core; privileged authoring stays Windows. ----
    fun refreshSkills() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(skills = client.api.listSkills()) } catch (e: Exception) { fail(e) }
        }
    }
    fun setSkillEnabled(id: String, enabled: Boolean) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.setSkillEnabled(id, com.parux.android.core.network.SkillEnabledRequest(enabled)); refreshSkills() } catch (e: Exception) { fail(e) }
        }
    }
    fun createSkill(displayName: String, description: String, slashCommand: String, preferredModelRole: String, internetRequired: Boolean) {
        if (displayName.isBlank()) return
        val client = container.client.value ?: return
        val manifest = SkillManifest(
            displayName = displayName, name = displayName.lowercase().replace(' ', '-'),
            description = description, slashCommand = slashCommand, category = "Custom", source = "user",
            preferredModelRole = preferredModelRole, internetPolicy = if (internetRequired) "required" else "off"
        )
        viewModelScope.launch {
            try { client.api.createSkill(com.parux.android.core.network.SkillCreateRequest(manifest)); refreshSkills() } catch (e: Exception) { fail(e) }
        }
    }
    fun deleteSkill(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.deleteSkill(id); refreshSkills() } catch (e: Exception) { fail(e) }
        }
    }

    // ---- Research ----
    fun runResearch(question: String, mode: String) {
        if (question.isBlank()) return
        val client = container.client.value ?: return
        _state.value = _state.value.copy(researchLoading = true, researchAnswer = null)
        viewModelScope.launch {
            try {
                val result = client.api.research(ResearchRequest(question = question, mode = mode))
                _state.value = _state.value.copy(researchAnswer = result, researchLoading = false)
            } catch (e: Exception) {
                _state.value = _state.value.copy(researchLoading = false)
                fail(e)
            }
        }
    }

    // ---- Presentations ----
    fun generatePresentation(topic: String, slideCount: Int) {
        if (topic.isBlank()) return
        val client = container.client.value ?: return
        _state.value = _state.value.copy(presentationLoading = true, presentation = null)
        viewModelScope.launch {
            try {
                val result = client.api.generatePresentation(com.parux.android.core.network.PresentationRequest(topic = topic, slideCount = slideCount))
                _state.value = _state.value.copy(presentation = result, presentationLoading = false)
            } catch (e: Exception) {
                _state.value = _state.value.copy(presentationLoading = false)
                fail(e)
            }
        }
    }

    // ---- Speech ----
    fun refreshSpeechEngine() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(speechEngine = client.api.getSpeechEngineStatus()) } catch (_: Exception) {}
        }
    }
    fun speak(text: String) {
        if (text.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val result = client.api.synthesize(SynthesizeRequest(text = text))
                _state.value = _state.value.copy(ttsAudioBase64 = result.base64Data, ttsResult = "Spoken locally · ${"%.1f".format(result.durationSeconds)}s")
            } catch (e: Exception) { fail(e) }
        }
    }
    fun transcribe(audioBase64: String, fileName: String) {
        val client = container.client.value ?: return
        _state.value = _state.value.copy(sttLoading = true, sttResult = null)
        viewModelScope.launch {
            try {
                val result = client.api.transcribe(TranscribeRequest(audioBase64 = audioBase64, fileName = fileName))
                _state.value = _state.value.copy(sttLoading = false, sttResult = result.text)
            } catch (e: Exception) {
                _state.value = _state.value.copy(sttLoading = false)
                fail(e)
            }
        }
    }

    // ---- Model Manager: search/download by server-issued metadata; no workstation path input. ----
    fun refreshModelBudget() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(modelBudget = client.api.getModelBudget()) } catch (_: Exception) {}
        }
    }
    fun searchModels(query: String) {
        if (query.isBlank()) return
        val client = container.client.value ?: return
        _state.value = _state.value.copy(modelSearchLoading = true)
        viewModelScope.launch {
            try {
                val results = client.api.searchModels(query)
                _state.value = _state.value.copy(modelResults = results, modelSearchLoading = false)
            } catch (e: Exception) {
                _state.value = _state.value.copy(modelSearchLoading = false)
                fail(e)
            }
        }
    }
    fun startModelDownload(repoId: String, fileName: String, url: String, sizeBytes: Long) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val status = client.api.startModelDownload(ModelDownloadRequest(repoId, fileName, url, sizeBytes))
                pollModelDownload(status.id)
            } catch (e: Exception) { fail(e) }
        }
    }
    private fun pollModelDownload(id: String) {
        viewModelScope.launch {
            while (isActive) {
                try {
                    val downloads = container.client.value?.api?.listModelDownloads() ?: break
                    _state.value = _state.value.copy(modelDownloads = downloads)
                    val current = downloads.firstOrNull { it.id == id } ?: break
                    if (current.status in listOf("completed", "failed", "cancelled")) {
                        refreshModelBudget()
                        break
                    }
                } catch (_: Exception) { break }
                delay(1500)
            }
        }
    }

    // ---- Avatar Studio ----
    fun refreshAvatarStatus() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(avatarStatus = client.api.getAvatarStatus()) } catch (_: Exception) {}
        }
    }
    fun generateAvatar(text: String) {
        if (text.isBlank()) return
        val client = container.client.value ?: return
        _state.value = _state.value.copy(avatarLoading = true)
        viewModelScope.launch {
            try {
                client.api.generateAvatar(AvatarGenerateRequest(text = text))
                _state.value = _state.value.copy(avatarLoading = false)
            } catch (e: Exception) {
                _state.value = _state.value.copy(avatarLoading = false)
                fail(e)
            }
        }
    }

    // ---- 3D Studio: engine installation stays desktop-only; generation is a safe Core action. ----
    fun refreshThreedEngine() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(threedEngine = client.api.getThreedEngineStatus()) } catch (_: Exception) {}
        }
    }
    fun generateThreed(sourceImageBase64: String, fileName: String) {
        val client = container.client.value ?: return
        _state.value = _state.value.copy(threedGenerating = true, threedResultBase64 = null, threedResultInfo = null)
        viewModelScope.launch {
            try {
                val result = client.api.generateThreed(ThreedGenerateRequest(sourceImageBase64 = sourceImageBase64, fileName = fileName))
                _state.value = _state.value.copy(
                    threedGenerating = false, threedResultBase64 = result.base64Data,
                    threedResultInfo = "${result.fileName} · ${"%.1f".format(result.durationSeconds)}s"
                )
            } catch (e: Exception) {
                _state.value = _state.value.copy(threedGenerating = false)
                fail(e)
            }
        }
    }

    // ---- Computer Use: durable Core/Windows task, never Android Accessibility. ----
    private var computerUseJob: Job? = null
    private var computerUseTaskId: String? = null

    fun runComputerUse(goal: String) {
        if (goal.isBlank()) return
        val client = container.client.value ?: run {
            _state.value = _state.value.copy(computerUseSummary = "Not paired with a PC yet.")
            return
        }
        if (computerUseJob?.isActive == true) return

        _state.value = _state.value.copy(
            computerUseRunning = true,
            computerUseLog = emptyList(),
            computerUseSummary = "Creating a task on the paired Windows PC…"
        )

        computerUseJob = viewModelScope.launch {
            try {
                val created = client.api.createAgentTask(
                    CreateAgentTaskRequest(goal = goal.trim(), role = "computer-use", maxRepairAttempts = 0)
                )
                computerUseTaskId = created.id
                _state.value = _state.value.copy(
                    computerUseLog = listOf("Core task ${created.id.take(12)} created on Windows."),
                    computerUseSummary = "Windows owns execution. GUI control still requires task-scoped Approve once."
                )

                while (isActive) {
                    val current = client.api.getAgentTask(created.id)
                    val serverLogs = try {
                        client.api.getAgentTaskLogs(created.id).logs.map { it.message }.takeLast(20)
                    } catch (_: Exception) {
                        emptyList()
                    }
                    val stage = current.stage?.takeIf { it.isNotBlank() } ?: "Core task"
                    val statusLine = "${current.status.uppercase()} · $stage · ${current.progressPercent}%"
                    val terminal = current.status in listOf("completed", "failed", "cancelled")
                    val approvalPending = current.status in listOf("waiting", "blocked") &&
                        (stage.contains("approval", ignoreCase = true) || stage.contains("computer", ignoreCase = true))
                    val summary = when (current.status) {
                        "completed" -> current.resultJson ?: "Computer Use completed on Windows."
                        "failed" -> current.error ?: "Computer Use failed on Windows."
                        "cancelled" -> "Computer Use task cancelled."
                        else -> if (approvalPending)
                            "Waiting for task-scoped Approve once in PC Studio. The phone has no direct input privilege."
                        else
                            "Running on the paired Windows PC."
                    }
                    _state.value = _state.value.copy(
                        computerUseRunning = !terminal,
                        computerUseLog = (serverLogs + statusLine).distinct().takeLast(24),
                        computerUseSummary = summary
                    )
                    if (terminal) break
                    delay(1000)
                }
            } catch (e: Exception) {
                if (isActive) {
                    _state.value = _state.value.copy(
                        computerUseRunning = false,
                        computerUseSummary = "Computer Use request failed: ${e.message ?: "unknown error"}"
                    )
                }
            } finally {
                computerUseTaskId = null
                computerUseJob = null
                if (_state.value.computerUseRunning)
                    _state.value = _state.value.copy(computerUseRunning = false)
            }
        }
    }

    fun stopComputerUse() {
        val client = container.client.value
        val taskId = computerUseTaskId
        computerUseJob?.cancel()
        computerUseJob = null
        computerUseTaskId = null
        _state.value = _state.value.copy(
            computerUseRunning = false,
            computerUseSummary = if (taskId == null) "Stopped." else "Cancellation requested for the Windows task."
        )
        if (client != null && taskId != null) {
            viewModelScope.launch {
                try { client.api.cancelAgentTask(taskId); refreshAgentTasks() }
                catch (e: Exception) { fail(e) }
            }
        }
    }

    // ---- Multi-Agent Orchestrator + Autopilot supervision ----
    fun refreshAgentTasks() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(agentTasks = client.api.listAgentTasks().tasks) } catch (e: Exception) { fail(e) }
        }
    }

    fun cancelAgentTask(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.cancelAgentTask(id); refreshAgentTasks() } catch (e: Exception) { fail(e) }
        }
    }

    fun startAutopilotRun(goal: String, permissionMode: String) {
        if (goal.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val run = client.api.startAutopilotRun(com.parux.android.core.network.StartAutopilotRunRequest(goal, permissionMode))
                _state.value = _state.value.copy(autopilotRun = run, autopilotStatusMessage = "Run ${run.id}: ${run.status}")
            } catch (e: Exception) { fail(e) }
        }
    }

    fun checkAutopilotRun(id: String) {
        if (id.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val run = client.api.getAutopilotRun(id)
                _state.value = _state.value.copy(autopilotRun = run, autopilotStatusMessage = "Run ${run.id}: ${run.status}")
            } catch (e: Exception) { fail(e) }
        }
    }

    fun approveAutopilotRun(id: String) {
        if (id.isBlank()) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.approveAutopilotRun(id); checkAutopilotRun(id) } catch (e: Exception) { fail(e) }
        }
    }

    fun stopAllAgents() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val result = client.api.stopAllAgents()
                _state.value = _state.value.copy(
                    stopAllMessage = "Stopped ${result.tasksCancelled} task(s), cancelled ${result.autopilotRunsCancelled} Autopilot run(s), automations paused."
                )
                refreshAgentTasks()
            } catch (e: Exception) { fail(e) }
        }
    }

    fun resumeAutomations() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.resumeAutomations(); _state.value = _state.value.copy(stopAllMessage = "Automations resumed.") }
            catch (e: Exception) { fail(e) }
        }
    }

    // ---- Automation Engine supervision: authoring stays Windows-only. ----
    fun refreshAutomations() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(automations = client.api.listAutomations().automations) } catch (e: Exception) { fail(e) }
        }
    }

    fun setAutomationEnabled(id: String, enabled: Boolean) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.setAutomationEnabled(id, com.parux.android.core.network.SetAutomationEnabledRequest(enabled)); refreshAutomations() }
            catch (e: Exception) { fail(e) }
        }
    }

    fun deleteAutomation(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.deleteAutomation(id); refreshAutomations() } catch (e: Exception) { fail(e) }
        }
    }

    // ---- Local Context Graph Inspector: read-only on Android. ----
    fun refreshContextEntities(type: String? = null) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(contextEntities = client.api.listContextEntities(type).entities) } catch (e: Exception) { fail(e) }
        }
    }

    fun viewContextNeighborhood(entityId: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { _state.value = _state.value.copy(contextNeighborhood = client.api.getContextNeighborhood(entityId)) } catch (e: Exception) { fail(e) }
        }
    }
}
