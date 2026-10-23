package com.phonemicbridge.companion.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.phonemicbridge.companion.audio.AdbStreamReceiverServer
import com.phonemicbridge.companion.audio.AndroidAudioRouteController
import com.phonemicbridge.companion.audio.AudioRouteController
import com.phonemicbridge.companion.bluetooth.BluetoothBridgeMonitor
import com.phonemicbridge.companion.model.AudioRouteTarget
import com.phonemicbridge.companion.model.BridgeConnectionState
import com.phonemicbridge.companion.model.BridgeStateEvaluator
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
    private var adbStreamServer: AdbStreamReceiverServer? = null
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        audioController = AndroidAudioRouteController(this)
        notificationManager = RouteNotificationManager(this)

        bridgeMonitor = BluetoothBridgeMonitor(this) { isConnected, deviceName, isScoActive ->
            val currentState = _serviceState.value
            val bridgeState = BridgeStateEvaluator.evaluate(
                isBridgeConnected = isConnected,
                isScoAudioActive = isScoActive,
                isAdbStreamActive = currentState.isAdbStreamActive
            )
            val statusMsg = BridgeStateEvaluator.buildStatusMessage(
                state = bridgeState,
                connectedBridgeName = deviceName,
                isAdbStreamActive = currentState.isAdbStreamActive,
                adbClientAddress = currentState.adbClientAddress
            )

            _serviceState.value = currentState.copy(
                isBridgeConnected = isConnected,
                connectedBridgeName = deviceName,
                isScoAudioActive = isScoActive,
                bridgeConnectionState = bridgeState,
                statusMessage = statusMsg
            )

            notificationManager.update(
                activeRoute = _serviceState.value.activeRoute,
                bridgeState = bridgeState
            )
        }
        bridgeMonitor.start()

        startAdbStreamServer()

        val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = powerManager.newWakeLock(
            PowerManager.PARTIAL_WAKE_LOCK,
            "PhoneMicBridge::AudioRouteWakeLock"
        ).apply {
            setReferenceCounted(false)
        }
    }

    private fun startAdbStreamServer() {
        adbStreamServer = AdbStreamReceiverServer(
            port = AdbStreamReceiverServer.DEFAULT_PORT,
            onStreamStateChanged = { isStreaming, clientAddr ->
                val currentState = _serviceState.value
                val bridgeState = BridgeStateEvaluator.evaluate(
                    isBridgeConnected = currentState.isBridgeConnected,
                    isScoAudioActive = currentState.isScoAudioActive,
                    isAdbStreamActive = isStreaming
                )
                val statusMsg = BridgeStateEvaluator.buildStatusMessage(
                    state = bridgeState,
                    connectedBridgeName = currentState.connectedBridgeName,
                    isAdbStreamActive = isStreaming,
                    adbClientAddress = clientAddr
                )

                _serviceState.value = currentState.copy(
                    isAdbStreamActive = isStreaming,
                    adbClientAddress = clientAddr,
                    bridgeConnectionState = bridgeState,
                    statusMessage = statusMsg
                )

                notificationManager.update(
                    activeRoute = _serviceState.value.activeRoute,
                    bridgeState = bridgeState
                )
            }
        )
        adbStreamServer?.start()
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
            bridgeState = _serviceState.value.bridgeConnectionState
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val fgsType = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK or
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
            startForeground(
                RouteNotificationManager.NOTIFICATION_ID,
                notification,
                fgsType
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
            bridgeState = _serviceState.value.bridgeConnectionState
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
        adbStreamServer?.close()
        adbStreamServer = null

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
