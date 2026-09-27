package com.phonemicbridge.companion

import android.content.ComponentName
import android.content.Context
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Bundle
import android.telecom.CallAudioState
import android.telecom.CallEndpoint
import android.telecom.CallEndpointException
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
 * Establishes a self-managed outgoing call session with Android's Telecom subsystem so that
 * BluetoothInCallService acknowledges an ACTIVE call, causing Android to respond to Windows
 * AT+CLCC queries with numActive=1 instead of IDLE.
 * This prevents Windows BthHFAud.sys from terminating the SCO channel via HCI_ERR_PEER_USER (0x13).
 */
class BridgeConnectionService : ConnectionService() {

    companion object {
        private const val TAG = "BridgeConnService"
        const val ACCOUNT_ID = "PhoneMicBridgeVoipAccount"

        @Volatile
        var currentConnection: Connection? = null
            private set

        var onConnectionActive: (() -> Unit)? = null
        var onBluetoothAudioRouteActive: (() -> Unit)? = null
        var onConnectionDisconnected: (() -> Unit)? = null

        fun getPhoneAccountHandle(context: Context): PhoneAccountHandle {
            return PhoneAccountHandle(
                ComponentName(context, BridgeConnectionService::class.java),
                ACCOUNT_ID
            )
        }

        fun registerPhoneAccountIfNeeded(context: Context): Boolean {
            return try {
                val telecomManager = context.getSystemService(Context.TELECOM_SERVICE) as? TelecomManager
                    ?: return false
                val handle = getPhoneAccountHandle(context)
                val account = PhoneAccount.builder(handle, "Phone Mic Bridge")
                    .setCapabilities(PhoneAccount.CAPABILITY_SELF_MANAGED)
                    .setIcon(Icon.createWithResource(context, R.mipmap.ic_launcher))
                    .setShortDescription("Phone Mic Bridge Audio Engine")
                    .build()
                telecomManager.registerPhoneAccount(account)
                Log.d(TAG, "Registered self-managed PhoneAccount: $handle")
                true
            } catch (e: Exception) {
                Log.e(TAG, "Failed to register PhoneAccount", e)
                false
            }
        }

        fun startOutgoingCallSession(context: Context): Boolean {
            return try {
                registerPhoneAccountIfNeeded(context)
                val telecomManager = context.getSystemService(Context.TELECOM_SERVICE) as? TelecomManager
                    ?: return false
                val handle = getPhoneAccountHandle(context)

                val isPermitted = telecomManager.isOutgoingCallPermitted(handle)
                Log.d(TAG, "isOutgoingCallPermitted for $handle: $isPermitted")

                val uri = Uri.fromParts("tel", "1000", null)
                val extras = Bundle().apply {
                    putParcelable(TelecomManager.EXTRA_PHONE_ACCOUNT_HANDLE, handle)
                }

                Log.d(TAG, "Invoking telecomManager.placeCall() with self-managed account...")
                telecomManager.placeCall(uri, extras)
                Log.d(TAG, "placeCall() successfully invoked")
                true
            } catch (e: Exception) {
                Log.e(TAG, "Error starting Telecom outgoing call session", e)
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
                } else {
                    Log.d(TAG, "endCallSession called but currentConnection is null")
                }
            } catch (e: Exception) {
                Log.e(TAG, "Error ending Telecom call session", e)
            }
        }
    }

    override fun onCreateOutgoingConnection(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ): Connection {
        Log.d(TAG, "onCreateOutgoingConnection requested: request=$request")

        val conn = object : Connection() {
            @Suppress("DEPRECATION")
            override fun onCallAudioStateChanged(state: CallAudioState?) {
                super.onCallAudioStateChanged(state)
                Log.d(TAG, "[OutgoingConn] onCallAudioStateChanged: route=${state?.route} btDevs=${state?.supportedBluetoothDevices}")
                if (state != null && state.route == CallAudioState.ROUTE_BLUETOOTH) {
                    onBluetoothAudioRouteActive?.invoke()
                } else if (state != null && state.route != CallAudioState.ROUTE_BLUETOOTH) {
                    setAudioRoute(CallAudioState.ROUTE_BLUETOOTH)
                }
            }

            override fun onAvailableCallEndpointsChanged(availableEndpoints: MutableList<CallEndpoint>) {
                super.onAvailableCallEndpointsChanged(availableEndpoints)
                Log.d(TAG, "[OutgoingConn] onAvailableCallEndpointsChanged: ${availableEndpoints.map { "${it.endpointName}(${it.endpointType})" }}")
                val bt = availableEndpoints.firstOrNull { it.endpointType == CallEndpoint.TYPE_BLUETOOTH }
                if (bt != null) {
                    requestCallEndpointChange(bt, mainExecutor, object : android.os.OutcomeReceiver<Void, CallEndpointException> {
                        override fun onResult(result: Void?) {
                            Log.d(TAG, "[OutgoingConn] CallEndpoint successfully changed to BLUETOOTH")
                            onBluetoothAudioRouteActive?.invoke()
                        }
                        override fun onError(error: CallEndpointException) {
                            Log.w(TAG, "[OutgoingConn] requestCallEndpointChange error: ${error.message}")
                        }
                    })
                }
            }

            override fun onCallEndpointChanged(callEndpoint: CallEndpoint) {
                super.onCallEndpointChanged(callEndpoint)
                Log.d(TAG, "[OutgoingConn] onCallEndpointChanged: ${callEndpoint.endpointName} type=${callEndpoint.endpointType}")
                if (callEndpoint.endpointType == CallEndpoint.TYPE_BLUETOOTH) {
                    onBluetoothAudioRouteActive?.invoke()
                }
            }

            override fun onDisconnect() {
                Log.d(TAG, "[OutgoingConn] onDisconnect called")
                setDisconnected(DisconnectCause(DisconnectCause.LOCAL))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }

            override fun onAbort() {
                Log.d(TAG, "[OutgoingConn] onAbort called")
                setDisconnected(DisconnectCause(DisconnectCause.CANCELED))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }
        }

        conn.connectionProperties = Connection.PROPERTY_SELF_MANAGED
        conn.audioModeIsVoip = true

        val callAddress = request?.address ?: Uri.fromParts("tel", "1000", null)
        conn.setAddress(callAddress, TelecomManager.PRESENTATION_ALLOWED)
        conn.setCallerDisplayName("Host PC", TelecomManager.PRESENTATION_ALLOWED)

        // State machine progression: DIALING -> ACTIVE
        conn.setDialing()
        Log.d(TAG, "[OutgoingConn] State set to DIALING with address $callAddress")
        conn.setActive()
        Log.d(TAG, "[OutgoingConn] State set to ACTIVE")

        @Suppress("DEPRECATION")
        conn.setAudioRoute(CallAudioState.ROUTE_BLUETOOTH)

        currentConnection = conn
        Log.d(TAG, "Self-managed outgoing connection created, set to ACTIVE and ROUTE_BLUETOOTH")
        onConnectionActive?.invoke()
        return conn
    }

    override fun onCreateOutgoingConnectionFailed(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ) {
        super.onCreateOutgoingConnectionFailed(connectionManagerPhoneAccount, request)
        Log.e(TAG, "onCreateOutgoingConnectionFailed! account=$connectionManagerPhoneAccount request=$request")
    }

    override fun onCreateIncomingConnection(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ): Connection {
        Log.d(TAG, "onCreateIncomingConnection requested (fallback stub)")
        val conn = object : Connection() {
            override fun onDisconnect() {
                setDisconnected(DisconnectCause(DisconnectCause.LOCAL))
                destroy()
                currentConnection = null
                onConnectionDisconnected?.invoke()
            }
        }
        conn.connectionProperties = Connection.PROPERTY_SELF_MANAGED
        conn.audioModeIsVoip = true
        conn.setActive()
        currentConnection = conn
        return conn
    }

    override fun onCreateIncomingConnectionFailed(
        connectionManagerPhoneAccount: PhoneAccountHandle?,
        request: ConnectionRequest?
    ) {
        super.onCreateIncomingConnectionFailed(connectionManagerPhoneAccount, request)
        Log.e(TAG, "onCreateIncomingConnectionFailed! account=$connectionManagerPhoneAccount request=$request")
    }
}
