using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.Media.Core;

namespace PaRuX.Services
{
    public sealed class VoiceCaptureException : Exception
    {
        public bool PermissionDenied { get; }
        public VoiceCaptureException(string message, bool permissionDenied, Exception? inner = null) : base(message, inner)
        {
            PermissionDenied = permissionDenied;
        }
    }

    /// <summary>
    /// Thin WinRT wrapper around MediaCapture (audio only, WAV, in-memory stream). No policy lives
    /// here: duration limits, silence checks and messages are in the pure classes.
    /// </summary>
    public sealed class MicrophoneRecorder : IDisposable
    {
        private MediaCapture? _capture;
        private InMemoryRandomAccessStream? _stream;

        public async Task StartAsync()
        {
            try
            {
                _capture = new MediaCapture();
                await _capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                });
                _stream = new InMemoryRandomAccessStream();
                await _capture.StartRecordToStreamAsync(MediaEncodingProfile.CreateWav(AudioEncodingQuality.Medium), _stream);
            }
            catch (UnauthorizedAccessException ex)
            {
                Dispose();
                throw new VoiceCaptureException(VoiceMessages.PermissionDenied(), true, ex);
            }
            catch (Exception ex)
            {
                Dispose();
                Log.Write("Microphone start failed: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                throw new VoiceCaptureException(VoiceMessages.NoMicrophone(), false, ex);
            }
        }

        /// <summary>Stops and returns the recorded WAV bytes.</summary>
        public async Task<byte[]> StopAsync()
        {
            if (_capture == null || _stream == null) return Array.Empty<byte>();
            try
            {
                await _capture.StopRecordAsync();
                _stream.Seek(0);
                using Stream read = _stream.AsStreamForRead();
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy);
                return copy.ToArray();
            }
            finally { Dispose(); }
        }

        /// <summary>Discards the recording without producing audio.</summary>
        public async Task CancelAsync()
        {
            try { if (_capture != null) await _capture.StopRecordAsync(); }
            catch (Exception ex) { Log.Write("Microphone cancel: " + ex.Message); }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            try { _capture?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _capture = null;
            _stream = null;
        }
    }

    /// <summary>Thin wrapper around MediaPlayer that plays one WAV byte array at a time.</summary>
    public sealed class WavPlayer : IDisposable
    {
        private MediaPlayer? _player;
        private InMemoryRandomAccessStream? _stream;

        public event Action? Ended;

        public bool IsPlaying => _player != null;

        public async Task PlayAsync(byte[] wav)
        {
            Stop();
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(wav);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var player = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, "audio/wav") };
            _stream = stream;
            _player = player;
            player.MediaEnded += (s, e) => { if (ReferenceEquals(_player, player)) { Stop(); Ended?.Invoke(); } };
            player.MediaFailed += (s, e) =>
            {
                Log.Write("Read aloud playback failed: " + e.ErrorMessage);
                if (ReferenceEquals(_player, player)) { Stop(); Ended?.Invoke(); }
            };
            player.Play();
        }

        public void Stop()
        {
            MediaPlayer? p = _player;
            InMemoryRandomAccessStream? s = _stream;
            _player = null;
            _stream = null;
            try { p?.Pause(); } catch { }
            try { p?.Dispose(); } catch { }
            try { s?.Dispose(); } catch { }
        }

        public void Dispose() => Stop();
    }
}
