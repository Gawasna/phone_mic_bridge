package com.phonemicbridge.companion.model

object BridgeStateEvaluator {

    fun evaluate(
        isBridgeConnected: Boolean,
        isScoAudioActive: Boolean,
        isAdbStreamActive: Boolean
    ): BridgeConnectionState {
        return when {
            isScoAudioActive || isAdbStreamActive -> BridgeConnectionState.STREAMING_ACTIVE
            isBridgeConnected -> BridgeConnectionState.STANDBY
            else -> BridgeConnectionState.DISCONNECTED
        }
    }

    fun buildStatusMessage(
        state: BridgeConnectionState,
        connectedBridgeName: String = "",
        isAdbStreamActive: Boolean = false,
        adbClientAddress: String = ""
    ): String {
        return when (state) {
            BridgeConnectionState.STREAMING_ACTIVE -> {
                if (isAdbStreamActive) {
                    val client = if (adbClientAddress.isNotBlank()) " ($adbClientAddress)" else ""
                    "Audio Streaming Active via USB ADB$client"
                } else {
                    val host = if (connectedBridgeName.isNotBlank()) " ($connectedBridgeName)" else ""
                    "Host Audio Stream Active$host"
                }
            }
            BridgeConnectionState.STANDBY -> {
                val host = if (connectedBridgeName.isNotBlank()) " ($connectedBridgeName)" else ""
                "Connected in Standby$host"
            }
            BridgeConnectionState.DISCONNECTED -> "Bridge Disconnected"
        }
    }
}
