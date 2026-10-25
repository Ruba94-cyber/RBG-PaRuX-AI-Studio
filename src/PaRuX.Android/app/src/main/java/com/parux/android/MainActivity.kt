package com.parux.android

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Chat
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.PermMedia
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Task
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.parux.android.core.AppContainer
import com.parux.android.ui.PaRuXViewModelFactory
import com.parux.android.ui.chat.ChatScreen
import com.parux.android.ui.chat.ChatViewModel
import com.parux.android.ui.goals.GoalsScreen
import com.parux.android.ui.goals.GoalsViewModel
import com.parux.android.ui.jobs.JobsScreen
import com.parux.android.ui.jobs.JobsViewModel
import com.parux.android.ui.media.MediaScreen
import com.parux.android.ui.pairing.PairingScreen
import com.parux.android.ui.settings.SettingsScreen
import com.parux.android.ui.settings.SettingsViewModel
import com.parux.android.ui.studio.PcStudioScreen
import com.parux.android.ui.studio.PcStudioViewModel
import com.parux.android.ui.theme.PaRuXTheme
import com.parux.android.ui.tools.ToolsScreen
import com.parux.android.ui.tools.ToolsViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

private enum class PaRuXTab(val label: String) {
    Chat("Chat"), PC("PC"), Goals("Goals"), Media("Media"), Jobs("Jobs"), Tools("Tools"), Settings("Settings")
}

class MainActivity : ComponentActivity() {
    private val openPcRequests = MutableStateFlow(0L)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val container = (application as PaRuXApplication).container
        handleNavigationIntent(intent)

        // Debug-only automation hook: `adb shell am start ... -e pair '<qr json>'` completes
        // pairing without a camera or manual typing (the emulator's `input text` cannot reliably
        // inject '{', '"', etc.), used only to script end-to-end tests during development.
        if (BuildConfig.DEBUG) {
            intent?.getStringExtra("pair_b64")?.let { encoded ->
                val raw = String(android.util.Base64.decode(encoded, android.util.Base64.URL_SAFE))
                com.parux.android.core.pairing.PairingPayload.parse(raw)?.let { payload ->
                    lifecycleScope.launch {
                        container.pairingRepository.completePairing(payload)
                        container.refreshClient()
                    }
                }
            }
        }

        setContent {
            PaRuXTheme {
                Surface(color = MaterialTheme.colorScheme.background) {
                    PaRuXApp(container, openPcRequests)
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleNavigationIntent(intent)
    }

    private fun handleNavigationIntent(intent: Intent?) {
        if (intent?.getBooleanExtra(EXTRA_OPEN_PC_STUDIO, false) == true)
            openPcRequests.value = openPcRequests.value + 1
    }

    companion object {
        const val EXTRA_OPEN_PC_STUDIO = "com.parux.android.OPEN_PC_STUDIO"
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PaRuXApp(container: AppContainer, openPcRequests: StateFlow<Long>) {
    val client by container.client.collectAsState()
    val context = LocalContext.current
    val openPcRequest by openPcRequests.collectAsState()
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }

    LaunchedEffect(client != null) {
        if (client != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            val prefs = context.getSharedPreferences("parux_ui", Context.MODE_PRIVATE)
            if (!prefs.getBoolean("notification_permission_prompted", false)) {
                prefs.edit().putBoolean("notification_permission_prompted", true).apply()
                notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    if (client == null) {
        PairingScreen(pairingRepository = container.pairingRepository) {
            container.refreshClient()
        }
        return
    }

    val factory = remember { PaRuXViewModelFactory(container) }
    val chatViewModel: ChatViewModel = viewModel(factory = factory)
    val goalsViewModel: GoalsViewModel = viewModel(factory = factory)
    val jobsViewModel: JobsViewModel = viewModel(factory = factory)
    val settingsViewModel: SettingsViewModel = viewModel(factory = factory)
    val toolsViewModel: ToolsViewModel = viewModel(factory = factory)
    val pcStudioViewModel: PcStudioViewModel = viewModel(factory = factory)

    var tab by remember { mutableStateOf(PaRuXTab.Chat) }
    LaunchedEffect(openPcRequest) {
        if (openPcRequest > 0) tab = PaRuXTab.PC
    }

    Scaffold(
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    selected = tab == PaRuXTab.Chat,
                    onClick = { tab = PaRuXTab.Chat },
                    icon = { Icon(Icons.Default.Chat, contentDescription = null) },
                    label = { Text(PaRuXTab.Chat.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.PC,
                    onClick = { tab = PaRuXTab.PC },
                    icon = { Icon(Icons.Default.Computer, contentDescription = null) },
                    label = { Text(PaRuXTab.PC.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.Goals,
                    onClick = { tab = PaRuXTab.Goals },
                    icon = { Icon(Icons.Default.Flag, contentDescription = null) },
                    label = { Text(PaRuXTab.Goals.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.Media,
                    onClick = { tab = PaRuXTab.Media },
                    icon = { Icon(Icons.Default.PermMedia, contentDescription = null) },
                    label = { Text(PaRuXTab.Media.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.Jobs,
                    onClick = { tab = PaRuXTab.Jobs },
                    icon = { Icon(Icons.Default.Task, contentDescription = null) },
                    label = { Text(PaRuXTab.Jobs.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.Tools,
                    onClick = { tab = PaRuXTab.Tools },
                    icon = { Icon(Icons.Default.Build, contentDescription = null) },
                    label = { Text(PaRuXTab.Tools.label) }
                )
                NavigationBarItem(
                    selected = tab == PaRuXTab.Settings,
                    onClick = { tab = PaRuXTab.Settings },
                    icon = { Icon(Icons.Default.Settings, contentDescription = null) },
                    label = { Text(PaRuXTab.Settings.label) }
                )
            }
        }
    ) { padding ->
        Surface(Modifier.padding(padding)) {
            when (tab) {
                PaRuXTab.Chat -> ChatScreen(chatViewModel)
                PaRuXTab.PC -> PcStudioScreen(pcStudioViewModel)
                PaRuXTab.Goals -> GoalsScreen(goalsViewModel)
                PaRuXTab.Media -> MediaScreen(jobsViewModel, container)
                PaRuXTab.Jobs -> JobsScreen(jobsViewModel)
                PaRuXTab.Tools -> ToolsScreen(toolsViewModel)
                PaRuXTab.Settings -> SettingsScreen(settingsViewModel) { }
            }
        }
    }
}
