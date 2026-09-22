package com.phonemicbridge.companion.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.phonemicbridge.companion.audio.AndroidAudioRouteController
import com.phonemicbridge.companion.audio.AudioRouteController
import com.phonemicbridge.companion.bluetooth.BluetoothBridgeMonitor
import com.phonemicbridge.companion.model.AudioRouteTarget
import com.phonemicbridge.companion.model.CompanionState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class AudioRouteService : Service() {

    companion object {
        const val ACTION_START = "com.phonemicbridge.companion.ACTION_START"
        const val ACTION_STOP = RouteNotificationManager.ACTION_STOP
        const val ACTION_ROUTE_SPEAKER = RouteNotificationManager.ACTION_ROUTE_SPEAKER
        const val ACTION_ROUTE_EARPIECE = RouteNotificationManager.ACTION_ROUTE_EARPIECE
        const val ACTION_ROUTE_BLUETOOTH = RouteNotificationManager.ACTION_ROUTE_BLUETOOTH

        private val _serviceState = MutableStateFlow(CompanionState())
        val serviceState: StateFlow<CompanionState> = _serviceState.asStateFlow()
    }

    private lateinit var audioController: AudioRouteController
    private lateinit var notificationManager: RouteNotificationManager
    private lateinit var bridgeMonitor: BluetoothBridgeMonitor
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        audioController = AndroidAudioRouteController(this)
        notificationManager = RouteNotificationManager(this)

        bridgeMonitor = BluetoothBridgeMonitor(this) { isConnected, deviceName, isScoActive ->
            val statusMsg = when {
                isScoActive -> "Host Audio Stream Active ($deviceName)"
                isConnected -> "Connected via Bluetooth HFP ($deviceName)"
                else -> "Bluetooth Bridge Disconnected"
            }

            _serviceState.value = _serviceState.value.copy(
                isBridgeConnected = isConnected,
                connectedBridgeName = deviceName,
                isScoAudioActive = isScoActive,
                statusMessage = statusMsg
            )

            notificationManager.update(
                activeRoute = _serviceState.value.activeRoute,
                isBridgeConnected = isConnected
            )
        }
        bridgeMonitor.start()

        val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = powerManager.newWakeLock(
            PowerManager.PARTIAL_WAKE_LOCK,
            "PhoneMicBridge::AudioRouteWakeLock"
        ).apply {
            setReferenceCounted(false)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val action = intent?.action ?: ACTION_START

        when (action) {
            ACTION_STOP -> {
                stopRoutingService()
                return START_NOT_STICKY
            }
            ACTION_ROUTE_SPEAKER -> setRoute(AudioRouteTarget.SPEAKER)
            ACTION_ROUTE_EARPIECE -> setRoute(AudioRouteTarget.EARPIECE)
            ACTION_ROUTE_BLUETOOTH -> setRoute(AudioRouteTarget.BLUETOOTH_SCO)
            ACTION_START -> {
                startForegroundWithInitialRoute(AudioRouteTarget.SPEAKER)
            }
        }

        return START_STICKY
    }

    private fun startForegroundWithInitialRoute(target: AudioRouteTarget) {
        wakeLock?.acquire(60 * 60 * 1000L) // 1 hour safety timeout
        setRoute(target)

        val notification = notificationManager.buildNotification(
            activeRoute = target,
            isBridgeConnected = _serviceState.value.isBridgeConnected
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                RouteNotificationManager.NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            )
        } else {
            startForeground(RouteNotificationManager.NOTIFICATION_ID, notification)
        }

        updateState(isRunning = true, route = target)
    }

    private fun setRoute(target: AudioRouteTarget) {
        audioController.routeTo(target)
        notificationManager.update(
            activeRoute = target,
            isBridgeConnected = _serviceState.value.isBridgeConnected
        )
        updateState(isRunning = true, route = target)
    }

    private fun updateState(isRunning: Boolean, route: AudioRouteTarget) {
        _serviceState.value = _serviceState.value.copy(
            isRunning = isRunning,
            activeRoute = route,
            activeDeviceName = audioController.activeDeviceName
        )
    }

    private fun stopRoutingService() {
        bridgeMonitor.close()
        audioController.release()

        if (wakeLock?.isHeld == true) {
            wakeLock?.release()
        }

        _serviceState.value = CompanionState()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        stopRoutingService()
        super.onDestroy()
    }
}
