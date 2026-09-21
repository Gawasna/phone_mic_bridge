package com.phonemicbridge.companion.model

data class CompanionState(
    val isRunning: Boolean = false,
    val activeRoute: AudioRouteTarget = AudioRouteTarget.SYSTEM_DEFAULT,
    val activeDeviceName: String = "Default",
    val statusMessage: String = "Ready"
)
