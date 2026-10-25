package com.parux.android.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.parux.android.core.AppContainer
import com.parux.android.core.network.EngineRaceSummaryDto
import com.parux.android.core.network.ObservabilityMetricDto
import com.parux.android.core.network.SetExecutionQualityProfileRequestDto
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class SettingsUiState(
    val endpoint: String = "",
    val deviceName: String = "",
    val serverReachable: Boolean? = null,
    val gamingMode: Boolean = false,
    val providerReady: Boolean = false,
    val executionQualityProfile: String = "balanced",
    val preferredContextTokens: Int = 0,
    val reasoningEffort: Int = 2,
    val executionEvidenceSource: String = "",
    val executionProfileChanging: Boolean = false,
    val executionProfileError: String? = null,
    val workstationMetrics: List<ObservabilityMetricDto> = emptyList(),
    val engineRaceSummary: EngineRaceSummaryDto = EngineRaceSummaryDto()
)

class SettingsViewModel(private val container: AppContainer) : ViewModel() {
    private val _state = MutableStateFlow(SettingsUiState())
    val state: StateFlow<SettingsUiState> = _state.asStateFlow()

    init {
        val credential = container.credentialStore.load()
        _state.value = _state.value.copy(
            endpoint = credential?.endpoint ?: "",
            deviceName = credential?.deviceName ?: ""
        )
        refreshStatus()
    }

    fun refreshStatus() {
        val client = container.client.value ?: return
        viewModelScope.launch {
            try {
                val health = client.api.health()
                val profile = runCatching { client.api.getExecutionProfile() }.getOrNull()
                val dashboard = runCatching { client.api.getObservabilityDashboard(20, 6) }.getOrNull()
                val effectiveProfile = profile ?: dashboard?.executionProfile
                _state.value = _state.value.copy(
                    serverReachable = true,
                    gamingMode = health.gamingMode,
                    providerReady = health.providerReady,
                    executionQualityProfile = effectiveProfile?.activeQualityProfile ?: _state.value.executionQualityProfile,
                    preferredContextTokens = effectiveProfile?.preferredContextTokens ?: _state.value.preferredContextTokens,
                    reasoningEffort = effectiveProfile?.reasoningEffort ?: _state.value.reasoningEffort,
                    executionEvidenceSource = effectiveProfile?.evidenceSource ?: _state.value.executionEvidenceSource,
                    executionProfileError = if (effectiveProfile == null) "Execution profile is unavailable on this Core." else null,
                    workstationMetrics = dashboard?.metrics ?: _state.value.workstationMetrics,
                    engineRaceSummary = dashboard?.engineRaceSummary ?: _state.value.engineRaceSummary
                )
            } catch (_: Exception) {
                _state.value = _state.value.copy(serverReachable = false)
            }
        }
    }

    fun setExecutionQuality(profile: String) {
        if (profile !in QualityProfiles || _state.value.executionProfileChanging) return
        val client = container.client.value ?: return
        viewModelScope.launch {
            _state.value = _state.value.copy(executionProfileChanging = true, executionProfileError = null)
            try {
                val updated = client.api.setExecutionQuality(SetExecutionQualityProfileRequestDto(profile))
                _state.value = _state.value.copy(
                    executionQualityProfile = updated.activeQualityProfile,
                    preferredContextTokens = updated.preferredContextTokens,
                    reasoningEffort = updated.reasoningEffort,
                    executionEvidenceSource = updated.evidenceSource,
                    executionProfileChanging = false,
                    executionProfileError = null
                )
            } catch (e: Exception) {
                _state.value = _state.value.copy(
                    executionProfileChanging = false,
                    executionProfileError = e.message ?: "Could not update the shared execution profile."
                )
            }
        }
    }

    fun forgetDevice() {
        container.forgetDevice()
    }

    companion object {
        val QualityProfiles = listOf("fast", "balanced", "maximum-quality", "overnight")
    }
}
