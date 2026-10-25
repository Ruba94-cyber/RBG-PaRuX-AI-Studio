package com.parux.android.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import com.parux.android.core.AppContainer
import com.parux.android.ui.chat.ChatViewModel
import com.parux.android.ui.goals.GoalsViewModel
import com.parux.android.ui.jobs.JobsViewModel
import com.parux.android.ui.settings.SettingsViewModel
import com.parux.android.ui.studio.PcStudioViewModel
import com.parux.android.ui.tools.ToolsViewModel

class PaRuXViewModelFactory(private val container: AppContainer) : ViewModelProvider.Factory {
    @Suppress("UNCHECKED_CAST")
    override fun <T : ViewModel> create(modelClass: Class<T>): T = when (modelClass) {
        ChatViewModel::class.java -> ChatViewModel(container) as T
        GoalsViewModel::class.java -> GoalsViewModel(container) as T
        JobsViewModel::class.java -> JobsViewModel(container) as T
        SettingsViewModel::class.java -> SettingsViewModel(container) as T
        ToolsViewModel::class.java -> ToolsViewModel(container) as T
        PcStudioViewModel::class.java -> PcStudioViewModel(container) as T
        else -> throw IllegalArgumentException("Unknown ViewModel: $modelClass")
    }
}
