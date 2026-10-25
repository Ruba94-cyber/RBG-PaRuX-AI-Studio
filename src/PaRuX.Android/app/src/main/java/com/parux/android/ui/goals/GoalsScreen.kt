package com.parux.android.ui.goals

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GoalsScreen(viewModel: GoalsViewModel) {
    val state by viewModel.state.collectAsState()
    var goal by remember { mutableStateOf("") }
    var criteria by remember { mutableStateOf("") }
    var role by remember { mutableStateOf("coding") }

    Scaffold(topBar = { TopAppBar(title = { Text("Goal Mode") }) }) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxSize().padding(horizontal = 16.dp)) {
            if (state.loading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }

            item {
                Text(
                    "Define done before PaRuX starts",
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(top = 14.dp)
                )
                Text(
                    "Android creates the same immutable/versioned Goal Mode V2 specification used by Windows. The task still executes on the Windows workstation and Android cannot grant itself extra capabilities.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp, bottom = 12.dp)
                )
                OutlinedTextField(
                    value = goal,
                    onValueChange = { goal = it },
                    label = { Text("Goal") },
                    minLines = 2,
                    modifier = Modifier.fillMaxWidth()
                )
                OutlinedTextField(
                    value = criteria,
                    onValueChange = { criteria = it },
                    label = { Text("Acceptance criteria · one per line") },
                    minLines = 4,
                    modifier = Modifier.fillMaxWidth().padding(top = 10.dp)
                )
                Text("Windows worker", style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 12.dp))
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 6.dp)) {
                    items(
                        listOf(
                            "coding" to "Code",
                            "planner" to "Plan",
                            "research" to "Research",
                            "review" to "Review",
                            "computer-use" to "PC control"
                        )
                    ) { option ->
                        FilterChip(
                            selected = role == option.first,
                            onClick = { role = option.first },
                            label = { Text(option.second) }
                        )
                    }
                }
                Button(
                    onClick = {
                        viewModel.createAndRun(goal, criteria, role)
                        goal = ""
                        criteria = ""
                    },
                    enabled = !state.loading && goal.isNotBlank() && criteria.isNotBlank(),
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
                ) { Text("Create immutable goal and run on PC") }
            }

            state.message?.let { message ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 12.dp)) {
                        Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                            Text(message, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                            TextButton(onClick = viewModel::dismissMessage) { Text("OK") }
                        }
                    }
                }
            }
            state.error?.let { error ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 12.dp)) {
                        Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                            Text(error, color = MaterialTheme.colorScheme.error, modifier = Modifier.weight(1f))
                            TextButton(onClick = viewModel::dismissError) { Text("Dismiss") }
                        }
                    }
                }
            }

            state.latestSpecification?.let { specification ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 16.dp)) {
                        Column(Modifier.padding(14.dp)) {
                            Text("Latest Goal · v${specification.version}", style = MaterialTheme.typography.titleSmall)
                            Text(specification.goal, modifier = Modifier.padding(top = 4.dp))
                            Text(
                                "sha256 ${specification.contentSha256.take(12)}…",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            specification.acceptanceCriteria.sortedBy { it.order }.forEach { criterion ->
                                Text(
                                    "✓ ${criterion.text}",
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.padding(top = 4.dp)
                                )
                            }
                        }
                    }
                }
            }

            state.latestTask?.let { response ->
                item {
                    Card(Modifier.fillMaxWidth().padding(top = 10.dp)) {
                        Column(Modifier.padding(14.dp)) {
                            Text("Windows task", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "${response.task.role ?: "auto"} · ${response.task.status}" +
                                    (response.task.stage?.let { " · $it" } ?: ""),
                                style = MaterialTheme.typography.bodySmall,
                                modifier = Modifier.padding(top = 4.dp)
                            )
                            Text("Task ${response.task.id.take(10)}", style = MaterialTheme.typography.labelSmall)
                            TextButton(onClick = viewModel::refreshVerification) { Text("Refresh verification") }
                        }
                    }
                }
            }

            if (state.verificationRecords.isNotEmpty()) {
                item { Text("Independent verification", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 18.dp)) }
                items(state.verificationRecords, key = { it.id }) { record ->
                    Card(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
                        Column(Modifier.padding(12.dp)) {
                            Text(
                                "Pass ${record.verificationPass} · ${if (record.allRequiredSatisfied) "accepted" else "not accepted"}",
                                color = if (record.allRequiredSatisfied) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
                                style = MaterialTheme.typography.labelLarge
                            )
                            record.criteria.forEach { criterion ->
                                Text(
                                    "${if (criterion.satisfied) "✓" else "✗"} ${criterion.evidence}",
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.padding(top = 4.dp)
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
