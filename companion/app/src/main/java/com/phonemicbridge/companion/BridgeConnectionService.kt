package com.phonemicbridge.companion

import android.content.ComponentName
import android.content.Context
import android.os.Bundle
import android.telecom.Connection
import android.telecom.ConnectionRequest
import android.telecom.ConnectionService
import android.telecom.DisconnectCause
import android.telecom.PhoneAccount
import android.telecom.PhoneAccountHandle
import android.telecom.TelecomManager
import android.util.Log

/**
 * Telecom ConnectionService for Phone Mic Bridge.
 * Establishes a self-managed call session with Android's Telecom subsystem so that
 * the Bluetooth HFP / SCO link to the Windows PC is acknowledged by the OS as an
 * active call, preventing premature HCI teardown (HCI_ERR_PEER_USER).
 */
class BridgeConnectionService : ConnectionService() {

    companion object {
        private const val TAG = "BridgeConnService"
        const val ACCOUNT_ID = "PhoneMicBridgeVoipAccount"

        @Volatile
        var currentConnection: Connection? = null
            private set

        var onConnectionActive: (() -> Unit)? = null
        var onConnectionDisconnected: (() -> Unit)? = null

        fun getPhoneAccountHandle(context: Context): PhoneAccountHandle {
            return PhoneAccountHandle(
                ComponentName(context, BridgeConnectionService::class.java),
                ACCOUNT_ID
            )
        }

        fun registerPhoneAccountIfNeeded(context: Context) {
            try {
                val telecomManager = context.getSystemService(Context.TELECOM_SERVICE) as? TelecomManager
                    ?: return
                val handle = getPhoneAccountHandle(context)
                val account = PhoneAccount.builder(handle, "Phone Mic Bridge")
                    .setCapabilities(PhoneAccount.CAPABILITY_SELF_MANAGED)
                    .build()
                telecomManager.registerPhoneAccount(account)
                Log.d(TAG, "Registered self-managed PhoneAccount: $handle")
            } catch (e: Exception) {
                Log.e(TAG, "Failed to register PhoneAccount", e)
            }
        }

        fun startCallSession(context: Context): Boolean {
            return try {
                registerPhoneAccountIfNeeded(context)
                val telecomManager = context.getSystemService(Context.TELECOM_SERVICE) as? TelecomManager
                    ?: return false
                val handle = getPhoneAccountHandle(context)
                val extras = Bundle().apply {
                    putParcelable(TelecomManager.EXTRA_PHONE_ACCOUNT_HANDLE, handle)
                }
                telecomManager.addNewIncomingCall(handle, extras)
                Log.d(TAG, "Triggered addNewIncomingCall for Telecom session")
                true
            } catch (e: Exception) {
                Log.e(TAG, "Error starting Telecom call session", e)
                false
            }
        }

        fun endCallSession() {
            try {
                val conn = currentConnection
                if (conn != null) {
                    Log.d(TAG, "Ending active Telecom call session")
                    conn.setDisconnected(DisconnectCause(DisconnectCause.LOCAL))
                    conn.destroy()
                    currentConnection = null
                    onConnectionDisconnected?.invoke()
                }
            } catch (e: Exception) {
                Log.e(TAG, "Error ending Telecom call session", e)
            }
        }
    }

    override fun onCreateIncomingConnection(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ): Connection {
        Log.d(TAG, "onCreateIncomingConnection requested")
        val conn = object : Connection() {
            override fun onShowIncomingCallUi() {
                Log.d(TAG, "onShowIncomingCallUi -> auto-setting active")
                setActive()
                setAudioRoute(android.telecom.CallAudioState.ROUTE_BLUETOOTH)
            }

            override fun onAnswer() {
                Log.d(TAG, "onAnswer -> setActive")
                setActive()
                setAudioRoute(android.telecom.CallAudioState.ROUTE_BLUETOOTH)
            }

            @Suppress("DEPRECATION")
            override fun onCallAudioStateChanged(state: android.telecom.CallAudioState?) {
                super.onCallAudioStateChanged(state)
                Log.d(TAG, "onCallAudioStateChanged: route=${state?.route} btDevs=${state?.supportedBluetoothDevices}")
                if (state != null && state.route != android.telecom.CallAudioState.ROUTE_BLUETOOTH) {
                    setAudioRoute(android.telecom.CallAudioState.ROUTE_BLUETOOTH)
                }
            }

            override fun onAvailableCallEndpointsChanged(availableEndpoints: MutableList<android.telecom.CallEndpoint>) {
                super.onAvailableCallEndpointsChanged(availableEndpoints)
                Log.d(TAG, "onAvailableCallEndpointsChanged: ${availableEndpoints.map { "${it.endpointName}(${it.endpointType})" }}")
                val bt = availableEndpoints.firstOrNull { it.endpointType == android.telecom.CallEndpoint.TYPE_BLUETOOTH }
                if (bt != null) {
                    requestCallEndpointChange(bt, mainExecutor, object : android.os.OutcomeReceiver<Void, android.telecom.CallEndpointException> {
                        override fun onResult(result: Void?) {
                            Log.d(TAG, "CallEndpoint successfully changed to BLUETOOTH")
                        }
                        override fun onError(error: android.telecom.CallEndpointException) {
                            Log.w(TAG, "requestCallEndpointChange error: ${error.message}")
                        }
                    })
                }
            }

            override fun onCallEndpointChanged(callEndpoint: android.telecom.CallEndpoint) {
                super.onCallEndpointChanged(callEndpoint)
                Log.d(TAG, "onCallEndpointChanged: ${callEndpoint.endpointName} type=${callEndpoint.endpointType}")
            }

            override fun onDisconnect() {
                Log.d(TAG, "onDisconnect called")
                setDisconnected(DisconnectCause(DisconnectCause.LOCAL))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }

            override fun onAbort() {
                Log.d(TAG, "onAbort called")
                setDisconnected(DisconnectCause(DisconnectCause.CANCELED))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }
        }

        conn.connectionProperties = Connection.PROPERTY_SELF_MANAGED
        conn.audioModeIsVoip = true
        conn.setActive()
        conn.setAudioRoute(android.telecom.CallAudioState.ROUTE_BLUETOOTH)
        currentConnection = conn
        Log.d(TAG, "Self-managed connection created, set to ACTIVE and ROUTE_BLUETOOTH")
        onConnectionActive?.invoke()
        return conn
    }

    override fun onCreateOutgoingConnection(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ): Connection {
        Log.d(TAG, "onCreateOutgoingConnection requested")
        val conn = object : Connection() {
            override fun onDisconnect() {
                Log.d(TAG, "onDisconnect called (outgoing)")
                setDisconnected(DisconnectCause(DisconnectCause.LOCAL))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }

            override fun onAbort() {
                Log.d(TAG, "onAbort called (outgoing)")
                setDisconnected(DisconnectCause(DisconnectCause.CANCELED))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }
        }
        conn.connectionProperties = Connection.PROPERTY_SELF_MANAGED
        conn.audioModeIsVoip = true
        conn.setActive()
        conn.setAudioRoute(android.telecom.CallAudioState.ROUTE_BLUETOOTH)
        currentConnection = conn
        Log.d(TAG, "Self-managed outgoing connection created, set to ACTIVE and ROUTE_BLUETOOTH")
        onConnectionActive?.invoke()
        return conn
    }
}
