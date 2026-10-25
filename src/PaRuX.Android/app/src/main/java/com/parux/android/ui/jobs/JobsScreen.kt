package com.parux.android.ui.jobs

import android.net.Uri
import android.util.Base64
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
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
import com.parux.android.core.network.MediaJobStatus

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun JobsScreen(viewModel: JobsViewModel) {
    val state by viewModel.state.collectAsState()
    val context = LocalContext.current
    var showCreateDialog by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { viewModel.startPolling(context) }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Jobs") }) },
        floatingActionButton = {
            FloatingActionButton(onClick = { showCreateDialog = true }) {
                Icon(Icons.Default.Add, contentDescription = "New generation job")
            }
        }
    ) { padding ->
        if (state.jobs.isEmpty()) {
            Column(
                Modifier.padding(padding).fillMaxSize().padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Text("No jobs yet", style = MaterialTheme.typography.titleMedium)
                Text(
                    "Tap + to generate an image or video. It shows up here with progress and ETA.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            LazyColumn(Modifier.padding(padding).fillMaxSize().padding(12.dp)) {
                items(state.jobs, key = { it.id }) { job -> JobRow(job, onCancel = { viewModel.cancelJob(job.id) }) }
            }
        }
    }

    if (showCreateDialog) {
        CreateJobDialog(
            onDismiss = { showCreateDialog = false },
            onCreate = { type, prompt, sourceImageBase64 ->
                viewModel.createJob(type, prompt, sourceImageBase64)
                showCreateDialog = false
            }
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CreateJobDialog(onDismiss: () -> Unit, onCreate: (String, String, String?) -> Unit) {
    var type by remember { mutableStateOf("image") }
    var prompt by remember { mutableStateOf("") }
    var sourceImageBase64 by remember { mutableStateOf<String?>(null) }
    var sourceImageName by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            try {
                val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
                if (bytes != null) {
                    sourceImageBase64 = Base64.encodeToString(bytes, Base64.NO_WRAP)
                    sourceImageName = "image"
                }
            } catch (_: Exception) { }
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Generate") },
        text = {
            Column {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(selected = type == "image", onClick = { type = "image" }, label = { Text("Image") })
                    FilterChip(selected = type == "video", onClick = { type = "video" }, label = { Text("Video") })
                    FilterChip(selected = type == "edit", onClick = { type = "edit" }, label = { Text("Edit") })
                }
                if (type == "edit") {
                    TextButton(onClick = { imagePicker.launch("image/*") }, modifier = Modifier.padding(top = 8.dp)) {
                        Text(if (sourceImageName != null) "Image attached ✓ (tap to change)" else "Pick an image to edit…")
                    }
                }
                OutlinedTextField(
                    value = prompt,
                    onValueChange = { prompt = it },
                    label = { Text(if (type == "edit") "Edit instruction" else "Prompt") },
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onCreate(type, prompt, sourceImageBase64) },
                enabled = prompt.isNotBlank() && (type != "edit" || sourceImageBase64 != null)
            ) { Text("Generate") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } }
    )
}

@Composable
private fun JobRow(job: MediaJobStatus, onCancel: () -> Unit) {
    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Column(Modifier.padding(12.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("${job.type.replaceFirstChar { it.uppercase() }} · ${job.stage}", style = MaterialTheme.typography.titleSmall)
                Text(job.status, style = MaterialTheme.typography.labelMedium)
            }
            if (job.status == "running" || job.status == "cancelling") {
                LinearProgressIndicator(
                    progress = { job.progressPercent / 100f },
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                )
                job.etaSeconds?.let {
                    Text("ETA ~${it / 60}m ${it % 60}s", style = MaterialTheme.typography.labelSmall)
                }
            }
            job.error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            if (job.status == "queued" || job.status == "running") {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onCancel) { Text("Cancel") }
                }
            }
        }
    }
}
