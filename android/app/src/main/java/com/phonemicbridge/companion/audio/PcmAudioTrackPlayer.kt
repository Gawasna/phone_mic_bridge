package com.phonemicbridge.companion.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.util.Log

/**
 * Low-latency AudioTrack player for incoming raw PCM 16-bit LE audio stream.
 * Default format: 48000Hz, Stereo (or Mono), 16-bit PCM.
 * Configured with AudioTrack.PERFORMANCE_MODE_LOW_LATENCY.
 */
class PcmAudioTrackPlayer(
    val sampleRate: Int = DEFAULT_SAMPLE_RATE,
    val channelConfig: Int = DEFAULT_CHANNEL_CONFIG,
    val audioFormat: Int = DEFAULT_AUDIO_FORMAT
) : AutoCloseable {

    companion object {
        private const val TAG = "PcmAudioTrackPlayer"
        const val DEFAULT_SAMPLE_RATE = 48000
        const val DEFAULT_CHANNEL_CONFIG = AudioFormat.CHANNEL_OUT_STEREO
        const val DEFAULT_AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT
    }

    private var audioTrack: AudioTrack? = null
    private var isPlaying = false
    private val bufferSize: Int

    init {
        val minBufferSize = AudioTrack.getMinBufferSize(sampleRate, channelConfig, audioFormat)
        bufferSize = if (minBufferSize > 0) minBufferSize.coerceAtLeast(4096) else 8192
        initializeAudioTrack()
    }

    private fun initializeAudioTrack() {
        try {
            val attributes = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                .build()

            val format = AudioFormat.Builder()
                .setSampleRate(sampleRate)
                .setChannelMask(channelConfig)
                .setEncoding(audioFormat)
                .build()

            val builder = AudioTrack.Builder()
                .setAudioAttributes(attributes)
                .setAudioFormat(format)
                .setBufferSizeInBytes(bufferSize)
                .setTransferMode(AudioTrack.MODE_STREAM)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                builder.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            }

            audioTrack = builder.build()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to create AudioTrack: ${e.message}", e)
        }
    }

    @Synchronized
    fun start() {
        if (isPlaying) return
        try {
            audioTrack?.play()
            isPlaying = true
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start AudioTrack playback: ${e.message}", e)
        }
    }

    /**
     * Zero-allocation write: writes raw pcm byte array directly to AudioTrack.
     */
    fun write(buffer: ByteArray, offset: Int, size: Int): Int {
        val track = audioTrack ?: return -1
        if (!isPlaying) {
            start()
        }
        return try {
            track.write(buffer, offset, size, AudioTrack.WRITE_BLOCKING)
        } catch (e: Exception) {
            Log.e(TAG, "Error writing PCM buffer to AudioTrack: ${e.message}", e)
            -1
        }
    }

    @Synchronized
    fun stop() {
        if (!isPlaying) return
        try {
            audioTrack?.let {
                if (it.state == AudioTrack.STATE_INITIALIZED) {
                    it.stop()
                    it.flush()
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error stopping AudioTrack: ${e.message}", e)
        } finally {
            isPlaying = false
        }
    }

    @Synchronized
    override fun close() {
        stop()
        try {
            audioTrack?.release()
        } catch (e: Exception) {
            Log.e(TAG, "Error releasing AudioTrack: ${e.message}", e)
        } finally {
            audioTrack = null
        }
    }

    fun isInitialized(): Boolean = audioTrack?.state == AudioTrack.STATE_INITIALIZED
}
