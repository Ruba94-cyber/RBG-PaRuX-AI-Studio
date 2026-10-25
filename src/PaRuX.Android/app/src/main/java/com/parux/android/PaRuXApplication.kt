package com.parux.android

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.parux.android.core.AppContainer
import com.parux.android.core.supervision.PcTaskSupervisor

class PaRuXApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
        createNotificationChannels()
        // Local-only background discovery. If the device is not paired the worker exits cheaply;
        // after pairing, the same persisted credential store lets it supervise PC tasks even after
        // Android has killed and recreated the app process.
        PcTaskSupervisor.scheduleDiscovery(this)
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_JOBS,
                "Job completion",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply { description = "Image and video generation finished on PaRuX PC." }
        )
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_PC,
                "PC supervision",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = "Computer Use approvals and AgentTask completion from the paired PaRuX PC."
            }
        )
    }

    companion object {
        const val CHANNEL_JOBS = "parux_jobs"
        const val CHANNEL_PC = "parux_pc_supervision"
    }
}
