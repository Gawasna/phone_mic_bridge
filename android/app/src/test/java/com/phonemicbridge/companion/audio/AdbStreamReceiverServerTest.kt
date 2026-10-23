package com.phonemicbridge.companion.audio

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

class AdbStreamReceiverServerTest {

    private lateinit var testScope: CoroutineScope
    private var testPort: Int = 0

    class FakeAudioTrackPlayer : AudioTrackPlayer {
        var initialized = false
        var closed = false
        var writtenBytes = 0

        override val isInitialized: Boolean get() = initialized
        override fun initialize(): Boolean {
            initialized = true
            return true
        }
        override fun write(buffer: ByteArray, offset: Int, count: Int): Int {
            writtenBytes += count
            return count
        }
        override fun stop() {}
        override fun close() {
            closed = true
            initialized = false
        }
    }

    @Before
    fun setUp() {
        testScope = CoroutineScope(Dispatchers.IO)
        val socket = ServerSocket(0)
        testPort = socket.localPort
        socket.close()
    }

    @After
    fun tearDown() {
        testScope.cancel()
    }

    @Test
    fun startAndStop_ReleasesSocketPortCleanly() {
        val server = AdbStreamReceiverServer(
            port = testPort,
            scope = testScope,
            playerFactory = { FakeAudioTrackPlayer() }
        )

        assertTrue("Server should start successfully", server.start())
        assertTrue("Server should be running", server.isRunning)

        // Stop server
        server.stop()
        assertFalse("Server should not be running after stop", server.isRunning)

        // Port should be reusable immediately
        val checkSocket = ServerSocket(testPort)
        assertTrue("Port should be free and bound again", checkSocket.isBound)
        checkSocket.close()
    }

    @Test
    fun clientConnection_NotifiesStreamStateChanges() = runBlocking {
        val streamingActive = AtomicBoolean(false)
        var clientAddr = ""
        val fakePlayer = FakeAudioTrackPlayer()

        val server = AdbStreamReceiverServer(
            port = testPort,
            scope = testScope,
            playerFactory = { fakePlayer },
            onStreamStateChanged = { active, addr ->
                streamingActive.set(active)
                clientAddr = addr
            }
        )

        assertTrue(server.start())

        // Connect client socket
        val client = Socket("127.0.0.1", testPort)
        val os: OutputStream = client.getOutputStream()
        // Send PCM data chunk
        val testPcmChunk = ByteArray(512)
        os.write(testPcmChunk)
        os.flush()

        // Wait for server to process first read
        var retries = 20
        while (!streamingActive.get() && retries > 0) {
            delay(50)
            retries--
        }

        assertTrue("Streaming should be marked active", streamingActive.get())
        assertTrue("Client address should be captured", clientAddr.isNotBlank())
        assertTrue("Player should be initialized", fakePlayer.initialized)
        assertTrue("Bytes should be written to player", fakePlayer.writtenBytes > 0)

        // Close client
        client.close()

        retries = 20
        while (streamingActive.get() && retries > 0) {
            delay(50)
            retries--
        }

        assertFalse("Streaming should be marked inactive after client disconnect", streamingActive.get())
        assertTrue("Player should be closed upon disconnect", fakePlayer.closed)

        server.close()
    }
}
