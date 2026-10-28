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
        private ulong _levelPos = 44;

        /// <summary>Raised when Windows reports the capture failed (for example the device was unplugged) while recording.</summary>
        public event Action<string>? CaptureFailed;

        public async Task StartAsync()
        {
            try
            {
                _capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio };
                string? deviceId = await ChooseMicrophoneAsync();
                if (deviceId != null) settings.AudioDeviceId = deviceId;
                await _capture.InitializeAsync(settings);
                _capture.Failed += (s, e) => CaptureFailed?.Invoke(e.Message);
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

        /// <summary>
        /// Peak level (0..1) of the audio written since the previous call, read from the tail of the in-memory
        /// WAV while recording (used for voice-activity detection). 0 when nothing new was written or the
        /// format is not 16-bit PCM.
        /// </summary>
        public async Task<double> PeekLevelAsync()
        {
            InMemoryRandomAccessStream? s = _stream;
            if (s == null) return 0;
            ulong size = s.Size;
            if (size <= _levelPos) return 0;
            uint count = (uint)Math.Min(size - _levelPos, 65536UL);
            count &= ~1u;
            if (count == 0) return 0;
            using IInputStream input = s.GetInputStreamAt(_levelPos);
            using var reader = new DataReader(input);
            uint loaded = await reader.LoadAsync(count);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            reader.DetachStream();
            _levelPos += loaded & ~1u;
            int peak = 0;
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                int v = Math.Abs((short)(bytes[i] | (bytes[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            return peak / 32768.0;
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
        /// <summary>Prefers a real microphone over a virtual/streaming default device (see <see cref="MicrophoneChooser"/>).</summary>
        private static async System.Threading.Tasks.Task<string?> ChooseMicrophoneAsync()
        {
            try
            {
                var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.AudioCapture);
                string defaultId = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(Windows.Media.Devices.AudioDeviceRole.Default);
                var devices = new System.Collections.Generic.List<MicrophoneChooser.Device>();
                foreach (var d in found) devices.Add(new MicrophoneChooser.Device(d.Id, d.Name, d.Id == defaultId));
                return MicrophoneChooser.Choose(devices);
            }
            catch (System.Exception) { return null; }
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
