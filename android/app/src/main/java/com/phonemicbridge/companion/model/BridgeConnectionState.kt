package com.phonemicbridge.companion.model

enum class BridgeConnectionState(val displayName: String) {
    DISCONNECTED("Disconnected"),
    STANDBY("Standby"),
    STREAMING_ACTIVE("Streaming Active")
}
