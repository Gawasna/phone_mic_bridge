package com.phonemicbridge.companion.bluetooth

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothHeadset
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioManager
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat

/**
 * Monitors Bluetooth HFP (Hands-Free Profile) connection and SCO audio channel state.
 * Emits state changes cleanly without polling.
 */
class BluetoothBridgeMonitor(
    private val context: Context,
    private val onConnectionChanged: (isConnected: Boolean, deviceName: String, isScoActive: Boolean) -> Unit
) : AutoCloseable {

    companion object {
        private const val TAG = "BluetoothBridgeMonitor"
    }

    private val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
    private val bluetoothAdapter: BluetoothAdapter? = bluetoothManager?.adapter

    private var bluetoothHeadset: BluetoothHeadset? = null
    private var isReceiverRegistered = false

    private val profileListener = object : BluetoothProfile.ServiceListener {
        override fun onServiceConnected(profile: Int, proxy: BluetoothProfile) {
            if (profile == BluetoothProfile.HEADSET) {
                bluetoothHeadset = proxy as BluetoothHeadset
                Log.d(TAG, "BluetoothHeadset proxy connected")
                evaluateCurrentState()
            }
        }

        override fun onServiceDisconnected(profile: Int) {
            if (profile == BluetoothProfile.HEADSET) {
                bluetoothHeadset = null
                Log.d(TAG, "BluetoothHeadset proxy disconnected")
                evaluateCurrentState()
            }
        }
    }

    private val bluetoothReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.action) {
                BluetoothHeadset.ACTION_CONNECTION_STATE_CHANGED,
                BluetoothDevice.ACTION_ACL_CONNECTED,
                BluetoothDevice.ACTION_ACL_DISCONNECTED,
                BluetoothAdapter.ACTION_CONNECTION_STATE_CHANGED -> {
                    evaluateCurrentState()
                }
                AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED -> {
                    val scoState = intent.getIntExtra(
                        AudioManager.EXTRA_SCO_AUDIO_STATE,
                        AudioManager.SCO_AUDIO_STATE_DISCONNECTED
                    )
                    val isScoActive = scoState == AudioManager.SCO_AUDIO_STATE_CONNECTED
                    evaluateCurrentState(forceScoState = isScoActive)
                }
            }
        }
    }

    fun start() {
        bluetoothAdapter?.getProfileProxy(context, profileListener, BluetoothProfile.HEADSET)

        val filter = IntentFilter().apply {
            addAction(BluetoothHeadset.ACTION_CONNECTION_STATE_CHANGED)
            addAction(BluetoothHeadset.ACTION_AUDIO_STATE_CHANGED)
            addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
            addAction(BluetoothDevice.ACTION_ACL_DISCONNECTED)
            addAction(BluetoothAdapter.ACTION_CONNECTION_STATE_CHANGED)
            addAction(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED)
        }

        if (!isReceiverRegistered) {
            context.registerReceiver(bluetoothReceiver, filter)
            isReceiverRegistered = true
        }

        evaluateCurrentState()
    }

    fun evaluateCurrentState(forceScoState: Boolean? = null) {
        val hasPermission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.BLUETOOTH_CONNECT
            ) == PackageManager.PERMISSION_GRANTED
        } else {
            true
        }

        var isConnected = false
        var deviceName = ""

        if (hasPermission && bluetoothHeadset != null) {
            try {
                val connectedDevices = bluetoothHeadset?.connectedDevices
                if (!connectedDevices.isNullOrEmpty()) {
                    val firstDevice = connectedDevices.first()
                    isConnected = true
                    deviceName = firstDevice.name ?: firstDevice.address ?: "Bluetooth Host"
                }
            } catch (e: SecurityException) {
                Log.w(TAG, "Permission missing while querying connected devices", e)
            }
        }

        // Check if SCO audio is active
        val isScoActive = forceScoState ?: audioManager.isBluetoothScoOn

        onConnectionChanged(isConnected, deviceName, isScoActive)
    }

    override fun close() {
        if (isReceiverRegistered) {
            try {
                context.unregisterReceiver(bluetoothReceiver)
            } catch (e: Exception) {
                Log.w(TAG, "Error unregistering receiver", e)
            }
            isReceiverRegistered = false
        }

        bluetoothHeadset?.let { proxy ->
            bluetoothAdapter?.closeProfileProxy(BluetoothProfile.HEADSET, proxy)
            bluetoothHeadset = null
        }
    }
}
