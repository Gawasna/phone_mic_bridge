package com.phonemicbridge.companion.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

class AudioRouteTargetTest {

    @Test
    fun fromAction_WithValidActions_ShouldMapToCorrectTarget() {
        assertEquals(AudioRouteTarget.SPEAKER, AudioRouteTarget.fromAction("ACTION_ROUTE_SPEAKER"))
        assertEquals(AudioRouteTarget.EARPIECE, AudioRouteTarget.fromAction("ACTION_ROUTE_EARPIECE"))
        assertEquals(AudioRouteTarget.BLUETOOTH_SCO, AudioRouteTarget.fromAction("ACTION_ROUTE_BLUETOOTH"))
        assertEquals(AudioRouteTarget.SYSTEM_DEFAULT, AudioRouteTarget.fromAction("ACTION_ROUTE_DEFAULT"))
    }

    @Test
    fun fromAction_WithUnknownAction_ShouldReturnNull() {
        assertNull(AudioRouteTarget.fromAction("UNKNOWN_ACTION"))
        assertNull(AudioRouteTarget.fromAction(null))
    }

    @Test
    fun companionState_DefaultValues_ShouldBeConsistent() {
        val state = CompanionState()
        assertFalse(state.isRunning)
        assertEquals(AudioRouteTarget.SYSTEM_DEFAULT, state.activeRoute)
        assertEquals("Default", state.activeDeviceName)
        assertEquals("Ready", state.statusMessage)
    }
}
