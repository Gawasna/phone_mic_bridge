package com.phonemicbridge.companion.audio

import java.io.Closeable

/**
 * Interface defining audio track playback contract.
 * Allows decoupling Android AudioTrack runtime from unit tests.
 */
interface AudioTrackPlayer : Closeable {
    val isInitialized: Boolean
    fun initialize(): Boolean
    fun write(buffer: ByteArray, offset: Int, count: Int): Int
    fun stop()
}
