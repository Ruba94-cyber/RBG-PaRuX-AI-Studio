package com.parux.android.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.parux.android.BuildConfig

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(viewModel: SettingsViewModel, onForgetDevice: () -> Unit) {
    val state by viewModel.state.collectAsState()

    Scaffold(topBar = { TopAppBar(title = { Text("Settings") }) }) { padding ->
        Column(
            Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp)
        ) {
            SectionHeader("Connection")
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text("PaRuX PC")
                        Text(
                            if (state.serverReachable == true) "Connected" else if (state.serverReachable == false) "Offline" else "Checking…",
                            color = if (state.serverReachable == true) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
                        )
                    }
                    Text(state.endpoint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Divider(Modifier.padding(vertical = 12.dp))
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable(role = Role.Button) { viewModel.forgetDevice(); onForgetDevice() }
                            .padding(vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text("Forget this device", color = MaterialTheme.colorScheme.error)
                            Text(
                                "Removes the pairing locally. Revoke the device from PaRuX Windows too if you no longer trust it.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        Text(
                            "Forget",
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.labelLarge,
                            modifier = Modifier.padding(start = 12.dp)
                        )
                    }
                }
            }

            SectionHeader("Execution")
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    Text("Shared quality profile", style = MaterialTheme.typography.titleSmall)
                    Text(
                        "Uses the same Fast / Balanced / Maximum Quality / Overnight policy as PaRuX Windows. Low-level runtime tuning remains PC-owned.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 4.dp, bottom = 8.dp)
                    )
                    QualityProfileRow("Fast", "fast", state, viewModel)
                    QualityProfileRow("Balanced", "balanced", state, viewModel)
                    QualityProfileRow("Maximum Quality", "maximum-quality", state, viewModel)
                    QualityProfileRow("Overnight", "overnight", state, viewModel)
                    Divider(Modifier.padding(vertical = 10.dp))
                    StatusRow("Preferred context", formatTokens(state.preferredContextTokens))
                    StatusRow("Reasoning effort", state.reasoningEffort.toString())
                    if (state.executionEvidenceSource.isNotBlank())
                        StatusRow("Policy evidence", state.executionEvidenceSource)
                    state.executionProfileError?.let {
                        Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.padding(top = 8.dp))
                    }
                }
            }

            SectionHeader("Workstation health")
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    if (state.workstationMetrics.isEmpty()) {
                        Text("No V2 observability evidence yet.", style = MaterialTheme.typography.bodySmall)
                    } else {
                        state.workstationMetrics.take(6).forEach { metric ->
                            StatusRow(metric.label, metric.value)
                            metric.detail?.takeIf { it.isNotBlank() }?.let {
                                Text(it, style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.padding(bottom = 4.dp))
                            }
                        }
                    }
                    Divider(Modifier.padding(vertical = 8.dp))
                    StatusRow("Engine races", state.engineRaceSummary.total.toString())
                    StatusRow("Race winners", state.engineRaceSummary.completed.toString())
                }
            }

            SectionHeader("System")
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    StatusRow("Gaming Mode", if (state.gamingMode) "Active" else "Off")
                    StatusRow("Model runtime", if (state.providerReady) "Ready" else "Not loaded")
                }
            }

            SectionHeader("Advanced")
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    StatusRow("API surface", "v1 + safe v2")
                    StatusRow("App version", BuildConfig.VERSION_NAME)
                    StatusRow(
                        "Source revision",
                        if (BuildConfig.SOURCE_COMMIT == "development") "development build"
                        else BuildConfig.SOURCE_COMMIT.take(12)
                    )
                }
            }
        }
    }
}

@Composable
private fun QualityProfileRow(
    label: String,
    value: String,
    state: SettingsUiState,
    viewModel: SettingsViewModel
) {
    val selected = state.executionQualityProfile == value
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(
                enabled = state.serverReachable == true && !state.executionProfileChanging,
                role = Role.Button
            ) { viewModel.setExecutionQuality(value) }
            .padding(vertical = 9.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
        Text(
            if (selected) "✓ Active" else if (state.executionProfileChanging) "…" else "Select",
            style = MaterialTheme.typography.labelMedium,
            color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun SectionHeader(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 16.dp, bottom = 8.dp)
    )
}

@Composable
private fun StatusRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value)
    }
}

private fun formatTokens(tokens: Int): String = when {
    tokens >= 1_000_000 -> String.format("%.2fM", tokens / 1_000_000.0)
    tokens >= 1_000 -> String.format("%.0fk", tokens / 1_000.0)
    tokens > 0 -> tokens.toString()
    else -> "Auto"
}