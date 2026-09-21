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
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        audioController = AndroidAudioRouteController(this)
        notificationManager = RouteNotificationManager(this)

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

        val notification = notificationManager.buildNotification(target)
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
        notificationManager.update(target)
        updateState(isRunning = true, route = target)
    }

    private fun updateState(isRunning: Boolean, route: AudioRouteTarget) {
        _serviceState.value = CompanionState(
            isRunning = isRunning,
            activeRoute = route,
            activeDeviceName = audioController.activeDeviceName,
            statusMessage = if (isRunning) "Active: ${route.displayName}" else "Stopped"
        )
    }

    private fun stopRoutingService() {
        audioController.release()
        if (wakeLock?.isHeld == true) {
            wakeLock?.release()
        }

        updateState(isRunning = false, route = AudioRouteTarget.SYSTEM_DEFAULT)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        stopRoutingService()
        super.onDestroy()
    }
}
