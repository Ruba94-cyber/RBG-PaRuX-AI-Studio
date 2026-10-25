package com.parux.android.core

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class ActiveProjectSelection(
    val initialized: Boolean = false,
    val projectId: String? = null
)

/**
 * Device-local pointer to the Project currently being worked on. The Project itself remains
 * Core-owned on the PC; Android persists only this opaque id so Chat and PC Studio share one
 * explicit scope across tab switches and process restarts. A null id with initialized=true means
 * the user explicitly selected "No Project" and must not be auto-replaced by the first Project.
 */
class ActiveProjectStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("parux_active_project", Context.MODE_PRIVATE)
    private val _selection = MutableStateFlow(
        ActiveProjectSelection(
            initialized = prefs.getBoolean(KEY_INITIALIZED, false),
            projectId = prefs.getString(KEY_PROJECT_ID, null)
        )
    )
    val selection: StateFlow<ActiveProjectSelection> = _selection.asStateFlow()

    fun select(projectId: String?) {
        prefs.edit()
            .putBoolean(KEY_INITIALIZED, true)
            .apply {
                if (projectId.isNullOrBlank()) remove(KEY_PROJECT_ID)
                else putString(KEY_PROJECT_ID, projectId)
            }
            .apply()
        _selection.value = ActiveProjectSelection(initialized = true, projectId = projectId?.takeIf { it.isNotBlank() })
    }

    companion object {
        private const val KEY_INITIALIZED = "initialized"
        private const val KEY_PROJECT_ID = "project_id"
    }
}
