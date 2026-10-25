package com.parux.android.ui.jobs

import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.PaRuXApplication
import com.parux.android.R
import com.parux.android.core.AppContainer
import com.parux.android.core.network.MediaJobRequest
import com.parux.android.core.network.MediaJobStatus
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class JobsUiState(
    val jobs: List<MediaJobStatus> = emptyList(),
    val isLoading: Boolean = false,
    val errorMessage: String? = null
)

/**
 * Polls the shared job queue (GET /api/v1/jobs - the same list Windows sees, since the server
 * no longer partitions jobs per authenticated caller) while this screen is active. No push
 * notifications from a closed app in 1.0 (no FCM, LAN-only by design); a completed job while
 * the app was backgrounded is picked up the next time polling resumes.
 */
class JobsViewModel(private val container: AppContainer) : ViewModel() {
    private val _state = MutableStateFlow(JobsUiState())
    val state: StateFlow<JobsUiState> = _state.asStateFlow()

    private var pollingStarted = false
    private val notifiedCompletedIds = mutableSetOf<String>()

    fun startPolling(context: Context) {
        if (pollingStarted) return
        pollingStarted = true
        viewModelScope.launch {
            while (isActive) {
                refreshOnce(context.applicationContext)
                delay(3000)
            }
        }
    }

    private suspend fun refreshOnce(appContext: Context) {
        val client = container.client.value ?: return
        try {
            val jobs = client.api.listJobs()
            val newlyCompleted = jobs.filter { it.status == "completed" && it.id !in notifiedCompletedIds }
            newlyCompleted.forEach {
                notifiedCompletedIds.add(it.id)
                notifyCompleted(appContext, it)
            }
            _state.value = _state.value.copy(jobs = jobs, isLoading = false, errorMessage = null)
        } catch (e: Exception) {
            _state.value = _state.value.copy(isLoading = false)
        }
    }

    fun createJob(type: String, prompt: String, sourceImageBase64: String? = null) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                client.api.createJob(MediaJobRequest(type = type, prompt = prompt, sourceImageBase64 = sourceImageBase64 ?: ""))
            } catch (e: Exception) {
                _state.value = _state.value.copy(errorMessage = e.message ?: "Couldn't start the job.")
            }
        }
    }

    fun cancelJob(id: String) {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try { client.api.cancelJob(id) } catch (_: Exception) { }
        }
    }

    private fun notifyCompleted(appContext: Context, job: MediaJobStatus) {
        val manager = appContext.getSystemService(NotificationManager::class.java) ?: return
        val notification = NotificationCompat.Builder(appContext, PaRuXApplication.CHANNEL_JOBS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("PaRuX")
            .setContentText("${job.type.replaceFirstChar { it.uppercase() }} completed")
            .setAutoCancel(true)
            .build()
        manager.notify(job.id.hashCode(), notification)
    }
}
