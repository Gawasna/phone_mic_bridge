package com.phonemicbridge.companion.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import com.phonemicbridge.companion.R
import com.phonemicbridge.companion.model.AudioRouteTarget
import com.phonemicbridge.companion.model.BridgeConnectionState
import com.phonemicbridge.companion.ui.MainActivity

class RouteNotificationManager(
    private val context: Context
) {
    companion object {
        const val CHANNEL_ID = "phone_mic_bridge_audio_channel"
        const val NOTIFICATION_ID = 2001

        const val ACTION_ROUTE_SPEAKER = "com.phonemicbridge.companion.ACTION_ROUTE_SPEAKER"
        const val ACTION_ROUTE_EARPIECE = "com.phonemicbridge.companion.ACTION_ROUTE_EARPIECE"
        const val ACTION_ROUTE_BLUETOOTH = "com.phonemicbridge.companion.ACTION_ROUTE_BLUETOOTH"
        const val ACTION_STOP = "com.phonemicbridge.companion.ACTION_STOP"
    }

    private val notificationManager =
        context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

    init {
        createChannel()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                context.getString(R.string.service_channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = context.getString(R.string.service_channel_desc)
                setShowBadge(false)
            }
            notificationManager.createNotificationChannel(channel)
        }
    }

    fun buildNotification(
        activeRoute: AudioRouteTarget,
        bridgeState: BridgeConnectionState = BridgeConnectionState.DISCONNECTED
    ): Notification {
        val openAppIntent = Intent(context, MainActivity::class.java).let {
            PendingIntent.getActivity(context, 0, it, PendingIntent.FLAG_IMMUTABLE)
        }

        val speakerIntent = createServiceActionIntent(ACTION_ROUTE_SPEAKER, 1)
        val earpieceIntent = createServiceActionIntent(ACTION_ROUTE_EARPIECE, 2)
        val stopIntent = createServiceActionIntent(ACTION_STOP, 3)

        val linkStatus = when (bridgeState) {
            BridgeConnectionState.STREAMING_ACTIVE -> context.getString(R.string.bridge_status_streaming)
            BridgeConnectionState.STANDBY -> context.getString(R.string.bridge_status_standby)
            BridgeConnectionState.DISCONNECTED -> context.getString(R.string.bridge_status_disconnected)
        }

        val title = context.getString(R.string.notification_title, linkStatus)
        val routeText = context.getString(R.string.notification_route_desc, activeRoute.displayName)

        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(routeText)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentIntent(openAppIntent)
            .setOngoing(true)
            .addAction(android.R.drawable.ic_lock_silent_mode_off, context.getString(R.string.notif_action_speaker), speakerIntent)
            .addAction(android.R.drawable.ic_btn_speak_now, context.getString(R.string.notif_action_earpiece), earpieceIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, context.getString(R.string.notif_action_stop), stopIntent)
            .build()
    }

    fun update(
        activeRoute: AudioRouteTarget,
        bridgeState: BridgeConnectionState = BridgeConnectionState.DISCONNECTED
    ) {
        notificationManager.notify(NOTIFICATION_ID, buildNotification(activeRoute, bridgeState))
    }

    private fun createServiceActionIntent(action: String, requestCode: Int): PendingIntent {
        val intent = Intent(context, AudioRouteService::class.java).apply {
            this.action = action
        }
        return PendingIntent.getService(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE
        )
    }
}
