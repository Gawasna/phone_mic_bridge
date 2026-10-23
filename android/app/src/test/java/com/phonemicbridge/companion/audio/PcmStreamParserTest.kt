package com.phonemicbridge.companion.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class PcmStreamParserTest {

    @Test
    fun parseHeader_WithValidHeader_ShouldExtractConfig() {
        val parser = PcmStreamParser()
        val headerBytes = ByteArray(12)
        // Magic "PCM1"
        headerBytes[0] = 0x50
        headerBytes[1] = 0x43
        headerBytes[2] = 0x4D
        headerBytes[3] = 0x31

        val buffer = ByteBuffer.wrap(headerBytes, 4, 8).order(ByteOrder.LITTLE_ENDIAN)
        buffer.putInt(48000) // sample rate
        buffer.putShort(2)     // 2 channels (stereo)
        buffer.putShort(16)    // 16-bit

        val config = parser.parseHeader(headerBytes)
        assertNotNull(config)
        assertEquals(48000, config!!.sampleRate)
        assertEquals(2, config.channelCount)
        assertEquals(16, config.bitsPerSample)
        assertEquals(4, config.bytesPerFrame)
    }

    @Test
    fun parseHeader_WithRawPcmData_ShouldReturnNull() {
        val parser = PcmStreamParser()
        val rawData = byteArrayOf(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B)
        val config = parser.parseHeader(rawData)
        assertNull(config)
    }

    @Test
    fun parseHeader_WithInsufficientBytes_ShouldReturnNull() {
        val parser = PcmStreamParser()
        val shortBytes = byteArrayOf(0x50, 0x43, 0x4D)
        val config = parser.parseHeader(shortBytes)
        assertNull(config)
    }

    @Test
    fun alignLength_ShouldAlignToFrameBoundary() {
        // Stereo 16-bit: 2 channels * 2 bytes = 4 bytes per frame
        val parser = PcmStreamParser(AudioStreamConfig(sampleRate = 48000, channelCount = 2, bitsPerSample = 16))
        assertEquals(4, parser.config.bytesPerFrame)
        assertEquals(100, parser.alignLength(100))
        assertEquals(100, parser.alignLength(101))
        assertEquals(100, parser.alignLength(102))
        assertEquals(100, parser.alignLength(103))
        assertEquals(104, parser.alignLength(104))

        // Mono 16-bit: 1 channel * 2 bytes = 2 bytes per frame
        val monoParser = PcmStreamParser(AudioStreamConfig(sampleRate = 16000, channelCount = 1, bitsPerSample = 16))
        assertEquals(2, monoParser.config.bytesPerFrame)
        assertEquals(100, monoParser.alignLength(100))
        assertEquals(100, monoParser.alignLength(101))
    }
}
