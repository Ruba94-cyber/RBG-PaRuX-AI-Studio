package com.parux.android.core.supervision

import android.Manifest
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.parux.android.MainActivity
import com.parux.android.PaRuXApplication
import com.parux.android.R
import com.parux.android.core.network.AgentTaskDto
import com.parux.android.core.network.PcComputerUseApproval
import kotlinx.coroutines.delay
import java.util.concurrent.TimeUnit

/**
 * Local-only Android supervision for durable PC AgentTasks. There is intentionally no FCM/cloud
 * push dependency: foreground PC Studio still polls rapidly, while WorkManager provides bounded
 * background checks that survive process death and device restarts.
 *
 * A task-specific worker polls for at most five minutes per execution, every 15 seconds. If work is
 * still active it returns retry and lets WorkManager backoff/restart it. This keeps ordinary tasks
 * responsive without a permanent foreground service and without an ever-growing delay between
 * every individual status check.
 */
object PcTaskSupervisor {
    private const val DISCOVERY_WORK = "parux-pc-task-discovery"
    private const val TASK_WORK_PREFIX = "parux-pc-task-"
    private const val KEY_TASK_ID = "taskId"
    private const val PREFS = "parux_pc_notifications"

    private val connected = Constraints.Builder()
        .setRequiredNetworkType(NetworkType.CONNECTED)
        .build()

    fun scheduleDiscovery(context: Context) {
        val request = PeriodicWorkRequestBuilder<PcTaskDiscoveryWorker>(15, TimeUnit.MINUTES)
            .setConstraints(connected)
            .build()
        WorkManager.getInstance(context.applicationContext).enqueueUniquePeriodicWork(
            DISCOVERY_WORK,
            ExistingPeriodicWorkPolicy.KEEP,
            request
        )
    }

    fun monitor(context: Context, taskId: String) {
        if (taskId.isBlank()) return
        val request = OneTimeWorkRequestBuilder<PcTaskMonitorWorker>()
            .setInputData(workDataOf(KEY_TASK_ID to taskId))
            .setConstraints(connected)
            .setBackoffCriteria(BackoffPolicy.LINEAR, 15, TimeUnit.SECONDS)
            .build()
        WorkManager.getInstance(context.applicationContext).enqueueUniqueWork(
            TASK_WORK_PREFIX + taskId,
            ExistingWorkPolicy.KEEP,
            request
        )
    }

    fun markHandled(context: Context, taskId: String, stateKey: String) {
        if (taskId.isBlank()) return
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString("task:$taskId", stateKey).apply()
    }

    internal fun notificationState(context: Context, taskId: String): String? =
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString("task:$taskId", null)

    internal fun taskId(parameters: WorkerParameters): String? =
        parameters.inputData.getString(KEY_TASK_ID)?.takeIf { it.isNotBlank() }
}

class PcTaskDiscoveryWorker(
    appContext: Context,
    params: WorkerParameters
) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val app = applicationContext as? PaRuXApplication ?: return Result.success()
        app.container.refreshClient()
        val client = app.container.client.value ?: return Result.success()
        return try {
            client.api.listAgentTasks().tasks
                .filterNot(::isTerminal)
                .forEach { PcTaskSupervisor.monitor(applicationContext, it.id) }
            Result.success()
        } catch (_: Exception) {
            Result.retry()
        }
    }
}

class PcTaskMonitorWorker(
    appContext: Context,
    private val params: WorkerParameters
) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val taskId = PcTaskSupervisor.taskId(params) ?: return Result.failure()
        val app = applicationContext as? PaRuXApplication ?: return Result.success()
        app.container.refreshClient()
        val client = app.container.client.value ?: return Result.success()

        repeat(MAX_POLLS_PER_RUN) { pollIndex ->
            val task = try {
                client.api.getAgentTask(taskId)
            } catch (_: Exception) {
                return Result.retry()
            }

            if (PcComputerUseApproval.isPending(task)) {
                notifyOnce(
                    task,
                    stateKey = "approval-required",
                    title = "Computer Use approval needed",
                    body = task.goal.take(180)
                )
            } else if (isTerminal(task)) {
                val title = when (task.status) {
                    "completed" -> "PC task completed"
                    "failed" -> "PC task failed"
                    "cancelled" -> "PC task cancelled"
                    "blocked" -> "PC task blocked"
                    else -> "PC task updated"
                }
                val detail = task.error?.takeIf { it.isNotBlank() }
                    ?: task.stage?.takeIf { it.isNotBlank() }
                    ?: task.goal
                notifyOnce(task, task.status, title, detail.take(180))
                return Result.success()
            }

            if (pollIndex + 1 < MAX_POLLS_PER_RUN) delay(POLL_INTERVAL_MS)
        }

        return Result.retry()
    }

    private fun notifyOnce(task: AgentTaskDto, stateKey: String, title: String, body: String) {
        if (PcTaskSupervisor.notificationState(applicationContext, task.id) == stateKey) return

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return

        val intent = Intent(applicationContext, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(MainActivity.EXTRA_OPEN_PC_STUDIO, true)
        val pendingIntent = PendingIntent.getActivity(
            applicationContext,
            task.id.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(applicationContext, PaRuXApplication.CHANNEL_PC)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(applicationContext).notify(task.id.hashCode(), notification)
        PcTaskSupervisor.markHandled(applicationContext, task.id, stateKey)
    }

    companion object {
        private const val MAX_POLLS_PER_RUN = 20
        private const val POLL_INTERVAL_MS = 15_000L
    }
}

private fun isTerminal(task: AgentTaskDto): Boolean =
    task.status in setOf("completed", "failed", "cancelled", "blocked")
