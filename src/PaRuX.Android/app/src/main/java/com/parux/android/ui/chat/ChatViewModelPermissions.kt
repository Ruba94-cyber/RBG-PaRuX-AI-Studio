package com.parux.android.ui.chat

/**
 * Permission-denial hook kept outside the ViewModel's audio/network logic: the system permission
 * dialog is owned by Compose/Android. Denial simply ensures no voice session remains active; the
 * user can tap the microphone again later to re-request according to Android's normal policy.
 */
fun ChatViewModel.microphonePermissionDenied() {
    stopVoiceMode()
}
