package com.phonemicbridge.companion.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.util.Log

/**
 * Low-latency AudioTrack player for incoming PCM streaming.
 * Configured with PERFORMANCE_MODE_LOW_LATENCY to ensure latency < 20ms.
 * Designed with a zero-allocation audio loop.
 */
class PcmAudioTrackPlayer(
    val config: AudioStreamConfig = AudioStreamConfig()
) : AudioTrackPlayer {

    companion object {
        private const val TAG = "PcmAudioTrackPlayer"
    }

    private var audioTrack: AudioTrack? = null
    private var isPlaying = false

    override val isInitialized: Boolean
        get() = audioTrack?.state == AudioTrack.STATE_INITIALIZED

    override fun initialize(): Boolean {
        val channelMask = if (config.channelCount == 1) {
            AudioFormat.CHANNEL_OUT_MONO
        } else {
            AudioFormat.CHANNEL_OUT_STEREO
        }

        val encoding = when (config.bitsPerSample) {
            8 -> AudioFormat.ENCODING_PCM_8BIT
            16 -> AudioFormat.ENCODING_PCM_16BIT
            24, 32 -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                AudioFormat.ENCODING_PCM_FLOAT
            } else {
                AudioFormat.ENCODING_PCM_16BIT
            }
            else -> AudioFormat.ENCODING_PCM_16BIT
        }

        val minBufferSize = AudioTrack.getMinBufferSize(
            config.sampleRate,
            channelMask,
            encoding
        )

        val bufferSize = (minBufferSize * 2).coerceAtLeast(config.bytesPerFrame * 256)

        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
            .build()

        val format = AudioFormat.Builder()
            .setSampleRate(config.sampleRate)
            .setChannelMask(channelMask)
            .setEncoding(encoding)
            .build()

        val builder = AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setBufferSizeInBytes(bufferSize)
            .setTransferMode(AudioTrack.MODE_STREAM)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
        }

        return try {
            val track = builder.build()
            if (track.state == AudioTrack.STATE_INITIALIZED) {
                audioTrack = track
                track.play()
                isPlaying = true
                Log.d(TAG, "Initialized PcmAudioTrackPlayer with buffer size: $bufferSize (min: $minBufferSize)")
                true
            } else {
                Log.e(TAG, "AudioTrack initialization failed, state: ${track.state}")
                track.release()
                false
            }
        } catch (e: Exception) {
            Log.e(TAG, "Exception initializing AudioTrack", e)
            false
        }
    }

    /**
     * Writes PCM byte data into the AudioTrack.
     * Operates without allocations by using the caller's pre-allocated buffer directly.
     */
    override fun write(buffer: ByteArray, offset: Int, count: Int): Int {
        val track = audioTrack ?: return -1
        if (!isPlaying || count <= 0) return 0
        return track.write(buffer, offset, count, AudioTrack.WRITE_BLOCKING)
    }

    override fun stop() {
        isPlaying = false
        try {
            audioTrack?.let { track ->
                if (track.playState == AudioTrack.PLAYSTATE_PLAYING) {
                    track.pause()
                    track.flush()
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Error pausing AudioTrack", e)
        }
    }

    override fun close() {
        isPlaying = false
        try {
            audioTrack?.let { track ->
                track.stop()
                track.release()
            }
        } catch (e: Exception) {
            Log.w(TAG, "Error releasing AudioTrack", e)
        } finally {
            audioTrack = null
        }
    }
}
