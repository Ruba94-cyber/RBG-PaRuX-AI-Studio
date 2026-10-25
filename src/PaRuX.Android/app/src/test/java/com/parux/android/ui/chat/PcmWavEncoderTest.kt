package com.parux.android.ui.chat

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PcmWavEncoderTest {

    @Test
    fun encode_writes_canonical_pcm16_mono_header_and_payload() {
        val pcm = byteArrayOf(0x01, 0x02, 0x03, 0x04)
        val wav = PcmWavEncoder.encode(pcm, sampleRate = 16_000, channels = 1, bitsPerSample = 16)
        val little = ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)

        assertEquals(44 + pcm.size, wav.size)
        assertEquals("RIFF", wav.copyOfRange(0, 4).toString(Charsets.US_ASCII))
        assertEquals(36 + pcm.size, little.getInt(4))
        assertEquals("WAVE", wav.copyOfRange(8, 12).toString(Charsets.US_ASCII))
        assertEquals("fmt ", wav.copyOfRange(12, 16).toString(Charsets.US_ASCII))
        assertEquals(16, little.getInt(16))
        assertEquals(1, little.getShort(20).toInt())
        assertEquals(1, little.getShort(22).toInt())
        assertEquals(16_000, little.getInt(24))
        assertEquals(32_000, little.getInt(28))
        assertEquals(2, little.getShort(32).toInt())
        assertEquals(16, little.getShort(34).toInt())
        assertEquals("data", wav.copyOfRange(36, 40).toString(Charsets.US_ASCII))
        assertEquals(pcm.size, little.getInt(40))
        assertArrayEquals(pcm, wav.copyOfRange(44, wav.size))
    }

    @Test
    fun encode_rejects_non_pcm16_contract() {
        assertThrows(IllegalArgumentException::class.java) {
            PcmWavEncoder.encode(byteArrayOf(1, 2), sampleRate = 16_000, channels = 1, bitsPerSample = 8)
        }
    }
}
