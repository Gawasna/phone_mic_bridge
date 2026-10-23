package com.phonemicbridge.companion.model

enum class AudioRouteTarget(val displayName: String) {
    SYSTEM_DEFAULT("System Default"),
    SPEAKER("Built-in Speaker"),
    EARPIECE("Built-in Earpiece"),
    BLUETOOTH_SCO("Bluetooth SCO");

    companion object {
        fun fromAction(action: String?): AudioRouteTarget? = when (action) {
            "ACTION_ROUTE_SPEAKER" -> SPEAKER
            "ACTION_ROUTE_EARPIECE" -> EARPIECE
            "ACTION_ROUTE_BLUETOOTH" -> BLUETOOTH_SCO
            "ACTION_ROUTE_DEFAULT" -> SYSTEM_DEFAULT
            else -> null
        }
    }
}
