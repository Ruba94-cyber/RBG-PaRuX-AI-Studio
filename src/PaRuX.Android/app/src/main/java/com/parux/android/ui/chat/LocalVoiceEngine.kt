package com.parux.android.ui.chat

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaPlayer
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.os.SystemClock
import android.util.Base64
import com.parux.android.core.network.SpeechStreamFrame
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.max
import kotlin.math.sqrt
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.produceIn
import kotlinx.coroutines.isActive
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withContext

/**
 * Android-side audio transport for PaRuX Live Voice. It deliberately contains no speech model:
 * microphone PCM is sent only to the paired PaRuX Core, where local Parakeet STT runs, and
 * synthesized audio is streamed back from the local Kokoro TTS worker as PCM16 frames.
 *
 * Turn detection is energy based and device local. The ordinary capture path keeps a short pre-roll
 * so the first syllable is not clipped, learns the room noise floor before speech, stops after a
 * bounded silence window, and has hard listening/utterance timeouts. Playback opens a streaming
 * AudioTrack and, when supported by the device, a VOICE_COMMUNICATION recorder with Android
 * AcousticEchoCanceler so sustained near-end speech can interrupt playback. Barge-in cancels the
 * upstream HTTP flow, which in turn aborts the Core request and resets the resident TTS worker.
 */
class LocalVoiceEngine(private val context: Context) {

    enum class PlaybackResult { Completed, BargeIn, Stopped }

    companion object {
        const val SampleRate = 16_000
        const val TtsSampleRate = 24_000
        private const val FrameMilliseconds = 20
        private const val FrameSamples = SampleRate * FrameMilliseconds / 1_000
        private const val PreRollFrames = 15
        private const val SpeechStartFrames = 3
        private const val EndSilenceFrames = 40
        private const val MinimumSpeechMilliseconds = 260L
        private const val MaximumWaitMilliseconds = 12_000L
        private const val MaximumUtteranceMilliseconds = 20_000L
        private const val MinimumVoiceRms = 500.0
    }

    @Volatile private var activeRecorder: AudioRecord? = null
    @Volatile private var activePlayer: MediaPlayer? = null
    @Volatile private var activeTrack: AudioTrack? = null
    @Volatile private var stopRequested = false

    fun stopAll() {
        stopRequested = true
        stopRecorder()
        stopPlayback()
    }

    fun stopPlayback() {
        // Also flips the loop guard used by streaming playback. ChatViewModel cancels the matching
        // HTTP stream at the same time; this local flag guarantees an already-buffered PCM frame is
        // never written after the user explicitly interrupts playback.
        stopRequested = true

        val player = activePlayer
        activePlayer = null
        if (player != null) {
            runCatching { if (player.isPlaying) player.stop() }
            runCatching { player.release() }
        }

        val track = activeTrack
        activeTrack = null
        if (track != null) {
            runCatching { if (track.playState == AudioTrack.PLAYSTATE_PLAYING) track.stop() }
            runCatching { track.flush() }
            runCatching { track.release() }
        }
    }

    private fun stopRecorder() {
        val recorder = activeRecorder ?: return
        activeRecorder = null
        runCatching { if (recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) recorder.stop() }
        runCatching { recorder.release() }
    }

    @SuppressLint("MissingPermission")
    suspend fun captureUtterance(): ByteArray? = withContext(Dispatchers.IO) {
        stopRequested = false
        val recorder = createRecorder(MediaRecorder.AudioSource.VOICE_RECOGNITION)
        activeRecorder = recorder
        val startedAt = SystemClock.elapsedRealtime()
        var speechStartedAt = 0L
        var noiseFloor = 180.0
        var consecutiveVoice = 0
        var silenceFrames = 0
        var speechStarted = false
        val preRoll = ArrayDeque<ShortArray>(PreRollFrames)
        val pcm = ByteArrayOutputStream()
        val frame = ShortArray(FrameSamples)

        try {
            recorder.startRecording()
            if (recorder.recordingState != AudioRecord.RECORDSTATE_RECORDING)
                throw IllegalStateException("Android did not start microphone recording for PaRuX Live Voice.")

            while (!stopRequested) {
                val read = recorder.read(frame, 0, frame.size, AudioRecord.READ_BLOCKING)
                val now = SystemClock.elapsedRealtime()
                if (read < 0)
                    throw IllegalStateException("Android microphone capture failed (${audioReadError(read)}).")
                if (read == 0) {
                    if (!speechStarted && now - startedAt >= MaximumWaitMilliseconds) return@withContext null
                    if (speechStarted && now - speechStartedAt >= MaximumUtteranceMilliseconds) break
                    SystemClock.sleep(5)
                    continue
                }

                val samples = frame.copyOf(read)
                val rms = rms(samples)
                val threshold = max(MinimumVoiceRms, noiseFloor * 2.8)

                if (!speechStarted) {
                    preRoll.addLast(samples)
                    while (preRoll.size > PreRollFrames) preRoll.removeFirst()

                    if (rms >= threshold) {
                        consecutiveVoice++
                    } else {
                        consecutiveVoice = 0
                        noiseFloor = noiseFloor * 0.94 + rms * 0.06
                    }

                    if (consecutiveVoice >= SpeechStartFrames) {
                        speechStarted = true
                        speechStartedAt = now
                        for (buffer in preRoll) writePcm16(pcm, buffer)
                        preRoll.clear()
                        silenceFrames = 0
                    } else if (now - startedAt >= MaximumWaitMilliseconds) {
                        return@withContext null
                    }
                    continue
                }

                writePcm16(pcm, samples)
                if (rms >= threshold * 0.72) silenceFrames = 0 else silenceFrames++

                val spokenFor = now - speechStartedAt
                if (spokenFor >= MinimumSpeechMilliseconds && silenceFrames >= EndSilenceFrames) break
                if (spokenFor >= MaximumUtteranceMilliseconds) break
            }
        } finally {
            if (activeRecorder === recorder) activeRecorder = null
            runCatching { if (recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) recorder.stop() }
            runCatching { recorder.release() }
        }

        if (stopRequested || !speechStarted || pcm.size() < SampleRate / 5 * 2) null
        else PcmWavEncoder.encode(pcm.toByteArray(), SampleRate, 1, 16)
    }

    /**
     * Low-latency Live Voice playback. The Flow owns the HTTP call; returning early for barge-in or
     * Stop cancels collection and therefore closes/cancels the request instead of letting Core keep
     * synthesizing unheard audio. The start frame defines the one supported PCM format and every
     * audio frame is written as soon as it arrives.
     */
    @SuppressLint("MissingPermission")
    suspend fun playStreamingPcmWithBargeIn(frames: Flow<SpeechStreamFrame>): PlaybackResult = coroutineScope {
        stopRequested = false
        val channel = frames.produceIn(this)
        var track: AudioTrack? = null
        var detector: Deferred<Boolean>? = null
        var samplesWritten = 0L
        var sawDone = false

        try {
            while (currentCoroutineContext().isActive && !stopRequested && !sawDone) {
                var nextFrame: SpeechStreamFrame? = null
                var detectorCompleted = false
                val currentDetector = detector
                val interrupted = if (currentDetector == null) {
                    nextFrame = channel.receiveCatching().getOrNull()
                    false
                } else {
                    select<Boolean> {
                        channel.onReceiveCatching { received ->
                            nextFrame = received.getOrNull()
                            false
                        }
                        currentDetector.onAwait { value ->
                            detectorCompleted = true
                            value
                        }
                    }
                }
                if (detectorCompleted) detector = null
                if (interrupted) {
                    stopTrack(track)
                    return@coroutineScope PlaybackResult.BargeIn
                }

                val frame = nextFrame
                    ?: if (stopRequested) return@coroutineScope PlaybackResult.Stopped
                    else throw IllegalStateException("PaRuX continuous speech ended before a done frame.")

                when (frame.type) {
                    "start" -> {
                        if (track != null) throw IllegalStateException("PaRuX continuous speech emitted duplicate start.")
                        if (frame.sampleRate != TtsSampleRate || frame.channels != 1 || frame.format != "pcm_s16le")
                            throw IllegalStateException("PaRuX continuous speech returned an unsupported PCM format.")
                        val newTrack = createPlaybackTrack(frame.sampleRate)
                        track = newTrack
                        activeTrack = newTrack
                        newTrack.play()
                        detector = async(Dispatchers.IO) { detectBargeIn { isTrackRunning(newTrack) } }
                    }
                    "audio" -> {
                        val currentTrack = track ?: throw IllegalStateException("PaRuX continuous speech emitted audio before start.")
                        val pcm = try { Base64.decode(frame.base64Data, Base64.DEFAULT) }
                        catch (e: IllegalArgumentException) { throw IllegalStateException("PaRuX continuous speech returned invalid PCM Base64.", e) }
                        if (pcm.size != frame.samples * 2)
                            throw IllegalStateException("PaRuX continuous speech PCM size does not match its sample count.")
                        if (!writePcmFully(currentTrack, pcm))
                            return@coroutineScope PlaybackResult.Stopped
                        samplesWritten += frame.samples.toLong()
                    }
                    "done" -> sawDone = true
                    "error" -> throw IllegalStateException(frame.error ?: "PaRuX continuous speech failed.")
                    else -> throw IllegalStateException("Unsupported PaRuX continuous speech frame '${frame.type}'.")
                }
            }

            if (stopRequested) return@coroutineScope PlaybackResult.Stopped
            val finalTrack = track ?: return@coroutineScope PlaybackResult.Completed

            // AudioTrack.write() means copied into the device buffer, not necessarily heard. Drain
            // the small remaining tail while still honoring near-end speech; this avoids chopping
            // the final word when Core sends done immediately after its last PCM frame.
            val drainDeadline = SystemClock.elapsedRealtime() + 4_000L
            while (currentCoroutineContext().isActive
                && !stopRequested
                && finalTrack.playState == AudioTrack.PLAYSTATE_PLAYING
                && finalTrack.playbackHeadPosition.toLong() < samplesWritten
                && SystemClock.elapsedRealtime() < drainDeadline) {
                val d = detector
                if (d != null && d.isCompleted) {
                    val interrupted = runCatching { d.await() }.getOrDefault(false)
                    detector = null
                    if (interrupted) {
                        stopTrack(finalTrack)
                        return@coroutineScope PlaybackResult.BargeIn
                    }
                }
                delay(12)
            }
            if (stopRequested) PlaybackResult.Stopped else PlaybackResult.Completed
        } finally {
            channel.cancel()
            detector?.cancel()
            if (activeTrack === track) activeTrack = null
            stopTrack(track)
        }
    }

    /** Legacy full-WAV playback retained for non-live tools/artifacts. */
    @SuppressLint("MissingPermission")
    suspend fun playBase64WavWithBargeIn(base64: String): PlaybackResult = coroutineScope {
        if (base64.isBlank()) return@coroutineScope PlaybackResult.Completed
        stopRequested = false
        val file = File.createTempFile("parux-voice-", ".wav", context.cacheDir)
        file.writeBytes(Base64.decode(base64, Base64.DEFAULT))

        val player = MediaPlayer()
        activePlayer = player
        try {
            player.setDataSource(file.absolutePath)
            player.prepare()
            player.start()

            val detector = async(Dispatchers.IO) { detectBargeIn { isPlayerRunning(player) } }
            var result = PlaybackResult.Completed
            while (isActive && !stopRequested && isPlayerRunning(player)) {
                if (detector.isCompleted) {
                    val interrupted = runCatching { detector.await() }.getOrDefault(false)
                    if (interrupted) {
                        result = PlaybackResult.BargeIn
                        runCatching { player.stop() }
                        break
                    }
                }
                delay(45)
            }
            if (stopRequested) result = PlaybackResult.Stopped
            detector.cancel()
            result
        } finally {
            if (activePlayer === player) activePlayer = null
            runCatching { if (player.isPlaying) player.stop() }
            runCatching { player.release() }
            runCatching { file.delete() }
        }
    }

    @SuppressLint("MissingPermission")
    private fun detectBargeIn(isPlaybackRunning: () -> Boolean): Boolean {
        val recorder = try { createRecorder(MediaRecorder.AudioSource.VOICE_COMMUNICATION) } catch (_: Exception) { return false }
        activeRecorder = recorder
        val echoCanceler = if (AcousticEchoCanceler.isAvailable())
            runCatching { AcousticEchoCanceler.create(recorder.audioSessionId) }.getOrNull()
        else null
        runCatching { echoCanceler?.enabled = true }

        val frame = ShortArray(FrameSamples)
        var baseline = 220.0
        var baselineFrames = 0
        var voicedFrames = 0
        try {
            recorder.startRecording()
            if (recorder.recordingState != AudioRecord.RECORDSTATE_RECORDING) return false
            while (!stopRequested && isPlaybackRunning()) {
                val read = recorder.read(frame, 0, frame.size, AudioRecord.READ_BLOCKING)
                if (read < 0) return false
                if (read == 0) {
                    SystemClock.sleep(5)
                    continue
                }
                val level = rms(frame, read)

                if (baselineFrames < 18) {
                    baseline = if (baselineFrames == 0) level else baseline * 0.82 + level * 0.18
                    baselineFrames++
                    continue
                }

                val trigger = max(900.0, baseline * 2.15)
                if (level >= trigger) voicedFrames++ else voicedFrames = max(0, voicedFrames - 1)
                if (voicedFrames >= 9) return true
                if (level < trigger) baseline = baseline * 0.985 + level * 0.015
            }
            return false
        } finally {
            if (activeRecorder === recorder) activeRecorder = null
            runCatching { echoCanceler?.release() }
            runCatching { if (recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) recorder.stop() }
            runCatching { recorder.release() }
        }
    }

    private fun createPlaybackTrack(sampleRate: Int): AudioTrack {
        val minimum = AudioTrack.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_OUT_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (minimum <= 0) throw IllegalStateException("This Android device cannot stream 24 kHz mono PCM speech.")
        val track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build()
            )
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(max(minimum, sampleRate * 2 / 4))
            .build()
        if (track.state != AudioTrack.STATE_INITIALIZED) {
            runCatching { track.release() }
            throw IllegalStateException("Android could not initialize streaming playback for PaRuX Live Voice.")
        }
        return track
    }

    private suspend fun writePcmFully(track: AudioTrack, pcm: ByteArray): Boolean = withContext(Dispatchers.IO) {
        var offset = 0
        while (offset < pcm.size) {
            if (stopRequested) return@withContext false
            val written = try {
                track.write(pcm, offset, pcm.size - offset, AudioTrack.WRITE_BLOCKING)
            } catch (e: IllegalStateException) {
                if (stopRequested) return@withContext false
                throw e
            }
            if (written < 0) {
                if (stopRequested) return@withContext false
                throw IllegalStateException("Android AudioTrack write failed with code $written.")
            }
            if (written == 0) {
                SystemClock.sleep(2)
                continue
            }
            offset += written
        }
        true
    }

    @SuppressLint("MissingPermission")
    private fun createRecorder(source: Int): AudioRecord {
        val minimum = AudioRecord.getMinBufferSize(
            SampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (minimum <= 0) throw IllegalStateException("This Android device does not expose a 16 kHz mono microphone input.")
        val recorder = AudioRecord(
            source,
            SampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            max(minimum, FrameSamples * 2 * 8)
        )
        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            runCatching { recorder.release() }
            throw IllegalStateException("Android could not initialize the microphone for PaRuX Live Voice.")
        }
        return recorder
    }

    private fun stopTrack(track: AudioTrack?) {
        if (track == null) return
        runCatching { if (track.playState == AudioTrack.PLAYSTATE_PLAYING) track.stop() }
        runCatching { track.flush() }
        runCatching { track.release() }
    }

    private fun audioReadError(code: Int): String = when (code) {
        AudioRecord.ERROR_INVALID_OPERATION -> "invalid operation"
        AudioRecord.ERROR_BAD_VALUE -> "bad buffer value"
        AudioRecord.ERROR_DEAD_OBJECT -> "audio service disconnected"
        AudioRecord.ERROR -> "generic audio error"
        else -> "error $code"
    }

    private fun isPlayerRunning(player: MediaPlayer): Boolean =
        !stopRequested && runCatching { player.isPlaying }.getOrDefault(false)

    private fun isTrackRunning(track: AudioTrack): Boolean =
        !stopRequested && runCatching { track.playState == AudioTrack.PLAYSTATE_PLAYING }.getOrDefault(false)

    private fun rms(samples: ShortArray, length: Int = samples.size): Double {
        if (length <= 0) return 0.0
        var sum = 0.0
        for (i in 0 until length) {
            val value = samples[i].toDouble()
            sum += value * value
        }
        return sqrt(sum / length)
    }

    private fun writePcm16(output: ByteArrayOutputStream, samples: ShortArray) {
        val bytes = ByteBuffer.allocate(samples.size * 2).order(ByteOrder.LITTLE_ENDIAN)
        for (sample in samples) bytes.putShort(sample)
        output.write(bytes.array())
    }
}

internal object PcmWavEncoder {
    fun encode(pcm: ByteArray, sampleRate: Int, channels: Int, bitsPerSample: Int): ByteArray {
        require(sampleRate > 0 && channels > 0 && bitsPerSample == 16)
        val byteRate = sampleRate * channels * bitsPerSample / 8
        val blockAlign = channels * bitsPerSample / 8
        val output = ByteBuffer.allocate(44 + pcm.size).order(ByteOrder.LITTLE_ENDIAN)
        output.put("RIFF".toByteArray(Charsets.US_ASCII))
        output.putInt(36 + pcm.size)
        output.put("WAVE".toByteArray(Charsets.US_ASCII))
        output.put("fmt ".toByteArray(Charsets.US_ASCII))
        output.putInt(16)
        output.putShort(1.toShort())
        output.putShort(channels.toShort())
        output.putInt(sampleRate)
        output.putInt(byteRate)
        output.putShort(blockAlign.toShort())
        output.putShort(bitsPerSample.toShort())
        output.put("data".toByteArray(Charsets.US_ASCII))
        output.putInt(pcm.size)
        output.put(pcm)
        return output.array()
    }
}
