package com.phonemicbridge.companion.model

data class CompanionState(
    val isRunning: Boolean = false,
    val activeRoute: AudioRouteTarget = AudioRouteTarget.SYSTEM_DEFAULT,
    val activeDeviceName: String = "Default",
    val statusMessage: String = "Ready",
    val isBridgeConnected: Boolean = false,
    val connectedBridgeName: String = "",
    val isScoAudioActive: Boolean = false,
    val isAdbStreamActive: Boolean = false,
    val adbClientAddress: String = "",
    val bridgeConnectionState: BridgeConnectionState = BridgeConnectionState.DISCONNECTED
)
