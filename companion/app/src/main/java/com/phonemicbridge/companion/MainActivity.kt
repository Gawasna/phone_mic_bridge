package com.phonemicbridge.companion

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.phonemicbridge.companion.databinding.ActivityMainBinding
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.log10
import kotlin.math.sqrt

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var audioManager: AudioManager
    private val mainHandler = Handler(Looper.getMainLooper())
    private val logBuilder = StringBuilder()

    // VU meter
    private var audioRecord: AudioRecord? = null
    private var audioTrack: AudioTrack? = null
    private var vuJob: Job? = null
    private var playbackJob: Job? = null
    private val ioScope = CoroutineScope(Dispatchers.IO)

    // Proof Verification: Sample Recording & Playback
    private val recordedPcmList = ArrayList<ShortArray>()
    @Volatile private var isRecordingSample = false
    private val sampleTargetCount = 16000 * 5 // 5 seconds at 16kHz
    private var sampleRecordedCount = 0
    @Volatile private var isStartingVu = false
    private var playbackSampleJob: Job? = null

    // Timeout handler for HyperOS 8-second SCO delay
    private var scoTimeoutRunnable: Runnable? = null
    private var telecomActiveRunnable: Runnable? = null
    @Volatile private var isConnectingSco = false
    @Volatile private var isScoConnectedHandled = false

    private val commDeviceListener = AudioManager.OnCommunicationDeviceChangedListener { device ->
        appendLog("[COMM_LISTENER] Device changed: ${device?.productName} (${deviceTypeName(device?.type ?: -1)})")
        if (device?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO) {
            triggerFullDuplexSetup("OnCommunicationDeviceChangedListener")
        }
    }

    private fun triggerFullDuplexSetup(source: String) {
        if (isScoConnectedHandled) return
        isScoConnectedHandled = true
        scoTimeoutRunnable?.let { mainHandler.removeCallbacks(it) }
        appendLog("[SETUP] Triggering full-duplex setup from: $source")
        mainHandler.post { onScoConnected() }
    }
    
    // SCO state receiver - triggers full-duplex audio setup when HCI link is confirmed
    private val scoReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val state = intent.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1)
            val stateName = when (state) {
                AudioManager.SCO_AUDIO_STATE_CONNECTED    -> "SCO_CONNECTED"
                AudioManager.SCO_AUDIO_STATE_DISCONNECTED -> "SCO_DISCONNECTED"
                AudioManager.SCO_AUDIO_STATE_CONNECTING   -> "SCO_CONNECTING"
                AudioManager.SCO_AUDIO_STATE_ERROR        -> "SCO_ERROR"
                else -> "UNKNOWN($state)"
            }
            appendLog("[SCO_BROADCAST] $stateName")
            if (state == AudioManager.SCO_AUDIO_STATE_CONNECTED) {
                triggerFullDuplexSetup("scoReceiver (SCO_AUDIO_STATE_CONNECTED)")
            } else if (state == AudioManager.SCO_AUDIO_STATE_DISCONNECTED) {
                appendLog("[SCO_BROADCAST] SCO disconnected")
                isScoConnectedHandled = false
                stopVuMeter()
                updateScoUi(AudioManager.SCO_AUDIO_STATE_DISCONNECTED)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        audioManager = getSystemService(AUDIO_SERVICE) as AudioManager

        registerReceiver(
            scoReceiver,
            IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED),
            RECEIVER_EXPORTED
        )

        audioManager.addOnCommunicationDeviceChangedListener(mainExecutor, commDeviceListener)

        binding.btnAttempt1.setOnClickListener { connectSco() }
        binding.btnAttempt2.setOnClickListener { connectSco() } // Same method now
        binding.btnStop.setOnClickListener    { releaseAll() }
        binding.btnRecordSample.setOnClickListener { startRecordingSample() }
        binding.btnPlaybackSample.setOnClickListener { playRecordedSample() }

        BridgeConnectionService.onConnectionActive = {
            mainHandler.post {
                appendLog("[TELECOM] BridgeConnection ACTIVE (numActive=1 ready for Windows AT+CLCC)")
                telecomActiveRunnable?.let { mainHandler.removeCallbacks(it) }
                establishScoHardwareTransport()
            }
        }
        BridgeConnectionService.onBluetoothAudioRouteActive = {
            mainHandler.post {
                appendLog("[TELECOM] Bluetooth Call Audio Route Active!")
                triggerFullDuplexSetup("BridgeConnectionService (Bluetooth route)")
            }
        }
        BridgeConnectionService.onConnectionDisconnected = {
            mainHandler.post {
                appendLog("[TELECOM] BridgeConnection disconnected")
            }
        }

        appendLog("[INIT] SDK ${android.os.Build.VERSION.SDK_INT} | ${android.os.Build.MODEL}")
        appendLog("[INIT] BT enabled: ${isBtEnabled()}")
        logPairedDevices()
        logAvailableCommunicationDevices()
    }

    // =========================================================================
    // MAIN SCO CONNECTION METHOD (Telecom-First Architecture)
    // =========================================================================
    
    private fun connectSco() {
        appendLog("[CONNECT] ═══════════════════════════════════════")
        appendLog("[CONNECT] Starting SCO connection (Telecom-First architecture)")
        
        // PHASE 0: Pre-flight Checks
        if (!verifyAllPermissionsGranted()) {
            appendLog("[ABORT] Required permissions not granted")
            return
        }
        appendLog("[PHASE0] ✓ All permissions verified")
        
        // PHASE 1: Request Exclusive AudioFocus (Critical for HyperOS)
        if (!requestExclusiveAudioFocus()) {
            appendLog("[WARN] AudioFocus not granted - HyperOS may delay SCO")
        } else {
            appendLog("[PHASE1] ✓ Exclusive AudioFocus granted")
        }
        
        // PHASE 2A: Trigger Telecom Outgoing Call FIRST
        updateScoUi(-99) // PENDING state
        isConnectingSco = true
        
        // Fallback timer: If Telecom callback takes > 1.5s, proceed anyway
        telecomActiveRunnable?.let { mainHandler.removeCallbacks(it) }
        telecomActiveRunnable = Runnable {
            appendLog("[TELECOM] Fallback timer triggered, establishing SCO transport...")
            establishScoHardwareTransport()
        }
        mainHandler.postDelayed(telecomActiveRunnable!!, 1500)
        
        val callTriggered = BridgeConnectionService.startOutgoingCallSession(this)
        appendLog("[PHASE2A] Telecom Outgoing Call placed: $callTriggered (Waiting for ACTIVE state...)")
    }

    private fun establishScoHardwareTransport() {
        if (!isConnectingSco) return
        isConnectingSco = false
        telecomActiveRunnable?.let { mainHandler.removeCallbacks(it) }

        // PHASE 2B: System-Level SCO Establishment AFTER Telecom Call is Active
        audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
        appendLog("[PHASE2B] Audio mode set to MODE_IN_COMMUNICATION")
        
        val btScoDevice = audioManager.availableCommunicationDevices
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
            
        if (btScoDevice == null) {
            appendLog("[ABORT] No BT_SCO device in availableCommunicationDevices")
            appendLog("[ABORT] Available devices:")
            audioManager.availableCommunicationDevices.forEach { dev ->
                appendLog("[ABORT]   - ${deviceTypeName(dev.type)}: '${dev.productName}'")
            }
            return
        }
        
        appendLog("[PHASE2B] Found BT_SCO device: '${btScoDevice.productName}'")
        
        val success = audioManager.setCommunicationDevice(btScoDevice)
        if (!success) {
            appendLog("[ABORT] setCommunicationDevice() returned false")
            return
        }
        appendLog("[PHASE2B] ✓ setCommunicationDevice() successful with active Telecom call session")

        // Direct verification fallback: if device is already BT_SCO, trigger setup after brief settling window
        mainHandler.postDelayed({
            if (!isScoConnectedHandled && audioManager.communicationDevice?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO) {
                appendLog("[FALLBACK] BT_SCO is active communication device, triggering setup...")
                triggerFullDuplexSetup("Direct communicationDevice verification")
            }
        }, 600)
        
        // PHASE 3: Install 8-Second Timeout for HyperOS
        scoTimeoutRunnable = Runnable {
            if (audioManager.communicationDevice?.type != AudioDeviceInfo.TYPE_BLUETOOTH_SCO) {
                appendLog("[TIMEOUT] 8 seconds elapsed, SCO not connected")
                appendLog("[TIMEOUT] Falling back to SPEAKER")
                fallbackToSpeaker()
            }
        }
        mainHandler.postDelayed(scoTimeoutRunnable!!, 8000)
        appendLog("[PHASE3] ✓ 8-second timeout armed for HyperOS")
        appendLog("[PHASE3] Waiting for SCO_AUDIO_STATE_CONNECTED broadcast...")
    }

    // =========================================================================
    // SCO CONNECTED CALLBACK (Called from broadcast receiver)
    // =========================================================================
    
    private fun onScoConnected() {
        appendLog("[PHASE4] ═══════════════════════════════════════")
        appendLog("[PHASE4] SCO_CONNECTED received - preparing full-duplex")
        
        val scoInput = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
        val scoOutput = audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
        
        if (scoInput == null || scoOutput == null) {
            appendLog("[ERROR] SCO devices not found in hardware enumeration")
            return
        }
        
        appendLog("[PHASE4] ✓ SCO Input: '${scoInput.productName}' (id=${scoInput.id})")
        appendLog("[PHASE4] ✓ SCO Output: '${scoOutput.productName}' (id=${scoOutput.id})")
        
        val sampleRate = 16000
        val bufSize = AudioRecord.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        ).coerceAtLeast(3200)
        
        stopVuMeter()
        
        audioRecord = AudioRecord(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION,
            sampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            bufSize
        )
        
        if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
            appendLog("[ERROR] AudioRecord failed to initialize")
            audioRecord?.release()
            audioRecord = null
            return
        }
        appendLog("[PHASE5] ✓ AudioRecord created (VOICE_COMMUNICATION)")
        
        val trackBufSize = AudioTrack.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_OUT_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        ).coerceAtLeast(3200)
        
        audioTrack = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .build()
            )
            .setBufferSizeInBytes(trackBufSize)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        
        if (audioTrack?.state != AudioTrack.STATE_INITIALIZED) {
            appendLog("[ERROR] AudioTrack failed to initialize")
            audioTrack?.release()
            audioTrack = null
            return
        }
        appendLog("[PHASE5] ✓ AudioTrack created (USAGE_VOICE_COMMUNICATION)")
        
        // CRITICAL TIMING: Set preferred AFTER SCO connected, BEFORE start
        val recordPrefSet = audioRecord?.setPreferredDevice(scoInput)
        val trackPrefSet = audioTrack?.setPreferredDevice(scoOutput)
        
        appendLog("[PHASE6] ✓ AudioRecord.setPreferredDevice(): $recordPrefSet")
        appendLog("[PHASE6] ✓ AudioTrack.setPreferredDevice(): $trackPrefSet")
        
        audioRecord?.addOnRoutingChangedListener({ record ->
            val routed = record.routedDevice
            appendLog("[ROUTE] AudioRecord → ${routed?.productName} (${deviceTypeName(routed?.type ?: -1)})")
            mainHandler.post { updateRoutingUi(routed) }
        }, mainHandler)
        
        audioTrack?.addOnRoutingChangedListener({ track ->
            val routed = track.routedDevice
            appendLog("[ROUTE] AudioTrack → ${routed?.productName} (${deviceTypeName(routed?.type ?: -1)})")
        }, mainHandler)
        
        audioRecord?.startRecording()
        audioTrack?.play()
        
        appendLog("[PHASE7] ✓ AudioRecord.startRecording()")
        appendLog("[PHASE7] ✓ AudioTrack.play()")
        
        playbackJob = ioScope.launch {
            val silence = ShortArray(trackBufSize / 2)
            while (isActive && audioTrack?.playState == AudioTrack.PLAYSTATE_PLAYING) {
                audioTrack?.write(silence, 0, silence.size, AudioTrack.WRITE_BLOCKING)
            }
        }
        appendLog("[PHASE7] ✓ AudioTrack keep-alive loop started")
        
        startVuMeterLoop()
        
        appendLog("[SUCCESS] ═══════════════════════════════════════")
        appendLog("[SUCCESS] Full-duplex SCO streaming ACTIVE")
        
        updateScoUi(AudioManager.SCO_AUDIO_STATE_CONNECTED)
        mainHandler.post {
            binding.tvRecordState.text = "RECORDING"
            binding.btnRecordSample.isEnabled = true
        }
    }
    
    // =========================================================================
    // HELPER METHODS
    // =========================================================================
    
    private fun verifyAllPermissionsGranted(): Boolean {
        val required = mapOf(
            "RECORD_AUDIO" to Manifest.permission.RECORD_AUDIO,
            "BLUETOOTH_CONNECT" to Manifest.permission.BLUETOOTH_CONNECT,
            "MODIFY_AUDIO_SETTINGS" to Manifest.permission.MODIFY_AUDIO_SETTINGS
        )
        
        val missing = required.filter { (_, perm) ->
            ContextCompat.checkSelfPermission(this, perm) != PackageManager.PERMISSION_GRANTED
        }
        
        if (missing.isNotEmpty()) {
            appendLog("[PERM] Missing permissions: ${missing.keys.joinToString()}")
            ActivityCompat.requestPermissions(
                this,
                missing.values.toTypedArray(),
                1
            )
            return false
        }
        
        return true
    }
    
    private fun requestExclusiveAudioFocus(): Boolean {
        @Suppress("DEPRECATION")
        val result = audioManager.requestAudioFocus(
            null,
            AudioManager.STREAM_VOICE_CALL,
            AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE
        )
        return result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
    }
    
    private fun fallbackToSpeaker() {
        appendLog("[FALLBACK] Clearing BT_SCO, routing to SPEAKER")
        audioManager.clearCommunicationDevice()
        
        val speaker = audioManager.availableCommunicationDevices
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
        
        if (speaker != null) {
            audioManager.setCommunicationDevice(speaker)
            appendLog("[FALLBACK] Speaker device set: '${speaker.productName}'")
        }
        
        updateScoUi(AudioManager.SCO_AUDIO_STATE_ERROR)
    }

    // =========================================================================
    // RELEASE / CLEANUP
    // =========================================================================
    
    private fun releaseAll() {
        appendLog("[STOP] Releasing SCO session...")
        
        isConnectingSco = false
        isScoConnectedHandled = false
        telecomActiveRunnable?.let { mainHandler.removeCallbacks(it) }
        scoTimeoutRunnable?.let { mainHandler.removeCallbacks(it) }
        
        stopVuMeter()
        
        BridgeConnectionService.endCallSession()
        appendLog("[STOP] Ended Telecom Call Session")
        
        @Suppress("DEPRECATION")
        audioManager.abandonAudioFocus(null)
        
        audioManager.clearCommunicationDevice()
        audioManager.mode = AudioManager.MODE_NORMAL
        
        updateScoUi(AudioManager.SCO_AUDIO_STATE_DISCONNECTED)
        appendLog("[STOP] Released. Mode reset to NORMAL.")
    }

    // =========================================================================
    // VU METER LOOP
    // =========================================================================
    
    private fun startVuMeterLoop() {
        vuJob = ioScope.launch {
            val sampleRate = 16000
            val bufSize = AudioRecord.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(3200)
            val buffer = ShortArray(bufSize / 2)
            
            while (isActive && audioRecord?.recordingState == AudioRecord.RECORDSTATE_RECORDING) {
                val read = audioRecord?.read(buffer, 0, buffer.size) ?: break
                if (read > 0) {
                    // Handle sample recording if active
                    if (isRecordingSample) {
                        val chunk = buffer.copyOf(read)
                        synchronized(recordedPcmList) {
                            recordedPcmList.add(chunk)
                        }
                        sampleRecordedCount += read
                        val pct = (sampleRecordedCount * 100 / sampleTargetCount).coerceAtMost(100)
                        mainHandler.post {
                            binding.btnRecordSample.text = "RECORDING ($pct%)..."
                        }
                        if (sampleRecordedCount >= sampleTargetCount) {
                            isRecordingSample = false
                            mainHandler.post {
                                binding.btnRecordSample.text = "Record 5s"
                                binding.btnRecordSample.isEnabled = true
                                binding.btnPlaybackSample.isEnabled = true
                                appendLog("[REC] Captured 5s sample. Tap 'Play' to listen!")
                            }
                        }
                    }
                    
                    // Calculate VU meter
                    val rms = computeRms(buffer, read)
                    val dbfs = if (rms > 0) 20 * log10(rms / 32767.0) else -90.0
                    val percent = ((dbfs + 60) / 60.0 * 100).coerceIn(0.0, 100.0).toInt()
                    
                    mainHandler.post {
                        binding.vuMeter.progress = percent
                        binding.tvDbLevel.text = "%.1f dBFS".format(dbfs)
                        val color = when {
                            dbfs > -6  -> android.graphics.Color.parseColor("#EF4444")
                            dbfs > -20 -> android.graphics.Color.parseColor("#10B981")
                            dbfs > -40 -> android.graphics.Color.parseColor("#F59E0B")
                            else       -> android.graphics.Color.parseColor("#374151")
                        }
                        binding.tvDbLevel.setTextColor(color)
                    }
                }
            }
            mainHandler.post {
                binding.tvRecordState.text = "IDLE"
                binding.vuMeter.progress = 0
                binding.tvDbLevel.text = "--- dBFS"
            }
        }
    }

    private fun stopVuMeter() {
        vuJob?.cancel()
        vuJob = null
        audioRecord?.stop()
        audioRecord?.release()
        audioRecord = null

        playbackJob?.cancel()
        playbackJob = null
        try {
            audioTrack?.stop()
            audioTrack?.release()
        } catch (_: Exception) {}
        audioTrack = null

        isRecordingSample = false
        mainHandler.post {
            binding.tvRecordState.text = "IDLE"
            binding.vuMeter.progress = 0
            binding.tvDbLevel.text = "--- dBFS"
            binding.tvInputSource.text = "SOURCE: NONE"
            binding.tvInputSource.setTextColor(android.graphics.Color.parseColor("#6B7280"))
            binding.btnRecordSample.text = "Record 5s"
            binding.btnRecordSample.isEnabled = false
        }
    }

    private fun updateRoutingUi(routed: AudioDeviceInfo?) {
        val devName = routed?.let { "'${it.productName}' (${deviceTypeName(it.type)})" } ?: "UNKNOWN"
        val isSco = routed?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        binding.tvInputSource.text = if (isSco) "SOURCE: $devName" else "WARN: $devName (NOT SCO!)"
        binding.tvInputSource.setTextColor(
            if (isSco) android.graphics.Color.parseColor("#10B981")
            else android.graphics.Color.parseColor("#EF4444")
        )
    }

    private fun startRecordingSample() {
        if (audioRecord == null || audioRecord?.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            appendLog("[REC] Cannot record: AudioRecord is not running. Connect SCO first!")
            return
        }
        synchronized(recordedPcmList) {
            recordedPcmList.clear()
        }
        sampleRecordedCount = 0
        isRecordingSample = true
        binding.btnRecordSample.isEnabled = false
        binding.btnPlaybackSample.isEnabled = false
        binding.btnRecordSample.text = "RECORDING (0%)..."
        appendLog("[REC] Started recording 5s audio sample...")
    }

    private fun playRecordedSample() {
        val hasSamples = synchronized(recordedPcmList) { recordedPcmList.isNotEmpty() }
        if (!hasSamples) {
            appendLog("[PLAY] No recorded sample to play")
            return
        }
        binding.btnPlaybackSample.isEnabled = false
        binding.btnPlaybackSample.text = "PLAYING..."
        appendLog("[PLAY] Playing 5s sample out loud through phone speaker...")

        playbackSampleJob = ioScope.launch {
            try {
                val sampleRate = 16000
                val trackBufSize = AudioTrack.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_MONO,
                    AudioFormat.ENCODING_PCM_16BIT
                ).coerceAtLeast(3200)

                val audioAttributes = AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .setFlags(AudioAttributes.FLAG_AUDIBILITY_ENFORCED)
                    .build()

                val format = AudioFormat.Builder()
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .build()

                val player = AudioTrack.Builder()
                    .setAudioAttributes(audioAttributes)
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(trackBufSize)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()

                player.setVolume(1.0f)

                val speakerDevice = audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
                    .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                if (speakerDevice != null) {
                    player.setPreferredDevice(speakerDevice)
                }

                val snapshot = synchronized(recordedPcmList) { ArrayList(recordedPcmList) }
                var maxSample = 0
                for (chunk in snapshot) {
                    for (s in chunk) {
                        val v = kotlin.math.abs(s.toInt())
                        if (v > maxSample) maxSample = v
                    }
                }
                val gain = if (maxSample > 0) (28000.0f / maxSample).coerceIn(1.0f, 6.0f) else 1.0f
                appendLog("[PLAY] Playing 5s sample on phone speaker (gain: ${"%.1f".format(gain)}x, peak: $maxSample)...")

                player.play()
                for (chunk in snapshot) {
                    if (gain > 1.05f) {
                        val boosted = ShortArray(chunk.size) { i ->
                            (chunk[i] * gain).toInt().coerceIn(-32767, 32767).toShort()
                        }
                        player.write(boosted, 0, boosted.size, AudioTrack.WRITE_BLOCKING)
                    } else {
                        player.write(chunk, 0, chunk.size, AudioTrack.WRITE_BLOCKING)
                    }
                }
                player.stop()
                player.release()
                appendLog("[PLAY] Playback completed.")
            } catch (e: Exception) {
                appendLog("[PLAY] Playback error: ${e.message}")
            } finally {
                mainHandler.post {
                    binding.btnPlaybackSample.text = "Play (Speaker)"
                    binding.btnPlaybackSample.isEnabled = true
                }
            }
        }
    }

    private fun computeRms(buffer: ShortArray, count: Int): Double {
        var sum = 0.0
        for (i in 0 until count) sum += buffer[i].toLong() * buffer[i].toLong()
        return sqrt(sum / count)
    }

    // -------------------------------------------------------------------------
    // UI helpers
    // -------------------------------------------------------------------------
    private fun updateScoUi(state: Int) {
        mainHandler.post {
            when (state) {
                AudioManager.SCO_AUDIO_STATE_CONNECTED -> {
                    binding.tvScoState.text = "SCO CONNECTED"
                    binding.tvScoState.setTextColor(android.graphics.Color.parseColor("#10B981"))
                    binding.tvScoDetail.text = "HFP session active - VU meter running"
                }
                AudioManager.SCO_AUDIO_STATE_CONNECTING -> {
                    binding.tvScoState.text = "SCO CONNECTING..."
                    binding.tvScoState.setTextColor(android.graphics.Color.parseColor("#F59E0B"))
                    binding.tvScoDetail.text = "Waiting for HFP negotiation..."
                }
                AudioManager.SCO_AUDIO_STATE_DISCONNECTED -> {
                    binding.tvScoState.text = "DISCONNECTED"
                    binding.tvScoState.setTextColor(android.graphics.Color.parseColor("#EF4444"))
                    binding.tvScoDetail.text = "No active HFP session"
                }
                AudioManager.SCO_AUDIO_STATE_ERROR -> {
                    binding.tvScoState.text = "SCO ERROR"
                    binding.tvScoState.setTextColor(android.graphics.Color.parseColor("#EF4444"))
                    binding.tvScoDetail.text = "HFP negotiation failed"
                }
                -99 -> { // pending
                    binding.tvScoState.text = "PENDING..."
                    binding.tvScoState.setTextColor(android.graphics.Color.parseColor("#6B7280"))
                    binding.tvScoDetail.text = "Trigger sent, awaiting SCO broadcast"
                }
            }
        }
    }

    private fun appendLog(msg: String) {
        android.util.Log.d("PhoneMicBridge", msg)
        val ts = SimpleDateFormat("HH:mm:ss.SSS", Locale.getDefault()).format(Date())
        val line = "[$ts] $msg\n"
        logBuilder.append(line)
        mainHandler.post {
            binding.tvLog.text = logBuilder.toString()
        }
    }

    // -------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------
    private fun logPairedDevices() {
        try {
            val btManager = getSystemService(BluetoothManager::class.java)
            val adapter = btManager?.adapter
            if (ActivityCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT)
                == PackageManager.PERMISSION_GRANTED) {
                val bonded = adapter?.bondedDevices
                appendLog("[BT] Paired devices: ${bonded?.size ?: 0}")
                bonded?.forEach { dev ->
                    appendLog("[BT]   '${dev.name}' ${dev.address}")
                }
            } else {
                appendLog("[BT] BLUETOOTH_CONNECT permission missing - cannot list paired devices")
            }
        } catch (e: Exception) {
            appendLog("[BT] Error listing paired devices: ${e.message}")
        }
    }

    private fun logAvailableCommunicationDevices() {
        val devices = audioManager.availableCommunicationDevices
        appendLog("[COMM] Available comm devices at startup: ${devices.size}")
        devices.forEach { dev ->
            appendLog("[COMM]   type=${deviceTypeName(dev.type)} name='${dev.productName}'")
        }
    }

    private fun isBtEnabled(): Boolean {
        val btManager = getSystemService(BluetoothManager::class.java)
        return btManager?.adapter?.isEnabled ?: false
    }

    private fun deviceTypeName(type: Int): String = when (type) {
        AudioDeviceInfo.TYPE_BLUETOOTH_SCO      -> "BT_SCO"
        AudioDeviceInfo.TYPE_BLUETOOTH_A2DP     -> "BT_A2DP"
        AudioDeviceInfo.TYPE_BUILTIN_MIC        -> "BUILTIN_MIC"
        AudioDeviceInfo.TYPE_BUILTIN_EARPIECE   -> "EARPIECE"
        AudioDeviceInfo.TYPE_BUILTIN_SPEAKER    -> "SPEAKER"
        AudioDeviceInfo.TYPE_WIRED_HEADSET      -> "WIRED_HEADSET"
        AudioDeviceInfo.TYPE_USB_DEVICE         -> "USB"
        else -> "OTHER($type)"
    }

    // -------------------------------------------------------------------------
    // Permissions
    // -------------------------------------------------------------------------
    private fun checkPermissions(): Boolean {
        val needed = mutableListOf<String>()
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED) needed.add(Manifest.permission.RECORD_AUDIO)
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT)
            != PackageManager.PERMISSION_GRANTED) needed.add(Manifest.permission.BLUETOOTH_CONNECT)
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.MODIFY_AUDIO_SETTINGS)
            != PackageManager.PERMISSION_GRANTED) needed.add(Manifest.permission.MODIFY_AUDIO_SETTINGS)

        return if (needed.isNotEmpty()) {
            appendLog("[PERM] Requesting: ${needed.joinToString()}")
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 1)
            false
        } else true
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        grantResults.forEachIndexed { i, result ->
            appendLog("[PERM] ${permissions[i]}: ${if (result == PackageManager.PERMISSION_GRANTED) "GRANTED" else "DENIED"}")
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        releaseAll()
        unregisterReceiver(scoReceiver)
    }
}
