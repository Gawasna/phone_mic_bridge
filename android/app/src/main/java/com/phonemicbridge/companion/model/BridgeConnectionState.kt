package com.phonemicbridge.companion.model

enum class BridgeConnectionState(val displayName: String, val badgeColorHex: String) {
    DISCONNECTED("DISCONNECTED", "#FF5252"),       // Mất liên kết / Không có kết nối
    STANDBY("STANDBY", "#FFC107"),                 // Đã liên kết OS nhưng bridge không stream
    STREAMING_ACTIVE("STREAMING ACTIVE", "#00E676") // SCO / Socket stream đang hoạt động
}
