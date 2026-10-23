package com.phonemicbridge.companion.audio

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Audio stream configuration for ADB stream.
 */
data class AudioStreamConfig(
    val sampleRate: Int = 48000,
    val channelCount: Int = 2,
    val bitsPerSample: Int = 16
) {
    val bytesPerFrame: Int = channelCount * (bitsPerSample / 8)
}

/**
 * Lightweight parser for raw PCM audio chunks or header-prefixed PCM frames.
 * Supports zero-allocation streaming: parses directly into pre-allocated byte buffers.
 */
class PcmStreamParser(
    val config: AudioStreamConfig = AudioStreamConfig()
) {
    companion object {
        val MAGIC_HEADER = byteArrayOf(0x50, 0x43, 0x4D, 0x31) // "PCM1"
        const val HEADER_SIZE = 12 // 4 magic + 4 sampleRate + 2 channels + 2 bitsPerSample
    }

    /**
     * Checks if a buffer begins with the optional "PCM1" header and extracts stream config.
     * Returns parsed AudioStreamConfig if valid header found, or null if it's raw PCM data.
     */
    fun parseHeader(headerBytes: ByteArray, offset: Int = 0): AudioStreamConfig? {
        if (headerBytes.size - offset < HEADER_SIZE) return null
        for (i in MAGIC_HEADER.indices) {
            if (headerBytes[offset + i] != MAGIC_HEADER[i]) {
                return null
            }
        }
        val buffer = ByteBuffer.wrap(headerBytes, offset + 4, 8).order(ByteOrder.LITTLE_ENDIAN)
        val sampleRate = buffer.int
        val channels = buffer.short.toInt()
        val bitsPerSample = buffer.short.toInt()

        if (sampleRate in 8000..192000 && channels in 1..2 && bitsPerSample in listOf(8, 16, 24, 32)) {
            return AudioStreamConfig(
                sampleRate = sampleRate,
                channelCount = channels,
                bitsPerSample = bitsPerSample
            )
        }
        return null
    }

    /**
     * Validates frame alignment to avoid partial audio frames.
     * Uses provided customConfig if specified, otherwise falls back to default parser config.
     */
    fun alignLength(byteCount: Int, customConfig: AudioStreamConfig = config): Int {
        val frameSize = customConfig.bytesPerFrame.coerceAtLeast(1)
        return (byteCount / frameSize) * frameSize
    }
}
