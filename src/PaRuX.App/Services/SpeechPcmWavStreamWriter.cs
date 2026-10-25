using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services;

internal readonly record struct SpeechPcmWavStreamResult(long DataBytes, long FileBytes, double DurationSeconds);

/// <summary>Incrementally persists validated PCM16 speech frames as a standard WAV artifact.</summary>
internal static class SpeechPcmWavStreamWriter
{
    private const int HeaderLength = 44;
    private const int BitsPerSample = 16;

    public static async Task<SpeechPcmWavStreamResult> WriteAsync(
        IAsyncEnumerable<SpeechStreamFrame> frames,
        string destinationPath,
        CancellationToken cancellationToken,
        IProgress<long>? bytesWritten = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A destination path is required.", nameof(destinationPath));

        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        string partialPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";

        bool completed = false;
        try
        {
            bool sawStart = false;
            bool sawAudio = false;
            bool sawDone = false;
            int sampleRate = 0;
            int channels = 0;
            long dataBytes = 0;
            double durationSeconds = 0;

            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(new byte[HeaderLength], cancellationToken).ConfigureAwait(false);

                await foreach (SpeechStreamFrame frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (frame == null) throw new InvalidDataException("Speech stream contained an empty frame.");

                    if (string.Equals(frame.Type, "start", StringComparison.Ordinal))
                    {
                        if (sawStart || sawAudio) throw new InvalidDataException("Speech stream contained an invalid start frame.");
                        ValidatePcmFormat(frame);
                        sampleRate = frame.SampleRate;
                        channels = frame.Channels;
                        sawStart = true;
                        continue;
                    }

                    if (!sawStart) throw new InvalidDataException("Speech stream did not begin with a start frame.");
                    if (string.Equals(frame.Type, "audio", StringComparison.Ordinal))
                    {
                        ValidatePcmFormat(frame);
                        if (frame.SampleRate != sampleRate || frame.Channels != channels)
                            throw new InvalidDataException("Speech stream changed its PCM format mid-request.");

                        byte[] pcm;
                        try { pcm = Convert.FromBase64String(frame.Base64Data ?? string.Empty); }
                        catch (FormatException ex) { throw new InvalidDataException("Speech stream contained invalid base64 audio.", ex); }

                        if (pcm.Length == 0 || (pcm.Length & 1) != 0 || frame.Samples <= 0 || pcm.Length != checked(frame.Samples * 2))
                            throw new InvalidDataException("Speech stream contained an invalid PCM16 sample count.");
                        dataBytes = checked(dataBytes + pcm.LongLength);
                        if (dataBytes > uint.MaxValue - 36L)
                            throw new InvalidDataException("Speech WAV exceeds the RIFF size limit.");

                        await output.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
                        sawAudio = true;
                        bytesWritten?.Report(dataBytes);
                        continue;
                    }

                    if (string.Equals(frame.Type, "error", StringComparison.Ordinal))
                        throw new InvalidDataException(string.IsNullOrWhiteSpace(frame.Error)
                            ? "Speech stream failed before completing audio."
                            : "Speech stream failed: " + frame.Error);

                    if (string.Equals(frame.Type, "done", StringComparison.Ordinal))
                    {
                        if (!sawAudio) throw new InvalidDataException("Speech stream completed without audio samples.");
                        ValidatePcmFormat(frame);
                        if (frame.SampleRate != sampleRate || frame.Channels != channels)
                            throw new InvalidDataException("Speech stream changed its PCM format at completion.");
                        if (!double.IsFinite(frame.DurationSeconds) || frame.DurationSeconds < 0)
                            throw new InvalidDataException("Speech stream reported an invalid duration.");

                        durationSeconds = frame.DurationSeconds > 0
                            ? frame.DurationSeconds
                            : dataBytes / (double)(sampleRate * channels * (BitsPerSample / 8));
                        sawDone = true;
                        break;
                    }

                    throw new InvalidDataException("Speech stream returned unsupported frame type '" + frame.Type + "'.");
                }

                if (!sawDone) throw new InvalidDataException("Speech stream ended without a done frame.");

                byte[] header = CreateHeader(dataBytes, sampleRate, channels);
                output.Position = 0;
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, fullPath, overwrite: false);
            completed = true;
            return new SpeechPcmWavStreamResult(dataBytes, checked(dataBytes + HeaderLength), durationSeconds);
        }
        finally
        {
            if (!completed)
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); }
                catch { /* Preserve the stream/write failure; the orphan is a uniquely named partial file. */ }
            }
        }
    }

    private static void ValidatePcmFormat(SpeechStreamFrame frame)
    {
        if (!string.Equals(frame.Format, "pcm_s16le", StringComparison.Ordinal)
            || frame.SampleRate <= 0
            || frame.Channels != 1)
            throw new InvalidDataException("Only mono PCM16 little-endian speech streams are supported.");
    }

    private static byte[] CreateHeader(long dataBytes, int sampleRate, int channels)
    {
        if (dataBytes < 0 || dataBytes > uint.MaxValue - 36L)
            throw new InvalidDataException("Speech WAV exceeds the RIFF size limit.");

        uint byteRate = checked((uint)(sampleRate * channels * (BitsPerSample / 8)));
        ushort blockAlign = checked((ushort)(channels * (BitsPerSample / 8)));
        byte[] header = new byte[HeaderLength];
        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), checked((uint)(dataBytes + 36)));
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34, 2), BitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), checked((uint)dataBytes));
        return header;
    }
}
