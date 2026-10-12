package com.phonemicbridge.companion.audio

import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import com.phonemicbridge.companion.model.AudioRouteTarget
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

interface AudioRouteController {
    val activeRoute: StateFlow<AudioRouteTarget>
    val activeDeviceName: String
    fun routeTo(target: AudioRouteTarget): Result<Unit>
    fun reset(): Result<Unit>
    fun release()
}

class AndroidAudioRouteController(
    private val context: Context
) : AudioRouteController {

    private val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    private val _activeRoute = MutableStateFlow(AudioRouteTarget.SYSTEM_DEFAULT)
    override val activeRoute: StateFlow<AudioRouteTarget> = _activeRoute.asStateFlow()

    override val activeDeviceName: String
        get() = when (_activeRoute.value) {
            AudioRouteTarget.SPEAKER -> "Built-in Speaker"
            AudioRouteTarget.EARPIECE -> "Built-in Earpiece"
            AudioRouteTarget.BLUETOOTH_SCO -> "Bluetooth SCO"
            AudioRouteTarget.SYSTEM_DEFAULT -> "System Default"
        }

    override fun routeTo(target: AudioRouteTarget): Result<Unit> = runCatching {
        when (target) {
            AudioRouteTarget.SPEAKER -> applySpeakerRoute()
            AudioRouteTarget.EARPIECE -> applyEarpieceRoute()
            AudioRouteTarget.BLUETOOTH_SCO -> applyBluetoothRoute()
            AudioRouteTarget.SYSTEM_DEFAULT -> applyDefaultRoute()
        }
        _activeRoute.value = target
    }

    override fun reset(): Result<Unit> = routeTo(AudioRouteTarget.SYSTEM_DEFAULT)

    override fun release() {
        applyDefaultRoute()
        _activeRoute.value = AudioRouteTarget.SYSTEM_DEFAULT
    }

    private fun applySpeakerRoute() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val speaker = audioManager.availableCommunicationDevices.find {
                it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
            }
            if (speaker != null) {
                audioManager.setCommunicationDevice(speaker)
            }
        } else {
            audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
            @Suppress("DEPRECATION")
            audioManager.isSpeakerphoneOn = true
        }
    }

    private fun applyEarpieceRoute() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val earpiece = audioManager.availableCommunicationDevices.find {
                it.type == AudioDeviceInfo.TYPE_BUILTIN_EARPIECE
            }
            if (earpiece != null) {
                audioManager.setCommunicationDevice(earpiece)
            }
        } else {
            audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
            @Suppress("DEPRECATION")
            audioManager.isSpeakerphoneOn = false
        }
    }

    private fun applyBluetoothRoute() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            audioManager.clearCommunicationDevice()
            val btDevice = audioManager.availableCommunicationDevices.find {
                it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
            }
            if (btDevice != null) {
                audioManager.setCommunicationDevice(btDevice)
            }
        } else {
            @Suppress("DEPRECATION")
            audioManager.isSpeakerphoneOn = false
            @Suppress("DEPRECATION")
            audioManager.startBluetoothSco()
            @Suppress("DEPRECATION")
            audioManager.isBluetoothScoOn = true
        }
    }

    private fun applyDefaultRoute() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            audioManager.clearCommunicationDevice()
        } else {
            @Suppress("DEPRECATION")
            audioManager.isSpeakerphoneOn = false
            @Suppress("DEPRECATION")
            audioManager.stopBluetoothSco()
            @Suppress("DEPRECATION")
            audioManager.isBluetoothScoOn = false
            audioManager.mode = AudioManager.MODE_NORMAL
        }
    }
}
