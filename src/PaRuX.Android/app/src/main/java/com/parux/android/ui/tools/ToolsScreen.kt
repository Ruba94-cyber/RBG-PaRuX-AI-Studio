package com.parux.android.ui.tools

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.util.Base64
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.parux.android.core.network.MemoryEntryDto
import com.parux.android.core.network.RagSourceDto
import com.parux.android.core.network.SkillManifest
import java.io.IOException

/**
 * Mobile control plane for Core-owned capabilities. Workstation runtime installation, raw Windows
 * paths, manual model benchmarks, Training Lab job authoring and GUI input execution remain on the
 * paired Windows workstation. Android only requests/supervises durable Core-owned work.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ToolsScreen(viewModel: ToolsViewModel) {
    val state by viewModel.state.collectAsState()
    LaunchedEffect(Unit) { viewModel.refreshAll() }

    Scaffold(topBar = { TopAppBar(title = { Text("Tools") }) }) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxSize(), contentPadding = PaddingValues(12.dp)) {
            item { InternetSection(state.internetEnabled, viewModel::setInternet) }
            item { MemorySection(state.memory, viewModel::addMemory, viewModel::deleteMemory) }
            item { RagSection(state.ragSources, viewModel::reindexRag, viewModel::removeRag) }
            item { SkillsSection(state.skills, viewModel::setSkillEnabled, viewModel::deleteSkill, viewModel::createSkill) }
            item {
                ResearchSection(
                    loading = state.researchLoading,
                    result = state.researchAnswer,
                    internetEnabled = state.internetEnabled,
                    onAsk = viewModel::runResearch
                )
            }
            item {
                PresentationSection(
                    loading = state.presentationLoading,
                    result = state.presentation,
                    onGenerate = viewModel::generatePresentation
                )
            }
            item {
                SpeechSection(
                    engineStatus = state.speechEngine,
                    ttsResult = state.ttsResult,
                    ttsAudioBase64 = state.ttsAudioBase64,
                    sttResult = state.sttResult,
                    sttLoading = state.sttLoading,
                    onSpeak = viewModel::speak,
                    onTranscribe = viewModel::transcribe
                )
            }
            item {
                ModelManagerSection(
                    budget = state.modelBudget,
                    results = state.modelResults,
                    loading = state.modelSearchLoading,
                    downloads = state.modelDownloads,
                    onSearch = viewModel::searchModels,
                    onDownload = viewModel::startModelDownload
                )
            }
            item { TrainingLabSection() }
            item { AvatarSection(status = state.avatarStatus, loading = state.avatarLoading, onGenerate = viewModel::generateAvatar) }
            item {
                ComputerUseSection(
                    running = state.computerUseRunning,
                    log = state.computerUseLog,
                    summary = state.computerUseSummary,
                    onRun = viewModel::runComputerUse,
                    onStop = viewModel::stopComputerUse
                )
            }
            item {
                ThreedSection(
                    engine = state.threedEngine,
                    generating = state.threedGenerating,
                    resultBase64 = state.threedResultBase64,
                    resultInfo = state.threedResultInfo,
                    onGenerate = viewModel::generateThreed
                )
            }
            item {
                AgentsSection(
                    tasks = state.agentTasks,
                    autopilotRun = state.autopilotRun,
                    autopilotStatusMessage = state.autopilotStatusMessage,
                    stopAllMessage = state.stopAllMessage,
                    onRefreshTasks = viewModel::refreshAgentTasks,
                    onCancelTask = viewModel::cancelAgentTask,
                    onStartAutopilotRun = viewModel::startAutopilotRun,
                    onCheckAutopilotRun = viewModel::checkAutopilotRun,
                    onApproveAutopilotRun = viewModel::approveAutopilotRun,
                    onStopAllAgents = viewModel::stopAllAgents,
                    onResumeAutomations = viewModel::resumeAutomations
                )
            }
            item {
                AutomationsSection(
                    automations = state.automations,
                    onRefresh = viewModel::refreshAutomations,
                    onSetEnabled = viewModel::setAutomationEnabled,
                    onDelete = viewModel::deleteAutomation
                )
            }
            item {
                ContextGraphSection(
                    entities = state.contextEntities,
                    neighborhood = state.contextNeighborhood,
                    onRefresh = viewModel::refreshContextEntities,
                    onViewNeighborhood = viewModel::viewContextNeighborhood
                )
            }

            state.errorMessage?.let { message ->
                item {
                    Surface(color = MaterialTheme.colorScheme.errorContainer, modifier = Modifier.fillMaxWidth()) {
                        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(message, color = MaterialTheme.colorScheme.onErrorContainer, modifier = Modifier.weight(1f))
                            TextButton(onClick = viewModel::dismissError) { Text("Dismiss") }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SectionCard(title: String, caption: String? = null, initiallyExpanded: Boolean = false, content: @Composable () -> Unit) {
    var expanded by remember { mutableStateOf(initiallyExpanded) }
    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Column(Modifier.padding(16.dp)) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                IconButton(onClick = { expanded = !expanded }) {
                    Icon(if (expanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore, contentDescription = null)
                }
            }
            caption?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            if (expanded) Column(Modifier.padding(top = 8.dp)) { content() }
        }
    }
}

@Composable
private fun InternetSection(enabled: Boolean, onToggle: (Boolean) -> Unit) {
    SectionCard("Internet", "Off by default. Required only for explicit Internet features such as Research/model discovery.") {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text(if (enabled) "On" else "Off")
            Switch(checked = enabled, onCheckedChange = onToggle)
        }
    }
}

@Composable
private fun MemorySection(memory: List<MemoryEntryDto>, onAdd: (String) -> Unit, onDelete: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    SectionCard("Memory", "${memory.size} entries. Core-owned - same store Windows uses.") {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = text, onValueChange = { text = it }, modifier = Modifier.weight(1f), placeholder = { Text("Add a memory…") })
            TextButton(onClick = { onAdd(text); text = "" }, enabled = text.isNotBlank()) { Text("Add") }
        }
        memory.forEach { entry ->
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text(entry.content, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                TextButton(onClick = { onDelete(entry.id) }) { Text("Delete") }
            }
        }
    }
}

@Composable
private fun RagSection(sources: List<RagSourceDto>, onReindex: (String) -> Unit, onRemove: (String) -> Unit) {
    SectionCard("RAG (local documents)", "Folders are selected on PaRuX Windows; Android manages only existing Core-owned source ids.") {
        if (sources.isEmpty()) Text("No folders indexed yet.", style = MaterialTheme.typography.bodySmall)
        sources.forEach { source ->
            Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
                Text(source.folderPath, style = MaterialTheme.typography.bodyMedium)
                Text("${source.documentCount} documents · ${source.chunkCount} chunks", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row {
                    TextButton(onClick = { onReindex(source.id) }) { Text("Reindex") }
                    TextButton(onClick = { onRemove(source.id) }) { Text("Remove") }
                }
            }
        }
    }
}

@Composable
private fun SkillsSection(
    skills: List<SkillManifest>,
    onSetEnabled: (String, Boolean) -> Unit,
    onDelete: (String) -> Unit,
    onCreate: (String, String, String, String, Boolean) -> Unit
) {
    var showCreate by remember { mutableStateOf(false) }
    SectionCard("Skills", "${skills.size} skills. Mobile-created skills are prompt/routing-only and Core strips privileged tools/permissions.") {
        TextButton(onClick = { showCreate = !showCreate }) { Text(if (showCreate) "Cancel" else "New safe skill…") }
        if (showCreate) {
            var name by remember { mutableStateOf("") }
            var description by remember { mutableStateOf("") }
            var slash by remember { mutableStateOf("") }
            var internetRequired by remember { mutableStateOf(false) }
            OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Display name") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(value = description, onValueChange = { description = it }, label = { Text("Description") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(value = slash, onValueChange = { slash = it }, label = { Text("Slash command (optional)") }, modifier = Modifier.fillMaxWidth())
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Text("Requires Internet")
                Switch(checked = internetRequired, onCheckedChange = { internetRequired = it })
            }
            Text("Filesystem, shell, browser automation, Computer Use and external-process permissions can only be authored on Windows.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Button(onClick = {
                onCreate(name, description, slash, "MAIN_LLM", internetRequired)
                showCreate = false; name = ""; description = ""; slash = ""; internetRequired = false
            }, enabled = name.isNotBlank()) { Text("Create") }
        }
        skills.sortedBy { it.displayName }.forEach { skill ->
            var expanded by remember(skill.id) { mutableStateOf(false) }
            Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f).padding(end = 8.dp)) {
                        Text(skill.displayName + if (skill.slashCommand.isNotEmpty()) "  ${skill.slashCommand}" else "", style = MaterialTheme.typography.bodyMedium)
                        Text(skill.description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(checked = skill.enabled, onCheckedChange = { onSetEnabled(skill.id, it) })
                }
                Row {
                    TextButton(onClick = { expanded = !expanded }) { Text(if (expanded) "Hide" else "Details") }
                    if (skill.source == "user" || skill.source == "imported") TextButton(onClick = { onDelete(skill.id) }) { Text("Delete") }
                }
                if (expanded) {
                    Column {
                        Text("Source: ${skill.source}" + if (skill.license.isNotEmpty()) " · ${skill.license}" else "", style = MaterialTheme.typography.bodySmall)
                        Text("Platforms: ${skill.platforms.joinToString(", ")}", style = MaterialTheme.typography.bodySmall)
                        Text("Model role: ${skill.preferredModelRole}", style = MaterialTheme.typography.bodySmall)
                        Text("Tools: " + if (skill.allowedTools.isEmpty()) "none" else skill.allowedTools.joinToString(", "), style = MaterialTheme.typography.bodySmall)
                        Text("Internet/Memory/RAG: ${skill.internetPolicy}/${skill.memoryPolicy}/${skill.ragPolicy}", style = MaterialTheme.typography.bodySmall)
                        Text(
                            "Permissions: read=${skill.permissions.readFiles} write=${skill.permissions.writeFiles} shell=${skill.permissions.shell} computerUse=${skill.permissions.computerUse}",
                            style = MaterialTheme.typography.bodySmall
                        )
                        if (skill.workflow.isNotEmpty()) Text("Workflow: " + skill.workflow.joinToString(" → "), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ResearchSection(loading: Boolean, result: com.parux.android.core.network.ResearchResult?, internetEnabled: Boolean, onAsk: (String, String) -> Unit) {
    var question by remember { mutableStateOf("") }
    var mode by remember { mutableStateOf("balanced") }
    var modeMenu by remember { mutableStateOf(false) }
    SectionCard("Research", "Explicit Internet feature. Search is blocked while Internet is off." + if (!internetEnabled) " Enable Internet above to use it." else "") {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { modeMenu = true }) { Text("Mode: $mode") }
            DropdownMenu(expanded = modeMenu, onDismissRequest = { modeMenu = false }) {
                listOf("fast", "balanced", "deep").forEach { m ->
                    DropdownMenuItem(text = { Text(m) }, onClick = { mode = m; modeMenu = false })
                }
            }
        }
        OutlinedTextField(value = question, onValueChange = { question = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Ask a research question…") })
        Button(onClick = { onAsk(question, mode) }, enabled = internetEnabled && question.isNotBlank() && !loading) { Text("Ask") }
        if (loading) CircularProgressIndicator(Modifier.padding(top = 8.dp))
        result?.let { r ->
            Text(r.answer, Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodyMedium)
            r.sources.forEach { source -> Text("• ${source.title} (${source.domain})", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary) }
        }
    }
}

@Composable
private fun PresentationSection(loading: Boolean, result: com.parux.android.core.network.PresentationResponse?, onGenerate: (String, Int) -> Unit) {
    var topic by remember { mutableStateOf("") }
    var slideCount by remember { mutableStateOf("6") }
    SectionCard("Presentations", "Local-LLM outline + native PPTX generation on the paired PC.") {
        OutlinedTextField(value = topic, onValueChange = { topic = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Topic…") })
        OutlinedTextField(value = slideCount, onValueChange = { slideCount = it.filter(Char::isDigit) }, label = { Text("Slides") })
        Button(onClick = { onGenerate(topic, slideCount.toIntOrNull() ?: 6) }, enabled = topic.isNotBlank() && !loading) { Text("Generate") }
        if (loading) CircularProgressIndicator(Modifier.padding(top = 8.dp))
        result?.let { r ->
            Text("${r.fileName} · ${r.slides.size} slides", Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall)
            r.slides.forEach { slide ->
                Text(slide.title, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 4.dp))
                slide.bullets.forEach { Text("• $it", style = MaterialTheme.typography.bodySmall) }
            }
        }
    }
}

@Composable
private fun SpeechSection(
    engineStatus: com.parux.android.core.network.SpeechEngineStatus,
    ttsResult: String?,
    ttsAudioBase64: String?,
    sttResult: String?,
    sttLoading: Boolean,
    onSpeak: (String) -> Unit,
    onTranscribe: (String, String) -> Unit
) {
    var text by remember { mutableStateOf("") }
    val context = LocalContext.current
    val audioPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) uriToBase64(context.contentResolver, uri)?.let { (base64, name) -> onTranscribe(base64, name) }
    }
    LaunchedEffect(ttsAudioBase64) {
        val base64 = ttsAudioBase64 ?: return@LaunchedEffect
        try {
            val bytes = Base64.decode(base64, Base64.NO_WRAP)
            val file = java.io.File.createTempFile("parux-tts-", ".wav", context.cacheDir)
            file.writeBytes(bytes)
            android.media.MediaPlayer().apply {
                setDataSource(file.absolutePath)
                setOnCompletionListener { release(); file.delete() }
                prepare()
                start()
            }
        } catch (_: Exception) { }
    }
    SectionCard("Speech", "STT: ${if (engineStatus.sttInstalled) "installed" else "not installed"} · TTS: ${if (engineStatus.ttsInstalled) "installed" else "not installed"}") {
        Text("Text to speech", style = MaterialTheme.typography.titleSmall)
        OutlinedTextField(value = text, onValueChange = { text = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Text to speak…") })
        Button(onClick = { onSpeak(text) }, enabled = text.isNotBlank()) { Text("Speak") }
        ttsResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }

        Text("Speech to text", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 12.dp))
        Button(onClick = { audioPicker.launch("audio/*") }, enabled = !sttLoading) { Text("Pick audio to transcribe…") }
        if (sttLoading) CircularProgressIndicator(Modifier.padding(top = 8.dp))
        sttResult?.let { Text(it, Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodyMedium) }
    }
}

@Composable
private fun ModelManagerSection(
    budget: com.parux.android.core.network.AiStorageBudgetStatus?,
    results: List<com.parux.android.core.network.ModelSearchResult>,
    loading: Boolean,
    downloads: List<com.parux.android.core.network.ModelDownloadStatus>,
    onSearch: (String) -> Unit,
    onDownload: (String, String, String, Long) -> Unit
) {
    var query by remember { mutableStateOf("") }
    SectionCard("Model Manager", budget?.let { "Downloaded: ${formatBytes(it.usedBytes)} / ${formatBytes(it.limitBytes)}" } ?: "Search/download GGUF through Core.") {
        Text("Manual benchmarks and raw model paths are desktop-only. New managed GGUF downloads are benchmarked by Core automatically.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = query, onValueChange = { query = it }, modifier = Modifier.weight(1f), placeholder = { Text("Search GGUF models…") })
            TextButton(onClick = { onSearch(query) }, enabled = query.isNotBlank() && !loading) { Text("Search") }
        }
        if (loading) CircularProgressIndicator(Modifier.padding(top = 8.dp))
        results.forEach { repo ->
            Text(repo.repoId, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 8.dp))
            repo.ggufFiles.forEach { file ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    Text("${file.fileName} (${formatBytes(file.sizeBytes)})", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                    TextButton(onClick = { onDownload(repo.repoId, file.fileName, file.downloadUrl, file.sizeBytes) }) { Text("Download") }
                }
            }
        }
        downloads.forEach { d ->
            val percent = if (d.totalBytes > 0) (100f * d.bytesDownloaded / d.totalBytes) else 0f
            Column(Modifier.padding(top = 6.dp)) {
                Text("${d.fileName} · ${d.status}", style = MaterialTheme.typography.bodySmall)
                LinearProgressIndicator(progress = { percent / 100f }, modifier = Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun TrainingLabSection() {
    SectionCard(
        "Training Lab",
        "Dataset paths, engine installation and fine-tuning job authoring are intentionally Windows-only."
    ) {
        Text(
            "Prepare/export the dataset and start SFT/DPO from PaRuX Windows. Android never sends a workstation filesystem path and can supervise the resulting Core tasks through PC Studio.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun AvatarSection(status: com.parux.android.core.network.AvatarProviderStatus, loading: Boolean, onGenerate: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    SectionCard(
        "Avatar Studio",
        if (status.configured && status.reachable) "Provider: ${status.providerName} · reachable"
        else if (status.configured) "Provider ${status.providerName} configured but not reachable"
        else "No local avatar provider configured."
    ) {
        OutlinedTextField(value = text, onValueChange = { text = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Text for the avatar to say…") }, enabled = status.configured)
        Button(onClick = { onGenerate(text) }, enabled = status.configured && text.isNotBlank() && !loading) { Text("Generate") }
        if (loading) CircularProgressIndicator(Modifier.padding(top = 8.dp))
    }
}

@Composable
private fun ComputerUseSection(
    running: Boolean,
    log: List<String>,
    summary: String?,
    onRun: (String) -> Unit,
    onStop: () -> Unit
) {
    var goal by remember { mutableStateOf("") }
    SectionCard(
        "Computer Use (paired Windows PC)",
        "Creates a durable Core task. Execution stays on Windows and GUI control still requires task-scoped Approve once; Android has no Accessibility or raw-input privilege."
    ) {
        OutlinedTextField(
            value = goal,
            onValueChange = { goal = it },
            modifier = Modifier.fillMaxWidth(),
            placeholder = { Text("Goal, e.g. open Windows Settings and check the display configuration") },
            enabled = !running
        )
        Row(Modifier.padding(top = 8.dp)) {
            if (running) Button(onClick = onStop) { Text("Cancel task") }
            else Button(onClick = { onRun(goal) }, enabled = goal.isNotBlank()) { Text("Request on PC") }
            if (running) CircularProgressIndicator(Modifier.padding(start = 12.dp).size(20.dp))
        }
        summary?.let {
            Text(it, modifier = Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodyMedium)
        }
        log.takeLast(6).forEach { line ->
            Text("• $line", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ThreedSection(
    engine: com.parux.android.core.network.ThreedEngineStatus,
    generating: Boolean,
    resultBase64: String?,
    resultInfo: String?,
    onGenerate: (String, String) -> Unit
) {
    val context = LocalContext.current
    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) uriToBase64(context.contentResolver, uri)?.let { (base64, _) -> onGenerate(base64, "source.png") }
    }
    val saveModel = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("model/gltf-binary")) { uri: Uri? ->
        if (uri != null && resultBase64 != null) saveGlb(context, uri, resultBase64)
    }
    SectionCard(
        "3D Studio",
        (if (engine.installed) "Engine installed" + (engine.torchVersion?.let { " · torch $it" } ?: "")
        else engine.lastError?.let { "Engine unavailable: $it" } ?: "Engine not installed on the PC.") +
            "\nEngine installation is desktop-only. Generation stays local through Core."
    ) {
        if (!engine.installed) {
            Text("Install/configure the 3D engine from PaRuX Windows, then refresh this screen.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        } else {
            Button(onClick = { imagePicker.launch("image/*") }, enabled = !generating) { Text("Pick image → generate 3D model") }
            if (generating) {
                CircularProgressIndicator(Modifier.padding(top = 8.dp))
                Text("Generating locally on the paired PC…", style = MaterialTheme.typography.bodySmall)
            }
            resultInfo?.let { Text(it, Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall) }
            resultBase64?.let {
                Text("GLB generated locally. Preview does not load external JavaScript; save it to open with an installed 3D viewer.", Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall)
                Button(
                    onClick = { saveModel.launch("parux-model-${System.currentTimeMillis()}.glb") },
                    modifier = Modifier.padding(top = 8.dp)
                ) { Text("Save .glb") }
            }
        }
    }
}

@Composable
private fun AgentsSection(
    tasks: List<com.parux.android.core.network.AgentTaskDto>,
    autopilotRun: com.parux.android.core.network.AutopilotRunDto?,
    autopilotStatusMessage: String?,
    stopAllMessage: String?,
    onRefreshTasks: () -> Unit,
    onCancelTask: (String) -> Unit,
    onStartAutopilotRun: (String, String) -> Unit,
    onCheckAutopilotRun: (String) -> Unit,
    onApproveAutopilotRun: (String) -> Unit,
    onStopAllAgents: () -> Unit,
    onResumeAutomations: () -> Unit
) {
    var autopilotGoal by remember { mutableStateOf("") }
    var permissionMode by remember { mutableStateOf("supervised") }
    var runIdInput by remember { mutableStateOf("") }
    var modeMenuExpanded by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { onRefreshTasks() }

    SectionCard(
        "Multi-Agent Tasks & Autopilot",
        "Core-owned task graphs only. Windows filesystem/shell work stays inside the configured coding worker; GUI control remains approval-gated."
    ) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text("${tasks.size} task(s)", style = MaterialTheme.typography.bodyMedium)
            TextButton(onClick = onRefreshTasks) { Text("Refresh") }
        }
        tasks.take(8).forEach { task ->
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(task.goal, style = MaterialTheme.typography.bodySmall, maxLines = 2)
                    Text((task.role ?: "no role") + " · " + task.status.uppercase(), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (task.status !in listOf("completed", "failed", "cancelled")) TextButton(onClick = { onCancelTask(task.id) }) { Text("Cancel") }
            }
        }

        Text("Autopilot", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 12.dp))
        OutlinedTextField(value = autopilotGoal, onValueChange = { autopilotGoal = it }, modifier = Modifier.fillMaxWidth(), placeholder = { Text("Goal for Autopilot") })
        Row(Modifier.padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box {
                TextButton(onClick = { modeMenuExpanded = true }) { Text("Mode: $permissionMode") }
                DropdownMenu(expanded = modeMenuExpanded, onDismissRequest = { modeMenuExpanded = false }) {
                    listOf("supervised", "balanced", "autonomous").forEach { mode ->
                        DropdownMenuItem(text = { Text(mode) }, onClick = { permissionMode = mode; modeMenuExpanded = false })
                    }
                }
            }
            Button(onClick = { onStartAutopilotRun(autopilotGoal, permissionMode) }, enabled = autopilotGoal.isNotBlank(), modifier = Modifier.padding(start = 8.dp)) { Text("Start run") }
        }
        OutlinedTextField(value = runIdInput, onValueChange = { runIdInput = it }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp), placeholder = { Text("Run id") })
        Row(Modifier.padding(top = 4.dp)) {
            TextButton(onClick = { onCheckAutopilotRun(runIdInput) }, enabled = runIdInput.isNotBlank()) { Text("Check status") }
            TextButton(onClick = { onApproveAutopilotRun(runIdInput) }, enabled = runIdInput.isNotBlank()) { Text("Approve") }
        }
        autopilotStatusMessage?.let { Text(it, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.bodySmall) }
        autopilotRun?.finalReportJson?.let { Text("Report: $it", Modifier.padding(top = 4.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }

        Text("Universal STOP", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 12.dp))
        Text("Cancels in-flight tasks and active Autopilot runs and pauses existing Automations without deleting their definitions.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Row(Modifier.padding(top = 4.dp)) {
            Button(onClick = onStopAllAgents, colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.errorContainer)) {
                Text("STOP ALL AGENTS", color = MaterialTheme.colorScheme.onErrorContainer)
            }
            TextButton(onClick = onResumeAutomations, modifier = Modifier.padding(start = 8.dp)) { Text("Resume automations") }
        }
        stopAllMessage?.let { Text(it, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.bodySmall) }
    }
}

@Composable
private fun AutomationsSection(
    automations: List<com.parux.android.core.network.AutomationDto>,
    onRefresh: () -> Unit,
    onSetEnabled: (String, Boolean) -> Unit,
    onDelete: (String) -> Unit
) {
    LaunchedEffect(Unit) { onRefresh() }
    SectionCard("Automations", "Supervise existing Core-owned automations. Trigger/action authoring stays Windows-only.") {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text("${automations.size} automation(s)", style = MaterialTheme.typography.bodyMedium)
            TextButton(onClick = onRefresh) { Text("Refresh") }
        }
        automations.forEach { automation ->
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(automation.name, style = MaterialTheme.typography.bodySmall)
                    Text(automation.triggerType + " → " + automation.actionType + (automation.lastResult?.let { " · $it" } ?: ""), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2)
                }
                Switch(checked = automation.enabled, onCheckedChange = { onSetEnabled(automation.id, it) })
                TextButton(onClick = { onDelete(automation.id) }) { Text("Delete") }
            }
        }
    }
}

@Composable
private fun ContextGraphSection(
    entities: List<com.parux.android.core.network.ContextEntityDto>,
    neighborhood: com.parux.android.core.network.ContextNeighborhoodResponse?,
    onRefresh: (String?) -> Unit,
    onViewNeighborhood: (String) -> Unit
) {
    var typeFilter by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { onRefresh(null) }
    SectionCard("Context Graph Inspector", "Read-only on Android. Manual entity/relation authoring is desktop-only.") {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = typeFilter, onValueChange = { typeFilter = it }, modifier = Modifier.weight(1f), placeholder = { Text("Filter by type (optional)") })
            TextButton(onClick = { onRefresh(typeFilter.ifBlank { null }) }, modifier = Modifier.padding(start = 8.dp)) { Text("Refresh") }
        }
        Text("${entities.size} entities", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 4.dp))
        entities.take(15).forEach { entity ->
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(entity.name, style = MaterialTheme.typography.bodySmall)
                    Text(entity.entityType, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                TextButton(onClick = { onViewNeighborhood(entity.id) }) { Text("Neighborhood") }
            }
        }
        neighborhood?.let { n ->
            Text("Neighborhood", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp))
            Text("${n.center.name} (${n.center.entityType}) · ${n.outgoingRelations.size} outgoing · ${n.incomingRelations.size} incoming · ${n.neighborEntities.size} neighbor(s)", style = MaterialTheme.typography.bodySmall)
            n.outgoingRelations.forEach { Text("  → ${it.relationType} → ${it.toEntityId}", style = MaterialTheme.typography.labelSmall) }
            n.incomingRelations.forEach { Text("  ← ${it.relationType} ← ${it.fromEntityId}", style = MaterialTheme.typography.labelSmall) }
        }
    }
}

private fun saveGlb(context: Context, uri: Uri, glbBase64: String) {
    try {
        val bytes = Base64.decode(glbBase64, Base64.NO_WRAP)
        val output = context.contentResolver.openOutputStream(uri)
            ?: throw IOException("The selected destination could not be opened.")
        output.use { it.write(bytes) }
        Toast.makeText(context, "3D model saved", Toast.LENGTH_SHORT).show()
    } catch (e: Exception) {
        Toast.makeText(context, "Couldn't save: ${e.message}", Toast.LENGTH_SHORT).show()
    }
}

private fun uriToBase64(resolver: ContentResolver, uri: Uri): Pair<String, String>? = try {
    val bytes = resolver.openInputStream(uri)?.use { it.readBytes() } ?: return null
    val mimeType = resolver.getType(uri) ?: "audio/wav"
    Base64.encodeToString(bytes, Base64.NO_WRAP) to "audio.${mimeType.substringAfterLast('/')}"
} catch (_: Exception) {
    null
}

private fun formatBytes(bytes: Long): String {
    val gb = bytes / 1024.0 / 1024.0 / 1024.0
    return if (gb >= 1) "%.1f GB".format(gb) else "%.0f MB".format(bytes / 1024.0 / 1024.0)
}
