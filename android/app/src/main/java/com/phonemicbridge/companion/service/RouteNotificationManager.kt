package com.phonemicbridge.companion.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import com.phonemicbridge.companion.model.AudioRouteTarget
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
                "Phone Mic Bridge Audio Routing",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Shows active audio route status and quick controls"
                setShowBadge(false)
            }
            notificationManager.createNotificationChannel(channel)
        }
    }

    fun buildNotification(activeRoute: AudioRouteTarget): Notification {
        val openAppIntent = Intent(context, MainActivity::class.java).let {
            PendingIntent.getActivity(context, 0, it, PendingIntent.FLAG_IMMUTABLE)
        }

        val speakerIntent = createServiceActionIntent(ACTION_ROUTE_SPEAKER, 1)
        val earpieceIntent = createServiceActionIntent(ACTION_ROUTE_EARPIECE, 2)
        val stopIntent = createServiceActionIntent(ACTION_STOP, 3)

        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setContentTitle("Phone Mic Bridge Active")
            .setContentText("Enforced Route: ${activeRoute.displayName}")
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentIntent(openAppIntent)
            .setOngoing(true)
            .addAction(android.R.drawable.ic_lock_silent_mode_off, "Speaker", speakerIntent)
            .addAction(android.R.drawable.ic_btn_speak_now, "Earpiece", earpieceIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Stop", stopIntent)
            .build()
    }

    fun update(activeRoute: AudioRouteTarget) {
        notificationManager.notify(NOTIFICATION_ID, buildNotification(activeRoute))
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
