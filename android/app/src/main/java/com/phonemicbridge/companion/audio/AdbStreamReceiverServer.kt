package com.phonemicbridge.companion.audio

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.InputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException

/**
 * Server that listens on a local TCP port (e.g. 127.0.0.1:34567) mapped via ADB forward.
 * Receives raw PCM streams and feeds them to AudioTrackPlayer.
 */
class AdbStreamReceiverServer(
    val port: Int = DEFAULT_PORT,
    private val scope: CoroutineScope = CoroutineScope(Dispatchers.IO),
    private val playerFactory: (AudioStreamConfig) -> AudioTrackPlayer = { config -> PcmAudioTrackPlayer(config) },
    private val onStreamStateChanged: (isActive: Boolean, clientAddress: String) -> Unit = { _, _ -> }
) : AutoCloseable {

    companion object {
        const val DEFAULT_PORT = 34567
        private const val TAG = "AdbStreamReceiver"
        private const val BUFFER_SIZE = 4096
    }

    private var serverSocket: ServerSocket? = null
    private var serverJob: Job? = null
    private var activePlayer: AudioTrackPlayer? = null
    private var activeClientSocket: Socket? = null

    @Volatile
    var isRunning: Boolean = false
        private set

    fun start(): Boolean {
        if (isRunning) return true

        return try {
            val server = ServerSocket()
            server.reuseAddress = true
            server.bind(InetSocketAddress("127.0.0.1", port))
            serverSocket = server
            isRunning = true

            serverJob = scope.launch {
                acceptLoop(server)
            }
            Log.d(TAG, "AdbStreamReceiverServer started on 127.0.0.1:$port")
            true
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start AdbStreamReceiverServer on port $port", e)
            close()
            false
        }
    }

    private suspend fun acceptLoop(server: ServerSocket) {
        while (scope.isActive && !server.isClosed) {
            try {
                val client = server.accept()
                val clientAddress = client.remoteSocketAddress.toString()
                Log.d(TAG, "Accepted ADB stream connection from: $clientAddress")

                // Disconnect previous client if any
                activeClientSocket?.close()
                activeClientSocket = client

                handleClientStream(client, clientAddress)
            } catch (e: SocketException) {
                // Expected when server socket is closed
                break
            } catch (e: Exception) {
                if (scope.isActive) {
                    Log.w(TAG, "Error accepting connection", e)
                }
            }
        }
    }

    private fun handleClientStream(client: Socket, clientAddress: String) {
        val readBuffer = ByteArray(BUFFER_SIZE)
        var player: AudioTrackPlayer? = null
        val parser = PcmStreamParser()

        try {
            client.tcpNoDelay = true
            val inputStream: InputStream = client.getInputStream()

            // Read initial chunk to check for optional header
            val firstRead = inputStream.read(readBuffer)
            if (firstRead <= 0) {
                client.close()
                return
            }

            val detectedConfig = parser.parseHeader(readBuffer, 0)
            val effectiveConfig = detectedConfig ?: AudioStreamConfig(sampleRate = 48000, channelCount = 2, bitsPerSample = 16)
            val dataOffset = if (detectedConfig != null) PcmStreamParser.HEADER_SIZE else 0
            val dataCount = firstRead - dataOffset

            player = playerFactory(effectiveConfig)
            player.initialize()
            activePlayer = player

            onStreamStateChanged(true, clientAddress)

            if (dataCount > 0) {
                val aligned = parser.alignLength(dataCount, effectiveConfig)
                if (aligned > 0) {
                    player.write(readBuffer, dataOffset, aligned)
                }
            }

            // Zero-allocation loop: continuously read into pre-allocated buffer
            while (scope.isActive && !client.isClosed) {
                val read = inputStream.read(readBuffer, 0, BUFFER_SIZE)
                if (read == -1) break
                if (read > 0) {
                    val aligned = parser.alignLength(read, effectiveConfig)
                    if (aligned > 0) {
                        player.write(readBuffer, 0, aligned)
                    }
                }
            }
        } catch (e: Exception) {
            Log.d(TAG, "Client stream ended: ${e.message}")
        } finally {
            onStreamStateChanged(false, "")
            try {
                player?.close()
            } catch (_: Exception) {}
            activePlayer = null

            try {
                client.close()
            } catch (_: Exception) {}
            if (activeClientSocket == client) {
                activeClientSocket = null
            }
        }
    }

    fun stop() {
        close()
    }

    override fun close() {
        isRunning = false
        serverJob?.cancel()
        serverJob = null

        try {
            activeClientSocket?.close()
        } catch (_: Exception) {}
        activeClientSocket = null

        try {
            activePlayer?.close()
        } catch (_: Exception) {}
        activePlayer = null

        try {
            serverSocket?.close()
        } catch (e: Exception) {
            Log.w(TAG, "Error closing server socket", e)
        }
        serverSocket = null
        onStreamStateChanged(false, "")
    }
}
