using System;

namespace PaRuX.Services
{
    /// <summary>Result of inspecting a recorded WAV. Peak is the largest absolute sample, 0..1.</summary>
    public sealed record WavAnalysis(bool Valid, double DurationSeconds, double Peak, string? Problem)
    {
        public bool IsSilent => Valid && Peak < WavSilenceDetector.SilencePeakThreshold;
    }

    /// <summary>
    /// Pure WAV inspector used before a microphone recording is sent to speech-to-text, so an
    /// all-silent capture (muted mic, wrong device) is reported instead of transcribed.
    /// Supports PCM 8/16/24/32-bit and IEEE float 32-bit, any channel count.
    /// </summary>
    public static class WavSilenceDetector
    {
        /// <summary>Peak below this (about -60 dBFS) counts as silence.</summary>
        public const double SilencePeakThreshold = 0.001;

        public static WavAnalysis Analyze(byte[]? wav)
        {
            if (wav == null || wav.Length < 12) return Invalid("The recording is empty.");
            if (!Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) return Invalid("The recording is not a WAV file.");

            int format = 0, channels = 0, sampleRate = 0, bits = 0;
            bool haveFmt = false;
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                uint size = BitConverter.ToUInt32(wav, pos + 4);
                int body = pos + 8;
                if (Tag(wav, pos, "fmt "))
                {
                    if (size < 16 || body + 16 > wav.Length) return Invalid("The WAV header is damaged.");
                    format = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToUInt16(wav, body + 14);
                    if (format == 0xFFFE && size >= 26 && body + 26 <= wav.Length)
                        format = BitConverter.ToUInt16(wav, body + 24); // extensible: sub-format code
                    haveFmt = true;
                }
                else if (Tag(wav, pos, "data"))
                {
                    if (!haveFmt) return Invalid("The WAV header is damaged.");
                    if (channels <= 0 || sampleRate <= 0 || bits <= 0 || bits % 8 != 0)
                        return Invalid("The WAV header is damaged.");
                    long available = wav.Length - body;
                    // Streamed recordings may carry 0 or 0xFFFFFFFF as the data size.
                    long length = (size == 0 || size == uint.MaxValue || size > available) ? available : size;
                    return Measure(wav, body, length, format, channels, sampleRate, bits);
                }
                long next = (long)body + size + (size & 1);
                if (next > wav.Length) break;
                pos = (int)next;
            }
            return Invalid("The WAV file has no audio data.");
        }

        private static WavAnalysis Measure(byte[] wav, int start, long length, int format, int channels, int sampleRate, int bits)
        {
            int bytesPerSample = bits / 8;
            bool pcm = format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
            bool floating = format == 3 && bits == 32;
            if (!pcm && !floating) return Invalid("The recording uses an unsupported audio format.");

            long frameBytes = (long)bytesPerSample * channels;
            long frames = length / frameBytes;
            double duration = (double)frames / sampleRate;
            double peak = 0;
            long samples = frames * channels;
            int p = start;
            for (long i = 0; i < samples; i++, p += bytesPerSample)
            {
                double v;
                if (floating) v = Math.Abs(BitConverter.ToSingle(wav, p));
                else if (bits == 8) v = Math.Abs((wav[p] - 128) / 128.0);
                else if (bits == 16) v = Math.Abs(BitConverter.ToInt16(wav, p) / 32768.0);
                else if (bits == 24) v = Math.Abs((((wav[p + 2] << 24) | (wav[p + 1] << 16) | (wav[p] << 8)) >> 8) / 8388608.0);
                else v = Math.Abs(BitConverter.ToInt32(wav, p) / 2147483648.0);
                if (double.IsNaN(v)) continue;
                if (v > peak) peak = v;
            }
            return new WavAnalysis(true, duration, Math.Min(peak, 1.0), null);
        }

        private static WavAnalysis Invalid(string problem) => new WavAnalysis(false, 0, 0, problem);

        private static bool Tag(byte[] b, int offset, string tag)
        {
            if (offset + 4 > b.Length) return false;
            for (int i = 0; i < 4; i++) if (b[offset + i] != tag[i]) return false;
            return true;
        }
    }
}
