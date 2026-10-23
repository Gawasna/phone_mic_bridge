package com.phonemicbridge.companion.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.phonemicbridge.companion.R
import com.phonemicbridge.companion.model.BridgeConnectionState
import com.phonemicbridge.companion.model.CompanionState
import com.phonemicbridge.companion.service.AudioRouteService
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var statusText: TextView
    private lateinit var bridgeStatusText: TextView
    private lateinit var hostDeviceText: TextView
    private lateinit var routeText: TextView
    private lateinit var toggleServiceButton: Button
    private lateinit var speakerRouteButton: Button
    private lateinit var earpieceRouteButton: Button
    private lateinit var bluetoothRouteButton: Button

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {
        // Permissions updated, trigger re-evaluation of state
        if (AudioRouteService.serviceState.value.isRunning) {
            sendServiceCommand(AudioRouteService.ACTION_START)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        bindViews()
        setupListeners()
        observeServiceState()
        checkAndRequestPermissions()

        // Automatically ensure route service is started
        if (!AudioRouteService.serviceState.value.isRunning) {
            sendServiceCommand(AudioRouteService.ACTION_START)
        }
    }

    private fun bindViews() {
        statusText = findViewById(R.id.statusText)
        bridgeStatusText = findViewById(R.id.bridgeStatusText)
        hostDeviceText = findViewById(R.id.hostDeviceText)
        routeText = findViewById(R.id.routeText)
        toggleServiceButton = findViewById(R.id.toggleServiceButton)
        speakerRouteButton = findViewById(R.id.speakerRouteButton)
        earpieceRouteButton = findViewById(R.id.earpieceRouteButton)
        bluetoothRouteButton = findViewById(R.id.bluetoothRouteButton)
    }

    private fun setupListeners() {
        toggleServiceButton.setOnClickListener {
            val currentState = AudioRouteService.serviceState.value
            if (currentState.isRunning) {
                sendServiceCommand(AudioRouteService.ACTION_STOP)
            } else {
                sendServiceCommand(AudioRouteService.ACTION_START)
            }
        }

        speakerRouteButton.setOnClickListener {
            sendServiceCommand(AudioRouteService.ACTION_ROUTE_SPEAKER)
        }

        earpieceRouteButton.setOnClickListener {
            sendServiceCommand(AudioRouteService.ACTION_ROUTE_EARPIECE)
        }

        bluetoothRouteButton.setOnClickListener {
            sendServiceCommand(AudioRouteService.ACTION_ROUTE_BLUETOOTH)
        }
    }

    private fun observeServiceState() {
        lifecycleScope.launch {
            AudioRouteService.serviceState.collectLatest { state ->
                renderState(state)
            }
        }
    }

    private fun renderState(state: CompanionState) {
        if (state.isRunning) {
            statusText.text = getString(R.string.status_running)
            toggleServiceButton.text = getString(R.string.btn_stop_service)
        } else {
            statusText.text = getString(R.string.status_stopped)
            toggleServiceButton.text = getString(R.string.btn_start_service)
        }

        when (state.bridgeConnectionState) {
            BridgeConnectionState.STREAMING_ACTIVE -> {
                bridgeStatusText.text = getString(R.string.bridge_status_streaming)
                bridgeStatusText.setTextColor(ContextCompat.getColor(this, R.color.status_green_bright))

                val streamDetail = if (state.isAdbStreamActive) {
                    val client = if (state.adbClientAddress.isNotBlank()) " (${state.adbClientAddress})" else ""
                    getString(R.string.host_stream_adb, client)
                } else {
                    val dev = if (state.connectedBridgeName.isNotBlank()) state.connectedBridgeName else "Windows Host"
                    getString(R.string.host_stream_sco, dev)
                }
                hostDeviceText.text = streamDetail
                hostDeviceText.setTextColor(ContextCompat.getColor(this, R.color.status_green_dark))
            }
            BridgeConnectionState.STANDBY -> {
                bridgeStatusText.text = getString(R.string.bridge_status_standby)
                bridgeStatusText.setTextColor(ContextCompat.getColor(this, R.color.status_amber_bright))

                val dev = if (state.connectedBridgeName.isNotBlank()) state.connectedBridgeName else "Windows Host"
                hostDeviceText.text = getString(R.string.host_standby, dev)
                hostDeviceText.setTextColor(ContextCompat.getColor(this, R.color.status_amber_dark))
            }
            BridgeConnectionState.DISCONNECTED -> {
                bridgeStatusText.text = getString(R.string.bridge_status_disconnected)
                bridgeStatusText.setTextColor(ContextCompat.getColor(this, R.color.status_red))
                hostDeviceText.text = getString(R.string.host_disconnected)
                hostDeviceText.setTextColor(ContextCompat.getColor(this, R.color.status_gray))
            }
        }

        routeText.text = getString(
            R.string.current_route_format,
            state.activeRoute.displayName,
            state.activeDeviceName
        )
    }

    private fun sendServiceCommand(action: String) {
        val intent = Intent(this, AudioRouteService::class.java).apply {
            this.action = action
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    private fun checkAndRequestPermissions() {
        val requiredPermissions = mutableListOf<String>()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requiredPermissions.add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                requiredPermissions.add(Manifest.permission.BLUETOOTH_CONNECT)
            }
        }

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requiredPermissions.add(Manifest.permission.RECORD_AUDIO)
        }

        if (requiredPermissions.isNotEmpty()) {
            permissionLauncher.launch(requiredPermissions.toTypedArray())
        }
    }
}
