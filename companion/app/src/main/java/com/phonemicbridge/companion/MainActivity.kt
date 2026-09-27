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
    private var playbackSampleJob: Job? = null

    // SCO state receiver (logging / diagnostics only)
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

        // Register modern communication device listener as single source of truth
        audioManager.addOnCommunicationDeviceChangedListener(mainExecutor) { device ->
            val devName = device?.let { "'${it.productName}' type=${deviceTypeName(it.type)}" } ?: "null"
            appendLog("[COMM_CB] Device changed: $devName")
            if (device?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO) {
                appendLog("[COMM_CB] SCO device ACTIVE -> starting VU meter")
                updateScoUi(AudioManager.SCO_AUDIO_STATE_CONNECTED)
                startVuMeter()
                val scoInput = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
                    .firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
                if (scoInput != null && audioRecord != null) {
                    val ok = audioRecord?.setPreferredDevice(scoInput)
                    appendLog("[COMM_CB] Set AudioRecord.preferredDevice('${scoInput.productName}'): $ok")
                }
            } else {
                appendLog("[COMM_CB] Non-SCO device active ($devName)")
            }
        }

        binding.btnAttempt1.setOnClickListener { tryAttempt1() }
        binding.btnAttempt2.setOnClickListener { tryAttempt2() }
        binding.btnStop.setOnClickListener    { releaseAll() }
        binding.btnRecordSample.setOnClickListener { startRecordingSample() }
        binding.btnPlaybackSample.setOnClickListener { playRecordedSample() }

        appendLog("[INIT] SDK ${android.os.Build.VERSION.SDK_INT} | ${android.os.Build.MODEL}")
        appendLog("[INIT] BT enabled: ${isBtEnabled()}")
        logPairedDevices()
        logAvailableCommunicationDevices()
    }

    // -------------------------------------------------------------------------
    // Attempt 1: Legacy startBluetoothSco (API 16+, deprecated in 31 but still works)
    // -------------------------------------------------------------------------
    @Suppress("DEPRECATION")
    private fun tryAttempt1() {
        if (!checkPermissions()) return
        appendLog("[A1] Attempt 1: setMode(IN_COMMUNICATION) + startBluetoothSco()")
        audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
        audioManager.startBluetoothSco()
        audioManager.isBluetoothScoOn = true
        appendLog("[A1] startBluetoothSco() called - waiting for broadcast...")
        updateScoUi(-99) // pending
    }

    // -------------------------------------------------------------------------
    // Attempt 2: Modern setCommunicationDevice (API 31+)
    // -------------------------------------------------------------------------
    private fun tryAttempt2() {
        if (!checkPermissions()) return
        appendLog("[A2] Attempt 2: setCommunicationDevice() - setting MODE_IN_COMMUNICATION...")
        audioManager.mode = AudioManager.MODE_IN_COMMUNICATION

        val available = audioManager.availableCommunicationDevices
        appendLog("[A2] Available comm devices: ${available.size}")
        available.forEach { dev ->
            appendLog("[A2]   type=${deviceTypeName(dev.type)} id=${dev.id} name='${dev.productName}'")
        }

        val btScoDevice = available.firstOrNull {
            it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        }

        if (btScoDevice == null) {
            appendLog("[A2] No TYPE_BLUETOOTH_SCO found. Attempting auto-connect to paired PC...")
            connectPairedPc()
            return
        }

        appendLog("[A2] Found BT SCO device: '${btScoDevice.productName}' id=${btScoDevice.id}")
        val success = audioManager.setCommunicationDevice(btScoDevice)
        appendLog("[A2] setCommunicationDevice() returned: $success")
        if (success) {
            updateScoUi(AudioManager.SCO_AUDIO_STATE_CONNECTED)
            startVuMeter()
        }
    }

    private fun connectPairedPc() {
        try {
            val btManager = getSystemService(BluetoothManager::class.java)
            val adapter = btManager?.adapter ?: return
            if (ActivityCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                appendLog("[BT_CONN] BLUETOOTH_CONNECT not granted")
                return
            }
            val msi = adapter.bondedDevices.firstOrNull { 
                it.name?.contains("MSI", ignoreCase = true) == true || it.address.equals("14:75:5B:44:76:0D", ignoreCase = true) 
            }
            if (msi != null) {
                appendLog("[BT_CONN] Initiating connection to '${msi.name}' (${msi.address})...")
                val connectMethod = msi.javaClass.getMethod("connect")
                val res = connectMethod.invoke(msi)
                appendLog("[BT_CONN] connect() invoked, res=$res. Wait ~2s then tap Attempt 2 again.")
            } else {
                appendLog("[BT_CONN] No paired PC (MSI) found in bonded devices")
            }
        } catch (e: Exception) {
            appendLog("[BT_CONN] Error calling connect(): ${e.message}")
        }
    }

    // -------------------------------------------------------------------------
    // Release all
    // -------------------------------------------------------------------------
    @Suppress("DEPRECATION")
    private fun releaseAll() {
        appendLog("[STOP] Releasing SCO session...")
        stopVuMeter()
        audioManager.isBluetoothScoOn = false
        audioManager.stopBluetoothSco()
        audioManager.clearCommunicationDevice()
        audioManager.mode = AudioManager.MODE_NORMAL
        updateScoUi(AudioManager.SCO_AUDIO_STATE_DISCONNECTED)
        appendLog("[STOP] Released. Mode reset to NORMAL.")
    }

    // -------------------------------------------------------------------------
    // VU Meter - reads from VOICE_COMMUNICATION source (BT SCO mic)
    // -------------------------------------------------------------------------
    private fun startVuMeter() {
        if (audioRecord != null && audioRecord?.recordingState == AudioRecord.RECORDSTATE_RECORDING) {
            appendLog("[VU] AudioRecord already active, skipping restart")
            return
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED) {
            appendLog("[VU] RECORD_AUDIO permission missing - cannot start VU meter")
            return
        }
        stopVuMeter()

        val sampleRate = 16000
        val bufSize = AudioRecord.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        ).coerceAtLeast(3200)

        audioRecord = AudioRecord(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION,
            sampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            bufSize
        )

        if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
            appendLog("[VU] AudioRecord failed to initialize (state=${audioRecord?.state})")
            audioRecord?.release()
            audioRecord = null
            return
        }

        // Explicitly set preferred device to Bluetooth SCO input if available
        val scoInputDevice = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
        if (scoInputDevice != null) {
            val prefSet = audioRecord?.setPreferredDevice(scoInputDevice)
            appendLog("[VU] Set AudioRecord.preferredDevice('${scoInputDevice.productName}', id=${scoInputDevice.id}): $prefSet")
        } else {
            appendLog("[VU] No SCO input device in GET_DEVICES_INPUTS yet")
        }

        // Register routing changed listener on AudioRecord
        audioRecord?.addOnRoutingChangedListener({ record ->
            val routed = record.routedDevice
            appendLog("[VU_ROUTE] AudioRecord routed to: ${routed?.productName} type=${deviceTypeName(routed?.type ?: -1)}")
            mainHandler.post { updateRoutingUi(routed) }
        }, mainHandler)

        audioRecord?.startRecording()
        mainHandler.post { 
            binding.tvRecordState.text = "RECORDING"
            binding.btnRecordSample.isEnabled = true
            updateRoutingUi(audioRecord?.routedDevice)
        }
        appendLog("[VU] AudioRecord started - source: VOICE_COMMUNICATION")

        // Also start keep-alive AudioTrack to maintain full-duplex SCO link
        try {
            val trackBufSize = AudioTrack.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(3200)

            val audioAttributes = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                .build()

            val format = AudioFormat.Builder()
                .setSampleRate(sampleRate)
                .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .build()

            audioTrack = AudioTrack.Builder()
                .setAudioAttributes(audioAttributes)
                .setAudioFormat(format)
                .setBufferSizeInBytes(trackBufSize)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()

            audioTrack?.play()
            playbackJob = ioScope.launch {
                val silence = ShortArray(trackBufSize / 2)
                while (audioTrack?.playState == AudioTrack.PLAYSTATE_PLAYING) {
                    audioTrack?.write(silence, 0, silence.size, AudioTrack.WRITE_BLOCKING)
                }
            }
            appendLog("[VU] AudioTrack keep-alive started")
        } catch (e: Exception) {
            appendLog("[VU] AudioTrack init warning: ${e.message}")
        }

        vuJob = ioScope.launch {
            val buffer = ShortArray(bufSize / 2)
            while (audioRecord?.recordingState == AudioRecord.RECORDSTATE_RECORDING) {
                val read = audioRecord?.read(buffer, 0, buffer.size) ?: break
                if (read > 0) {
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
                                appendLog("[REC] Captured 5s sample (${sampleRecordedCount} samples). Tap 'Play' to listen via speaker!")
                            }
                        }
                    }

                    val rms = computeRms(buffer, read)
                    val dbfs = if (rms > 0) 20 * log10(rms / 32767.0) else -90.0
                    val percent = ((dbfs + 60) / 60.0 * 100).coerceIn(0.0, 100.0).toInt()

                    mainHandler.post {
                        binding.vuMeter.progress = percent
                        binding.tvDbLevel.text = "%.1f dBFS".format(dbfs)
                        // Color: green > -20, amber > -40, red otherwise
                        val color = when {
                            dbfs > -6  -> android.graphics.Color.parseColor("#EF4444") // clip warn
                            dbfs > -20 -> android.graphics.Color.parseColor("#10B981") // good
                            dbfs > -40 -> android.graphics.Color.parseColor("#F59E0B") // low
                            else       -> android.graphics.Color.parseColor("#374151") // silence
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
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
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

                val speakerDevice = audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
                    .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                if (speakerDevice != null) {
                    player.setPreferredDevice(speakerDevice)
                }

                player.play()
                val snapshot = synchronized(recordedPcmList) { ArrayList(recordedPcmList) }
                for (chunk in snapshot) {
                    player.write(chunk, 0, chunk.size, AudioTrack.WRITE_BLOCKING)
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
