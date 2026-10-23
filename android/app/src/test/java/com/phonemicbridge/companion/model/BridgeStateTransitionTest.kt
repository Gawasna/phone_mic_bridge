package com.phonemicbridge.companion.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BridgeStateTransitionTest {

    @Test
    fun evaluate_Disconnected_WhenNoBridgeAndNoAudio() {
        val state = BridgeStateEvaluator.evaluate(
            isBridgeConnected = false,
            isScoAudioActive = false,
            isAdbStreamActive = false
        )
        assertEquals(BridgeConnectionState.DISCONNECTED, state)
    }

    @Test
    fun evaluate_Standby_WhenBridgeConnectedButNoActiveStream() {
        val state = BridgeStateEvaluator.evaluate(
            isBridgeConnected = true,
            isScoAudioActive = false,
            isAdbStreamActive = false
        )
        assertEquals(BridgeConnectionState.STANDBY, state)
    }

    @Test
    fun evaluate_StreamingActive_WhenScoAudioIsActive() {
        val state = BridgeStateEvaluator.evaluate(
            isBridgeConnected = true,
            isScoAudioActive = true,
            isAdbStreamActive = false
        )
        assertEquals(BridgeConnectionState.STREAMING_ACTIVE, state)
    }

    @Test
    fun evaluate_StreamingActive_WhenAdbStreamIsActive() {
        val state = BridgeStateEvaluator.evaluate(
            isBridgeConnected = false,
            isScoAudioActive = false,
            isAdbStreamActive = true
        )
        assertEquals(BridgeConnectionState.STREAMING_ACTIVE, state)
    }

    @Test
    fun buildStatusMessage_ReflectsStateAccurately() {
        val disconnectedMsg = BridgeStateEvaluator.buildStatusMessage(BridgeConnectionState.DISCONNECTED)
        assertEquals("Bridge Disconnected", disconnectedMsg)

        val standbyMsg = BridgeStateEvaluator.buildStatusMessage(
            BridgeConnectionState.STANDBY,
            connectedBridgeName = "Windows-PC"
        )
        assertEquals("Connected in Standby (Windows-PC)", standbyMsg)

        val adbMsg = BridgeStateEvaluator.buildStatusMessage(
            BridgeConnectionState.STREAMING_ACTIVE,
            isAdbStreamActive = true,
            adbClientAddress = "127.0.0.1:52134"
        )
        assertEquals("Audio Streaming Active via USB ADB (127.0.0.1:52134)", adbMsg)

        val scoMsg = BridgeStateEvaluator.buildStatusMessage(
            BridgeConnectionState.STREAMING_ACTIVE,
            connectedBridgeName = "DESKTOP-1",
            isAdbStreamActive = false
        )
        assertEquals("Host Audio Stream Active (DESKTOP-1)", scoMsg)
    }

    @Test
    fun companionState_Defaults_BridgeConnectionStateDisconnected() {
        val state = CompanionState()
        assertEquals(BridgeConnectionState.DISCONNECTED, state.bridgeConnectionState)
        assertFalse(state.isAdbStreamActive)
        assertEquals("", state.adbClientAddress)
    }
}
