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
import com.phonemicbridge.companion.model.AudioRouteTarget
import com.phonemicbridge.companion.model.CompanionState
import com.phonemicbridge.companion.service.AudioRouteService
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var tvStatus: TextView
    private lateinit var tvRoute: TextView
    private lateinit var btnToggleService: Button
    private lateinit var btnSpeaker: Button
    private lateinit var btnEarpiece: Button
    private lateinit var btnBluetooth: Button

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {
        // Permissions evaluated, UI will reflect state reactively
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        bindViews()
        setupListeners()
        observeServiceState()
        checkAndRequestPermissions()
    }

    private fun bindViews() {
        tvStatus = findViewById(R.id.tvStatus)
        tvRoute = findViewById(R.id.tvRoute)
        btnToggleService = findViewById(R.id.btnToggleService)
        btnSpeaker = findViewById(R.id.btnSpeaker)
        btnEarpiece = findViewById(R.id.btnEarpiece)
        btnBluetooth = findViewById(R.id.btnBluetooth)
    }

    private fun setupListeners() {
        btnToggleService.setOnClickListener {
            val currentState = AudioRouteService.serviceState.value
            if (currentState.isRunning) {
                sendServiceCommand(AudioRouteService.ACTION_STOP)
            } else {
                sendServiceCommand(AudioRouteService.ACTION_START)
            }
        }

        btnSpeaker.setOnClickListener {
            sendServiceCommand(AudioRouteService.ACTION_ROUTE_SPEAKER)
        }

        btnEarpiece.setOnClickListener {
            sendServiceCommand(AudioRouteService.ACTION_ROUTE_EARPIECE)
        }

        btnBluetooth.setOnClickListener {
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
            tvStatus.text = getString(R.string.status_running)
            btnToggleService.text = getString(R.string.btn_stop_service)
        } else {
            tvStatus.text = getString(R.string.status_stopped)
            btnToggleService.text = getString(R.string.btn_start_service)
        }
        tvRoute.text = "Current Route: ${state.activeRoute.displayName} (${state.activeDeviceName})"
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
