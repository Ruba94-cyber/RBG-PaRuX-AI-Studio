package com.parux.android.ui.chat

import android.util.Base64
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.core.AppContainer
import com.parux.android.core.network.ChatCompletionMessage
import com.parux.android.core.network.ChatCompletionRequest
import com.parux.android.core.network.ChatImageContent
import com.parux.android.core.network.ChatModelRoutingClient
import com.parux.android.core.network.ChatRoutingPreference
import com.parux.android.core.network.ChatStreamClient
import com.parux.android.core.network.ChatStreamEvent
import com.parux.android.core.network.ConversationUpsertRequest
import com.parux.android.core.network.ManagedChatModelInfo
import com.parux.android.core.network.ProjectDto
import com.parux.android.core.network.SetChatRoutingPreferenceRequest
import com.parux.android.core.network.SpeechStreamClient
import com.parux.android.core.network.SynthesizeRequest
import com.parux.android.core.network.TranscribeRequest
import java.time.Instant
import java.util.Locale
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

enum class LiveVoicePhase { Off, Checking, Listening, Transcribing, Thinking, Speaking }

data class ChatUiState(
    val conversations: List<com.parux.android.core.network.ConversationSummary> = emptyList(),
    val current: UiConversation = UiConversation(),
    val isStreaming: Boolean = false,
    val isLoadingList: Boolean = false,
    val gamingMode: Boolean = false,
    val aiProfile: String = "STANDARD",
    val errorMessage: String? = null,
    val skills: List<com.parux.android.core.network.SkillManifest> = emptyList(),
    val projects: List<ProjectDto> = emptyList(),
    val selectedProjectId: String? = null,
    val voiceModeEnabled: Boolean = false,
    val voicePhase: LiveVoicePhase = LiveVoicePhase.Off,
    val voiceLastTranscript: String? = null,
    val forcedSkillId: String? = null,
    val forcedSkillLabel: String = "Auto",
    val modelRouting: ChatRoutingPreference = ChatRoutingPreference(),
    val managedModels: List<ManagedChatModelInfo> = emptyList()
)

class ChatViewModel(private val container: AppContainer) : ViewModel() {
    private val _state = MutableStateFlow(ChatUiState())
    val state: StateFlow<ChatUiState> = _state.asStateFlow()
    private val voiceEngine = LocalVoiceEngine(container.appContext)
    private var chatJob: Job? = null
    private var voiceJob: Job? = null
    private var streamClient: ChatStreamClient? = null
    private var voiceSpeechStreamClient: SpeechStreamClient? = null

    fun refreshConversationList() {
        val client = container.client.value ?: return
        _state.value = _state.value.copy(isLoadingList = true)
        viewModelScope.launch {
            try { _state.value = _state.value.copy(conversations = client.api.listConversations(), isLoadingList = false) }
            catch (e: Exception) { _state.value = _state.value.copy(isLoadingList = false, errorMessage = friendlyError(e)) }
        }
    }

    fun refreshProjects() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val projects = client.api.listProjects().projects.sortedByDescending { it.updatedUtc }
                val active = container.activeProjectStore.selection.value
                val selected = if (active.initialized && active.projectId != null && projects.any { it.id == active.projectId }) active.projectId
                else if (active.initialized && active.projectId == null) null
                else _state.value.selectedProjectId?.takeIf { id -> projects.any { it.id == id } }
                _state.value = _state.value.copy(projects = projects, selectedProjectId = selected)
            } catch (_: Exception) { }
        }
    }

    fun setProject(id: String?) {
        container.activeProjectStore.select(id)
        _state.value = _state.value.copy(selectedProjectId = id)
    }

    fun refreshHealthAndProfile() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val health = client.api.health()
                _state.value = _state.value.copy(gamingMode = health.gamingMode)
                if (health.gamingMode && _state.value.voiceModeEnabled) stopVoiceMode()
            } catch (_: Exception) { }
            try { _state.value = _state.value.copy(aiProfile = client.api.getAiProfile().textProfile) }
            catch (_: Exception) { }
        }
    }

    fun refreshModelRouting() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val routing = ChatModelRoutingClient(client.okHttp, client.endpoint)
                val preference = routing.getPreference()
                val models = routing.listManagedModels().sortedBy { it.alias?.takeIf(String::isNotBlank) ?: it.fileName }
                _state.value = _state.value.copy(modelRouting = preference, managedModels = models)
            } catch (e: Exception) { _state.value = _state.value.copy(errorMessage = friendlyError(e)) }
        }
    }

    fun setAutoModelRouting(profile: String) = setModelRouting(SetChatRoutingPreferenceRequest(mode = "auto", autoProfile = profile))
    fun setManualModelRouting(fileName: String) = setModelRouting(SetChatRoutingPreferenceRequest(mode = "manual", manualModelFileName = fileName))

    private fun setModelRouting(request: SetChatRoutingPreferenceRequest) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val preference = ChatModelRoutingClient(client.okHttp, client.endpoint).setPreference(request)
                _state.value = _state.value.copy(modelRouting = preference, errorMessage = null)
            } catch (e: Exception) {
                _state.value = _state.value.copy(errorMessage = friendlyError(e))
                refreshModelRouting()
            }
        }
    }

    fun setAiProfile(profile: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val updated = client.api.setAiProfile(com.parux.android.core.network.AiProfileRequest(profile))
                _state.value = _state.value.copy(aiProfile = updated.textProfile)
            } catch (e: Exception) { _state.value = _state.value.copy(errorMessage = friendlyError(e)) }
        }
    }

    fun newConversation() {
        if (_state.value.voiceModeEnabled) stopVoiceMode()
        _state.value = _state.value.copy(current = UiConversation())
    }

    fun refreshSkills() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val skills = client.api.listSkills().filter { it.enabled && it.platforms.contains("Android") }
                _state.value = _state.value.copy(skills = skills)
            } catch (_: Exception) { }
        }
    }

    fun setForcedSkill(id: String?, label: String) { _state.value = _state.value.copy(forcedSkillId = id, forcedSkillLabel = label) }

    fun parseSlashCommand(text: String): Pair<String?, String> {
        if (text.isEmpty() || text[0] != '/') return null to text
        val end = text.indexOfFirst { it.isWhitespace() }.let { if (it < 0) text.length else it }
        val command = text.substring(0, end)
        val match = _state.value.skills.firstOrNull { it.slashCommand.isNotEmpty() && it.slashCommand.equals(command, ignoreCase = true) }
            ?: return null to text
        return match.id to if (end < text.length) text.substring(end).trimStart() else ""
    }

    fun openConversation(id: String) {
        val client = container.client.value ?: return
        if (_state.value.voiceModeEnabled) stopVoiceMode()
        viewModelScope.launch {
            try {
                val doc = client.api.getConversation(id)
                val parsed = com.parux.android.core.network.PaRuXHttpClient.json.decodeFromString(
                    com.parux.android.core.network.ConversationDoc.serializer(), doc.documentJson)
                _state.value = _state.value.copy(current = parsed.toUi())
            } catch (e: Exception) { _state.value = _state.value.copy(errorMessage = friendlyError(e)) }
        }
    }

    fun sendMessage(text: String, image: PendingImage? = null, oneShotSkillId: String? = null) {
        if (text.isBlank() && image == null) return
        if (_state.value.voiceModeEnabled) stopVoiceMode()
        if (_state.value.isStreaming) return
        chatJob?.cancel()
        chatJob = viewModelScope.launch { runChatTurn(text, image, oneShotSkillId) }
    }

    fun startVoiceMode() {
        val client = container.client.value ?: return
        if (_state.value.voiceModeEnabled) return
        if (_state.value.gamingMode) {
            _state.value = _state.value.copy(errorMessage = "Gaming Mode — Live Voice stays off while PaRuX AI is suspended.")
            return
        }
        voiceEngine.stopAll()
        voiceSpeechStreamClient?.cancel()
        voiceSpeechStreamClient = null
        voiceJob?.cancel()
        _state.value = _state.value.copy(voiceModeEnabled = true, voicePhase = LiveVoicePhase.Checking, voiceLastTranscript = null, errorMessage = null)
        voiceJob = viewModelScope.launch {
            try {
                val speech = client.api.getSpeechEngineStatus()
                if (!speech.sttInstalled || !speech.ttsInstalled) {
                    failVoice("Live Voice needs the local Speech runtime on the PC (Parakeet STT + Kokoro TTS). Install/repair Speech from PaRuX Runtime/Settings first.")
                    return@launch
                }
                while (currentCoroutineContext().isActive && _state.value.voiceModeEnabled) {
                    _state.value = _state.value.copy(voicePhase = LiveVoicePhase.Listening)
                    val wav = voiceEngine.captureUtterance()
                    if (!_state.value.voiceModeEnabled) break
                    if (wav == null) continue
                    _state.value = _state.value.copy(voicePhase = LiveVoicePhase.Transcribing)
                    val transcription = client.api.transcribe(TranscribeRequest(Base64.encodeToString(wav, Base64.NO_WRAP), "parux-live-voice.wav"))
                    val spokenText = transcription.text.trim()
                    if (spokenText.isBlank()) continue
                    _state.value = _state.value.copy(voicePhase = LiveVoicePhase.Thinking, voiceLastTranscript = spokenText)
                    val answer = runChatTurn(
                        spokenText,
                        additionalSystemPrompt = "Live Voice mode is active. Answer conversationally and directly for speech playback. Prefer concise natural sentences; avoid markdown tables or long code blocks unless essential."
                    )
                    if (!_state.value.voiceModeEnabled) break
                    if (answer.isNullOrBlank()) continue
                    _state.value = _state.value.copy(voicePhase = LiveVoicePhase.Speaking)
                    val localVoice = if (Locale.getDefault().language.equals("it", ignoreCase = true)) "if_sara" else ""
                    val speechStream = SpeechStreamClient(client.okHttp, client.endpoint)
                    voiceSpeechStreamClient = speechStream
                    try {
                        voiceEngine.playStreamingPcmWithBargeIn(
                            speechStream.stream(SynthesizeRequest(answer, localVoice))
                        )
                    } finally {
                        speechStream.cancel()
                        if (voiceSpeechStreamClient === speechStream) voiceSpeechStreamClient = null
                    }
                }
            } catch (_: CancellationException) { }
            catch (e: SecurityException) { failVoice("Microphone permission is required for PaRuX Live Voice.") }
            catch (e: Exception) { failVoice("Live Voice stopped: " + friendlyError(e)) }
            finally {
                voiceSpeechStreamClient?.cancel()
                voiceSpeechStreamClient = null
            }
        }
    }

    fun interruptVoicePlayback() {
        if (!_state.value.voiceModeEnabled || _state.value.voicePhase != LiveVoicePhase.Speaking) return
        voiceEngine.stopPlayback()
        voiceSpeechStreamClient?.cancel()
        voiceSpeechStreamClient = null
        _state.value = _state.value.copy(voicePhase = LiveVoicePhase.Listening)
    }

    fun stopVoiceMode() {
        if (!_state.value.voiceModeEnabled && _state.value.voicePhase == LiveVoicePhase.Off) return
        _state.value = _state.value.copy(voiceModeEnabled = false, voicePhase = LiveVoicePhase.Off)
        voiceEngine.stopAll()
        voiceSpeechStreamClient?.cancel()
        voiceSpeechStreamClient = null
        streamClient?.cancel()
        voiceJob?.cancel()
        voiceJob = null
    }

    private fun failVoice(message: String) {
        voiceEngine.stopAll()
        voiceSpeechStreamClient?.cancel()
        voiceSpeechStreamClient = null
        _state.value = _state.value.copy(voiceModeEnabled = false, voicePhase = LiveVoicePhase.Off, errorMessage = message)
    }

    private suspend fun runChatTurn(
        text: String,
        image: PendingImage? = null,
        oneShotSkillId: String? = null,
        additionalSystemPrompt: String? = null
    ): String? {
        if (text.isBlank() && image == null) return null
        val client = container.client.value ?: return null
        if (_state.value.isStreaming) return null
        if (_state.value.gamingMode) {
            _state.value = _state.value.copy(errorMessage = "Gaming Mode — PaRuX AI is suspended to leave all resources for the game.")
            return null
        }

        val route = if (image != null) RouteKind.Vision else RouterService.classify(text)
        val previous = _state.value.current
        val userMessage = UiMessage(role = "user", content = text, route = RouterService.label(route), imageAttachment = image)
        val assistantMessage = UiMessage(role = "assistant", content = "", isStreaming = true)
        val conversation = previous.copy(messages = previous.messages + userMessage + assistantMessage, updatedUtc = Instant.now().toString())
        _state.value = _state.value.copy(current = conversation, isStreaming = true, errorMessage = null)

        val wireMessages = buildList {
            var system = RouterService.systemPrompt(route)
            if (!additionalSystemPrompt.isNullOrBlank()) system = if (system.isNullOrBlank()) additionalSystemPrompt else system + "\n\n" + additionalSystemPrompt
            system?.takeIf { it.isNotBlank() }?.let { add(ChatCompletionMessage(role = "system", content = it)) }
            conversation.messages.filter { !it.isStreaming && !it.isError }.forEach { m ->
                add(ChatCompletionMessage(
                    role = m.role,
                    content = m.content,
                    images = if (m.imageAttachment != null) listOf(ChatImageContent(m.imageAttachment.fileName, m.imageAttachment.mediaType, m.imageAttachment.base64Data)) else emptyList()
                ))
            }
        }.toMutableList()

        val forcedSkillId = oneShotSkillId ?: _state.value.forcedSkillId
        val selectedProjectId = _state.value.selectedProjectId
        val sc = ChatStreamClient(client.okHttp, client.endpoint)
        streamClient = sc
        val builder = StringBuilder()
        var finalized = false

        try {
            if (!selectedProjectId.isNullOrBlank()) {
                val pack = try { client.api.getProjectKnowledge(selectedProjectId) } catch (e: Exception) {
                    val message = "Project context could not be loaded, so no model request was sent. " + friendlyError(e)
                    updateAssistantMessage(assistantMessage.id, message, false, isError = true)
                    _state.value = _state.value.copy(isStreaming = false, errorMessage = message)
                    finishTurn()
                    finalized = true
                    return null
                }
                val projectContext = "You are working inside the explicitly selected PaRuX Project. Use the following Core-resolved bounded Project evidence as context. Do not invent missing Project state.\n\n" + pack.promptText
                if (wireMessages.firstOrNull()?.role == "system") {
                    val currentSystem = wireMessages.first()
                    wireMessages[0] = currentSystem.copy(content = currentSystem.content + "\n\n" + projectContext)
                } else wireMessages.add(0, ChatCompletionMessage(role = "system", content = projectContext))
            }

            sc.stream(
                ChatCompletionRequest(model = "local-default", messages = wireMessages, stream = true, skillId = forcedSkillId),
                conversationId = conversation.id,
                projectId = selectedProjectId
            ).collect { event ->
                when (event) {
                    is ChatStreamEvent.ModelRouted -> {
                        val selected = event.selectedModel ?: "configured model"
                        val policy = if (event.manualLock) "Manual lock" else "Auto · ${prettyAutoProfile(event.autoProfile)}"
                        val evidence = buildList {
                            event.selectedIntelligence?.let { add("capability ${"%.2f".format(Locale.US, it)}") }
                            event.tokensPerSecond?.let { add("${"%.1f".format(Locale.US, it)} tok/s") }
                        }.joinToString(" · ")
                        val label = listOf(selected, policy, evidence.takeIf { it.isNotBlank() }).filterNotNull().joinToString(" · ")
                        updateAssistantMessage(assistantMessage.id, builder.toString(), true, modelRouteLabel = label, modelRouteReason = event.reason)
                    }
                    is ChatStreamEvent.Delta -> {
                        builder.append(event.text)
                        updateAssistantMessage(assistantMessage.id, builder.toString(), true)
                    }
                    is ChatStreamEvent.CloudFallback -> {
                        val model = event.model.takeIf { it.isNotBlank() }?.let { " ($it)" } ?: ""
                        val why = event.reason?.takeIf { it.isNotBlank() }?.let { " — local inference: " + it.take(140) } ?: ""
                        updateAssistantMessage(
                            assistantMessage.id, builder.toString(), true,
                            cloudFallbackNotice = "Answered by ${event.provider}$model. This turn left your PC.$why"
                        )
                    }
                    ChatStreamEvent.SwitchedToUncensored -> updateAssistantMessage(assistantMessage.id, builder.toString(), true, switchedToUncensored = true)
                    is ChatStreamEvent.RagSources -> updateAssistantMessage(assistantMessage.id, builder.toString(), true, ragSources = event.files)
                    is ChatStreamEvent.SkillSelected -> {
                        if (!(event.mode == "auto" && event.displayName.equals("General", ignoreCase = true))) {
                            val label = event.displayName + when (event.mode) {
                                "forced" -> " (forced)"
                                "slash" -> " (/" + event.displayName.lowercase() + ")"
                                else -> ""
                            }
                            updateAssistantMessage(assistantMessage.id, builder.toString(), true, skillUsed = label)
                        }
                    }
                    ChatStreamEvent.Done -> {
                        updateAssistantMessage(assistantMessage.id, builder.toString(), false)
                        finishTurn()
                        finalized = true
                    }
                    is ChatStreamEvent.Error -> {
                        updateAssistantMessage(assistantMessage.id, builder.toString(), false, isError = builder.isEmpty())
                        _state.value = _state.value.copy(isStreaming = false, errorMessage = event.message)
                        finishTurn(persist = builder.isNotEmpty())
                        finalized = true
                    }
                }
            }
            return builder.toString().takeIf { finalized && it.isNotBlank() }
        } catch (e: CancellationException) {
            if (!finalized) {
                updateAssistantMessage(assistantMessage.id, builder.toString(), false)
                finishTurn(persist = builder.isNotEmpty())
                finalized = true
            }
            throw e
        } catch (e: Exception) {
            if (!finalized) {
                val message = friendlyError(e)
                updateAssistantMessage(assistantMessage.id, if (builder.isNotEmpty()) builder.toString() else message, false, isError = builder.isEmpty())
                _state.value = _state.value.copy(isStreaming = false, errorMessage = message)
                finishTurn(persist = builder.isNotEmpty())
                finalized = true
            }
            return null
        } finally {
            if (streamClient === sc) streamClient = null
            if (!finalized && _state.value.isStreaming) _state.value = _state.value.copy(isStreaming = false)
        }
    }

    fun stopGeneration() {
        streamClient?.cancel()
        chatJob?.cancel()
        chatJob = null
        if (_state.value.voiceModeEnabled) stopVoiceMode()
    }

    private fun updateAssistantMessage(
        id: String,
        content: String,
        streaming: Boolean,
        isError: Boolean = false,
        switchedToUncensored: Boolean? = null,
        ragSources: List<String>? = null,
        skillUsed: String? = null,
        modelRouteLabel: String? = null,
        modelRouteReason: String? = null,
        cloudFallbackNotice: String? = null
    ) {
        val conversation = _state.value.current
        val updatedMessages = conversation.messages.map { message ->
            if (message.id == id) message.copy(
                content = content,
                isStreaming = streaming,
                isError = isError,
                switchedToUncensored = switchedToUncensored ?: message.switchedToUncensored,
                ragSources = ragSources ?: message.ragSources,
                skillUsed = skillUsed ?: message.skillUsed,
                modelRouteLabel = modelRouteLabel ?: message.modelRouteLabel,
                modelRouteReason = modelRouteReason ?: message.modelRouteReason,
                cloudFallbackNotice = cloudFallbackNotice ?: message.cloudFallbackNotice
            ) else message
        }
        _state.value = _state.value.copy(current = conversation.copy(messages = updatedMessages))
    }

    private fun finishTurn(persist: Boolean = true) {
        _state.value = _state.value.copy(isStreaming = false)
        if (persist) persistCurrentConversation()
        refreshConversationList()
    }

    private fun persistCurrentConversation() {
        val client = container.client.value ?: return
        val conversation = _state.value.current
        if (conversation.messages.isEmpty()) return
        val title = if (conversation.title == "New chat") deriveTitle(conversation) else conversation.title
        val toSave = conversation.copy(title = title)
        viewModelScope.launch {
            try {
                val documentJson = com.parux.android.core.network.PaRuXHttpClient.json.encodeToString(
                    com.parux.android.core.network.ConversationDoc.serializer(), toSave.toDoc())
                client.api.saveConversation(toSave.id, ConversationUpsertRequest(title, toSave.createdUtc, toSave.updatedUtc, documentJson))
            } catch (_: Exception) { }
        }
    }

    fun dismissError() { _state.value = _state.value.copy(errorMessage = null) }

    override fun onCleared() {
        voiceEngine.stopAll()
        voiceSpeechStreamClient?.cancel()
        voiceSpeechStreamClient = null
        streamClient?.cancel()
        voiceJob?.cancel()
        chatJob?.cancel()
        super.onCleared()
    }

    private fun prettyAutoProfile(profile: String?): String = when (profile) {
        "max_intelligence" -> "Max intelligence"
        "responsive" -> "Responsive"
        "custom" -> "Custom"
        else -> "Balanced"
    }

    private fun friendlyError(e: Exception): String = ChatErrors.describe(e)
}